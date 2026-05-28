# AI Interpreter Workbench Architecture

> **Status:** First-draft canonical architecture spec for the MVP.
>
> **Audience:** Project owner, technical reviewers, future Claude Code sessions, and implementation agents.
>
> **Primary implementation constraint:** 3–4 days / ~15–20 hours total build effort.
>
> **Companion docs:** `PRESEARCH.md`, `RESEARCH.md`, `DECISIONS.md`, `DIAGRAM_PLAN.md`, `CLAUDE_CODE_HANDOFF.md`.
>
> **Build contract:** Claude Code should treat this as the first-draft source of truth, perform a second-pass gap audit, finalize it with confirmed edits, and only then create `MVP_TASKS.md` from the user's task template. Do not invent architecture in the task plan.

---

## Spec Anchor Index

| Anchor | Section |
|---|---|
| ARCH-001 | Executive Summary |
| ARCH-002 | Goals and Non-Goals |
| ARCH-003 | Locked Decisions |
| ARCH-004 | System Overview |
| ARCH-005 | Domain Model |
| ARCH-006 | Repository Scaffold |
| ARCH-007 | Frontend Architecture |
| ARCH-008 | Backend Architecture |
| ARCH-009 | API Contracts |
| ARCH-010 | Realtime Mode Architecture |
| ARCH-011 | Cascade Mode Architecture |
| ARCH-012 | Provider Interfaces |
| ARCH-013 | Metrics and Latency Model |
| ARCH-014 | Cost Estimation Model |
| ARCH-015 | WER Evaluation Model |
| ARCH-016 | Persistence Model |
| ARCH-017 | User Flows |
| ARCH-018 | Error Handling and Failure Modes |
| ARCH-019 | Security and Trust Boundaries |
| ARCH-020 | Testing Strategy |
| ARCH-021 | Local Development and Demo Strategy |
| ARCH-022 | Optional Deployment Strategy |
| ARCH-023 | Documentation and Git History Requirements |
| ARCH-024 | Alternatives Considered |
| ARCH-025 | MVP Boundaries and Deferred Work |
| ARCH-026 | Implementation Sequencing Guidance |
| ARCH-027 | Claude Code Review Instructions |

---

<a id="arch-001"></a>

## ARCH-001 — Executive Summary

AI Interpreter Workbench implements two live language interpretation architectures behind a single browser UI:

1. **Realtime mode:** direct voice-to-voice interpretation using OpenAI Realtime via browser WebRTC and a backend-minted ephemeral credential.
2. **Cascade mode:** composable STT → Translation → TTS pipeline using Deepgram STT, OpenAI text translation, and OpenAI TTS behind provider interfaces.

The architecture is intentionally an **instrumented comparison workbench**, not a production interpreter platform. The system exists to answer a Boostlingo-style architecture question:

> When should a live interpretation product prefer a vertically integrated realtime voice model versus a composable cascade pipeline?

The core posture is:

```text
One UI.
Two mode-specific execution paths.
One normalized session and metrics model.
One persisted evidence trail.
One comparison summary and recommendation.
```

The implementation should optimize for:

- Both modes working reliably in a local demo.
- Measured latency and cost evidence.
- Clear architecture seams.
- Provider abstractions that are real but not overbuilt.
- Testability through fake providers.
- A final 1–2 page write-up supported by session JSON results.

---

<a id="arch-002"></a>

## ARCH-002 — Goals and Non-Goals

### Goals

The MVP must:

1. Provide a browser SPA with microphone capture and audio playback.
2. Support explicit English → Spanish and Spanish → English language directions.
3. Support Realtime mode through OpenAI Realtime.
4. Support Cascade mode through STT → Translation → TTS.
5. Use turn-based click start/stop recording.
6. Display source and target transcripts.
7. Display top-level latency metrics for both modes.
8. Display stage-level latency metrics for Cascade mode.
9. Display estimated cost/minute by mode.
10. Include a comparison summary view.
11. Include a scripted WER Evaluation panel.
12. Persist session results to local JSON files.
13. Keep standard provider API keys server-side.
14. Include targeted tests for cascade orchestration, provider boundaries, WER, cost, metrics, and persistence.
15. Include README and `CLAUDE.md`/`AGENTS.md` documenting setup, architecture, and agent usage.
16. Produce a 1–2 page comparison write-up.

### Non-Goals

The MVP must not attempt:

- Production Boostlingo replacement.
- Multi-user rooms.
- PSTN/SIP/telephony.
- Human interpreter dispatch.
- Authentication/accounts.
- Customer billing.
- Production compliance workflows.
- Raw audio persistence.
- Multiple real providers per cascade stage.
- Seamless in-flight mode migration.
- Always-on full-duplex voice activity detection.
- Full semantic translation scoring.
- Production-grade AWS deployment as a blocker.

### Architecture Principle

Every implementation choice should preserve the comparison between:

```text
Realtime = lower app orchestration, lower stage-level control, likely lower latency.
Cascade  = more moving parts, more observability/control, more provider flexibility.
```

---

<a id="arch-003"></a>

## ARCH-003 — Locked Architecture Decisions

| Area | Decision | Rationale | Fallback |
|---|---|---|---|
| Product posture | Architecture evaluation workbench | Required by PRD tradeoff goal | Narrow UI if time slips |
| UX model | Turn-based click start/stop | Reduces audio drift and VAD complexity | Hold-to-talk later |
| Language | Explicit EN→ES and ES→EN | Satisfies PRD minimum | Auto-detect later |
| Realtime transport | Browser WebRTC + backend ephemeral credential | Best browser audio path and protects standard API key | Backend WebSocket proxy |
| Cascade STT | Deepgram streaming STT | Streaming STT, interim results, endpointing | OpenAI STT |
| Cascade translation | OpenAI text model | Low integration overhead | DeepL/Anthropic later |
| Cascade TTS | OpenAI TTS | Low integration overhead and streaming-capable | Deepgram Aura/ElevenLabs |
| Provider scope | One real provider per stage + fakes | Satisfies PRD without overbuilding | Add providers later |
| Backend | .NET/C# | PRD preferred backend | Node/Python only if blocker |
| Frontend | TypeScript SPA | PRD preferred frontend | Minimal framework |
| Persistence | Local JSON files | Inspectable, simple evidence trail | SQLite later |
| Audio storage | Do not persist raw audio | Avoids privacy/storage complexity | Optional later |
| Metrics | Shared latency event schema | Enables fair comparison | Top-level only if stage blocked |
| Cost | Config-driven estimated cost/min | Useful but honest | Write-up-only fallback |
| WER | Backend scripted WER utility | Deterministic STT quality metric | Manual transcript review |
| Deployment | Local-first, optional AWS later | PRD allows local-only | Deploy after stable demo |

---

<a id="arch-004"></a>

## ARCH-004 — System Overview

### Logical Components

```text
Browser SPA
  ├─ Session setup UI
  ├─ Mode/language selector
  ├─ Audio capture controller
  ├─ Playback controller
  ├─ Realtime WebRTC client
  ├─ Cascade API client
  ├─ Transcript panel
  ├─ Metrics panel
  ├─ Cost panel
  ├─ Evaluation/WER panel
  └─ Comparison summary panel

.NET Backend
  ├─ Session API
  ├─ Realtime client-secret broker
  ├─ Cascade orchestrator
  ├─ Provider interfaces
  │   ├─ ISttProvider
  │   ├─ ITranslationProvider
  │   └─ ITtsProvider
  ├─ Provider implementations
  │   ├─ DeepgramSttProvider
  │   ├─ OpenAiTranslationProvider
  │   ├─ OpenAiTtsProvider
  │   └─ Fake providers for tests
  ├─ Metrics normalizer
  ├─ Cost estimator
  ├─ WER evaluator
  ├─ Session persistence writer
  └─ Config/secrets loader

External Providers
  ├─ OpenAI Realtime
  ├─ Deepgram STT
  ├─ OpenAI text model
  └─ OpenAI TTS
```

### Runtime Data Plane

```text
Realtime mode:
Browser mic
→ Browser Realtime WebRTC client
→ OpenAI Realtime
→ Browser receives remote audio/events
→ Browser/backend record normalized metrics
→ Backend persists session JSON

Cascade mode:
Browser mic
→ Browser records turn
→ Backend cascade endpoint
→ Deepgram STT
→ OpenAI translation
→ OpenAI TTS
→ Backend returns/streams transcript/audio/metrics
→ Browser playback
→ Backend persists session JSON
```

### Control Plane

```text
Browser
→ Backend session APIs
→ Backend config/provider layer
→ JSON persistence
```

The backend owns session state and persistence. The frontend owns interactive UI state and browser media devices.

---

<a id="arch-005"></a>

## ARCH-005 — Domain Model

### Core Enums

```csharp
public enum InterpretationMode
{
    Realtime,
    Cascade
}

public enum LanguageCode
{
    En,
    Es
}

public enum TurnStatus
{
    Ready,
    Recording,
    Captured,
    Processing,
    Playing,
    Completed,
    Failed
}

public enum LatencyStage
{
    Capture,
    Realtime,
    Stt,
    Translation,
    Tts,
    Playback,
    Persistence,
    Evaluation
}
```

### Core Types

```csharp
public sealed record LanguageDirection(
    LanguageCode Source,
    LanguageCode Target);

public sealed record InterpretationSession(
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionConfig Config,
    List<InterpretationTurn> Turns,
    List<ModeTransitionEvent> ModeTransitions,
    SessionSummary? Summary);

public sealed record SessionConfig(
    InterpretationMode CurrentMode,
    LanguageDirection Direction,
    ProviderProfile ProviderProfile);

public sealed record InterpretationTurn(
    string TurnId,
    InterpretationMode Mode,
    LanguageDirection Direction,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long AudioDurationMs,
    List<TranscriptSegment> Transcripts,
    List<LatencyEvent> LatencyEvents,
    CostEstimate? CostEstimate,
    WerResult? WerResult,
    List<ProviderError> Errors,
    TurnStatus Status);

public sealed record TranscriptSegment(
    string SegmentId,
    string Role, // source | target
    string Text,
    bool IsFinal,
    string Provider,
    DateTimeOffset Timestamp);

public sealed record LatencyEvent(
    string Name,
    LatencyStage Stage,
    DateTimeOffset Timestamp,
    long RelativeMs,
    Dictionary<string, string> Metadata);

public sealed record CostEstimate(
    string Provider,
    string Model,
    string PricingBasis,
    decimal EstimatedUsd,
    decimal? EstimatedUsdPerMinute,
    Dictionary<string, decimal> Units,
    string PricingConfigVersion,
    string[] Assumptions);

public sealed record ProviderProfile(
    string RealtimeProvider,
    string RealtimeModel,
    string SttProvider,
    string SttModel,
    string TranslationProvider,
    string TranslationModel,
    string TtsProvider,
    string TtsModel);
```

### Evaluation Types

```csharp
public sealed record EvaluationPhrase(
    string PhraseId,
    LanguageCode Language,
    string ReferenceText,
    string Category);

public sealed record WerResult(
    string PhraseId,
    string Reference,
    string Hypothesis,
    string NormalizedReference,
    string NormalizedHypothesis,
    int Substitutions,
    int Insertions,
    int Deletions,
    int ReferenceWordCount,
    double Wer);
```

### Business Rules and Invariants

1. Every completed turn must include `Mode`, `Direction`, `StartedAt`, and at least one top-level latency metric.
2. Cascade completed turns should include `stt.final`, `translation.final`, and `tts.first_audio` or an explicit error explaining why not.
3. Realtime turns must not invent STT/translation/TTS stages.
4. Cost estimates must include provider/model/pricing basis.
5. WER can only be computed against a known `EvaluationPhrase`.
6. Session JSON must never include standard provider API keys.
7. Session JSON must not include raw audio for MVP.
8. Mode switching is forbidden during `Recording`, `Processing`, or `Playing` states.
9. Errors should be persisted as normalized `ProviderError` records.

---

<a id="arch-006"></a>

## ARCH-006 — Repository Scaffold

Recommended scaffold:

```text
ai-interpreter-workbench/
  README.md
  CLAUDE.md
  AGENTS.md                  # optional if CLAUDE.md is primary
  .gitignore
  .env.example
  docs/
    PRESEARCH.md
    RESEARCH.md
    DECISIONS.md
    ARCHITECTURE.md
    DIAGRAM_PLAN.md
    COMPARISON_WRITEUP.md
  data/
    sessions/                # gitignored except .gitkeep
      .gitkeep
  server/
    AiInterpreter.Api/
      Program.cs
      appsettings.json
      appsettings.Development.json
      Controllers/
        SessionsController.cs
        RealtimeController.cs
        CascadeController.cs
        EvaluationController.cs
      Realtime/
        RealtimeClientSecretService.cs
        RealtimeOptions.cs
      Cascade/
        CascadeOrchestrator.cs
        CascadeModels.cs
      Providers/
        Abstractions/
          ISttProvider.cs
          ITranslationProvider.cs
          ITtsProvider.cs
          ProviderEvents.cs
          ProviderErrors.cs
        Deepgram/
          DeepgramSttProvider.cs
          DeepgramOptions.cs
        OpenAI/
          OpenAiTranslationProvider.cs
          OpenAiTtsProvider.cs
          OpenAiOptions.cs
        Fakes/
          FakeSttProvider.cs
          FakeTranslationProvider.cs
          FakeTtsProvider.cs
      Sessions/
        SessionStore.cs
        SessionPersistenceWriter.cs
        SessionSummaryService.cs
        SessionModels.cs
      Metrics/
        LatencyEventFactory.cs
        MetricsAggregator.cs
      Cost/
        CostEstimator.cs
        PricingOptions.cs
      Evaluation/
        WerCalculator.cs
        EvaluationPhraseStore.cs
        EvaluationService.cs
        evaluation-phrases.json
      Security/
        ErrorSanitizer.cs
      Common/
        Result.cs
        Clock.cs
    AiInterpreter.Tests/
      CascadeOrchestratorTests.cs
      ProviderBoundaryTests.cs
      WerCalculatorTests.cs
      CostEstimatorTests.cs
      SessionPersistenceTests.cs
      MetricsAggregatorTests.cs
  web/
    package.json
    vite.config.ts
    src/
      main.tsx
      App.tsx
      api/
        sessionsApi.ts
        cascadeApi.ts
        realtimeApi.ts
        evaluationApi.ts
      audio/
        audioCaptureController.ts
        playbackController.ts
      realtime/
        realtimeWebRtcClient.ts
        realtimeEvents.ts
      cascade/
        cascadeClient.ts
      state/
        sessionStore.ts
      components/
        SessionSetup.tsx
        ModeToggle.tsx
        RecordingControls.tsx
        TranscriptPanel.tsx
        MetricsPanel.tsx
        CostPanel.tsx
        EvaluationPanel.tsx
        ComparisonSummary.tsx
        ErrorBanner.tsx
      types/
        domain.ts
        metrics.ts
```

### Gitignore Requirements

`.gitignore` must exclude:

```text
.env
.env.local
server/**/bin/
server/**/obj/
web/node_modules/
data/sessions/*.json
```

Keep `data/sessions/.gitkeep` if needed.

---

<a id="arch-007"></a>

## ARCH-007 — Frontend Architecture

### Responsibilities

The TypeScript SPA owns:

- Session setup UI.
- Optional session label input.
- Explicit language direction selection.
- Mode toggle.
- Microphone permission request.
- Turn recording lifecycle.
- Browser Realtime WebRTC connection.
- Cascade API call initiation.
- Audio playback queue.
- Transcript rendering.
- Metrics rendering.
- Cost rendering.
- WER Evaluation panel.
- Comparison summary rendering.
- User-facing error messages.

The frontend must not own:

- Standard provider API keys.
- Authoritative persisted session state.
- Provider-specific secrets.
- Cost pricing config source of truth.
- WER canonical calculation.

### Suggested Framework

Use React + Vite + TypeScript unless Claude Code identifies a strong reason not to. This is a pragmatic choice for speed and componentized UI.

### Frontend State Shape

```ts
export type UiSessionState = {
  sessionId: string | null;
  label?: string;
  mode: 'realtime' | 'cascade';
  direction: { source: 'en' | 'es'; target: 'en' | 'es' };
  status: 'idle' | 'configured' | 'active' | 'recording' | 'processing' | 'playing' | 'ended' | 'error';
  turns: TurnViewModel[];
  currentTurn?: TurnViewModel;
  summary?: SessionSummary;
  errors: UiError[];
};
```

### Recording Controls

Recording controls should enforce state transitions:

| Current State | Allowed Action |
|---|---|
| Idle | Configure/start session |
| Active/Ready | Start recording |
| Recording | Stop recording |
| Processing | No mode switch; no new recording |
| Playing | No mode switch unless playback cancel exists |
| Ended | Start new session |

### Audio Capture Controller

The MVP should start with the simplest reliable browser recording path:

- Use `navigator.mediaDevices.getUserMedia({ audio: true })`.
- Use `MediaRecorder` where acceptable.
- Capture a turn as an audio blob.
- Record start/stop timestamps in frontend and send them to backend.

If Realtime WebRTC needs direct track streaming, `RealtimeWebRtcClient` should request/use a live audio track independently from the cascade blob path.

### Playback Controller

The playback controller should:

- Play returned cascade audio from blob/URL/stream.
- Play Realtime remote audio track.
- Emit `playback.started`, `playback.ended`, and `playback.error` UI/backend events.
- Avoid overlapping playback by default.

### UI Components

#### `SessionSetup`

- Session label input.
- Direction selector.
- Start/end session buttons.

#### `ModeToggle`

- Realtime/Cascade selection.
- Disabled while recording/processing/playing.
- Shows current mode.

#### `RecordingControls`

- Start Recording.
- Stop Recording.
- Shows capture timer and status.

#### `TranscriptPanel`

- Source transcript.
- Target transcript.
- Partial vs final markers where available.
- Provider labels optional.

#### `MetricsPanel`

- Top-level latency.
- Cascade stage breakdown.
- Realtime event breakdown.
- Warnings if metrics unavailable.

#### `CostPanel`

- Estimated cost/minute.
- Estimated cost for current turn/session.
- Pricing assumptions link or tooltip.

#### `EvaluationPanel`

- Phrase selector.
- Reference phrase display.
- Start/stop recording for evaluation phrase.
- WER result display.
- Explanation that WER is STT-only.

#### `ComparisonSummary`

- Average latency by mode.
- Estimated cost/minute by mode.
- Error counts by mode/provider.
- WER summary.
- Turn count by mode.

### Frontend Error Philosophy

User-facing errors should be actionable:

- “Microphone permission denied. Enable mic access and retry.”
- “Realtime connection failed. Check OpenAI key/server logs or switch to Cascade.”
- “Cascade STT failed. Check Deepgram key/config.”
- “No speech detected. Try again closer to the microphone.”
- “Session persistence failed. Results may not be saved.”

Do not expose raw stack traces or provider secrets.

---

<a id="arch-008"></a>

## ARCH-008 — Backend Architecture

### Responsibilities

The .NET backend owns:

- Environment/config loading.
- Standard provider API keys.
- Realtime client-secret creation.
- Session creation, retrieval, ending, and summary.
- Cascade orchestration.
- Provider abstractions and real/fake implementations.
- Metrics normalization.
- Cost estimation.
- WER calculation.
- Session JSON persistence.
- Error normalization and sanitization.

### Suggested Backend Shape

Use ASP.NET Core Web API with minimal controllers or minimal APIs. Keep the project small but clearly layered.

Layering:

```text
Controllers/API endpoints
→ Application services
→ Provider abstractions
→ Provider implementations
→ Persistence/evaluation/cost utilities
```

Avoid putting provider logic directly in controllers.

### Configuration

Environment variables:

```text
OPENAI_API_KEY=
OPENAI_REALTIME_MODEL=gpt-realtime
OPENAI_TRANSLATION_MODEL=
OPENAI_TTS_MODEL=
DEEPGRAM_API_KEY=
DEEPGRAM_STT_MODEL=
SESSION_DATA_DIR=./data/sessions
PRICING_CONFIG_PATH=./config/pricing.json
ASPNETCORE_ENVIRONMENT=Development
```

`.env.example` should include names and comments, never real keys.

### Backend Services

| Service | Responsibility |
|---|---|
| `SessionStore` | In-memory active session state |
| `SessionPersistenceWriter` | Writes finalized/incremental session JSON |
| `SessionSummaryService` | Aggregates turns into summary |
| `RealtimeClientSecretService` | Creates OpenAI Realtime ephemeral credentials |
| `CascadeOrchestrator` | Runs STT → translation → TTS |
| `MetricsAggregator` | Computes latency summaries |
| `CostEstimator` | Produces estimated costs |
| `WerCalculator` | Computes WER from references/hypotheses |
| `EvaluationPhraseStore` | Loads scripted phrases |
| `ErrorSanitizer` | Converts internal/provider errors to safe UI errors |

### Backend State Model

Use in-memory active session state plus JSON persistence. Do not add a database.

Session write strategy:

- Create session in memory on `POST /api/sessions`.
- Append/update turn result after each completed turn.
- Write session JSON after each turn and again on end.
- If write fails, keep session running and emit `persistence.failed` error event.

This prevents losing all evidence if the app crashes late in the demo.

---

<a id="arch-009"></a>

## ARCH-009 — API Contracts

### Session APIs

#### `POST /api/sessions`

Creates a new session.

Request:

```json
{
  "label": "Demo run 1",
  "mode": "realtime",
  "sourceLanguage": "en",
  "targetLanguage": "es"
}
```

Response:

```json
{
  "sessionId": "session_abc123",
  "startedAt": "2026-05-28T15:30:00Z",
  "config": {
    "mode": "realtime",
    "sourceLanguage": "en",
    "targetLanguage": "es"
  }
}
```

#### `GET /api/sessions/{sessionId}`

Returns current/persisted session state.

#### `POST /api/sessions/{sessionId}/end`

Finalizes session summary and persists final JSON.

### Realtime API

#### `POST /api/realtime/client-secret`

Creates an OpenAI Realtime ephemeral credential.

Request:

```json
{
  "sessionId": "session_abc123",
  "sourceLanguage": "en",
  "targetLanguage": "es",
  "model": "gpt-realtime"
}
```

Response:

```json
{
  "clientSecret": "ephemeral_secret_value",
  "expiresAt": "2026-05-28T15:35:00Z",
  "model": "gpt-realtime"
}
```

The backend must use the standard OpenAI key server-side only.

#### `POST /api/sessions/{sessionId}/turns/{turnId}/events`

Allows frontend Realtime client to report normalized events/metrics for persistence.

Request:

```json
{
  "events": [
    {
      "name": "realtime.first_audio_delta",
      "stage": "realtime",
      "timestamp": "2026-05-28T15:30:05.123Z",
      "relativeMs": 842,
      "metadata": {}
    }
  ]
}
```

### Cascade API

#### `POST /api/cascade/turn`

Processes a recorded turn through cascade.

Request: `multipart/form-data`

Fields:

```text
sessionId: string
turnId: string optional; backend can generate
sourceLanguage: en|es
targetLanguage: en|es
audio: file/blob
recordingStartedAt: ISO timestamp
recordingStoppedAt: ISO timestamp
```

Response:

```json
{
  "turnId": "turn_001",
  "mode": "cascade",
  "sourceTranscript": "I need help checking in.",
  "targetTranscript": "Necesito ayuda para registrarme.",
  "audioContentType": "audio/mpeg",
  "audioBase64": "...",
  "latencyEvents": [],
  "costEstimate": {},
  "errors": []
}
```

If streaming response is feasible, use SSE or fetch streaming. If not, a full-turn response is acceptable for MVP as long as the backend records stage timings and the architecture notes that internal streaming/provider event shape is preserved.

### Evaluation APIs

#### `GET /api/evaluation/phrases`

Returns scripted phrases.

Response:

```json
[
  {
    "phraseId": "en_checkin_001",
    "language": "en",
    "referenceText": "I need help checking in for my appointment.",
    "category": "healthcare-intake"
  }
]
```

#### `POST /api/evaluation/wer`

Request:

```json
{
  "sessionId": "session_abc123",
  "turnId": "turn_001",
  "phraseId": "en_checkin_001",
  "hypothesis": "I need help checking in for my appointment"
}
```

Response:

```json
{
  "phraseId": "en_checkin_001",
  "reference": "I need help checking in for my appointment.",
  "hypothesis": "I need help checking in for my appointment",
  "substitutions": 0,
  "insertions": 0,
  "deletions": 0,
  "referenceWordCount": 8,
  "wer": 0.0
}
```

### Summary API

#### `GET /api/sessions/{sessionId}/summary`

Response:

```json
{
  "sessionId": "session_abc123",
  "turnCount": 8,
  "byMode": {
    "realtime": {
      "turnCount": 4,
      "avgSpeechEndToFirstAudioMs": 1120,
      "estimatedCostPerMinuteUsd": 0.034,
      "errorCount": 0
    },
    "cascade": {
      "turnCount": 4,
      "avgSpeechEndToFirstAudioMs": 2450,
      "estimatedCostPerMinuteUsd": 0.018,
      "avgSttFinalMs": 920,
      "avgTranslationFinalMs": 450,
      "avgTtsFirstAudioMs": 780,
      "errorCount": 1
    }
  },
  "wer": {
    "sampleCount": 3,
    "avgWer": 0.083
  }
}
```

---

<a id="arch-010"></a>

## ARCH-010 — Realtime Mode Architecture

### Purpose

Realtime mode demonstrates the vertically integrated voice-to-voice architecture.

### Responsibilities

Frontend:

- Request ephemeral client secret from backend.
- Create WebRTC peer connection.
- Attach microphone track.
- Receive model audio track.
- Receive data-channel events.
- Capture Realtime latency timestamps.
- Render transcripts/events where available.
- Report normalized turn events to backend for persistence.

Backend:

- Create ephemeral Realtime credential using standard OpenAI key.
- Never expose standard API key.
- Persist events reported by frontend.
- Estimate cost from provider usage or configured assumptions.

### Flow

```text
1. User starts Realtime session.
2. Browser calls POST /api/realtime/client-secret.
3. Backend creates ephemeral credential with OpenAI.
4. Browser creates RTCPeerConnection.
5. Browser attaches mic track.
6. Browser exchanges SDP with OpenAI Realtime endpoint.
7. Browser receives remote audio track and provider events.
8. User records a turn with click start/stop semantics.
9. Browser captures timestamps for speech end and first audio delta/playback.
10. Browser reports normalized events to backend.
11. Backend persists turn/session evidence.
```

### Realtime Prompt / Session Instruction

The Realtime session should be configured with explicit interpreting instruction:

```text
You are a live interpreter. Translate the user's speech from {sourceLanguage} to {targetLanguage}. Preserve meaning, tone, and intent. Output only the translated speech in {targetLanguage}. Do not answer as an assistant. Do not add explanations.
```

### Realtime Metrics

Required normalized events:

| Event | Meaning |
|---|---|
| `realtime.session.connected` | WebRTC connection established |
| `turn.recording.started` | User began turn capture |
| `turn.recording.stopped` | User ended turn capture; speech-end proxy |
| `realtime.first_audio_delta` | First model audio output event received |
| `realtime.first_transcript_delta` | First transcript/text delta if available |
| `playback.started` | Audio actually started playing |
| `turn.completed` | Turn finalized |

Computed metrics:

```text
speech_end_to_first_audio_ms = realtime.first_audio_delta - turn.recording.stopped
speech_end_to_playback_ms = playback.started - turn.recording.stopped
total_turn_ms = turn.completed - turn.recording.started
```

### Realtime Limitations to Preserve

The UI/write-up should explicitly show:

- Realtime has fewer internal stage metrics.
- Realtime offers less provider-level control.
- Realtime may have lower latency and simpler app orchestration.
- Realtime model availability/pricing must be configurable.

---

<a id="arch-011"></a>

## ARCH-011 — Cascade Mode Architecture

### Purpose

Cascade mode demonstrates a composable interpretation architecture where each stage can be swapped independently.

### Provider Baseline

```text
STT: Deepgram streaming STT
Translation: OpenAI text model
TTS: OpenAI TTS
```

### Cascade Flow

```text
1. User records turn in browser.
2. Browser sends audio turn to backend.
3. Backend creates turn and records capture timestamps.
4. CascadeOrchestrator calls ISttProvider.
5. STT emits partial/final transcript events.
6. CascadeOrchestrator calls ITranslationProvider with final or usable transcript.
7. Translation emits partial/final translated text events.
8. CascadeOrchestrator calls ITtsProvider.
9. TTS emits first audio/audio complete events.
10. Backend returns translated text/audio and metrics to browser.
11. Browser plays audio and reports playback timing if needed.
12. Backend persists turn/session evidence.
```

### Streaming Requirement Handling

The PRD asks for streaming throughout the cascade pipeline. The architecture should aim for event-shaped streaming at every boundary.

MVP acceptable simplification:

- Browser interaction is turn-based.
- Backend may receive a completed turn audio blob.
- Internally, provider interfaces are event-shaped.
- Stage metrics must still capture first partial/final timings where provider supports them.
- If a provider returns final-only output, wrap it as a final event and document that streaming partials are a future optimization for that stage.

This avoids lying about streaming while preserving architecture extensibility.

### Cascade Stage Events

Required stage events:

| Event | Stage |
|---|---|
| `cascade.audio.received` | Capture |
| `stt.started` | STT |
| `stt.first_partial` | STT |
| `stt.final` | STT |
| `translation.started` | Translation |
| `translation.first_token` | Translation |
| `translation.final` | Translation |
| `tts.started` | TTS |
| `tts.first_audio` | TTS |
| `tts.complete` | TTS |
| `playback.started` | Playback |
| `turn.completed` | Overall |

### Empty Transcript Rule

If STT returns an empty final transcript:

1. Do not call translation.
2. Do not call TTS.
3. Return a normalized `empty_transcript` error.
4. Persist the failed/partial turn.
5. UI should suggest retrying.

### Partial Failure Rule

If translation fails after STT succeeds:

- Persist source transcript.
- Mark translation/TTS as failed or skipped.
- Show source transcript and error in UI.

If TTS fails after translation succeeds:

- Persist source and target transcript.
- Mark audio output unavailable.
- Show target text and error in UI.

---

<a id="arch-012"></a>

## ARCH-012 — Provider Interfaces

### Design Goals

Provider interfaces must:

- Hide vendor-specific SDK/API details from orchestration.
- Emit normalized events for metrics/UI/persistence.
- Support fake providers for tests.
- Allow future provider swaps without rewriting the app.
- Keep provider-specific options in provider config.

### STT Interface

```csharp
public interface ISttProvider
{
    IAsyncEnumerable<SttEvent> TranscribeAsync(
        SttRequest request,
        CancellationToken cancellationToken);
}

public sealed record SttRequest(
    Stream AudioStream,
    string ContentType,
    LanguageCode SourceLanguage,
    string SessionId,
    string TurnId);

public abstract record SttEvent(DateTimeOffset Timestamp);

public sealed record SttStarted(DateTimeOffset Timestamp) : SttEvent(Timestamp);
public sealed record SttPartial(string Text, DateTimeOffset Timestamp) : SttEvent(Timestamp);
public sealed record SttFinal(string Text, DateTimeOffset Timestamp) : SttEvent(Timestamp);
public sealed record SttFailed(ProviderError Error, DateTimeOffset Timestamp) : SttEvent(Timestamp);
```

### Translation Interface

```csharp
public interface ITranslationProvider
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}

public sealed record TranslationRequest(
    string Text,
    LanguageCode SourceLanguage,
    LanguageCode TargetLanguage,
    string SessionId,
    string TurnId);

public abstract record TranslationEvent(DateTimeOffset Timestamp);

public sealed record TranslationStarted(DateTimeOffset Timestamp) : TranslationEvent(Timestamp);
public sealed record TranslationPartial(string TextDelta, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);
public sealed record TranslationFinal(string Text, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);
public sealed record TranslationFailed(ProviderError Error, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);
```

### TTS Interface

```csharp
public interface ITtsProvider
{
    IAsyncEnumerable<TtsEvent> SynthesizeAsync(
        TtsRequest request,
        CancellationToken cancellationToken);
}

public sealed record TtsRequest(
    string Text,
    LanguageCode TargetLanguage,
    string Voice,
    string SessionId,
    string TurnId);

public abstract record TtsEvent(DateTimeOffset Timestamp);

public sealed record TtsStarted(DateTimeOffset Timestamp) : TtsEvent(Timestamp);
public sealed record TtsFirstAudio(DateTimeOffset Timestamp, string ContentType) : TtsEvent(Timestamp);
public sealed record TtsAudioChunk(byte[] Bytes, DateTimeOffset Timestamp) : TtsEvent(Timestamp);
public sealed record TtsComplete(byte[] AudioBytes, string ContentType, DateTimeOffset Timestamp) : TtsEvent(Timestamp);
public sealed record TtsFailed(ProviderError Error, DateTimeOffset Timestamp) : TtsEvent(Timestamp);
```

### Provider Error Model

```csharp
public sealed record ProviderError(
    string Provider,
    string Stage,
    string Code,
    string SafeMessage,
    bool Retryable,
    int? HttpStatusCode = null);
```

### Fake Providers

Fake providers must support:

- Successful deterministic output.
- Timeout simulation.
- Empty STT transcript.
- Provider error simulation.
- Delayed event simulation for metrics tests.

---

<a id="arch-013"></a>

## ARCH-013 — Metrics and Latency Model

### Purpose

Metrics are the backbone of the project. Without credible metrics, the app is only an API demo.

### Latency Event Schema

All modes emit normalized `LatencyEvent` records:

```json
{
  "name": "stt.final",
  "stage": "stt",
  "timestamp": "2026-05-28T15:30:05.123Z",
  "relativeMs": 912,
  "metadata": {
    "provider": "deepgram",
    "model": "nova-3-multilingual"
  }
}
```

### Clock Rules

- Use server timestamps for backend provider stages.
- Use browser timestamps for browser-only media events.
- Persist source of event if necessary.
- Relative times should be calculated from turn recording start or turn processing start, consistently documented.
- The main PRD benchmark should use `turn.recording.stopped` as speech-end proxy.

### Universal Metrics

| Metric | Formula |
|---|---|
| `speech_end_to_first_audio_ms` | first output audio event or playback start minus `turn.recording.stopped` |
| `speech_end_to_playback_ms` | `playback.started` minus `turn.recording.stopped` |
| `total_turn_ms` | `turn.completed` minus `turn.recording.started` |
| `audio_duration_ms` | `turn.recording.stopped` minus `turn.recording.started` |
| `estimated_cost_usd` | stage/model-specific estimator |
| `estimated_cost_per_minute_usd` | cost divided by audio duration minutes |

### Cascade Stage Metrics

| Metric | Event Pair |
|---|---|
| `stt_first_partial_ms` | `stt.first_partial` - `cascade.audio.received` |
| `stt_final_ms` | `stt.final` - `cascade.audio.received` |
| `translation_first_token_ms` | `translation.first_token` - `translation.started` |
| `translation_final_ms` | `translation.final` - `translation.started` |
| `tts_first_audio_ms` | `tts.first_audio` - `tts.started` |
| `tts_complete_ms` | `tts.complete` - `tts.started` |

### Realtime Metrics

| Metric | Event Pair |
|---|---|
| `realtime_connect_ms` | `realtime.session.connected` - token request start or session start |
| `realtime_first_audio_delta_ms` | `realtime.first_audio_delta` - `turn.recording.stopped` |
| `realtime_first_transcript_delta_ms` | `realtime.first_transcript_delta` - `turn.recording.stopped` |
| `realtime_playback_ms` | `playback.started` - `turn.recording.stopped` |

### UI Requirements

The Metrics panel should show:

- Current turn top-level latency.
- Cascade stage breakdown when mode is Cascade.
- Realtime event breakdown when mode is Realtime.
- Session average by mode.
- Missing metric warnings.

### Write-Up Requirements

The comparison write-up should use actual measured values from session JSON and explicitly explain measurement limitations.

---

<a id="arch-014"></a>

## ARCH-014 — Cost Estimation Model

### Purpose

The PRD names cost per minute as an impact metric. The MVP should estimate live cost/minute while clearly avoiding billing-grade claims.

### Cost Estimator Responsibilities

- Load provider/model pricing config.
- Accept usage units from turns/stages.
- Estimate per-turn and per-session cost.
- Compute estimated cost/minute.
- Persist assumptions and pricing config version.
- Render “Estimated cost/min” in UI.

### Pricing Config Shape

```json
{
  "version": "2026-05-28-local",
  "currency": "USD",
  "providers": {
    "deepgram": {
      "stt": {
        "model": "nova-3-multilingual",
        "usdPerAudioMinute": 0.0092
      }
    },
    "openai": {
      "translation": {
        "model": "configurable",
        "inputUsdPerMillionTokens": 0.0,
        "outputUsdPerMillionTokens": 0.0
      },
      "tts": {
        "model": "configurable",
        "pricingBasis": "configure-from-current-openai-pricing"
      },
      "realtime": {
        "model": "gpt-realtime",
        "pricingBasis": "provider-usage-or-configured-estimate"
      }
    }
  }
}
```

Claude Code must update placeholder numeric values from current provider pricing or leave them clearly configured in `.env`/JSON with documentation.

### Cost Estimate Output

```json
{
  "provider": "deepgram",
  "model": "nova-3-multilingual",
  "pricingBasis": "usd_per_audio_minute",
  "estimatedUsd": 0.00031,
  "estimatedUsdPerMinute": 0.0092,
  "units": {
    "audioSeconds": 2.0
  },
  "pricingConfigVersion": "2026-05-28-local",
  "assumptions": [
    "Estimate based on configured public pricing, not provider invoice data."
  ]
}
```

### UI Copy

Use:

```text
Estimated cost/min
```

Do not use:

```text
Cost
```

without qualification.

---

<a id="arch-015"></a>

## ARCH-015 — WER Evaluation Model

### Purpose

WER gives an objective STT transcript quality signal for scripted phrases.

### Scope

WER applies to STT/source transcript accuracy. It does not measure full interpretation quality or semantic translation correctness.

### Evaluation Phrase File

`server/AiInterpreter.Api/Evaluation/evaluation-phrases.json`

Example:

```json
[
  {
    "phraseId": "en_001",
    "language": "en",
    "referenceText": "I need help checking in for my appointment.",
    "category": "appointment"
  },
  {
    "phraseId": "es_001",
    "language": "es",
    "referenceText": "Necesito ayuda para registrarme para mi cita.",
    "category": "appointment"
  }
]
```

### Normalization

Before WER:

- Lowercase.
- Remove punctuation.
- Normalize whitespace.
- Optionally strip accents only if documented; default should preserve language text unless tests require otherwise.

### Algorithm

Use dynamic-programming edit distance over word arrays and count substitutions, insertions, deletions.

```text
WER = (S + I + D) / N
```

where `N` is number of words in reference.

### Tests

Required cases:

- Perfect match returns 0.
- One deletion.
- One insertion.
- One substitution.
- Empty hypothesis.
- Empty reference should be rejected/handled explicitly.
- Punctuation/casing normalization.

### UI Explanation

Evaluation panel should display:

```text
WER compares the recognized transcript to a known reference phrase. It is useful for STT quality, not a full measure of translation quality.
```

---

<a id="arch-016"></a>

## ARCH-016 — Persistence Model

### Purpose

Persist session evidence for review, comparison write-up, and repeatability.

### Storage Approach

Use local JSON files under:

```text
data/sessions/
```

File name:

```text
session_YYYYMMDDTHHMMSSZ_<short-id>.json
```

### Persisted Fields

Persist:

- Session ID.
- Optional label.
- Started/ended timestamps.
- Provider profile.
- Mode transitions.
- Turns.
- Transcripts.
- Latency events.
- Cost estimates.
- WER results.
- Normalized errors.
- Summary.
- Pricing assumptions.

Do not persist:

- Raw audio.
- Standard API keys.
- Ephemeral client secret.
- Full provider response payloads if they include sensitive metadata.

### Example Session JSON

```json
{
  "sessionId": "session_abc123",
  "label": "Demo run 1",
  "startedAt": "2026-05-28T15:30:00Z",
  "endedAt": "2026-05-28T15:36:00Z",
  "providerProfile": {
    "realtimeProvider": "openai",
    "realtimeModel": "gpt-realtime",
    "sttProvider": "deepgram",
    "sttModel": "nova-3-multilingual",
    "translationProvider": "openai",
    "translationModel": "configurable",
    "ttsProvider": "openai",
    "ttsModel": "configurable"
  },
  "turns": [],
  "summary": {},
  "pricingConfigVersion": "2026-05-28-local"
}
```

### Write Strategy

- Write after session creation.
- Write after each completed/failed turn.
- Write after WER result.
- Write on session end.

Use atomic write pattern where feasible:

1. Serialize to temp file.
2. Rename temp file to final path.

If atomic write is too much for MVP, implement straightforward write and test it.

---

<a id="arch-017"></a>

## ARCH-017 — User Flows

### Flow A — Local Demo Setup

1. Clone repo.
2. Copy `.env.example` to `.env` or configure user secrets.
3. Add OpenAI and Deepgram keys.
4. Start backend.
5. Start frontend.
6. Open SPA.
7. Confirm provider health/config indicators.

### Flow B — Realtime Demo Turn

1. Start session.
2. Select English → Spanish.
3. Select Realtime mode.
4. Click Start Recording.
5. Speak phrase.
6. Click Stop Recording.
7. Hear Spanish output.
8. See transcripts/latency/cost.
9. Persist turn.

### Flow C — Cascade Demo Turn

1. Switch to Cascade mode between turns.
2. Click Start Recording.
3. Speak similar phrase.
4. Click Stop Recording.
5. Backend runs Deepgram → OpenAI translation → OpenAI TTS.
6. Hear output.
7. See per-stage latency.
8. Persist turn.

### Flow D — WER Evaluation

1. Open Evaluation panel.
2. Select phrase.
3. Read phrase aloud.
4. Capture STT transcript.
5. Compute WER.
6. Persist WER result.

### Flow E — Comparison Summary

1. Run multiple turns in both modes.
2. Open Summary panel.
3. Compare average latency, cost, errors, WER.
4. Use values in write-up.

### Flow F — End Session

1. Click End Session.
2. Backend finalizes summary.
3. JSON file is written.
4. UI shows path or success message.

---

<a id="arch-018"></a>

## ARCH-018 — Error Handling and Failure Modes

### Error Philosophy

Errors should be visible, normalized, persisted, and safe.

A failure in one cascade stage should not erase useful prior evidence.

### Failure Table

| Failure | Backend Behavior | UI Behavior | Test |
|---|---|---|---|
| Mic permission denied | No backend call | Show recovery hint | Manual |
| Missing OpenAI key | Config error | Disable Realtime/OpenAI stages | Config test |
| Missing Deepgram key | Config error | Disable Cascade STT/start warning | Config test |
| Realtime token failure | Sanitized error | Show retry/switch option | Service test |
| Realtime WebRTC failure | Frontend event + persisted error | Show connection failure | Manual/client test |
| STT timeout | Emit `stt.timeout` | Show STT failed | Fake provider test |
| Empty transcript | Skip translation/TTS | Show no speech detected | Fake provider test |
| Translation failure | Persist source transcript | Show translation failure | Fake provider test |
| TTS failure | Persist target transcript | Show audio unavailable | Fake provider test |
| Playback failure | Persist backend result | Show playback failed | Manual |
| Persistence failure | Continue session if possible | Show save warning | Writer test |
| Cost config missing | Estimate unavailable | Show N/A | Cost test |
| WER invalid phrase | Reject request | Show phrase error | Evaluation test |

### Retry Policy

MVP retry policy should be conservative:

- Do not auto-retry expensive model calls unless clearly safe.
- Allow user retry of a turn.
- Mark provider errors as retryable/non-retryable.
- Persist retries as separate turns or explicit retry metadata.

### Timeout Policy

Each provider should have configurable timeout:

```text
STT timeout: configured, e.g. 15–30s for MVP
Translation timeout: configured, e.g. 10–20s
TTS timeout: configured, e.g. 15–30s
Realtime token timeout: configured, e.g. 10s
```

Exact values can be tuned during implementation.

---

<a id="arch-019"></a>

## ARCH-019 — Security and Trust Boundaries

### Primary Trust Boundary

```text
Browser ↔ Backend ↔ External Providers
```

### Rules

1. Standard OpenAI and Deepgram API keys live only in backend environment/config.
2. Browser receives only ephemeral OpenAI Realtime credentials.
3. Session JSON excludes secrets and raw audio.
4. Provider errors are sanitized before UI display.
5. `.env` and session output files are gitignored.
6. CORS should be restricted to local frontend origin in development.
7. Backend validates session ID, mode, and language direction.
8. Backend validates uploaded audio size/content type for cascade endpoint.

### Sensitive Local Data

Even though raw audio is not persisted, transcripts may contain sensitive information. README should state:

```text
Session JSON may contain transcripts from local demo conversations. Treat files under data/sessions as sensitive local evaluation artifacts. Do not commit them.
```

### Non-Production Privacy Statement

Architecture/docs should explicitly say:

```text
This local-first workbench is for architecture evaluation only. It does not implement production authentication, customer account isolation, consent management, data retention policies, or regulated call handling.
```

---

<a id="arch-020"></a>

## ARCH-020 — Testing Strategy

### Testing Philosophy

Test the architecture seams, not browser audio internals exhaustively.

The critical correctness surfaces are:

- Cascade orchestration.
- Provider boundary behavior.
- Error mapping.
- Metrics aggregation.
- WER calculation.
- Cost estimation.
- Persistence.

### Backend Tests

Required:

| Test File | Coverage |
|---|---|
| `CascadeOrchestratorTests.cs` | Success path, stage ordering, partial failures |
| `ProviderBoundaryTests.cs` | Fake provider event contracts |
| `WerCalculatorTests.cs` | WER examples and normalization |
| `CostEstimatorTests.cs` | Deterministic pricing assumptions |
| `SessionPersistenceTests.cs` | JSON write/read and secret exclusion |
| `MetricsAggregatorTests.cs` | Average latency and stage metrics |
| `ErrorSanitizerTests.cs` | No raw secret/stack leakage |

### Frontend Tests

Optional/lightweight due to timebox:

- Component rendering for summary/metrics panels.
- State transition tests for mode toggle disabled during recording.
- Manual browser tests for mic/playback.

### Manual Preflight Checklist

Before demo:

1. Backend starts with env vars.
2. Frontend starts.
3. Mic permission works.
4. Realtime client secret endpoint works.
5. Realtime mode connects.
6. Cascade STT works.
7. Translation works.
8. TTS works.
9. Session JSON writes.
10. Summary panel updates.
11. WER phrase returns score.
12. 5-minute demo script run does not disconnect.

---

<a id="arch-021"></a>

## ARCH-021 — Local Development and Demo Strategy

### Local Setup

README should include:

```bash
# backend
cd server/AiInterpreter.Api
dotnet restore
dotnet run

# frontend
cd web
npm install
npm run dev
```

Exact commands may vary depending on final scaffold.

### Environment Setup

`.env.example`:

```text
OPENAI_API_KEY=sk-...
OPENAI_REALTIME_MODEL=gpt-realtime
OPENAI_TRANSLATION_MODEL=...
OPENAI_TTS_MODEL=...
DEEPGRAM_API_KEY=...
DEEPGRAM_STT_MODEL=...
SESSION_DATA_DIR=../../data/sessions
```

### Demo Script

The repo should include a 5-minute demo script in README or `docs/DEMO_SCRIPT.md`:

1. Start local app.
2. Start new session labeled `5-minute-demo`.
3. Run 2 English→Spanish Realtime turns.
4. Switch to Cascade.
5. Run 2 English→Spanish Cascade turns.
6. Switch direction to Spanish→English.
7. Run 1 Realtime turn.
8. Run 1 Cascade turn.
9. Run one WER evaluation phrase.
10. Show summary panel.
11. Open session JSON.
12. Explain tradeoffs.

### Suggested Demo Phrases

English:

- “I need help checking in for my appointment.”
- “Can you tell me where the front desk is?”
- “I have been waiting for about twenty minutes.”
- “Could you repeat that more slowly?”

Spanish:

- “Necesito ayuda para registrarme para mi cita.”
- “¿Puede decirme dónde está la recepción?”
- “He estado esperando unos veinte minutos.”
- “¿Puede repetir eso más despacio?”

---

<a id="arch-022"></a>

## ARCH-022 — Optional Deployment Strategy

Deployment is optional and should happen only after local demo works.

### Minimal AWS Direction

If deploying:

| Component | Option |
|---|---|
| Frontend | S3 + CloudFront or AWS Amplify |
| Backend | AWS App Runner, ECS Fargate, or Elastic Beanstalk |
| Secrets | Environment variables or AWS Secrets Manager |
| Session JSON | Local container disk for demo only, or S3 if needed |

### Deployment Non-Goals

Do not add:

- Production auth.
- Multi-region infra.
- Database.
- CI/CD complexity.
- Observability stack beyond basic logs.

### Deployment Warning

Browser mic/WebRTC requires HTTPS outside localhost. If deployment is attempted, ensure HTTPS is available for frontend.

---

<a id="arch-023"></a>

## ARCH-023 — Documentation and Git History Requirements

### README Must Include

- Project overview.
- Architecture summary.
- Local setup.
- Required environment variables.
- Running backend/frontend.
- Demo script.
- Provider configuration.
- What metrics mean.
- Cost estimate disclaimer.
- WER explanation.
- Known limitations.

### `CLAUDE.md` / `AGENTS.md` Must Include

- How the coding agent was used.
- Architecture-first workflow.
- Important constraints.
- Do not expose secrets.
- Do not persist raw audio.
- Preserve provider interfaces.
- Keep commits scoped.

### Comparison Write-Up Must Include

1. What was built.
2. How measurements were collected.
3. Realtime results.
4. Cascade results.
5. Latency comparison.
6. Cost comparison.
7. Quality/WER observations.
8. Controllability/provider flexibility comparison.
9. Recommendation: when to use Realtime vs Cascade.
10. Limitations and next steps.

### Git History Expectations

Avoid one giant initial commit. Suggested commit sequence:

1. Scaffold docs and repo.
2. Add backend session/domain models.
3. Add provider interfaces and fakes.
4. Add cascade orchestrator tests.
5. Add Deepgram/OpenAI provider implementations.
6. Add frontend shell and session UI.
7. Add audio capture/playback.
8. Add Realtime WebRTC path.
9. Add metrics/cost/WER panels.
10. Add persistence and summary.
11. Add README/demo/write-up.

---

<a id="arch-024"></a>

## ARCH-024 — Alternatives Considered

### Backend Proxy for Realtime

Rejected for MVP because browser WebRTC + ephemeral credential better matches browser audio and avoids sending all Realtime audio through the backend.

### All-OpenAI Cascade

Rejected as baseline because it weakens the composable-provider comparison. Deepgram STT makes Cascade visibly provider-flexible.

### DeepL Translation

Deferred. Strong translation specialization, but another API and character-billing model. OpenAI is simpler for the MVP.

### ElevenLabs TTS

Deferred. Great voice quality, but another integration. OpenAI TTS is faster to integrate given OpenAI is already required.

### SQLite Persistence

Deferred. JSON files are enough and easier to inspect.

### Always-On/VAD UX

Deferred. Turn-based recording is more reliable under the timebox.

### Raw Audio Persistence

Deferred/rejected for MVP because transcripts/metrics are sufficient and raw audio adds privacy/storage concerns.

---

<a id="arch-025"></a>

## ARCH-025 — MVP Boundaries and Deferred Work

### MVP Boundary

MVP is successful if:

- Local app runs.
- Both modes complete turns.
- User hears translated audio.
- Transcripts display.
- Latency metrics display.
- Cost estimates display.
- Session JSON persists.
- WER evaluation works for scripted phrase.
- Summary compares both modes.
- Tests cover core backend seams.
- README/demo/write-up are complete.

### Deferred Work

| Deferred Item | Reason |
|---|---|
| Multi-user calls | Not required by PRD |
| Auth/accounts | Scope creep |
| Raw audio persistence | Privacy/storage complexity |
| Multiple real providers per stage | Timebox |
| Full streaming upload from browser to cascade | Can be optimized after turn-based MVP |
| Semantic translation scoring | Out of scope |
| Production deployment | Optional after local success |
| Production observability | Not needed for local demo |
| VAD/barge-in/interruption | High audio complexity |

---

<a id="arch-026"></a>

## ARCH-026 — Implementation Sequencing Guidance

This is not `MVP_TASKS.md`, but Claude Code should use this sequence when generating tasks later.

### Phase A — Repo and Config

- Scaffold frontend/backend/docs.
- Add `.env.example` and `.gitignore`.
- Add domain models.
- Add config classes.

### Phase B — Backend Core Before Providers

- Session store.
- Persistence writer.
- Metrics model.
- Cost estimator.
- WER calculator.
- Fake providers.
- Cascade orchestrator tests.

### Phase C — Cascade Real Providers

- Deepgram STT.
- OpenAI translation.
- OpenAI TTS.
- Error mapping.
- Cascade endpoint.

### Phase D — Frontend Core

- Session setup.
- Mode/direction selectors.
- Recording controls.
- Transcript/metrics/cost panels.
- Cascade API integration.
- Playback.

### Phase E — Realtime Mode

- Backend client-secret endpoint.
- Frontend WebRTC client.
- Realtime event normalization.
- Persistence of Realtime metrics.

### Phase F — Evaluation and Summary

- Evaluation phrases.
- WER API/UI.
- Comparison summary.
- Session JSON inspection/export path.

### Phase G — Docs and Demo

- README.
- CLAUDE.md/AGENTS.md.
- Demo script.
- Comparison write-up.
- Optional deployment notes.

---

<a id="arch-027"></a>

## ARCH-027 — Claude Code Review Instructions

Before implementation, Claude Code must:

1. Read PRD, `PRESEARCH.md`, `RESEARCH.md`, `DECISIONS.md`, this `ARCHITECTURE.md`, and `DIAGRAM_PLAN.md` end-to-end.
2. Do not start implementation immediately.
3. Perform a second-pass architecture gap audit.
4. Identify any missing contracts, unclear event names, impossible provider assumptions, untestable requirements, or scope creep.
5. Propose precise edits to this architecture.
6. Ask for human confirmation on load-bearing changes.
7. Apply confirmed edits.
8. Only after architecture is finalized, create `MVP_TASKS.md` using the user's template.
9. Every task in `MVP_TASKS.md` must cite architecture anchors like `ARCH-010` or `ARCH-012`.
10. If a task requires architecture not present here, flag it instead of inventing it.

### Gap Audit Checklist

Claude Code should specifically verify:

- Current OpenAI Realtime endpoint/model names.
- Current OpenAI Realtime ephemeral credential request payload.
- Current OpenAI TTS API response format and streaming handling.
- Current Deepgram SDK/API package for .NET.
- Browser audio format accepted by cascade path.
- Whether transcoding is needed.
- Whether Realtime transcript events are available with selected model.
- Current pricing values for configured providers.
- Tests can run without real API keys.
- Session JSON excludes secrets and raw audio.

