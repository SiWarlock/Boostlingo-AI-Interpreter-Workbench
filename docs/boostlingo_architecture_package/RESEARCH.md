# RESEARCH.md — AI Interpreter Workbench

> **Status:** Research findings for current/external facts that affect architecture decisions.
>
> **Important:** Provider capabilities/pricing can change. The architecture must keep model/provider/pricing values configurable.

---

## Research Questions

| ID | Question | Decision Informed | Status |
|---|---|---|---|
| RQ-001 | Recommended OpenAI Realtime browser/server integration? | Realtime transport | Researched |
| RQ-002 | Can Realtime expose events useful for transcripts/latency? | Metrics model | Researched |
| RQ-003 | Best STT provider for MVP? | Cascade STT | Researched |
| RQ-004 | Best translation provider for MVP? | Cascade translation | Researched |
| RQ-005 | Best TTS provider for MVP? | Cascade TTS | Researched |
| RQ-006 | Cost/min estimation inputs? | Cost estimator | Researched |
| RQ-007 | WER implementation approach? | Evaluation module | Resolved locally |

---

## R-001 — OpenAI Realtime Transport

### Question

How should a browser SPA and .NET backend connect to OpenAI Realtime for direct voice-to-voice interpretation?

### Findings

OpenAI's Realtime WebRTC documentation describes a browser flow where the client first asks a developer-controlled server for a session token/client secret. The server uses the standard OpenAI API key, and the browser uses the returned ephemeral credential for WebRTC. The docs explicitly warn that standard OpenAI API keys should only be used server-side, not in the browser.

Source: https://developers.openai.com/api/docs/guides/realtime-webrtc

### Architecture Impact

Use a backend endpoint:

```http
POST /api/realtime/client-secret
```

The browser then uses the ephemeral credential to establish a direct WebRTC connection to OpenAI Realtime.

### Decision Implication

Lock: Browser WebRTC + backend-minted ephemeral OpenAI credential.

### Fallback

If WebRTC blocks implementation, use a backend WebSocket proxy only as a fallback, accepting higher server complexity and likely higher latency.

---

## R-002 — Realtime Events, Transcripts, and Metrics

### Question

Can Realtime mode expose events useful for transcripts and latency measurement?

### Findings

OpenAI Realtime docs describe a session lifecycle where clients send audio/text and receive model response events over the Realtime connection. Current Realtime docs and event models support output audio events and text/transcript-style deltas depending on model/session configuration.

Source: https://developers.openai.com/api/docs/guides/realtime

### Architecture Impact

Realtime mode should emit normalized metrics:

- `realtime.session.connected`
- `turn.recording.started`
- `turn.recording.stopped`
- `realtime.first_audio_delta`
- `realtime.first_transcript_delta`, if available
- `playback.started`
- `turn.completed`

Realtime must not fake STT/translation/TTS stage boundaries.

### Decision Implication

Lock: Realtime metrics are comparable at top-level but intentionally less stage-specific than Cascade.

---

## R-003 — Cascade STT Provider

### Question

Which STT provider is best for low-latency English/Spanish streaming MVP?

### Findings

Deepgram supports real-time live transcription via SDKs and WebSocket streaming. Its docs describe interim results for preliminary transcripts and endpointing/utterance-end features for detecting speech boundaries. Deepgram also documents .NET SDK support and WebSocket-based Streaming API support. Its pricing page lists streaming and pre-recorded speech-to-text pricing by minute, including Nova-3 multilingual and Flux multilingual options.

Sources:

- https://developers.deepgram.com/docs/live-streaming-audio
- https://developers.deepgram.com/docs/understand-endpointing-interim-results
- https://developers.deepgram.com/docs/lower-level-websockets
- https://deepgram.com/pricing

### Options Considered

| Option | Pros | Cons | Fit |
|---|---|---|---|
| Deepgram | Streaming STT, interim results, endpointing, .NET SDK path, clear per-minute pricing | Additional provider key | Best MVP fit |
| OpenAI STT | Fewer vendors | Less provider diversity; may blur Realtime vs Cascade comparison | Fallback |
| AssemblyAI | Strong STT vendor | More integration research | Deferred |
| Soniox | Strong multilingual/live STT positioning | More integration uncertainty | Deferred |

### Decision Implication

Lock: Use Deepgram as first real `ISttProvider`.

### Fallback

Use OpenAI transcription if Deepgram access/setup blocks progress.

---

## R-004 — Cascade Translation Provider

### Question

Which translation provider should be used for the MVP?

### Findings

The PRD allows OpenAI, Anthropic Claude, or DeepL. For this MVP, OpenAI is already required for Realtime and selected for TTS, making OpenAI text translation the lowest integration risk. OpenAI text responses can be event-shaped behind an `ITranslationProvider` even if the first implementation emits only final text or limited partials. DeepL is a strong translation-specialized future provider but adds another API and character-billing integration. Anthropic is a valid future LLM translation provider but adds another provider key and cost surface.

Sources:

- https://openai.com/api/pricing/
- https://support.deepl.com/hc/en-us/articles/360020685720-Usage-count-and-billing-in-DeepL-API
- https://docs.anthropic.com/

### Decision Implication

Lock: Use OpenAI text model as first real `ITranslationProvider`.

### Fallback

Use DeepL if translation quality becomes more important than streaming simplicity. Use Anthropic later if a second LLM vendor is needed.

---

## R-005 — Cascade TTS Provider

### Question

Which TTS provider should be used for low-latency streaming audio?

### Findings

OpenAI's text-to-speech docs support model-generated speech output and streaming-oriented use cases. Deepgram also offers streaming TTS through its Aura WebSocket API and documents audio-output streaming patterns, but using Deepgram for both STT and TTS reduces provider diversity. ElevenLabs, Azure Speech, and Polly are viable alternatives but add integration overhead in a 15–20 hour MVP.

Sources:

- https://platform.openai.com/docs/guides/text-to-speech
- https://developers.deepgram.com/docs/streaming-text-to-speech
- https://developers.deepgram.com/docs/streaming-the-audio-output

### Decision Implication

Lock: Use OpenAI TTS as first real `ITtsProvider`.

### Fallback

Use Deepgram Aura TTS if OpenAI TTS integration blocks progress. Consider ElevenLabs later for voice quality comparison.

---

## R-006 — Cost/Minute Estimation

### Question

How should live cost/minute be estimated?

### Findings

OpenAI pricing is model-dependent and may use token-based audio/text units depending on model. Deepgram STT pricing is naturally minute-based for selected STT models. Because exact billable usage may not be available consistently during live demo, the MVP should use a config-driven cost estimator and persist the pricing assumptions used.

Sources:

- https://openai.com/api/pricing/
- https://deepgram.com/pricing

### Architecture Impact

Use config:

```json
{
  "pricing": {
    "realtime": {
      "provider": "openai",
      "model": "configurable",
      "basis": "provider_usage_or_estimate"
    },
    "stt": {
      "provider": "deepgram",
      "basis": "usd_per_audio_minute"
    },
    "translation": {
      "provider": "openai",
      "basis": "tokens_estimated_or_reported"
    },
    "tts": {
      "provider": "openai",
      "basis": "tokens_or_characters_estimated_or_reported"
    }
  }
}
```

### Decision Implication

Lock: Show **Estimated cost/min** and persist assumptions.

### Remaining Risk

Pricing can change and provider usage signals may be incomplete. The write-up must clearly say estimates are not billing-grade.

---

## R-007 — WER Implementation

### Question

Where should WER live?

### Findings

No external service is needed. WER can be implemented with Levenshtein edit distance over normalized word arrays:

```text
WER = (substitutions + insertions + deletions) / reference_word_count
```

### Architecture Impact

Implement backend C# utility:

```text
server/Evaluation/WerCalculator.cs
server/Evaluation/EvaluationPhraseStore.cs
server/Evaluation/EvaluationService.cs
```

### Decision Implication

Lock: Backend WER utility with unit tests.

---

## Research Summary

| Area | Decision |
|---|---|
| Realtime | Browser WebRTC + backend ephemeral credential |
| STT | Deepgram streaming STT |
| Translation | OpenAI text model |
| TTS | OpenAI TTS |
| Cost | Config-driven estimated cost/min |
| WER | Backend deterministic utility |

