# AI Interpreter Workbench — Architecture

> **Status:** Finalized canonical architecture spec (design contract) for the MVP.
>
> **Audience:** Project owner, technical reviewers, future Claude Code sessions, and implementation agents.
>
> **Build budget:** ~5 days max; aim to finish faster and keep scope tight. (The PRD states "3–4 days / ~15–20 hours"; the owner clarified the real ceiling is ~5 days. Favor the architecturally-correct, PRD-faithful implementation over scope-shedding, but do not gold-plate — keep the documented fallbacks and the trim catalog as the pressure valve.)
>
> **Companion docs:** `boostlingo_architecture_package/PRESEARCH.md`, `RESEARCH.md`, `DECISIONS.md`, `DIAGRAM_PLAN.md`, `CLAUDE_CODE_HANDOFF.md`. This document supersedes `ARCHITECTURE_DRAFT.md`.
>
> **Build contract:** This is the source of truth. `MVP_TASKS.md` is generated from it; every task cites one or more `ARCH-###` anchors. Do not invent architecture in the task plan — if a task needs architecture not present here, flag it and add it here first.

---

## Spec Anchor Index

> **Anchor convention.** `ARCH-###` are **stable citation anchors** (every section carries an `<a id="arch-nnn">`); `§` numbers are the **template structure**. **Cite `ARCH-###` in `MVP_TASKS.md`** — they never renumber even if `§` structure changes.

| Anchor | § | Section |
|---|---|---|
| ARCH-001 | Exec | Executive Summary |
| ARCH-002 | §1 | Goals and Non-Goals |
| ARCH-003 | §2 | Locked Architecture Decisions |
| ARCH-004 | §2 | System Overview |
| ARCH-005 | §3 | Domain Model & Lifecycle |
| ARCH-006 | §5 | Repository Scaffold |
| ARCH-007 | §4 | Frontend Architecture |
| ARCH-030 | §4 | Audio Capture & Format |
| ARCH-008 | §5 | Backend Architecture |
| ARCH-009 | §6 | API Contracts |
| ARCH-010 | §7 | Realtime Mode Architecture |
| ARCH-011 | §8 | Cascade Mode Architecture |
| ARCH-012 | §9 | Provider Interfaces |
| ARCH-013 | §10 | Metrics and Latency Model |
| ARCH-014 | §11 | Cost Estimation Model |
| ARCH-015 | §12 | WER Evaluation Model |
| ARCH-016 | §13 | Persistence Model |
| ARCH-017 | §14 | User Flows |
| ARCH-018 | §15 | Error Handling and Failure Modes |
| ARCH-019 | §15 | Security and Trust Boundaries |
| ARCH-028 | §15 | Configuration & Secrets |
| ARCH-029 | §15 | Build & Run Contract |
| ARCH-020 | §15 | Testing Strategy |
| ARCH-027 | §16 | Open Questions & Build-Time Confirmations |
| ARCH-021 | App B | Local Development and Demo Strategy |
| ARCH-022 | App B | Optional Deployment Strategy |
| ARCH-023 | App B | Documentation and Git History Requirements |
| ARCH-024 | App B | Alternatives Considered |
| ARCH-025 | App B | MVP Boundaries and Deferred Work |
| ARCH-026 | App B | Implementation Sequencing Guidance |

---

<a id="arch-001"></a>

## Executive summary

> **Architecture sentence:** _When should a live interpretation product prefer a vertically-integrated realtime voice model versus a composable cascade pipeline?_

The AI Interpreter Workbench is a **browser-based architecture-evaluation tool** that builds and instruments the two dominant patterns for live AI interpretation, then measures them head-to-head so Boostlingo can decide which fits which use case. It is not a production interpreter platform; its product is **evidence** — measured latency, cost, and quality from a real working build of both modes.

The system runs two mode-specific execution paths behind one mode-agnostic UI:

- **Realtime mode** — direct voice-to-voice interpretation using the **OpenAI Realtime API** (`gpt-realtime` / `gpt-realtime-mini`, GA) over a **browser WebRTC** peer connection, authorized by a **backend-minted ephemeral credential** so the standard API key never reaches the browser.
- **Cascade mode** — a **fully streaming** composable pipeline: live mic audio streams to the backend, **Deepgram** (`nova-3`, `language=multi`) transcribes with interim results, **OpenAI** (`gpt-5.4-nano` / `gpt-5.4-mini`, switchable) translates with streamed tokens, and **OpenAI TTS** synthesizes streamed audio — all behind swappable provider interfaces with fakes for tests.

The posture in one line: **one UI, two transports, one normalized session + metrics model, one persisted evidence trail, one comparison write-up.** The major subsystems are: (1) a **TypeScript SPA** owning mic capture, playback, the two transports, and all rendering; (2) a **.NET/C# backend** owning secrets, the ephemeral-credential broker, the streaming cascade orchestrator, provider abstractions (+ real + fake implementations), metrics normalization, cost estimation, WER, and JSON persistence; (3) **provider interfaces** (`ISttProvider`, `ITranslationProvider`, `ITtsProvider`) emitting normalized streaming events; and (4) the **evidence layer** — a shared `LatencyEvent` schema, a config-driven cost estimator, a scripted WER utility, and local JSON session files (no raw audio, no secrets).

Both modes stream end-to-end with **no full-utterance blocking** and render **source and target transcripts as they are produced**, satisfying the PRD's streaming and live-transcript mandates while preserving a turn-based click start/stop UX (the click delimits the turn; audio streams within it). Persistence is local JSON only — inspectable, no database. Cost is a clearly-labeled estimate, not billing-grade. WER scores STT transcript accuracy against scripted phrases only, not semantic translation quality.

The build budget is ~5 days. The two highest **technical-risk** integrations are Realtime WebRTC and the live-streaming cascade; they are sequenced last, each with a documented fallback (backend WebSocket proxy for Realtime; blob + Deepgram pre-recorded for cascade) so a blocker degrades gracefully rather than sinking the demo.

---

<a id="arch-002"></a>

## §1 — Goals and Non-Goals

### Goals

The MVP must:

1. Provide a browser SPA with microphone capture and audio playback.
2. Support explicit English → Spanish and Spanish → English directions (no auto-detect).
3. Support Realtime mode through the OpenAI Realtime API (`gpt-realtime`, with `gpt-realtime-mini` selectable).
4. Support Cascade mode as a **fully streaming** STT → Translation → TTS pipeline (no full-utterance blocking).
5. Use turn-based click start/stop recording as the turn delimiter; stream audio within the turn.
6. Display source **and** target transcripts **as they are produced** (live).
7. Display top-level latency metrics for both modes and **per-stage** latency for Cascade.
8. Display estimated cost/minute by mode, and support comparing model variants (both Realtime models; both translation models).
9. Include a comparison summary view aggregating metrics by mode.
10. Include a standalone WER **Evaluation panel** (committed MUST) backed by a deterministic backend utility.
11. Persist session results to local JSON files (no raw audio, no secrets).
12. Keep standard provider API keys server-side; the browser receives only an ephemeral Realtime credential.
13. Include targeted tests for cascade orchestration, provider boundaries, error mapping, metrics, WER, cost, persistence, and error sanitization.
14. Include README + `CLAUDE.md`/`AGENTS.md` documenting setup, architecture, and agent usage.
15. Produce a 1–2 page comparison write-up with a defensible recommendation.

### Non-Goals

Production Boostlingo replacement; multi-user rooms; PSTN/SIP/telephony; human-interpreter dispatch; auth/accounts; customer billing; production compliance workflows; raw-audio persistence; multiple real providers **per stage beyond the chosen one** (provider *abstractions* are required; multiple live providers per stage are not — except the two model variants explicitly chosen for Realtime and translation); seamless in-flight mode migration mid-turn; always-on full-duplex VAD/barge-in; full semantic translation scoring; production AWS deployment as a blocker.

### Architecture Principle

Every choice preserves a fair comparison:

```text
Realtime = lower app orchestration, fewer stage-level signals, likely lower latency, more vendor lock-in.
Cascade  = more moving parts, more observability/control, more provider flexibility, more latency surface.
```

---

<a id="arch-003"></a>

## §2 — System Overview

### Locked Architecture Decisions

| Area | Decision | Rationale | Fallback |
|---|---|---|---|
| Product posture | Architecture evaluation workbench | PRD tradeoff goal | Narrow UI if time slips |
| UX model | Turn-based click start/stop (delimits a turn; audio streams within it) | Reliable, measurable; no VAD tuning | Hold-to-talk later |
| Language | Explicit EN→ES and ES→EN | PRD minimum; avoids auto-detect | Auto-detect later |
| Realtime transport | Browser WebRTC + backend ephemeral credential | Official GA browser pattern; key stays server-side | Backend WebSocket proxy |
| Realtime model | `gpt-realtime` default, `gpt-realtime-mini` selectable | Enables premium-vs-efficient cost comparison | Single model if needed |
| Cascade transport | **Live streaming** (browser→backend WebSocket audio; backend→browser event stream) | PRD: streaming throughout, no full-utterance blocking, live transcripts | Blob upload + Deepgram pre-recorded |
| Cascade STT | Deepgram `nova-3` + `language=multi`, **live WebSocket** | Streaming + interim results + endpointing; EN/ES code-switching | Deepgram pre-recorded; OpenAI STT |
| Cascade translation | OpenAI `gpt-5.4-nano` default, `gpt-5.4-mini` switchable, **streamed** (Responses API) | Low latency; quality-vs-latency comparison | DeepL/Anthropic later |
| Cascade TTS | OpenAI TTS (`gpt-4o-mini-tts` default), **streamed** | Low integration overhead; streaming-capable | Deepgram Aura / ElevenLabs |
| Provider scope | One real provider per stage + interfaces + fakes (+ the two named model variants) | Meets PRD without overbuilding | Add providers later |
| Backend | .NET/C# (ASP.NET Core) | PRD preferred | Node/Python only if blocker |
| Frontend | TypeScript SPA (React + Vite) | PRD preferred; fast componentized UI | Minimal framework |
| Persistence | Local JSON session files | Inspectable evidence; no DB | SQLite later |
| Audio storage | Never persist raw audio | Privacy/storage scope | — |
| Metrics | Shared `LatencyEvent` schema, real (non-synthetic) stage stamps | Fair comparison | Top-level only if a stage signal is unavailable |
| Cost | Config-driven estimated cost/min (not billing-grade) | Honest, useful | Write-up-only fallback |
| WER | Backend scripted Levenshtein utility + standalone panel | Deterministic STT quality metric | Inline WER if panel trimmed |
| Deployment | Local-first; optional AWS later | PRD allows local-only | AWS after stable demo |

<a id="arch-004"></a>

### Logical Components

```text
Browser SPA (React + Vite + TypeScript)
  ├─ Session setup UI (label, direction, mode, model selectors)
  ├─ Mode / language selector (ModeToggle)
  ├─ Audio capture controller
  │    ├─ Streaming path: getUserMedia → AudioContext → AudioWorklet → linear16 PCM frames → WebSocket
  │    └─ Fallback path: MediaRecorder → blob → multipart POST
  ├─ Playback controller (MSE progressive + assembled-blob fallback)
  ├─ Realtime WebRTC client (RTCPeerConnection + 'oai-events' data channel)
  ├─ Cascade streaming client (WebSocket /api/cascade/stream)
  ├─ Transcript panel (live source + target partials/finals)
  ├─ Metrics panel · Cost panel · Evaluation/WER panel · Comparison summary · Error banner

.NET Backend (ASP.NET Core)
  ├─ Session API (create / get / end / summary / turn create+complete)
  ├─ Config & health API
  ├─ Realtime client-secret broker (mints ephemeral credential)
  ├─ Realtime turn-events ingest
  ├─ Cascade streaming orchestrator (WebSocket) + blob fallback (HTTP)
  ├─ Provider interfaces: ISttProvider · ITranslationProvider · ITtsProvider
  ├─ Provider impls: DeepgramSttProvider · OpenAiTranslationProvider · OpenAiTtsProvider · Fakes
  ├─ Metrics normalizer · Cost estimator · WER evaluator · Error sanitizer
  ├─ Session store (in-memory) + JSON persistence writer
  └─ Config/secrets loader (IOptions)

External Providers
  ├─ OpenAI Realtime (gpt-realtime / gpt-realtime-mini)
  ├─ Deepgram STT (nova-3, language=multi)
  ├─ OpenAI text model (gpt-5.4-nano / gpt-5.4-mini)
  └─ OpenAI TTS (gpt-4o-mini-tts)
```

### Runtime Data Plane

```text
Realtime mode (browser ↔ OpenAI direct, backend brokers + persists):
  Browser mic ─WebRTC track→ OpenAI Realtime ─remote audio track→ Browser playback
  Browser ─POST /api/realtime/client-secret→ Backend ─POST /v1/realtime/client_secrets→ OpenAI (mint)
  Browser ─normalized turn events→ Backend → JSON persistence

Cascade mode (fully streaming through the backend):
  Browser mic → AudioWorklet PCM frames ─WebSocket→ Backend orchestrator
    → Deepgram live WS (interim + final source transcript)
        → on each finalized segment → OpenAI translation (streamed target tokens)
            → OpenAI TTS (streamed target audio)
  Backend ─WebSocket event stream→ Browser (source/target transcript partials, per-stage LatencyEvents,
                                              TTS audio chunks, cost, errors, done)
  Backend → JSON persistence (per turn + on end)
```

### Control Plane

```text
Browser → Backend session/config APIs → config/provider layer → in-memory session store → JSON persistence
```

The backend owns session state and persistence. The frontend owns interactive UI state and browser media devices.

---

<a id="arch-005"></a>

## §3 — Domain Model & Lifecycle

> **Source of truth.** This section is **authoritative** for the domain model and lifecycle. `PRESEARCH.md` is descriptive background. All API JSON (ARCH-009) and persisted JSON (ARCH-016) are **camelCase serializations of these records** and must stay in sync (see Appendix A).

### Core Enums

```csharp
public enum InterpretationMode { Realtime, Cascade }
public enum LanguageCode { En, Es }

// Turn-level status (one recorded input→output cycle)
public enum TurnStatus { Ready, Recording, Captured, Processing, Playing, Completed, Failed }

// Session-level status (whole evaluation session)
public enum SessionStatus { Idle, Configured, Starting, Active, ReadyForTurn, Ending, Ended }

public enum LatencyStage { Capture, Realtime, Stt, Translation, Tts, Playback, Persistence, Evaluation, Overall }
public enum ClockSource { Server, Browser }
```

### Core Records

```csharp
public sealed record LanguageDirection(LanguageCode Source, LanguageCode Target);

public sealed record ProviderProfile(
    string RealtimeProvider,      // "openai"
    string RealtimeModel,         // "gpt-realtime" | "gpt-realtime-mini"
    string SttProvider,           // "deepgram"
    string SttModel,              // "nova-3"
    string SttLanguage,           // "multi"
    string TranslationProvider,   // "openai"
    string TranslationModel,      // "gpt-5.4-nano" | "gpt-5.4-mini"
    string TtsProvider,           // "openai"
    string TtsModel,              // "gpt-4o-mini-tts"
    string TtsVoice);             // e.g. "alloy"

public sealed record SessionConfig(
    InterpretationMode CurrentMode,
    LanguageDirection Direction,
    ProviderProfile ProviderProfile);

public sealed record InterpretationSession(
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionConfig Config,
    List<InterpretationTurn> Turns,
    List<ModeTransitionEvent> ModeTransitions,
    SessionSummary? Summary,
    string PricingConfigVersion);

public sealed record ModeTransitionEvent(
    string TransitionId,
    InterpretationMode FromMode,
    InterpretationMode ToMode,
    LanguageDirection DirectionAtTransition,
    DateTimeOffset OccurredAt,
    ClockSource ClockSource,
    string? TriggeredByTurnId);

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
    TurnStatus Status,
    string? TranslationModelUsed,   // records which model ran, for per-turn comparison
    string? TtsVoiceUsed,
    bool IsEvaluation = false);     // (F.4) true for a standalone WER-evaluation turn — set at POST /wer; excluded from per-mode ModeSummary, kept in WerSummary

public sealed record TranscriptSegment(
    string SegmentId,
    string Role,            // "source" | "target"
    string Text,
    bool IsFinal,
    string Provider,
    DateTimeOffset Timestamp,
    ClockSource ClockSource);

public sealed record LatencyEvent(
    string Name,            // e.g. "stt.final"
    LatencyStage Stage,
    DateTimeOffset Timestamp,
    long RelativeMs,        // ms from the turn's reference origin (see ARCH-013 clock rules)
    ClockSource ClockSource,
    Dictionary<string, string> Metadata);

public sealed record CostEstimate(
    string Provider,
    string Model,
    string PricingBasis,        // "usd_per_audio_minute" | "audio_output_tokens" | "characters" | "tokens"
    decimal EstimatedUsd,
    decimal? EstimatedUsdPerMinute,
    Dictionary<string, decimal> Units,
    string PricingConfigVersion,
    string[] Assumptions);

public sealed record SessionSummary(
    int TurnCount,
    ModeSummary? Realtime,
    ModeSummary? Cascade,
    WerSummary? Wer,
    DateTimeOffset ComputedAt,
    string PricingConfigVersion);

public sealed record ModeSummary(
    int TurnCount,
    double? AvgSpeechEndToFirstAudioMs,
    double? AvgSpeechEndToPlaybackMs,
    decimal? EstimatedCostPerMinuteUsd,
    int ErrorCount,
    // cascade-only (null for realtime):
    double? AvgSttFinalMs,
    double? AvgTranslationFinalMs,
    double? AvgTtsFirstAudioMs);

public sealed record WerSummary(int SampleCount, double AvgWer);

public sealed record EvaluationPhrase(
    string PhraseId, LanguageCode Language, string ReferenceText, string Category);

public sealed record WerResult(
    string PhraseId, string Reference, string Hypothesis,
    string NormalizedReference, string NormalizedHypothesis,
    int Substitutions, int Insertions, int Deletions,
    int ReferenceWordCount, double Wer);

public sealed record ProviderError(
    string Provider, string Stage, string Code, string SafeMessage,
    bool Retryable, int? HttpStatusCode = null);
```

### Canonical Lifecycle

**Session state machine** (whole evaluation session):

```text
Idle → Configured → Starting → Active → ReadyForTurn ⇄ (turn cycle) → Ending → Ended
```

**Turn state machine** (one recorded input→output cycle, nested inside `ReadyForTurn`):

```text
Ready → Recording → Captured → Processing → Playing → Completed
                                  └──────────────────→ Failed
```

**Failure overlays** (orthogonal flags that can attach to a turn/session without replacing the base state): `MicPermissionDenied`, `ProviderUnavailable`, `ProviderTimeout`, `ProviderRateLimited`, `RealtimeDisconnected`, `EmptyTranscript`, `PlaybackFailed`, `PersistenceFailed`. The frontend `UiSessionState.status` (ARCH-007) uses the same canonical names, separating session-level from turn-level status.

### Business Rules and Invariants

1. Every completed turn includes `Mode`, `Direction`, `StartedAt`, and ≥1 top-level latency metric.
2. Cascade completed turns include `stt.final`, `translation.final`, and `tts.first_audio`, **or** an explicit `ProviderError` explaining why not.
3. Realtime turns must not fabricate STT/translation/TTS stage boundaries.
4. Cost estimates always include provider, model, and pricing basis.
5. WER is computed only against a known `EvaluationPhrase`.
6. Session JSON never includes standard provider API keys.
7. **(6b)** Session JSON never includes the ephemeral Realtime client secret.
8. Session JSON never includes raw audio (MVP).
9. Mode switching is forbidden during `Recording`, `Processing`, or `Playing`; a `ModeTransitionEvent` is appended only between turns whenever `SessionConfig.CurrentMode` changes.
10. Errors are persisted as normalized `ProviderError` records with a code from the enumerated set (ARCH-018).

---

<a id="arch-007"></a>

## §4 — Frontend Architecture

### Responsibilities

The TypeScript SPA owns: session setup UI; optional label; direction selection; mode + model selectors; mic permission; turn recording lifecycle; the **streaming cascade WebSocket client**; the **Realtime WebRTC client**; the audio capture controller (streaming PCM + blob fallback); the playback queue; transcript/metrics/cost rendering; the WER Evaluation panel; the comparison summary; and user-facing error messages.

The frontend must **not** own: standard provider API keys; authoritative persisted session state; provider secrets; the cost pricing config source of truth; or the canonical WER calculation.

### Framework

React + Vite + TypeScript. Keep state minimal (a single store/hook is sufficient; no heavy state library required).

### Frontend State Shape

```ts
export type UiSessionState = {
  sessionId: string | null;
  label?: string;
  mode: 'realtime' | 'cascade';
  direction: { source: 'en' | 'es'; target: 'en' | 'es' };
  realtimeModel: 'gpt-realtime' | 'gpt-realtime-mini';
  translationModel: 'gpt-5.4-nano' | 'gpt-5.4-mini';
  // canonical names mirror ARCH-005 SessionStatus/TurnStatus:
  sessionStatus: 'idle' | 'configured' | 'starting' | 'active' | 'readyForTurn' | 'ending' | 'ended';
  turnStatus: 'ready' | 'recording' | 'captured' | 'processing' | 'playing' | 'completed' | 'failed';
  providerHealth?: ConfigResponse;   // from GET /api/config
  turns: TurnViewModel[];
  currentTurn?: TurnViewModel;
  summary?: SessionSummary;
  errors: UiError[];
};

export type TurnViewModel = {
  turnId: string;
  mode: 'realtime' | 'cascade';
  direction: { source: 'en' | 'es'; target: 'en' | 'es' };
  status: UiSessionState['turnStatus'];
  startedAt: string;
  completedAt?: string;
  audioDurationMs?: number;
  sourceTranscript: { text: string; isFinal: boolean }[];
  targetTranscript: { text: string; isFinal: boolean }[];
  latency: { speechEndToFirstAudioMs?: number; speechEndToPlaybackMs?: number; totalTurnMs?: number;
             stages?: Record<string, number> };  // e.g. { sttFinalMs, translationFinalMs, ttsFirstAudioMs }
  latencyEvents?: LatencyEvent[];  // (D.6) raw client+server timeline (absolute timestamps); deriveTurnMetrics computes the top-level deltas from these (the cascade WS gives the frontend the client-side markers the backend can't persist)
  estimatedCostUsd?: number;
  estimatedCostPerMinuteUsd?: number;
  cost?: CostEstimate;             // (D.6) full estimate retained for the CostPanel's model + assumptions tooltip
  translationModelUsed?: string;
  werWer?: number;
  errors: UiError[];
};

// Frontend projection of ProviderError AFTER the backend sanitizer — never carries raw provider text/stacks.
export type UiError = {
  code: string; safeMessage: string; stage?: string; retryable: boolean; turnId?: string;
};
```

### Recording Controls (enforced transitions)

| Current | Allowed action |
|---|---|
| Idle | Configure/start session |
| Active / ReadyForTurn | Start recording |
| Recording | Stop recording |
| Processing | No mode switch; no new recording |
| Playing | No mode switch unless playback-cancel exists |
| Ended | Start new session |

### UI Components

- **SessionSetup** — label, direction selector, **realtime-model + translation-model selectors**, start/end buttons. On load, calls `GET /api/config` and disables modes whose provider keys are absent.
- **ModeToggle** — Realtime/Cascade; disabled while recording/processing/playing; disabled for an unconfigured mode.
- **RecordingControls** — Start/Stop; capture timer + status.
- **TranscriptPanel** — subscribes to the live stream (cascade WebSocket events / Realtime data-channel events) and renders source + target **partials as they arrive, replaced by finals**. Shows a clear "source transcript unavailable" note if Realtime input transcription is off (ARCH-010).
- **MetricsPanel** — current-turn top-level latency; cascade stage breakdown; realtime event breakdown; session averages by mode; "n/a" for unavailable nice-to-have metrics (never an error).
- **CostPanel** — "Estimated cost/min" (always qualified as estimated), per-turn and per-session, with a pricing-assumptions tooltip; shows the model used so model variants are comparable.
- **EvaluationPanel** (committed MUST) — phrase selector; reference display; record-and-transcribe a phrase via `POST /api/evaluation/transcribe`; WER result; the "WER is STT-only" explanation.
- **ComparisonSummary** — avg latency by mode; estimated cost/min by mode and by model variant; error counts; WER summary; turn counts.
- **ErrorBanner** — renders `UiError` (sanitized) with actionable copy.

### Frontend Error Philosophy

Actionable, never raw: "Microphone permission denied. Enable mic access and retry." / "Realtime connection failed. Check the server logs or switch to Cascade." / "Cascade STT failed — check Deepgram config." / "No speech detected. Try again closer to the mic." / "Session may not have been saved." No stack traces, no secrets.

---

<a id="arch-030"></a>

## §4 — Audio Capture & Format (sub-anchor)

> The single home for the capture API, format choice, and the transcoding decision. **Realtime and Cascade use different capture paths** — do not conflate them.

### Cascade capture — streaming (MUST)

1. `getUserMedia({ audio: true })` → `MediaStream` (mono).
2. `AudioContext` + an **`AudioWorkletNode`** that receives Float32 frames, converts to **linear16 PCM** (Int16), and posts ~20–50ms frames to the main thread.
3. Frames are sent as **binary** over the cascade WebSocket (`/api/cascade/stream`).
4. The `start` control message declares `encoding: "linear16"` and `sampleRate` = **the actual `AudioContext.sampleRate`** (commonly 48000). **Do not resample** — pass the true rate to Deepgram (`encoding=linear16`, `sample_rate=<rate>`, `channels=1`). No transcoding needed.

### Cascade capture — fallback (pre-recorded)

If the streaming path is blocked: `MediaRecorder` with a probed mime type → one blob → `POST /api/cascade/turn` (multipart). Probe order: `audio/webm;codecs=opus` → `audio/webm` → `audio/mp4` → `audio/ogg;codecs=opus`; read `recorder.mimeType` and send it as `audioContentType`. Backend forwards `Content-Type` to **Deepgram pre-recorded**, which **auto-detects** the container (webm/opus, mp4/aac, ogg/opus, wav) — **no FFmpeg / no transcoding**.

> **Browser note:** Safari < 18.4 produces `audio/mp4`/AAC (not webm); never hardcode the container — always probe `MediaRecorder.isTypeSupported()`. Demo on Chrome/Edge for the most predictable behavior.

### Realtime capture

WebRTC owns the mic track and negotiates the Opus codec itself — the SPA does **not** pick a recording format for Realtime. It only `getUserMedia` + `pc.addTrack`.

### Playback

- **Streamed cascade audio:** append streamed chunks to a `MediaSource` buffer for progressive playback (mp3 is broadly MSE-supported); **fallback** to assembling chunks into one blob and playing via `HTMLAudioElement` if MSE append fails.
- **Realtime audio:** play the remote track from `pc.ontrack`.
- Stamp `playback.started` on the audio element's `playing` event (audible start) for both modes — the only browser-side TTS-output timing — for cross-mode comparability. Avoid overlapping playback.

> **Secure context:** `getUserMedia` (both paths) and WebRTC require HTTPS **or** `localhost`. On non-localhost HTTP, `navigator.mediaDevices` is `undefined`. Run the demo from `http://localhost`, not a LAN IP. (See ARCH-029.)

---

<a id="arch-008"></a>

## §5 — Backend Architecture

### Responsibilities

Config/secret loading; standard provider API keys; ephemeral Realtime credential minting; session create/get/end/summary + turn create/complete; the streaming cascade orchestrator (+ blob fallback); provider abstractions and real/fake implementations; metrics normalization; cost estimation; WER; JSON persistence; error normalization + sanitization; the config/health endpoint.

### Shape & Layering

ASP.NET Core Web API. WebSocket endpoint for the cascade stream; minimal controllers for the rest.

```text
Controllers / endpoints → Application services → Provider abstractions → Provider implementations → Persistence/evaluation/cost utilities
```

Provider logic never lives in controllers.

### Backend Services

| Service | Responsibility |
|---|---|
| `SessionStore` | In-memory active session state |
| `SessionPersistenceWriter` | Writes session JSON (per turn + on end) |
| `SessionSummaryService` | Computes `SessionSummary` from current turns (on demand) |
| `RealtimeClientSecretService` | Mints OpenAI Realtime ephemeral credentials |
| `CascadeStreamingOrchestrator` | Drives the live STT→translation→TTS stream over the WebSocket |
| `CascadeOrchestrator` | Blob-fallback path (pre-recorded STT → streamed translation → streamed TTS) |
| `MetricsAggregator` | Latency/stage summaries |
| `CostEstimator` | Model-aware estimated costs (branches on pricing basis) |
| `WerCalculator` / `EvaluationPhraseStore` / `EvaluationService` | WER |
| `ErrorSanitizer` | Internal/provider errors → safe `UiError`/`ProviderError` |
| `ConfigService` | Reports which capabilities are configured (no secret values) |

### State Model

In-memory active session + JSON persistence; **no database**. Write strategy:

- Create session in memory on `POST /api/sessions` (write JSON best-effort).
- Append/update each turn as it completes; **write JSON after each completed/failed turn (best-effort) and on session end (MUST)**.
- If a write fails, keep the session running and emit a `persistence.failed` error.
- **Trim candidates** (ARCH-025): atomic temp-file→rename durability; write-after-every-WER granularity.

`SessionSummary` is **computed on demand** (callable mid-session via `GET …/summary`); a cached snapshot is written on `/end` (and may be re-snapshotted per turn). `ComputedAt` disambiguates staleness.

> **(B.7b realization)** `SessionSummaryService.Compute` is **pure** — it returns a `SessionSummary` and never writes `session.Summary`; the `/end` cached-snapshot write is the persistence step's job (B.9). It reuses the per-turn `MetricsAggregator` (no re-implemented metric math) and averages each metric over its **non-null** turns (absent-on-all → `null`, never `0`); a `ModeSummary` is `null` iff its mode has no turns; the cascade-stage averages are `null` for realtime turns; WER averaging is **unbounded** (no clamp `>1.0`). `EstimatedCostPerMinuteUsd` is the **average of the mode's non-null per-turn `EstimatedUsdPerMinute`** (a labeled estimate-of-estimates, not a blended `sum/sum`). The summary is **mode-level only** — the model-variant comparison (both translation models / both realtime models) is the `ComparisonSummary` (F.3) consumer's client-side grouping from the raw turn list (`TranslationModelUsed` + `ProviderProfile`), read via `GET /api/sessions/{id}`, not a `/summary` field.

> **(F.4 realization — eval-turn exclusion from `ModeSummary`)** A standalone WER-evaluation turn is created with the session's current mode (so it would otherwise inflate that mode's count), but it is not an interpretation turn. `SummarizeMode` therefore **excludes `IsEvaluation` turns** from the per-mode `ModeSummary` — `TurnCount`, the per-stage/cost averages, AND `ErrorCount` (one `&& !t.IsEvaluation` filter) — so the Realtime-vs-Cascade comparison counts only real interpretation turns. **`SummarizeWer` is unchanged** (still all turns with a `WerResult`) — eval turns are *where WER comes from*. **Semantic to note:** top-level `SessionSummary.TurnCount` still counts **all** turns incl. eval (an honest "total turns in the session"), so **top-level total ≠ the sum of the per-mode `ModeSummary.TurnCount`s** (the delta is the eval-turn count); the comparison reads the per-mode counts, which are interpretation-only. The marker is set server-side at `POST /wer` (atomic with the `WerResult` attach), so the frontend stays unchanged. The rare orphan (a `computeWer` failure after the turn is created → unmarked, still counted) is a bounded documented residual.

---

<a id="arch-006"></a>

## §5 — Repository Scaffold

```text
ai-interpreter-workbench/
  README.md  CLAUDE.md  AGENTS.md  .gitignore  .env.example
  ARCHITECTURE.md            # this file (canonical)
  MVP_TASKS.md
  docs/
    COMPARISON_WRITEUP.md
    DEMO_SCRIPT.md
  config/
    pricing.json
  data/sessions/             # gitignored except .gitkeep
    .gitkeep
  server/
    AiInterpreter.Api/
      Program.cs  appsettings.json  appsettings.Development.json
      Controllers/ SessionsController.cs  RealtimeController.cs  CascadeController.cs
                   EvaluationController.cs  ConfigController.cs
      Realtime/    RealtimeClientSecretService.cs  RealtimeOptions.cs
      Cascade/     CascadeStreamingOrchestrator.cs  CascadeOrchestrator.cs  CascadeModels.cs
                   CascadeWebSocketEndpoint.cs
      Providers/
        Abstractions/ ISttProvider.cs  ITranslationProvider.cs  ITtsProvider.cs
                      ProviderEvents.cs  ProviderErrors.cs  AudioFrame.cs
        Deepgram/     DeepgramSttProvider.cs  DeepgramOptions.cs
        OpenAI/       OpenAiTranslationProvider.cs  OpenAiTtsProvider.cs  OpenAiOptions.cs
        Fakes/        FakeSttProvider.cs  FakeTranslationProvider.cs  FakeTtsProvider.cs
      Sessions/    SessionStore.cs  SessionPersistenceWriter.cs  SessionSummaryService.cs  SessionModels.cs
      Metrics/     LatencyEventFactory.cs  MetricsAggregator.cs
      Cost/        CostEstimator.cs  PricingOptions.cs
      Evaluation/  WerCalculator.cs  EvaluationPhraseStore.cs  EvaluationService.cs  evaluation-phrases.json
      Config/      ConfigService.cs
      Security/    ErrorSanitizer.cs
      Common/      Result.cs  Clock.cs
    AiInterpreter.Tests/
      CascadeOrchestratorTests.cs   ProviderBoundaryTests.cs   WerCalculatorTests.cs
      CostEstimatorTests.cs         SessionPersistenceTests.cs MetricsAggregatorTests.cs
      ErrorSanitizerTests.cs        ConfigEndpointTests.cs
  web/
    package.json  vite.config.ts
    src/
      main.tsx  App.tsx
      api/      sessionsApi.ts  cascadeApi.ts  realtimeApi.ts  evaluationApi.ts  configApi.ts
      audio/    audioCaptureController.ts  pcmWorklet.ts  playbackController.ts
      realtime/ realtimeWebRtcClient.ts  realtimeEvents.ts
      cascade/  cascadeStreamClient.ts
      state/    sessionStore.ts
      components/ SessionSetup.tsx ModeToggle.tsx RecordingControls.tsx TranscriptPanel.tsx
                  MetricsPanel.tsx CostPanel.tsx EvaluationPanel.tsx ComparisonSummary.tsx ErrorBanner.tsx
      types/    domain.ts  metrics.ts
```

`.gitignore` must exclude: `.env`, `.env.local`, `server/**/bin/`, `server/**/obj/`, `web/node_modules/`, `data/sessions/*.json` (keep `.gitkeep`).

> **Scaffold note (A.1):** the tree above lists the architecturally-significant files; **standard template / config files are implied and expected** alongside them — server: `Properties/launchSettings.json`, `appsettings*.json`, `GlobalUsings.cs`, plus `global.json` pinning the SDK band; web: `tsconfig*.json`, `index.html`, `eslint.config.js`, `.prettierrc.json`, `package-lock.json`. These ride the scaffold slice, not separate tasks.

---

<a id="arch-009"></a>

## §6 — API Contracts

> **Convention:** all JSON field names are **camelCase serializations of the ARCH-005 records**. Examples below must stay in sync with those records.
>
> **Timestamp format (A.3):** `DateTimeOffset` fields serialize as ISO-8601 via System.Text.Json's default — UTC instants render with an explicit `+00:00` offset (e.g. `2026-05-28T15:30:00+00:00`), equivalent to the `Z` shorthand used illustratively in the examples here / ARCH-013 / ARCH-016. No custom converter; the contract is the *instant* (parse + round-trip), not the literal `Z`.

### Session APIs

**`POST /api/sessions`** — create a session.
Request:
```json
{ "label": "Demo run 1", "mode": "realtime",
  "direction": { "source": "en", "target": "es" },
  "realtimeModel": "gpt-realtime", "translationModel": "gpt-5.4-nano" }
```
Response:
```json
{ "sessionId": "session_abc123", "startedAt": "2026-05-28T15:30:00Z",
  "config": { "currentMode": "realtime",
              "direction": { "source": "en", "target": "es" },
              "providerProfile": { "realtimeProvider": "openai", "realtimeModel": "gpt-realtime",
                "sttProvider": "deepgram", "sttModel": "nova-3", "sttLanguage": "multi",
                "translationProvider": "openai", "translationModel": "gpt-5.4-nano",
                "ttsProvider": "openai", "ttsModel": "gpt-4o-mini-tts", "ttsVoice": "alloy" } } }
```

**`GET /api/sessions/{sessionId}`** — current/persisted session state.
**`POST /api/sessions/{sessionId}/end`** — finalize summary + persist final JSON.
**`GET /api/sessions/{sessionId}/summary`** — recompute `SessionSummary` on demand (callable mid-session). Returns `SessionSummary` (camelCase; `realtime`/`cascade` are `ModeSummary`).

> **(B.9c-i realization)** `POST /api/sessions` and `GET /{id}` return the **full `InterpretationSession`** (the create example above is an abbreviation — those fields are a subset). `POST /{id}/end` returns `EndSessionResponse { session, persistedPath (filename-only — no absolute path/data-dir disclosure), persistenceWarning? }` at **200**: on a write failure the in-memory end still happened and `persistenceWarning` carries a `persistence.failed` `UiError` (ARCH-018 "continue session / save warning" — never a 500). An unknown id on any of these routed paths → **404 + a sanitized `session.not_found` `UiError`** (built via `ErrorSanitizer.ForCode` — an expected condition, not logged at Error); this is distinct from the framework 404/405 for *unrouted* paths (ProblemDetails, per the B.9a decision). The controller is thin → `SessionService` (ARCH-008); `Result`/`Result<T>` are mapped to DTOs at the boundary and **never serialized**.

### Turn lifecycle (all modes)

The **backend owns `turnId`** for every mode. A Realtime "turn" is an app-layer construct with no OpenAI counterpart.

**`POST /api/sessions/{sessionId}/turns`** — create an empty turn; returns `{ "turnId": "turn_001" }`.
**`POST /api/sessions/{sessionId}/turns/{turnId}/complete`** — finalize the turn + trigger persistence.
**`POST /api/sessions/{sessionId}/turns/{turnId}/events`** — Realtime client reports normalized events:
```json
{ "events": [ { "name": "realtime.first_audio_delta", "stage": "realtime",
  "timestamp": "2026-05-28T15:30:05.123Z", "relativeMs": 842, "clockSource": "browser", "metadata": {} } ] }
```

> **(B.9c-ii realization)** Backend owns `turnId` — `POST …/turns` (no request body) creates an empty turn inheriting the session's current `Mode`/`Direction`, returning `CreateTurnResponse { turnId: "turn_<short-id>" }` (the `turn_001` above is illustrative). `/complete` is the **realtime** finalize path (cascade turns are created via `POST …/turns` but persisted by C.4's WS — never `/complete`): `CompleteTurnRequest { audioDurationMs?, outputAudioDurationMs?, transcripts?[], status? }` (all optional; merged into the turn that already holds `/events` latency) — **cost is backend-owned (`CostEstimator`, ARCH-014; realtime cost is wired here at `/complete` in E.2b — input audio-seconds from `audioDurationMs`, output from `outputAudioDurationMs?` (E.4-reported; absent → output disclosed-unavailable in Assumptions, never a synthetic $0; no reported duration → `CostEstimate` null, not $0.00)), WER is F.1's (invariant #5, computed against a known phrase), and translation-model/tts-voice are cascade fields** — none are client-supplied here. `status` is coerced to terminal (`Failed` if reported, else `Completed`). Returns `CompleteTurnResponse { turn, persistenceWarning? }` at 200 (per-turn write is best-effort, ARCH-016 — a failure warns, never 500). Unknown session → `session.not_found`; known session + unknown turn → `turn.not_found` (sanitized 404 `UiError`). Collection caps: `events`/`transcripts` ≤ 500 (DataAnnotations → framework 400). Per-item string caps are deferred (bounded today by Kestrel's 30MB body limit + the count caps).

### Config / health

**`GET /api/config`** — capability flags from **key presence only** (never values):
```json
{ "realtime": { "configured": true, "models": ["gpt-realtime","gpt-realtime-mini"] },
  "cascade": { "stt": { "configured": true, "provider": "deepgram", "model": "nova-3" },
               "translation": { "configured": true, "provider": "openai", "models": ["gpt-5.4-nano","gpt-5.4-mini"] },
               "tts": { "configured": true, "provider": "openai", "model": "gpt-4o-mini-tts" } },
  "languages": ["en","es"], "pricingConfigVersion": "2026-05-28-payg-estimates" }
```
**`GET /api/health`** → `{ "status": "ok" }`.

### Realtime API

**`POST /api/realtime/client-secret`** — mint an ephemeral credential.
Request: `{ "sessionId": "...", "direction": { "source": "en", "target": "es" }, "model": "gpt-realtime" }`
Response: `{ "clientSecret": "ek_...", "expiresAt": "2026-05-28T15:40:00Z", "model": "gpt-realtime" }`
(Upstream mapping in ARCH-010. Standard key is used **server-side only**.)

### Cascade API — streaming (MUST)

**`WS /api/cascade/stream`** — bidirectional WebSocket for one streaming turn.

Client → server:
1. First text frame: `{ "type": "start", "sessionId": "...", "turnId": "...", "direction": {"source":"en","target":"es"}, "encoding": "linear16", "sampleRate": 48000, "translationModel": "gpt-5.4-nano", "ttsVoice": "alloy" }`
2. Then **binary** frames: raw linear16 PCM audio.
3. Final text frame: `{ "type": "stop" }` (end of speech for the turn).

Server → client (text frames unless noted):
- `{ "type": "transcript", "segment": { ...TranscriptSegment } }` — source/target partials + finals.
- `{ "type": "latency", "event": { ...LatencyEvent } }` — per-stage timings as they occur.
- `{ "type": "audio", "contentType": "audio/mpeg", "seq": 0, "base64": "..." }` — streamed TTS chunks. (Binary audio frames are an allowed optimization.)
- `{ "type": "cost", "estimate": { ...CostEstimate } }`
- `{ "type": "error", "error": { ...ProviderError } }`
- `{ "type": "done", "turnId": "...", "status": "completed" }`

### Cascade API — fallback (pre-recorded)

**`POST /api/cascade/turn`** — `multipart/form-data`: `sessionId`, `turnId?`, `direction`, `audio` (blob), `audioContentType`, `recordingStartedAt`, `recordingStoppedAt`. Returns the full result as one JSON body (transcripts, audioBase64, latencyEvents, costEstimate, errors). **Explicitly disclosed as the non-streaming fallback** — a buffered single response is NOT full streaming.

### Evaluation APIs

**`GET /api/evaluation/phrases`** → list of `EvaluationPhrase`.
**`POST /api/evaluation/transcribe`** — `multipart/form-data`: `sessionId`, `phraseId`, `language`, `audio`, timestamps. Runs **STT only** (no translation/TTS). Returns `{ "hypothesis": "...", "sttProvider": "deepgram", "sttModel": "nova-3", "latencyEvents": [] }`.
**`POST /api/evaluation/wer`** — `{ "sessionId","turnId?","phraseId","hypothesis" }` → full `WerResult` (incl. `normalizedReference`/`normalizedHypothesis`).

> **(F.1a realization — `GET /phrases` + `POST /wer`)** A thin `EvaluationController` (ARCH-008) over a new `EvaluationService` reusing the B.6 `WerCalculator` + `EvaluationPhraseStore`. `GET /phrases` returns the loaded phrases, or an **empty list** when the store failed to load — `LoadError`/path fragments are **never** surfaced (ARCH-019). `POST /wer` returns `WerResponse { result: WerResult, persistenceWarning?: UiError }`: the reference is sourced from the store by `phraseId` (unknown → `404 evaluation.phrase_not_found`), and when `turnId` is supplied the computed `WerResult` is **attached to the turn (server-authoritative, feeds the F.3 summary) via the existing `SessionStore.UpdateTurn` seam + a best-effort `SessionPersistenceWriter` write** (ARCH-016; no new store/write path — `UpdateTurn` is unconditional where `FinalizeTurn` would refuse the already-terminal turn). A persist failure degrades to a `persistenceWarning`, never a 500; unknown session/turn → `404 turn.not_found`; absent `turnId` → compute-and-return only. **SECURITY (ARCH-019):** the hypothesis length is capped (~2000 chars → `400 evaluation.invalid_phrase`) as the service's **first action, before `WerCalculator.Compute` allocates its n×m DP matrix** — the cap lives in the service (the single WER chokepoint, unit-pinned to run before the compute delegate) and returns a **domain 400 code, not a `[MaxLength]`** annotation (which would emit a generic `ProblemDetails` and bypass the chokepoint).

> **(F.1b realization — `POST /transcribe`, STT-only)** A multipart phrase recording (`sessionId`, `phraseId`, `language`, `audio`) runs **STT only** (no translation/TTS) and returns `TranscribeResponse { hypothesis, sttProvider, sttModel, latencyEvents }`. The blob is wrapped as a **single `AudioFrame` through the SAME `ISttProvider`** the cascade uses (the derived container content-type routes Deepgram's pre-recorded REST path, C.1); `SttFinal` text is **joined across multiple finals** so a segmented phrase isn't silently truncated; `stt.first_partial`/`stt.final` are stamped on real arrival only (never synthesized). **SECURITY (invariant #5):** the upload is size- + content-type-validated (`413`/`415`) **before any provider call**, reusing the C.5 `CascadeUploadValidation` surface (one validator across both audio routes; the `EVAL_MAX_UPLOAD_BYTES` cap falls back to `CASCADE_MAX_UPLOAD_BYTES`, and the framework body-limit backstop is the **max** of all routes' caps so a higher per-route cap isn't preempted by a 500). **Invariant #3:** transcribe is **stateless** — it touches no `SessionStore`/`SessionPersistenceWriter`, creates no turn, and never persists the uploaded audio. An id over-cap → `400 evaluation.invalid_request`; an STT failure surfaces the preserved sanitized `ProviderError` code.

---

<a id="arch-010"></a>

## §7 — Realtime Mode Architecture

### Backend client-secret minting (upstream OpenAI call)

`RealtimeClientSecretService` calls **`POST https://api.openai.com/v1/realtime/client_secrets`** (GA) with the standard `OPENAI_API_KEY` (`Authorization: Bearer sk-...`). **Never** use the legacy `/v1/realtime/sessions`. Body:

```json
{ "expires_after": { "anchor": "created_at", "seconds": 600 },
  "session": { "type": "realtime", "model": "<OPENAI_REALTIME_MODEL>",
    "instructions": "<interpreter prompt>", "output_modalities": ["audio"],
    "audio": { "input": { "turn_detection": null,
                          "transcription": { "model": "gpt-4o-transcribe" } },
               "output": { "voice": "<voice>" } } } }
```

Response (GA `client_secrets`): **top-level** `value` (the ephemeral `ek_...` token) + `expires_at` (Unix epoch), alongside a `session` object sibling — i.e. `{ "value": "ek_…", "expires_at": <epoch>, "session": { … } }`. Map `value → clientSecret`, `expires_at → expiresAt`. **The legacy `/v1/realtime/sessions` (Beta) endpoint nested these under `client_secret.{value,expires_at}`; that is NOT the GA shape.** E.1's `RealtimeClientSecretMapping.ParseResponse` parses **both** (top-level primary, nested fallback) as forward-insurance — verified against the live GA reference (Context7, 2026-05-29). TTL default 600s (10–7200s). Set an `OpenAI-Safety-Identifier` server-side. **Enabling `audio.input.transcription` is required** for the source transcript (ARCH-005 role=source); without it, only the target transcript exists and the UI must show "source unavailable" — otherwise PRD must-have 6 silently fails.

### Browser WebRTC handshake (GA)

1. Browser `GET`s a fresh ephemeral secret via `POST /api/realtime/client-secret`.
2. Create `RTCPeerConnection` + a data channel named **`oai-events`**.
3. `pc.addTrack(micTrack)` from `getUserMedia`.
4. `createOffer` / `setLocalDescription`.
5. **`POST offer.sdp` to `https://api.openai.com/v1/realtime/calls`** with `{ Authorization: Bearer <ephemeral value>, Content-Type: application/sdp }`.
6. `setRemoteDescription(answer)`. **Model is fixed by the minted session — do NOT append `?model=` (returns HTTP 400).**
7. Remote audio arrives via `pc.ontrack`; server events arrive on the `oai-events` data channel.

### Manual turn control (VAD off)

After the data channel opens, send a `session.update` with `session.audio.input.turn_detection = null` (authoritative; mint-time setting is a backup). On **Start Recording**: `input_audio_buffer.clear` then stream the mic track. On **Stop Recording**: `input_audio_buffer.commit` then `response.create` over the data channel. Record `turn.recording.stopped` at the `response.create` moment (speech-end proxy).

> **E.4b frontend realization (2026-05-30).** `realtimeTurnController` (the web §11/§17 cascade-recording analogue) sends these as `oai-events` DC frames: `session.update` = `{ "type":"session.update", "session":{ "audio":{ "input":{ "turn_detection":null }}}}` (Start, before clear) → `input_audio_buffer.clear` (Start) → `input_audio_buffer.commit` + `response.create` (Stop). The mic track streams **continuously** (E.3 `addTrack`) — turns are **buffer-delimited**, no per-turn mic toggle. `turn.recording.started`/`.stopped` are stamped browser-clock by the controller; `playback.started` is stamped once on the remote-`<audio>` `playing` event (the realtime first-audio fallback if the DC emits no `output_audio.delta`). `RecordingControls` dispatches to this controller when `currentMode==='realtime'` (the real entry point, `App.tsx`). Lazy-connect is the E.4b interim; E.5 hoists to a persistent pc + teardown. **Confirm the exact frame envelopes at the first real-key smoke** (the §18/§20 verify-the-real-shape discipline).

### Interpreter prompt (session instructions)

```text
You are a live interpreter. Translate the user's speech from {sourceLanguage} to {targetLanguage}.
Preserve meaning, tone, and register. Output ONLY the translated speech in {targetLanguage}.
Do not answer as an assistant. Do not add explanations or framing.
```

### GA event mapping

| Normalized | GA event |
|---|---|
| `realtime.session.connected` | `RTCPeerConnection` connectionstate `connected` |
| `realtime.first_audio_delta` | first `response.output_audio.delta` (accept legacy alias `response.audio.delta`) |
| `realtime.first_transcript_delta` (target) | first `response.output_audio_transcript.delta` |
| source transcript | `conversation.item.input_audio_transcription.delta` / `.completed` (requires input transcription enabled) |
| response lifecycle | `response.created` / `response.done` |
| `realtime.session.disconnected` | ICE connectionstate `failed`/`disconnected` |

> **E.3 frontend realization (2026-05-30).** The browser-side normalizer (`web/src/realtime/realtimeEvents.ts` — `parseRealtimeEvent` + `normalizeRealtimeEvent`) classifies these GA events into a **pure, stateless** `NormalizedRealtimeEvent` union (audioDelta / targetTranscriptDelta / sourceTranscriptDelta / sourceTranscriptCompleted / responseCreated / responseDone / error); E.4 maps the union to store actions + browser-clock latency stamps (the first-of-type stamping is stateful → E.4's layer, mirroring the cascade pure-router/store-action split, web §9/§10). Field reads are pinned by E.3 tests but **smoke-pending** against a live key: output-audio + transcript **deltas** read `delta`; `conversation.item.input_audio_transcription.completed` reads `transcript` (the full text, not a delta); an `error`/`response.error` event reads the nested `error.code` (the raw `error.message` is **never** echoed — classification only; E.5 builds the safe `ProviderError`/`UiError`). The legacy `response.audio.delta` alias is accepted for `response.output_audio.delta`; `response.done` is the target-transcript-final signal. **Confirm the exact `type` strings + the error envelope at the first real-key smoke** and re-pin this table if the live API differs (the §18/§20 "verify the real API shape" discipline). The `ek_` authorizes the SDP exchange transiently (`realtimeWebRtcClient.exchangeSdpOffer`, Bearer) and is never persisted (invariant #2).

### Metrics (computed off the first event of each type)

```text
speech_end_to_first_audio_ms = realtime.first_audio_delta − turn.recording.stopped
speech_end_to_playback_ms    = playback.started        − turn.recording.stopped
total_turn_ms                = turn.completed          − turn.recording.started
```

> **E.4a frontend realization (2026-05-30) — the metrics split + a first-audio smoke-confirm.** The browser stamps these markers across slices and reports them to the backend (`POST …/turns/{turnId}/events`), which aggregates the **canonical** realtime top-level metrics (unlike cascade, which the frontend computes — ARCH-013, web §13/§16). **Stamp ownership:** **E.4a** stamps the event-derived `realtime.first_audio_delta` + `realtime.first_transcript_delta` (off the GA deltas, browser clock, first-of-type) and lets the store own `turn.completed`; **E.4b** stamps `turn.recording.started`/`.stopped` (turn control); **E.5** stamps `realtime.session.connecting`/`.connected`/`.disconnected` (connection). **Smoke-confirm:** `speech_end_to_first_audio_ms` assumes `response.output_audio.delta` arrives on the `oai-events` data channel — but in WebRTC mode the audio is the media track (`pc.ontrack`), so the DC may not emit `output_audio.delta` at all (it may be WebSocket-transport-only). If absent, `realtime.first_audio_delta` stays honest `n/a` and `speech_end_to_playback_ms` (`playback.started` = the `<audio>` playing event) is the reliable browser-side first-audio timing. Confirm at the first real-key smoke.

### Connection lifecycle

**One `RTCPeerConnection` is created at session start and held open across turns**; it is torn down only on End Session or mode-switch-away (so `realtime_connect_ms` is a one-time cost). OpenAI caps a single Realtime session at **60 minutes** (not 15 — that was beta), so the 5-minute stability benchmark fits with no mid-demo re-mint. Teardown discipline: stop tracks, close the data channel, close the pc, release the `MediaStream`.

### Failure handling

- **Mid-session disconnect:** on ICE `failed`/`disconnected`, persist `realtime.session.disconnected` on the turn and surface it (never swallow). Default behavior = detect + persist + advise switch-to-Cascade. Optional nice-to-have: auto-reconnect (re-mint secret + rebuild pc, ≤2 attempts).
- **Ephemeral expiry:** frontend tracks `expiresAt`; re-mint before expiry / on disconnect via a fresh `POST /api/realtime/client-secret` (standard key never leaves backend).

> **E.5a frontend realization (2026-05-30).** `realtimeConnectionManager` holds one pc across turns (idempotent `ensureConnected`; the connect latch **resets on a failed connect** so a transient failure doesn't permanently brick the mode — the controller catches → `failTurn`+abort). `realtime.session.connecting` is stamped at connect-**initiation** (browser clock → `realtime_connect_ms`); `connected`/`disconnected` from the pc `connectionstate` (a settable `onConnectionState` shell delegate → a tested mapper). A disconnect → a frontend-synthesized sanitized `realtime.session.disconnected` `UiError` → `failTurn` (active turn, populating its `errors`) / `addError` (between turns) + `errorCopy` advise-switch-to-Cascade (never swallowed). `teardown()` (close DC/pc, stop tracks, release stream, detach `<audio>`, reset latches) is wired into `SessionSetup`'s End. **Re-mint on `expiresAt` + auto-reconnect are E.5b / documented fallbacks** (the 60-min cap > the 5-min demo). Confirm the pc `connectionState` event names (`connected`/`failed`/`disconnected`) at first real-key smoke.

### Realtime models

`OPENAI_REALTIME_MODEL` selects `gpt-realtime` (default) or `gpt-realtime-mini` (≈5× cheaper audio tokens). Both are exposed in `SessionSetup` so the cost write-up can compare a premium vs cost-efficient realtime tier. The Realtime API is **GA** (since 2025-08-28) — no beta header.

### Limitations to preserve (for the write-up)

Realtime has fewer internal stage metrics, less provider-level control, likely lower latency, and simpler app orchestration. Model availability/pricing stays configurable.

---

<a id="arch-011"></a>

## §8 — Cascade Mode Architecture

### Provider baseline

`STT: Deepgram nova-3 (language=multi, live WebSocket)` → `Translation: OpenAI gpt-5.4-nano|mini (Responses API, streamed)` → `TTS: OpenAI gpt-4o-mini-tts (streamed)`.

### Streaming pipeline (MUST)

The PRD requires **streaming throughout, no full-utterance blocking, live transcripts as produced.** The `CascadeStreamingOrchestrator` runs over `WS /api/cascade/stream`:

```text
1. Client sends `start` then streams linear16 PCM frames; orchestrator pushes frames into ISttProvider.
2. await foreach SttEvent:
     - SttStarted        → stamp stt.started
     - SttPartial        → emit source TranscriptSegment(isFinal=false); stamp stt.first_partial (first only)
     - SttFinal (segment)→ emit source TranscriptSegment(isFinal=true); stamp stt.final;
                           → trigger translation for THIS finalized segment
3. Per finalized source segment, await foreach TranslationEvent (streamed):
     - TranslationStarted → stamp translation.started
     - TranslationPartial → emit target TranscriptSegment(isFinal=false); stamp translation.first_token (first only)
     - TranslationFinal   → emit target TranscriptSegment(isFinal=true); stamp translation.final;
                          → trigger TTS for the finalized target text
4. await foreach TtsEvent (streamed):
     - TtsStarted    → stamp tts.started
     - TtsFirstAudio → stamp tts.first_audio; begin emitting audio chunks to the browser
     - TtsAudioChunk → emit { type: audio } chunks
     - TtsComplete   → stamp tts.complete
5. On `stop`, finalize remaining segments; emit cost + done; persist the turn.
```

**MVP streaming rule:** translation is triggered **per finalized STT segment** (Deepgram `is_final` / `UtteranceEnd`), not per interim partial — this satisfies "no full-utterance blocking" because the pipeline never waits for the entire turn's audio before transcribing/translating/synthesizing. Sub-utterance incremental translation is a deferred refinement (ARCH-025).

**Streaming contract (task-citable acceptance):** _"For a multi-word turn, the target transcript and the first TTS audio begin arriving at the browser before `tts.complete`, and the source transcript renders incrementally (partials before final)."_ A buffered single response does **not** satisfy this.

### STT path

`DeepgramSttProvider` uses the **live WebSocket** client (`ClientFactory.CreateListenWebSocketClient`) with `model=nova-3`, `language=multi`, `smart_format=true`, `interim_results=true`, `encoding=linear16`, `sample_rate=<from start msg>`, `channels=1`, and `utterance_end_ms` for segment boundaries. Emits `SttStarted` → `SttPartial*` → `SttFinal` (per segment).

**Fallback path** (`CascadeOrchestrator` + `POST /api/cascade/turn`): pre-recorded REST (`CreateListenRESTClient`) on the uploaded blob → single `SttFinal` (no interim); `stt.first_partial` is `n/a`. Translation + TTS still stream. Documented as the ARCH-025 fallback.

> **C.1 realization.** `DeepgramSttProvider` is a thin manual-smoke transport shell over a pure, unit-TDD'd `DeepgramSttMapping` (SDK-result→`SttEvent`, REST parse, exception→`SttFailed`, schema build). The callback-based Deepgram WS SDK is bridged to the `IAsyncEnumerable<SttEvent>` contract via a `Channel<SttEvent>` (callbacks write `ToSttEvent(...)`; `TranscribeAsync` reads + yields; cancellation completes the writer + `Stop()`s the socket). One `ISttProvider` seam serves both paths — `SttRequest.Encoding == "linear16"` routes to live WS, any other (recorded-container) encoding to the REST single-shot. See `server/LESSONS.md` §18 (the real-provider pattern, reused by C.2/C.3) + §19 (SDK-hidden-status recovery). **Error-status fidelity (C.6, resolved):** Deepgram v6.6.1 discards the HTTP status on its error-body path (the exception carries only a semantic `err_code` string), so `DeepgramSttMapping` recovers the status by matching `err_code` → `ProviderErrorMapper.MapStatus` — common-path 429/401/403/400 now classify correctly (the C.1 Q5 degrade is closed; only 5xx-with-body, which uses Deepgram's `error_code` key, falls through to the honest `stt.unknown`). See the ARCH-012 mapping note.

### Translation path

`OpenAiTranslationProvider` calls the **Responses API `POST /v1/responses` with `stream=true`** (Chat Completions is the documented secondary fallback). Map `response.created → TranslationStarted`; first `response.output_text.delta → translation.first_token + TranslationPartial`; subsequent deltas → `TranslationPartial`; `response.completed → TranslationFinal` (aggregated). Set **nested** `reasoning: {effort: "minimal"}` and `text: {verbosity: "low"}` to protect first-token latency against the <2s target. Read usage tokens off `response.completed` (`response.usage.input_tokens`/`output_tokens`, snake_case) for cost (Chat Completions needs `stream_options.include_usage`). System instruction = the faithful-interpreter prompt (output ONLY the translation, no framing).

> **C.2 API-shape note (Step-1 verified, 2026-05-29).** On `/v1/responses` the latency params are **nested objects** — `reasoning.effort` + `text.verbosity` — **not** the top-level `reasoning_effort` field (that form is Chat-Completions-only and is rejected by the Responses API). The Responses stream has **no `[DONE]` sentinel** (unlike Chat Completions) — it ends after `response.completed`; usage lives at `response.usage.{input_tokens,output_tokens}` (snake_case, not `prompt`/`completion_tokens`). GA endpoint — Bearer auth, no `OpenAI-Beta` header. **The translation stage must stream** — the final-only-wrap allowance (below) does not apply to it. `OPENAI_TRANSLATION_MODEL` selects `gpt-5.4-nano` (default) or `gpt-5.4-mini`; the model used is recorded per turn (`TranslationModelUsed`) for comparison.

### TTS path

`OpenAiTtsProvider` calls **`POST /v1/audio/speech`** with **chunk-transfer streaming** so `tts.first_audio` is the first chunk (not a relabeled completion). Default `ResponseFormat=mp3` (broad browser playback; opus-in-mp4 fails on Safari); `wav`/`pcm` are lower-latency options exposed via config. Voice is config-driven (`OPENAI_TTS_VOICE`, default `alloy`), language-agnostic for EN/ES (one voice serves both, though voices are English-optimized — a documented write-up limitation). Guard the 4096-char input cap as a non-retryable error.

> **C.3 API-shape note (Step-1 verified, 2026-05-29).** For `gpt-4o-mini-tts`, `/v1/audio/speech` **defaults `stream_format` to `sse`** (base64 audio wrapped in SSE events) — to get a raw **chunked-binary** audio body the request MUST send **`"stream_format": "audio"`**. (Legacy `tts-1*` don't support `sse` and always return raw; our target is `gpt-4o-mini-tts`, so the explicit param is required.) `ContentType` is read from the response header (`audio/mpeg` for mp3). In `audio` mode there is **no in-band error frame** — a mid-stream failure is a truncated stream / read throw (→ `tts.timeout`/mapped). The 4096-char cap is enforced **pre-call** (`> 4096` → `tts.invalid_request`, non-retryable, no HTTP request) via the C.6 `ProviderErrorMapper.MapStatus(400,…)` door. See `server/LESSONS.md` §20.

### Stage events

| Event | Stage | Tier |
|---|---|---|
| `cascade.audio.received` | Capture | must |
| `stt.started` / `stt.final` | STT | must |
| `stt.first_partial` | STT | nice (streaming-only; `n/a` on fallback) |
| `translation.started` / `translation.final` | Translation | must |
| `translation.first_token` | Translation | nice |
| `tts.started` / `tts.first_audio` | TTS | must |
| `tts.complete` | TTS | nice |
| `playback.started` | Playback | must |
| `turn.completed` | Overall | must |

> Honesty note: `tts.first_audio`/`tts.complete` are **backend-measured** against the OpenAI TTS stream; `playback.started` is the only browser-side TTS-output timing.

> Stage-label note (B.4 → resolved C.4a): turn-lifecycle events (`turn.recording.started`/`.stopped`, `turn.completed`) are stamped **`LatencyStage.Overall`** (the enum member added in C.4a; the cascade WS endpoint stamps `turn.recording.*` server-clock, the orchestrator stamps `turn.completed`). The aggregator keys metrics by event **name**, so the stage label is for doc/UI grouping fidelity. _(Pre-C.4a these were stamped `Capture` as the closest existing member; C.4a added `Overall` + re-stamped.)_

### Empty-transcript rule

If STT yields an empty final: do **not** call translation or TTS; emit `cascade.empty_transcript`; persist the failed turn; UI suggests retrying.

> **Ownership boundary (C.1).** The empty-transcript short-circuit is the **orchestrator's** responsibility, not the provider's. A real (or fake) STT provider emits `SttFinal("")` for a no-speech result — it never raises `cascade.empty_transcript` itself (that code belongs to stage `"cascade"`, via `ProviderErrorMapper.EmptyTranscript`, and `FakeStt.EmptyFinal` matches the `SttFinal("")` contract). The orchestrator detects the empty final and emits `cascade.empty_transcript`.

### Partial-failure rule

- Translation fails after STT: persist source transcript; mark translation/TTS skipped; show source + error.
- TTS fails after translation: persist source + target transcripts; mark audio unavailable; show target + error.

---

<a id="arch-012"></a>

## §9 — Provider Interfaces

### Design goals

Hide vendor SDK/API details from orchestration; emit normalized streaming events; support fakes; allow swaps without app rewrites; keep provider options in provider config.

### STT

```csharp
public sealed record AudioFrame(ReadOnlyMemory<byte> Bytes, DateTimeOffset CapturedAt);

public sealed record SttRequest(
    IAsyncEnumerable<AudioFrame> AudioFrames,  // live frames; fallback wraps the blob as a single sequence
    string ContentType, string Encoding, int SampleRate,
    LanguageCode SourceLanguage, string SttLanguage, string SessionId, string TurnId);

public interface ISttProvider
{
    IAsyncEnumerable<SttEvent> TranscribeAsync(SttRequest request, CancellationToken ct);
}

public abstract record SttEvent(DateTimeOffset Timestamp);
public sealed record SttStarted(DateTimeOffset Timestamp)                  : SttEvent(Timestamp);
public sealed record SttPartial(string Text, DateTimeOffset Timestamp)     : SttEvent(Timestamp);
public sealed record SttFinal(string Text, DateTimeOffset Timestamp)       : SttEvent(Timestamp);
public sealed record SttFailed(ProviderError Error, DateTimeOffset Timestamp) : SttEvent(Timestamp);
```

### Translation

```csharp
public sealed record TranslationRequest(
    string Text, LanguageCode SourceLanguage, LanguageCode TargetLanguage,
    string Model, string SessionId, string TurnId);

public interface ITranslationProvider
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

public abstract record TranslationEvent(DateTimeOffset Timestamp);
public sealed record TranslationStarted(DateTimeOffset Timestamp)                       : TranslationEvent(Timestamp);
public sealed record TranslationPartial(string TextDelta, DateTimeOffset Timestamp)     : TranslationEvent(Timestamp);
public sealed record TranslationFinal(string Text, int? InputTokens, int? OutputTokens, DateTimeOffset Timestamp) : TranslationEvent(Timestamp);
public sealed record TranslationFailed(ProviderError Error, DateTimeOffset Timestamp)   : TranslationEvent(Timestamp);
```

### TTS

```csharp
public sealed record TtsRequest(
    string Text, LanguageCode TargetLanguage, string Voice, string Model,
    string ResponseFormat, string? Instructions, string SessionId, string TurnId);

public interface ITtsProvider
{
    IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, CancellationToken ct);
}

public abstract record TtsEvent(DateTimeOffset Timestamp);
public sealed record TtsStarted(DateTimeOffset Timestamp)                                  : TtsEvent(Timestamp);
public sealed record TtsFirstAudio(string ContentType, DateTimeOffset Timestamp)           : TtsEvent(Timestamp);
public sealed record TtsAudioChunk(byte[] Bytes, int Seq, DateTimeOffset Timestamp)        : TtsEvent(Timestamp);
public sealed record TtsComplete(string ContentType, DateTimeOffset Timestamp)             : TtsEvent(Timestamp);
public sealed record TtsFailed(ProviderError Error, DateTimeOffset Timestamp)              : TtsEvent(Timestamp);
```

### Exception → ProviderError mapping

Each real provider catches its SDK/`HttpRequestException`, reads the status, sets `Code`/`Provider`/`Stage`, and emits a `*Failed` event:

| Condition | Code | Retryable | HTTP |
|---|---|---|---|
| 429 rate limit | `<stage>.rate_limited` | true | 429 |
| 401/403 | `<stage>.auth` | false | 401/403 |
| 400/422 | `<stage>.invalid_request` | false | 400/422 |
| STT empty | `cascade.empty_transcript` | true | — |
| 5xx / network | `<stage>.upstream_unavailable` | true | 5xx |
| timeout | `<stage>.timeout` | true | — |
| fallback | `<stage>.unknown` | false | — |

> **(C.4b) Fail-closed on a terminal-less stream.** Beyond the exception/timeout paths, a *real* provider stream can also end **cleanly without its terminal event** — a started translation/TTS stage that yields tokens/chunks but no `TranslationFinal`/`TtsComplete`, or STT partials with no `SttFinal` (fakes never do this; real providers can). The orchestrator **fails closed**: such a stage emits `<stage>.unknown` + `Done(Failed)` via a non-exception `ProviderErrorMapper.Unknown(provider, stage)` factory (fixed `SafeMessage`, invariant #4), rather than silently skipping downstream / completing with a missing target. STT that ends cleanly with **no partial and no final** is valid silence → `Completed`.

> **`ProviderErrorMapper` entry points + SDK-hidden-status recovery (C.6).** The table above is owned by `ProviderErrorMapper` (single owner; `SafeMessage` is a fixed string per code, never echoing `ex.Message`/`err_msg`). Entry points: `Map(Exception, …)` (the catch-all — `HttpRequestException` status / `OperationCanceledException`→timeout / fallback→unknown), `MapStatus(int statusCode, …)` (**C.6** — an explicit-status door into the same table, for providers whose SDK hides the HTTP status from the exception), plus `Timeout(…)` / `EmptyTranscript(…)` / **`Unknown(…)`** (**C.4b**) for the non-exception outcomes. The mapper stays **vendor-agnostic** (no provider-SDK dependency). **Deepgram realization:** the SDK (v6.6.1) discards the numeric `StatusCode` on its error-body path — the thrown exception carries only a semantic `err_code` string — so `DeepgramSttMapping` recovers the status by matching `err_code` (`TOO_MANY_REQUESTS`→429, `INVALID_AUTH`/`INSUFFICIENT_PERMISSIONS`→401/403, `Bad Request`→400) and calls `MapStatus`; an unrecognized `err_code` (incl. 5xx-with-body, which uses Deepgram's `error_code` key) falls through to the honest `stt.unknown`. (Supersedes the C.1 Q5 limitation — common-path 429/401/403/400 now classify correctly.)

### Timeout & cancellation

The orchestrator wraps each stage: `CancellationTokenSource.CreateLinkedTokenSource(requestToken).CancelAfter(Options.TimeoutSeconds)` and passes the linked token to the provider enumerable. On `OperationCanceledException`, emit the stage `*.timeout` event + `ProviderError(Code="<stage>.timeout", Retryable=true)` and skip downstream per the partial-failure rules. Per-stage timeouts; no separate global turn timeout for MVP. Suggested defaults: STT 30s, translation 20s, TTS 30s, realtime token mint 10s.

> **STT timeout is a per-event idle timeout (B.4 realization).** Because the cascade orchestrator runs each finalized segment's translation+TTS sub-pipeline *inside* the STT `await foreach` (the nested per-segment loop, ARCH-011), a single whole-stream `CancelAfter` on the STT stage would count segment-1's downstream wall-clock against segment-2's STT and fire spuriously — and a `CancellationTokenSource` that has already fired cannot be un-cancelled. So the STT stage **arms** `CancelAfter(timeout)` immediately before each `MoveNextAsync` and **disarms** it (`CancelAfter(Timeout.InfiniteTimeSpan)`) before running downstream — i.e. the STT timeout bounds the *inter-event gap*, not the whole stream. Translation/TTS keep a single whole-stage `CancelAfter` (nothing long is interleaved inside them). The catch is filtered `when (!ct.IsCancellationRequested)` so **caller cancellation** (client disconnect) propagates as `OperationCanceledException` rather than being mislabeled a retryable `<stage>.timeout`.

### Provider Options (enumerated)

- **`DeepgramOptions`**: `ApiKey`, `BaseUrl`/`WebSocketUrl`, `Model=nova-3`, `Language=multi`, `SmartFormat=true`, `Encoding=linear16`, `SampleRate`, `Channels=1`, `InterimResults=true`, `UtteranceEndMs`, `TimeoutSeconds`.
- **`OpenAiTranslationOptions`**: `ApiKey`, `Model`, `ReasoningEffort="minimal"`, `Verbosity="low"`, `Stream=true`, `TimeoutSeconds`.
- **`OpenAiTtsOptions`**: `ApiKey`, `Model`, `Voice` (+ optional per-language map), `ResponseFormat="mp3"`, `Stream=true`, `Instructions?`, `TimeoutSeconds`.
- **`RealtimeOptions`**: `ApiKey` (server-only), `Model`, `Voice`, `InstructionsTemplate`, `ExpirySeconds=600`, `TokenTimeoutSeconds`, `TranscriptionModel="gpt-4o-transcribe"`.
- **`PricingOptions`**: bound from `pricing.json` (ARCH-014).

Bind via `IOptions`. New env vars: `DEEPGRAM_ENCODING`, `DEEPGRAM_SAMPLE_RATE`, `OPENAI_TTS_VOICE`, `OPENAI_TTS_FORMAT`, per-stage `*_TIMEOUT_SECONDS`.

### Fake providers (test doubles)

Each fake is configurable to emit `Started → one+ Partial/first-token/audio-chunk → Final/Complete` with a configurable delay **before each event** (via `yield return` + awaited delays), and to honor the `CancellationToken` (throw `OperationCanceledException` or emit a `*Failed` timeout). Variants: `FakeStt`(success-with-partials, empty-final, partials-then-error), `FakeTranslation`(token-stream-then-final, immediate-final-only, error), `FakeTts`(chunked-then-complete, complete-only, error). These make metrics, cancellation, and partial-failure paths deterministically testable.

---

<a id="arch-013"></a>

## §10 — Metrics and Latency Model

Metrics are the backbone — without credible metrics this is just an API demo.

### Schema

```json
{ "name": "stt.final", "stage": "stt", "timestamp": "2026-05-28T15:30:05.123Z",
  "relativeMs": 912, "clockSource": "server",
  "metadata": { "provider": "deepgram", "model": "nova-3", "language": "multi" } }
```

### Clock rules

- Backend provider stages use **server** timestamps (`clockSource: "server"`); browser-only media events use **browser** timestamps (`clockSource: "browser"`).
- `relativeMs` is measured from the turn's documented reference origin (recording start or processing start) — stated per metric below.
- The PRD benchmark uses `turn.recording.stopped` as the speech-end proxy.
- The aggregator must handle cross-clock pairs explicitly (a small skew is acceptable and disclosed in the write-up).

### Universal metrics

| Metric | Formula |
|---|---|
| `speech_end_to_first_audio_ms` | first output-audio event (or playback start) − `turn.recording.stopped` |
| `speech_end_to_playback_ms` | `playback.started` − `turn.recording.stopped` |
| `total_turn_ms` | `turn.completed` − `turn.recording.started` |
| `audio_duration_ms` | `turn.recording.stopped` − `turn.recording.started` |
| `estimated_cost_usd` / `estimated_cost_per_minute_usd` | ARCH-014 |

### Cascade stage metrics

`stt_first_partial_ms` (`stt.first_partial`−`cascade.audio.received`), `stt_final_ms`, `translation_first_token_ms` (`translation.first_token`−`translation.started`), `translation_final_ms`, `tts_first_audio_ms` (`tts.first_audio`−`tts.started`), `tts_complete_ms`.

### Realtime metrics

`realtime_connect_ms`, `realtime_first_audio_delta_ms` (`realtime.first_audio_delta`−`turn.recording.stopped`), `realtime_first_transcript_delta_ms`, `realtime_playback_ms`.

### Metric origins (aggregator conventions — B.3)

The first-* stage metrics above carry explicit origins; the **final/complete** stage metrics and several realtime metrics were under-specified and are pinned here by `MetricsAggregator` (B.3), measured from each stage's start:

- **Cascade stage durations:** `stt_final_ms` = `stt.final` − `cascade.audio.received`; `translation_final_ms` = `translation.final` − `translation.started`; `tts_complete_ms` = `tts.complete` − `tts.started`.
- **Realtime:** `realtime_connect_ms` = `realtime.session.connected` − `realtime.session.connecting` (the latter a connect-start marker the WebRTC client emits at E.4 — `n/a` until then, never synthesized); `realtime_first_transcript_delta_ms` = `realtime.first_transcript_delta` − `turn.recording.stopped`; `realtime_playback_ms` = `playback.started` − `realtime.first_audio_delta` (client buffer latency — **distinct** from the universal `speech_end_to_playback_ms`, which is `playback.started` − `turn.recording.stopped`).
- **`speech_end_to_first_audio_ms`** selects the present output-audio event: `tts.first_audio` (cascade) ?? `realtime.first_audio_delta` (realtime) ?? `playback.started`, else `n/a`.
- The aggregator computes every metric from **absolute `Timestamp` subtraction**, so cross-clock pairs are wall-clock-correct and a small disclosed skew is **not** clamped; a missing endpoint event yields `n/a` (null), never an error. `LatencyEvent.relativeMs` is a per-event display value (stamped by `LatencyEventFactory` relative to a documented origin, clamped ≥ 0) — **not** a cross-event math input. Per-turn metrics are surfaced as `TurnMetrics` (area-local computed type); B.7's `SessionSummaryService` averages them into `ModeSummary`.

### Metric tiers

- **MUST (cascade):** `turn.recording.started/stopped`, `stt.final`, `translation.final`, `tts.first_audio`, `playback.started`, `turn.completed`.
- **MUST (realtime):** `realtime.session.connected`, `turn.recording.stopped`, `realtime.first_audio_delta`, `playback.started`, `turn.completed`.
- **NICE (emit if available, else `n/a` — never an error):** `stt.first_partial`, `translation.first_token`, `tts.complete`, `realtime.first_transcript_delta`.

`MetricsAggregatorTests` cover the MUST pairs first.

---

<a id="arch-014"></a>

## §11 — Cost Estimation Model

Estimate live cost/min; clearly label it as **not billing-grade**.

### Responsibilities

Load `pricing.json`; accept usage units per turn/stage; estimate per-turn + per-session cost; compute cost/min; persist assumptions + `pricingConfigVersion`; render "Estimated cost/min". The estimator **branches on pricing basis** (audio-minute vs token vs character) and converts realtime audio-seconds to tokens before applying token rates.

> **(E.2b realization)** Realtime per-turn cost is wired at `POST …/turns/{turnId}/complete` (`SessionService.CompleteTurnAsync`, the realtime finalize), mirroring the cascade WS cost fold — each mode prices at its own terminal, never double. It branches on `Mode==Realtime`, reads the session's realtime model, and builds `CostUsage` from `audioDurationMs` (input) + `outputAudioDurationMs?` (output, E.4-reported). **`CostEstimate.PricingBasis` is `"tokens"`** for realtime (it bills on audio tokens via the audio-seconds→tokens factor; the audio-seconds live in `Units`). A turn with **no reported audio duration → `CostEstimate` null** ("unavailable"), never a synthetic $0.00; absent output → output disclosed-unavailable in `Assumptions` (the wiring guards this — `EstimateRealtime` itself treats absent output seconds as 0 with no disclosure).

### pricing.json (starting values — re-verify at build; all estimates)

```json
{
  "version": "2026-05-28-payg-estimates",
  "currency": "USD",
  "disclaimer": "Estimates from configured public pricing; NOT billing-grade. Re-verify before relying on numbers.",
  "providers": {
    "deepgram": {
      "stt": { "model": "nova-3", "language": "multi",
        "streamingUsdPerAudioMinute": 0.0058,
        "preRecordedUsdPerAudioMinute": 0.0092,
        "effectiveUsdPerAudioMinute": 0.0058,
        "note": "Cascade uses streaming (live WS) — the cheaper rate." }
    },
    "openai": {
      "realtime": {
        "gpt-realtime":      { "audioInputUsdPerMillionTokens": 32.0, "cachedAudioInputUsdPerMillionTokens": 0.40, "audioOutputUsdPerMillionTokens": 64.0 },
        "gpt-realtime-mini": { "audioInputUsdPerMillionTokens": 10.0, "audioOutputUsdPerMillionTokens": 20.0 },
        "estimatorNote": "Convert audio seconds → tokens before applying rates; conversion factors are estimates — CONFIRM at build."
      },
      "translation": {
        "gpt-5.4-nano": { "inputUsdPerMillionTokens": 0.20, "outputUsdPerMillionTokens": 1.25 },
        "gpt-5.4-mini": { "inputUsdPerMillionTokens": 0.0,  "outputUsdPerMillionTokens": 0.0, "note": "CONFIRM at build — mini tier > nano." }
      },
      "tts": {
        "gpt-4o-mini-tts": { "pricingBasis": "audio_output_tokens", "textInputUsdPerMillionTokens": 0.60, "audioOutputUsdPerMillionTokens": 12.0, "approxUsdPerAudioMinute": 0.015 },
        "tts-1":    { "pricingBasis": "characters", "usdPerMillionCharacters": 15.0 },
        "tts-1-hd": { "pricingBasis": "characters", "usdPerMillionCharacters": 30.0 }
      }
    }
  }
}
```

> **Shape note (A.4):** in `openai.realtime`, `estimatorNote` is a **string sibling** of the per-model entries (`gpt-realtime`/`gpt-realtime-mini`). The `PricingOptions` binding reflects this — `realtime` is an explicit class (`[JsonPropertyName]` per model + `EstimatorNote`), while `translation`/`tts` (no sibling strings) are model-keyed dictionaries. Loaded via `PRICING_CONFIG_PATH` with degrade-don't-crash (ARCH-018), not an appsettings section.

### Cost estimate output

```json
{ "provider": "openai", "model": "gpt-5.4-nano", "pricingBasis": "tokens",
  "estimatedUsd": 0.00009, "estimatedUsdPerMinute": 0.011,
  "units": { "inputTokens": 14, "outputTokens": 18 },
  "pricingConfigVersion": "2026-05-28-payg-estimates",
  "assumptions": ["Estimate from configured public pricing, not provider invoice data."] }
```

### UI copy

Use **"Estimated cost/min"**. Never an unqualified "Cost".

### Estimator conventions (B.5)

- **Cascade per-turn cost is one composite `CostEstimate`.** A cascade turn has three priced stages (STT/translation/TTS) but the turn model + the WS `cost` message carry a single `CostEstimate` — so the estimator aggregates: `Provider="cascade"`, `Model=<translation model used>` (the cascade comparison axis, so the summary groups cascade cost by translation-model variant), `PricingBasis="composite"`, `EstimatedUsd = Σ stages`, with the per-stage breakdown in `Units`. **`"composite"` is a fifth `PricingBasis` value** beyond the four basis names in the §3 `CostEstimate` comment (`usd_per_audio_minute`/`audio_output_tokens`/`characters`/`tokens`); `PricingBasis` is a `string`, so this is a documented value, not a model change. The composite degrades wholesale (any stage's pricing missing → estimate unavailable; never a partial-garbage number).
- **Realtime audio-seconds→tokens factor is an explicit estimate.** `CostEstimator.RealtimeTokensPerAudioSecond = 50` is an **estimate pending build-time confirmation** (the `pricing.json` `estimatorNote` flags it) — it is surfaced in every realtime estimate's `Assumptions`, never presented as billing-grade. See §16 build-time-confirm items.
- **`0.0` configured rate ≠ absent config.** A model present in `pricing.json` with a `0.0` rate (e.g. `gpt-5.4-mini`, pending confirmation) **estimates to `0.0`** (a real, if provisional, number); only genuinely-missing config / model / usage degrades to `Result.Failure` ("estimate unavailable"). All math is `decimal`, unrounded (the UI formats).
- **Cascade cost-usage sourcing (C.4a).** The WS endpoint assembles `CostUsage` from observable signals because the orchestrator's `CascadeOutputEvent` stream carries no raw token counts: **STT** = audio-minutes from the recording duration (`turn.recording.started`→`.stopped`); **translation** = `inputTokens`/`outputTokens` read from the **`translation.final` `LatencyEvent.Metadata`** (the orchestrator stamps them there — an existing flexible field, no contract change; tokens accumulate **additively** across multi-segment turns, not overwritten); **TTS** = a **target-character proxy** (documented assumption — `/v1/audio/speech` in `stream_format:"audio"` returns **no usage block**, so precise TTS cost is unavailable without sse-mode, which would complicate the raw-byte reader). Missing any priced stage → the composite degrades wholesale to "unavailable" (never a partial-garbage number). The TTS char-proxy is a **G.5 write-up disclosure** (cost is an estimate, and the TTS component especially so).
- **Blob fallback does NOT price the STT stage (C.5).** The `POST /api/cascade/turn` blob path has no recording wall-clock (it's pre-recorded), and the pre-recorded `SttEvent` contract carries **no in-band audio-duration signal** — so the blob path supplies STT audio-minutes as **unsupplied**, and the composite degrades wholesale to **unavailable** for blob turns. A processing-wall-clock proxy was **rejected** (it's processing time, not audio duration — a ~30× undercount for pre-recorded, i.e. a synthetic metric the streaming-honesty posture forbids; degrade honestly per §9). The **primary streaming WS path prices fully** (its recording wall-clock ≈ live audio duration). A **G.5 write-up disclosure**. _(Tracked future fix: surface Deepgram's pre-recorded `SyncResponse.Metadata.Duration` via an additive `SttFinal` duration → `stt.final` Metadata → the cost layer, FORK-1a-style — a cross-doc `SttFinal` change, deferred at Phase-C-close.)_

---

<a id="arch-015"></a>

## §12 — WER Evaluation Model

### Purpose & scope

WER is an objective **STT transcript** quality signal for scripted phrases. It does **not** measure semantic translation quality.

### Phrase file

`server/AiInterpreter.Api/Evaluation/evaluation-phrases.json` — 8–12 EN + ES phrases:
```json
[ { "phraseId": "en_001", "language": "en", "referenceText": "I need help checking in for my appointment.", "category": "appointment" },
  { "phraseId": "es_001", "language": "es", "referenceText": "Necesito ayuda para registrarme para mi cita.", "category": "appointment" } ]
```

### Normalization & algorithm

Lowercase → remove punctuation → normalize whitespace. (Accent-stripping only if explicitly documented; default preserves language text.) DP edit distance over word arrays: `WER = (S + I + D) / N`, `N` = reference word count.

> **Implementation note (B.6):** normalize = **invariant**-lowercase → strip `\p{P}` punctuation (incl. inverted `¿`/`¡`) → collapse whitespace; **accents/`ñ` are preserved** (accent-stripping not taken). Punctuation is removed (replaced with `""`, not a space), so contractions collapse (`don't`→`dont`) — robust to STT apostrophe-dropping; author `evaluation-phrases.json` free of intra-word punctuation so word boundaries are unambiguous. The DP backtrace attributes S/I/D individually with tie-break precedence **match > substitution > deletion > insertion** (documented for reproducibility). **WER is unbounded** — `> 1.0` is valid (more insertions/substitutions than reference words) and is never clamped. An empty normalized reference (`N=0`) is a **precondition violation** → `ArgumentException` (never a divide-by-zero); the reference is always a validated scripted phrase.

### Flow & tests

The EvaluationPanel records a phrase → `POST /api/evaluation/transcribe` (STT-only) → `POST /api/evaluation/wer`. Required test cases (`WerCalculatorTests`, aligned with ARCH-020): perfect match → 0; one deletion; one insertion; one substitution; empty hypothesis; **empty reference (N=0) explicitly rejected** (no divide-by-zero); punctuation/casing normalization.

### UI explanation

"WER compares the recognized transcript to a known reference phrase. It is useful for STT quality, not a full measure of translation quality."

---

<a id="arch-016"></a>

## §13 — Persistence Model

### Approach

Local JSON under `data/sessions/`. Filename: `session_YYYYMMDDTHHMMSSZ_<short-id>.json`. The `sessionId`/short-id derives only from a **server-generated id matching `^[A-Za-z0-9_-]+$`**; canonicalize and assert the resolved path stays under `SESSION_DATA_DIR` (path-traversal guard). The optional label is a JSON field only — never in the filename.

> **(B.7a realization)** The filename timestamp is the session's `StartedAt` (stamped once at create), **not** write-time — so the write-on-end + best-effort per-turn writes target **one stable file per session** (overwrite-in-place), rather than spraying a new file per write. The path-traversal guard is **two layers**: an ASCII allowlist check (`^[A-Za-z0-9_-]+$`) before touching the filesystem, **plus** a canonicalized (`Path.GetFullPath`) **separator-terminated** `StartsWith(SESSION_DATA_DIR)` check. The trailing separator is load-bearing — without it a sibling like `…/data/sessions-evil` would pass a `…/data/sessions` prefix test.

### Persist / never-persist

**Persist:** session id, label, timestamps, provider profile, mode transitions, turns, transcripts, latency events (with `clockSource`), cost estimates, WER results, normalized errors, summary, `sttLanguage`, `ttsVoice`, pricing assumptions, `pricingConfigVersion`.
**Never persist:** raw audio; standard API keys; the ephemeral client secret; full provider payloads with sensitive metadata.

### Example (abbreviated)

```json
{ "sessionId": "session_abc123", "label": "Demo run 1",
  "startedAt": "2026-05-28T15:30:00Z", "endedAt": "2026-05-28T15:36:00Z",
  "config": { "currentMode": "cascade",
    "direction": { "source": "en", "target": "es" },
    "providerProfile": { "realtimeProvider": "openai", "realtimeModel": "gpt-realtime",
      "sttProvider": "deepgram", "sttModel": "nova-3", "sttLanguage": "multi",
      "translationProvider": "openai", "translationModel": "gpt-5.4-nano",
      "ttsProvider": "openai", "ttsModel": "gpt-4o-mini-tts", "ttsVoice": "alloy" } },
  "turns": [], "modeTransitions": [], "summary": null,
  "pricingConfigVersion": "2026-05-28-payg-estimates" }
```

### Write strategy & tiers

**MUST:** write final JSON on session end + best-effort write per completed/failed turn. **TRIM CANDIDATES (ARCH-025):** atomic temp→rename durability; write-after-every-WER. `SessionPersistenceTests` focus on round-trip + secret/raw-audio exclusion (not atomicity).

> **(B.7a) Sentinel scope.** The never-persist guarantee (#1 standard keys / #2 ephemeral `ek_` secret / #3 raw audio) rests on the **session model carrying no field** for any of them. The `SessionPersistenceTests` sentinel actively scans the serialized JSON for those patterns (`sk-`/`ek_`/`bytes`/`apikey`/`clientsecret`) to make the guarantee explicit + **drift-proof** — a future field that leaks one of them fails the test. The writer is deliberately **not** a runtime sanitization boundary: it serializes the normalized model verbatim, and scrubbing free-text content (e.g. a secret mistakenly placed in a `Metadata` value) is the error-sanitizer's job (B.8 / ARCH-019). The upstream invariant is that secrets/audio never enter the model in the first place. Persistence IO failure degrades to `persistence.failed` (`Result.Failure`), never an unhandled crash; `Result.Error` may embed a filesystem path so callers (B.9/C.4) must never echo it into a response body.

---

<a id="arch-017"></a>

## §14 — User Flows

**Flow A — Setup:** clone → copy `.env.example` → `.env`, add keys → start backend → start frontend → open SPA → SPA calls `GET /api/config` and disables unconfigured modes.
**Flow B — Realtime turn:** start session (pick realtime model) → select direction → Realtime mode → Start → speak → Stop → hear target audio → see transcripts/latency/cost → persist.
**Flow C — Cascade streaming turn:** switch to Cascade between turns → Start (opens WS, streams PCM) → speak (source partials render live) → Stop → target tokens + TTS audio stream in → see per-stage latency → persist.
**Flow D — WER:** open Evaluation panel → select phrase → read aloud → `transcribe` → `wer` → persist.
**Flow E — Summary:** run multiple turns/modes/models → open Summary → compare latency/cost/errors/WER → use in write-up.
**Flow F — End:** End Session → finalize summary → write JSON → UI shows path/success.
**Flow G — Switch mode between turns:** verify ReadyForTurn/TurnCompleted → if leaving Realtime: stop track, close DC, close pc, drain playback → emit `ModeTransitionEvent` → if entering Realtime: re-mint + rebuild pc + await `realtime.session.connected` before enabling recording; if entering Cascade: mark ready.
**Flow H — App refresh / recovery:** treat any in-progress turn as abandoned; frontend MAY reattach via `GET /api/sessions/{id}` (sessionId in localStorage) or start fresh; Realtime must re-establish. Backend stale-session auto-end/flush ensures abandoned sessions still produce a JSON artifact; completed turns are already safe via write-after-turn.

> **(E.5-backend realization)** The backend stale-flush fires **on `POST /api/sessions` (`SessionService.CreateAsync`)**: it flushes **all** prior un-ended sessions through the existing `EndAsync` finalize+persist seam (no duplicated persistence) **before** registering the new session (so the new one can't flush itself), over a snapshot `SessionStore.ActiveSessionIds()`. The owed artifact write uses **`CancellationToken.None`** — it must complete even if the `POST` is cancelled mid-flush, else the stale session would end in-memory with no artifact (the half-state the flush exists to prevent). A persist failure degrades (`EndedAt` still flips; logged; never thrown), never blocking the new session (lesson §11). Only un-ended sessions flush (idempotent). A TTL/timer sweep for long-idle abandonment is deferred (G hardening); on-create flush covers the single-user demo path.

---

## §15 — Cross-cutting concerns

<a id="arch-018"></a>

### Error Handling and Failure Modes

Errors are **visible, normalized, persisted, and safe**. A failure in one cascade stage never erases prior evidence.

**Normalized error codes** (`<stage>.<reason>`): `config.missing_openai_key`, `config.missing_deepgram_key`, `realtime.token_failed`, `realtime.connection_lost`, `realtime.rate_limited`, `stt.timeout`, `stt.failed`, `stt.rate_limited`, `cascade.empty_transcript`, `cascade.invalid_audio`, `cascade.invalid_request`, `cascade.forbidden_origin`, `translation.timeout`, `translation.failed`, `translation.rate_limited`, `tts.timeout`, `tts.failed`, `tts.rate_limited`, `playback.failed`, `persistence.failed`, `evaluation.invalid_phrase`, `evaluation.phrase_not_found`, `evaluation.invalid_request`, `session.not_found`, `turn.not_found`. Each has a default `Retryable`.

> **(C.4b) Cascade WS-boundary codes.** `cascade.invalid_audio` is reserved for the **audio-encoding** rejection (the closed `{linear16,pcm}` allowlist — the C.4a header-injection guard); a **malformed-JSON / missing-field / oversized (>256-char) start frame** is the distinct `cascade.invalid_request` (400-shaped); a **disallowed WS `Origin`** is `cascade.forbidden_origin` (403-shaped, see ARCH-019). All three are rejected at the WS boundary *before* any orchestration; none is retryable.

| Failure | Backend | UI | Test |
|---|---|---|---|
| Mic permission denied | no backend call | recovery hint; Start disabled | Frontend state test + manual |
| Missing OpenAI key | `config.missing_openai_key` | disable Realtime/OpenAI stages | `ConfigEndpointTests` |
| Missing Deepgram key | `config.missing_deepgram_key` | disable Cascade STT | `ConfigEndpointTests` |
| **HTTP 429 rate limit** (any stage) | `<stage>.rate_limited`, Retryable=true; **no auto-retry of model calls** | offer manual retry | `ProviderBoundaryTests` 429 case |
| Realtime token failure | sanitized error | retry/switch | service test |
| Realtime WebRTC / ICE failure | persist `realtime.session.disconnected` | show + advise switch to Cascade | manual/client test |
| Ephemeral credential expiring | re-mint via broker | transparent | broker re-mint test |
| STT timeout | `stt.timeout` | STT failed | fake provider test |
| Empty transcript | skip translation/TTS | no speech detected | fake provider test |
| Translation failure | persist source | translation failure | fake provider test |
| TTS failure | persist target | audio unavailable | fake provider test |
| Oversized/disallowed audio upload | `cascade.invalid_audio` (413/415) | invalid audio | controller test |
| Playback failure | persist backend result | playback failed | manual |
| Persistence failure | continue session | save warning | writer test |
| Cost config missing | estimate unavailable | N/A | cost test |
| WER invalid phrase | `evaluation.invalid_phrase` | phrase error | evaluation test |

**Retry policy:** conservative — no auto-retry of expensive model calls; user retries a turn; the **only** path allowed one bounded auto-retry is the Realtime client-secret mint (honor `Retry-After`). Persist retries as separate turns or explicit retry metadata.

> **(B.8) Sanitizer boundary.** `ErrorSanitizer` (a DI singleton) is the single place that turns any `Exception` / `ProviderError` / failed `Result` into a client-facing **`UiError`** (`{ code, safeMessage, stage?, retryable, turnId? }` — the backend record mirroring the ARCH-007 TS shape). It is **safe-by-construction**: a fixed message per code, **never** interpolating `ex.Message` / `Result.Error` / stack into the output, with the original logged server-side only (single-lined to prevent log forgery). HTTP status is preserved via a server-only `[JsonIgnore] HttpStatusCode` and applied at the response level by B.9's handler, so the wire body stays an exact TS mirror. The sanitizer **projects** an already-safe `ProviderError` → `UiError` (reusing `ProviderErrorMapper`, ARCH-012 — not duplicating its table) and owns the generic `Exception` + `Result` boundary. Caller contract: the `code` passed to the `Result` path is a normalized literal (never request-derived — it becomes `UiError.Code`).

> **(B.9a) Global handler.** The unhandled-exception path is caught by a thin global `IExceptionHandler` (`app.UseExceptionHandler()`, placed first so it wraps the whole pipeline) that routes through the same `ErrorSanitizer` → `UiError` — no framework error page / Developer Exception Page ever reaches the client. Parameterless `UseExceptionHandler()` requires `AddProblemDetails()` to initialize (our handler always writes `UiError`, so the ProblemDetails fallback is never reached); a side effect is that framework-level **404/405 routing errors emit `application/problem+json`, not `UiError`** — accepted for unrouted paths the SPA never calls (add `UseStatusCodePages`→`UiError` only if a real consumer needs the uniform contract; no leak either way).

<a id="arch-019"></a>

### Security and Trust Boundaries

Boundary: `Browser ↔ Backend ↔ External Providers`.

1. Standard OpenAI/Deepgram keys live only in backend env/config.
2. Browser receives only the ephemeral OpenAI Realtime credential (`ek_...`).
3. Session JSON excludes secrets (incl. the ephemeral secret) and raw audio.
4. Provider errors are sanitized (no stack traces / no secrets) before UI display.
5. `.env` and session files are gitignored.
6. CORS restricted to the local frontend origin in development.
7. Backend validates session id, mode, and direction.
8. **Audio upload validation:** `POST /api/cascade/turn` enforces a configurable max size (`CASCADE_MAX_UPLOAD_BYTES`, default ~10MB) and a content-type allow-list (`audio/webm`, `audio/ogg`, `audio/wav`, `audio/mpeg`, `audio/mp4`); violations → `cascade.invalid_audio` (413/415). _(C.5 realization: **MIME parameters are stripped before the allowlist** so `audio/webm; codecs=opus` — the browser MediaRecorder primary capture format — is accepted; the **multipart/Kestrel body limit is bounded** to the cap + a 1MB envelope so an oversized upload returns 413, not a 500 / a buffering-DoS window.)_
9. **Path-traversal guard** on session filenames (see ARCH-016).
10. **Cascade WS `Origin` validation (C.4b):** the `WS /api/cascade/stream` upgrade **bypasses the CORS middleware**, so `CascadeWebSocketEndpoint` validates the `Origin` header itself against `FRONTEND_ORIGIN` (exact ordinal match) **before** `AcceptWebSocketAsync`; a disallowed/missing/literal-`"null"` Origin → `403` + a `UiError`-shaped `cascade.forbidden_origin` body, **no socket accepted** (a non-WS request → a `UiError`-shaped `400`). The allow/deny decision is the pure unit-pinned `CascadeOriginValidation.IsAllowedOrigin`.
11. **WS start-frame field caps (C.4b):** every client-supplied start-frame string (`sessionId`/`turnId`/`translationModel`/`ttsVoice`) is length-capped (256) at the boundary — `sessionId`/`turnId` are store keys and `turnId` is echoed in every `done`, so an unbounded value is a store-key/echo DoS surface (extends the B.9 per-item-cap posture to the WS boundary).

**Sensitive local data:** README states session JSON may contain transcripts from local conversations — treat `data/sessions` as sensitive; do not commit.

**Non-production privacy statement:** "This local-first workbench is for architecture evaluation only. It does not implement production authentication, account isolation, consent management, retention policies, or regulated call handling."

<a id="arch-028"></a>

### Configuration & Secrets

Single home for configuration. Env vars (also in `.env.example`, names + comments, never real keys):

```text
OPENAI_API_KEY=                       # standard key — backend only, never to browser
OPENAI_REALTIME_MODEL=gpt-realtime    # or gpt-realtime-mini
OPENAI_REALTIME_VOICE=alloy
OPENAI_REALTIME_TRANSCRIPTION_MODEL=gpt-4o-transcribe
OPENAI_TRANSLATION_MODEL=gpt-5.4-nano # or gpt-5.4-mini
OPENAI_TTS_MODEL=gpt-4o-mini-tts
OPENAI_TTS_VOICE=alloy
OPENAI_TTS_FORMAT=mp3
DEEPGRAM_API_KEY=
DEEPGRAM_STT_MODEL=nova-3
DEEPGRAM_STT_LANGUAGE=multi
DEEPGRAM_ENCODING=linear16
DEEPGRAM_SAMPLE_RATE=48000
STT_TIMEOUT_SECONDS=30
TRANSLATION_TIMEOUT_SECONDS=20
TTS_TIMEOUT_SECONDS=30
REALTIME_TOKEN_TIMEOUT_SECONDS=10
SESSION_DATA_DIR=../../data/sessions
PRICING_CONFIG_PATH=../../config/pricing.json
EVALUATION_PHRASES_PATH=   # optional; default: <AppContext.BaseDirectory>/Evaluation/evaluation-phrases.json (B.6)
CASCADE_MAX_UPLOAD_BYTES=10485760   # blob fallback (C.5) upload cap (~10MB); bounds both the validator (413) + the Kestrel/multipart body limit
EVAL_MAX_UPLOAD_BYTES=10485760      # evaluation transcribe (F.1b) upload cap; optional — falls back to CASCADE_MAX_UPLOAD_BYTES. The framework body-limit backstop = max(cascade, eval).
ASPNETCORE_ENVIRONMENT=Development
```

Each provider has an `Options` class bound via `IOptions` (ARCH-012). Rule: **standard keys stay backend-only; the browser gets only the ephemeral credential.** Config-missing behavior cross-links ARCH-018. `ConfigService` reports configured booleans from key presence only.

> **Env→section bridge (A.5).** The flat operator env vars above are mapped to the PascalCase Options sections in one place in `Program.cs` (an explicit map → `AddInMemoryCollection` → `Configure<T>(GetSection(SectionName))`), setting **only keys that are present** so the inline Options defaults stand. `OPENAI_API_KEY` fans out to all three OpenAI services (`OpenAiTranslation`/`OpenAiTts`/`Realtime`). `PRICING_CONFIG_PATH` is read directly by the pricing loader (file-load, not section-bound); `SESSION_DATA_DIR` is consumed by persistence (B.7); `EVALUATION_PHRASES_PATH` is read directly by `EvaluationPhraseStore` (B.6, optional — sensible default), same degrade-don't-crash file-load as the pricing loader.

<a id="arch-029"></a>

### Build & Run Contract

Backend listen URL/port (e.g. `http://localhost:5179`); frontend dev port (e.g. `http://localhost:5173`); `VITE_API_BASE_URL` + a Vite dev proxy (or CORS allow-origin) so the SPA reaches the API and the cascade WebSocket; **secure-context note** (localhost is secure; non-localhost needs HTTPS for `getUserMedia`/WebRTC); `scripts/` for one-command start where helpful. (Demo script lives in ARCH-021.)

<a id="arch-020"></a>

### Testing Strategy

Test the **architecture seams**, not browser audio internals. Critical surfaces: cascade orchestration, provider boundaries, error mapping, metrics aggregation, WER, cost, persistence, error sanitization.

**Backend tests (priority-ranked so coverage tapers, not collapses):**

| File | Priority | Must assert |
|---|---|---|
| `CascadeOrchestratorTests` | CRITICAL | success path + stage ordering; **empty-transcript short-circuit (translation/TTS NEVER invoked — call-count spies; code `cascade.empty_transcript`; Status=Failed)**; two partial-failure cases (translation-fails → source persisted, no target, TTS never called; TTS-fails → both transcripts persisted, audio unavailable) |
| `ProviderBoundaryTests` | CRITICAL | fake provider event contracts; ordered streaming events; **429 → rate_limited(retryable)**; timeout → `*.timeout` |
| `WerCalculatorTests` | CRITICAL | the ARCH-015 cases incl. empty-reference rejection |
| `SessionPersistenceTests` | IMPORTANT | round-trip; **secret + ephemeral-secret + raw-audio exclusion via sentinel assertion**; path-traversal (`../`) rejection |
| `CostEstimatorTests` | IMPORTANT | deterministic estimates; basis branching (tokens vs chars vs audio-min) |
| `MetricsAggregatorTests` | THIN-IF-NEEDED | MUST-pair `relativeMs` formulas incl. cross-clock |
| `ErrorSanitizerTests` | THIN-IF-NEEDED | given an exception containing an API-key substring + stack, `SafeMessage` contains neither; `HttpStatusCode`/`Retryable` preserved |
| `ConfigEndpointTests` | IMPORTANT | `configured=false` when a key is absent; no secret echo |

**Frontend (intentionally light — PRD-aligned):** two transitions held to a bar — mode-toggle disabled during recording/processing/playing, and **mic-denied** (`getUserMedia` rejection → an actionable `mic.permission_denied` error surfaces + the turn fails: **Stop disabled, Start RE-ENABLED for retry** — matching the "enable mic access and retry" copy + the D.2 retry-after-fail design; "Start disabled" referred only to the transient in-flight moment, not the settled state — clarified D.7). Manual for mic/playback.

**Manual preflight (before demo):** backend starts; frontend starts; mic works; config endpoint reports correctly; Realtime client-secret works; Realtime connects + interprets; Cascade streams (source partials live, target + audio stream); session JSON writes; summary updates; WER returns a score; **5-minute run → (1) no disconnect, (2) no audio drift/overlap, (3) no memory leak**.

---

<a id="arch-027"></a>

## §16 — Open Questions & Build-Time Confirmations

These do not block task generation; each resolves at build time against the cited anchor. The owner decisions (streaming posture, both Realtime models, full WER panel, both translation models) are **resolved** and reflected above.

| # | Question | Owner | Resolving anchor | State |
|---|---|---|---|---|
| 1 | Is `gpt-realtime` (and `gpt-realtime-mini`) available in the target OpenAI account? | Project owner | ARCH-010 | Open — confirm with account/keys |
| 2 | Are Realtime input-transcription deltas reliably available with the chosen session config? If not, mark `realtime.first_transcript_delta`/source transcript optional and degrade the UI gracefully. | Build | ARCH-010, ARCH-013 | Open — verify at integration |
| 3 | Confirm browser capture format + that no transcoding is needed end-to-end (streaming PCM + pre-recorded fallback). | Build | ARCH-030, ARCH-011 | Mostly resolved — verify on target browsers |
| 4 | Re-verify all `pricing.json` values (esp. `gpt-5.4-mini`, realtime audio-token conversion factors) at build time. | Build | ARCH-014 | Open — values are estimates |
| 5 | Deepgram live-WS vs pre-recorded: live-WS is the MUST path; pre-recorded is the documented fallback. | Resolved | ARCH-011 | Resolved |

---

## Appendix A — Model / Contract Inventory

Canonical home for every cross-doc-invariant model. A field change on any row requires editing the cited anchor in the **same commit round**.

| Model | Anchor | Fields (summary) |
|---|---|---|
| `InterpretationMode` / `LanguageCode` / `TurnStatus` / `SessionStatus` / `LatencyStage` / `ClockSource` | ARCH-005 | enums. `LatencyStage` gained `Overall` (C.4a — turn-lifecycle stage grouping). |
| `LanguageDirection` | ARCH-005 | Source, Target |
| `ProviderProfile` | ARCH-005 | realtime/stt/translation/tts provider+model, SttLanguage, TtsVoice |
| `SessionConfig` | ARCH-005 | CurrentMode, Direction, ProviderProfile |
| `InterpretationSession` | ARCH-005 | id, label, timestamps, config, turns, modeTransitions, summary, pricingConfigVersion |
| `ModeTransitionEvent` | ARCH-005 | id, from/to mode, direction, occurredAt, clockSource, triggeredByTurnId |
| `InterpretationTurn` | ARCH-005 | id, mode, direction, timestamps, audioDurationMs, transcripts, latencyEvents, cost, wer, errors, status, translationModelUsed, ttsVoiceUsed, **isEvaluation** (F.4 — bool, default false; trailing so old JSON deserializes false) |
| `TranscriptSegment` | ARCH-005 | id, role, text, isFinal, provider, timestamp, clockSource |
| `LatencyEvent` | ARCH-005, ARCH-013 | name, stage, timestamp, relativeMs, clockSource, metadata |
| `CostEstimate` | ARCH-005, ARCH-014 | provider, model, pricingBasis, estimatedUsd, perMinute, units, version, assumptions |
| `SessionSummary` / `ModeSummary` / `WerSummary` | ARCH-005, ARCH-009 | aggregates by mode + WER |
| `EvaluationPhrase` / `WerResult` | ARCH-005, ARCH-015 | phrase ref; WER S/I/D/N + normalized text |
| `ProviderError` | ARCH-005, ARCH-012 | provider, stage, code, safeMessage, retryable, httpStatusCode |
| `ProviderErrorMapper` (static) | ARCH-012 | single owner of the exception/status→`ProviderError` table; entry points `Map(Exception,…)` / **`MapStatus(int,…)`** (C.6 — explicit-status door for SDK-hidden-status recovery) / `Timeout(…)` / `EmptyTranscript(…)`; vendor-agnostic; `SafeMessage` fixed-per-code (never echoes `ex.Message`/`err_msg`) |
| `UiError` (backend record) | ARCH-007, ARCH-018, ARCH-019 | code, safeMessage, stage?, retryable, turnId? — mirror of the ARCH-007 TS `UiError`; produced by `ErrorSanitizer` (B.8); server-only `[JsonIgnore] HttpStatusCode` is **NOT** part of the wire mirror |
| `ISttProvider` + `SttEvent`/`SttRequest`/`AudioFrame` | ARCH-012 | streaming STT contract |
| `ITranslationProvider` + `TranslationEvent`/`TranslationRequest` | ARCH-012 | streaming translation contract |
| `ITtsProvider` + `TtsEvent`/`TtsRequest` | ARCH-012 | streaming TTS contract |
| `DeepgramOptions` (§`"Deepgram"`) | ARCH-012, ARCH-028 | ApiKey (backend-only), BaseUrl/WebSocketUrl, Model=nova-3, Language=multi, SmartFormat, Encoding=linear16, SampleRate, Channels=1, InterimResults, UtteranceEndMs, TimeoutSeconds |
| `OpenAiTranslationOptions` (§`"OpenAiTranslation"`) | ARCH-012, ARCH-028 | ApiKey (backend-only), Model, ReasoningEffort=minimal, Verbosity=low, Stream, TimeoutSeconds |
| `OpenAiTtsOptions` (§`"OpenAiTts"`) | ARCH-012, ARCH-028 | ApiKey (backend-only), Model, Voice, VoiceByLanguage?, ResponseFormat=mp3, Stream, Instructions?, TimeoutSeconds |
| `RealtimeOptions` (§`"Realtime"`) | ARCH-012, ARCH-028, ARCH-019 | ApiKey (backend-only), Model, Voice, InstructionsTemplate, ExpirySeconds=600, TokenTimeoutSeconds, TranscriptionModel=gpt-4o-transcribe |
| `PricingOptions` | ARCH-014, ARCH-028 | Full `pricing.json` shape (A.4); file-loaded via `PRICING_CONFIG_PATH` (not section-bound); `realtime` explicit class (estimatorNote string sibling), `translation`/`tts` model-keyed dicts |
| `UiSessionState` / `TurnViewModel` / `UiError` (TS) | ARCH-007 | frontend projections |
| API DTOs: CreateSession, EndSessionResponse, CreateTurn(Response), CompleteTurn(Request/Response), AppendEvents, ClientSecret, CascadeStream msgs, Transcribe, Wer, ConfigResponse | ARCH-009 | camelCase serializations of the above. **Cascade WS (C.4a):** client `start{sessionId,turnId,direction,encoding,sampleRate,translationModel,ttsVoice}` + `stop{}`; server `transcript{segment}`/`latency{event}`/`audio{contentType,seq,base64}`/`cost{estimate}`/`error{error}`/`done{turnId,status}` (the wire contract; `CascadeOutputEvent`/`CascadeStartParams` stay **internal** — only these WS DTOs serialize). `CreateSession` (request) → returns the full `InterpretationSession`; `EndSessionResponse` (B.9c-i) = `{ session, persistedPath (filename-only), persistenceWarning? }` at 200; `CreateTurnResponse` = `{ turnId }`; `CompleteTurnRequest` (B.9c-ii; realtime-only; **E.2b** +`outputAudioDurationMs?`) = `{ audioDurationMs?, outputAudioDurationMs?, transcripts?, status? }` (cost/WER/model/voice are NOT client-supplied; `outputAudioDurationMs?` feeds the realtime output-audio cost at `/complete`, E.4-reported); `CompleteTurnResponse` = `{ turn, persistenceWarning? }`. **Cascade blob fallback (C.5):** `POST /api/cascade/turn` (multipart) → `CascadeTurnResponse` = `{ turn, audioBase64?, audioContentType?, persistenceWarning? }` (the assembled `InterpretationTurn` + the concatenated target TTS audio in-body, **response-only — never persisted**, invariant #3); the multipart form binds the turn params (`CascadeTurnForm` — input-binding only, not serialized out); `CascadeBlobParams`/`CascadeBlobResult` stay **internal**. **Realtime token (E.1):** the `ClientSecret` DTO = `RealtimeTokenRequest` `{ sessionId, direction, model? }` → `RealtimeTokenResponse` `{ clientSecret (`ek_…`), expiresAt (ISO-8601), model }`; standard key is server-side-only (invariant #1, Bearer-only), the `ek_` is response-only/never-persisted (invariant #2), GA top-level→camelCase mapping per ARCH-010; `RealtimeMintOutcome`/`RealtimeModelCatalog` stay **internal**. **Evaluation (F.1a):** `WerRequest` `{ sessionId, turnId?, phraseId, hypothesis }` → `WerResponse` `{ result: WerResult, persistenceWarning?: UiError }` (the computed `WerResult`; `persistenceWarning` set iff a `turnId`-attach write degraded); id fields `[MaxLength(256)]`, `hypothesis` capped in-service (not `[MaxLength]`) → `400 evaluation.invalid_phrase`; unknown phrase → `404 evaluation.phrase_not_found`. `EvaluationWerStatus`/`EvaluationWerOutcome` stay **internal**. **Evaluation transcribe (F.1b):** `POST /api/evaluation/transcribe` (multipart) binds `TranscribeForm` `{ sessionId, phraseId, language, audio, timestamps? }` (input-only, not serialized) → `TranscribeResponse` `{ hypothesis, sttProvider, sttModel, latencyEvents }`; STT-only, stateless (no turn/persist, invariant #3); upload size+type-validated via the shared `CascadeUploadValidation` (`413`/`415`, invariant #5); id over-cap → `400 evaluation.invalid_request`. `EvaluationTranscribeStatus`/`EvaluationTranscribeOutcome` stay **internal**. |

---

## Appendix B — Process & Build Discipline

<a id="arch-021"></a>

### Local Development and Demo Strategy

```bash
# backend
cd server/AiInterpreter.Api && dotnet restore && dotnet run
# frontend
cd web && npm install && npm run dev
```

**Demo script** (`docs/DEMO_SCRIPT.md`): start app → new session `5-minute-demo` → 2 EN→ES Realtime turns → switch to Cascade → 2 EN→ES Cascade turns → switch direction ES→EN → 1 Realtime + 1 Cascade turn → optionally switch translation model and repeat a cascade turn → 1 WER phrase → show Summary → open session JSON → explain tradeoffs.

**Suggested phrases** — EN: "I need help checking in for my appointment." / "Can you tell me where the front desk is?" / "I have been waiting for about twenty minutes." / "Could you repeat that more slowly?" — ES: "Necesito ayuda para registrarme para mi cita." / "¿Puede decirme dónde está la recepción?" / "He estado esperando unos veinte minutos." / "¿Puede repetir eso más despacio?"

<a id="arch-022"></a>

### Optional Deployment Strategy

Optional, only after a stable local demo. Frontend: S3+CloudFront or Amplify. Backend: App Runner / ECS Fargate / Elastic Beanstalk. Secrets: env vars or Secrets Manager. Session JSON: container disk (demo) or S3. **Do not add** production auth, multi-region, a database, CI/CD complexity, or an observability stack. **Browser mic/WebRTC requires HTTPS outside localhost** — ensure HTTPS for the frontend if deployed.

<a id="arch-023"></a>

### Documentation and Git History Requirements

**README:** overview, architecture summary, local setup, env vars, run commands, demo script, provider config, metric meanings, cost disclaimer, WER explanation, known limitations.
**CLAUDE.md/AGENTS.md:** how the agent was used; architecture-first workflow; constraints (no secrets to frontend; no raw audio; preserve provider interfaces; scoped commits).
**Comparison write-up:** what was built; how measurements were collected; Realtime results; Cascade results; latency comparison; cost comparison (incl. model variants); quality/WER observations; controllability/flexibility; recommendation (when Realtime vs Cascade); limitations + next steps.
**Git history:** no single "initial commit" dump — scoped commits roughly per the sequencing below.

<a id="arch-024"></a>

### Alternatives Considered

Backend proxy for Realtime (rejected for MVP; WebRTC fits browser audio + keeps key server-side; **kept as fallback**). All-OpenAI cascade (rejected baseline; Deepgram STT makes Cascade visibly provider-flexible). DeepL translation (deferred; another API + character billing). ElevenLabs TTS (deferred; voice quality vs integration cost). SQLite (deferred; JSON is enough). Always-on/VAD (deferred; turn-based is reliable). Raw-audio persistence (rejected; privacy/storage). **Deepgram Flux** (deferred: live-WS-only with built-in turn-taking; conflicts with the per-turn boundary and duplicates Realtime — Cascade STT stays `nova-3`+`multi`).

<a id="arch-025"></a>

### MVP Boundaries and Deferred Work

**MVP is done when:** local app runs; both modes complete turns; user hears translated audio; transcripts display live; latency (incl. per-stage cascade) displays; cost estimates display; session JSON persists; WER works in the Evaluation panel; summary compares modes (and model variants); tests cover core seams; README/demo/write-up complete.

**Deferred / trim catalog:** multi-user calls; auth/accounts; raw-audio persistence; multiple real providers per stage (beyond the named variants); **sub-utterance incremental translation** (translate mid-segment); semantic translation scoring; production deployment; production observability; VAD/barge-in. **Trim candidates under time pressure:** atomic-write persistence + per-WER write granularity; the standalone Evaluation panel UX (surface WER inline if cut — but currently a committed MUST); nice-tier metrics (`stt.first_partial`, `translation.first_token`, `tts.complete`, `realtime.first_transcript_delta`) degrade to `n/a`. **Fallbacks (use only if blocked):** Realtime backend WebSocket proxy; Cascade blob + Deepgram pre-recorded.

<a id="arch-026"></a>

### Implementation Sequencing Guidance

Backend seams + tests before UI polish; the two highest-risk integrations (Realtime WebRTC, live-streaming cascade) last, each with its fallback.

- **A — Repo & config:** scaffold; `.env.example`/`.gitignore`; domain models (ARCH-005); Options classes (ARCH-028); `pricing.json`.
- **B — Backend core (pre-providers):** session store; persistence writer; metrics model; cost estimator; WER calculator; fake providers; cascade orchestrator tests.
- **C — Cascade real providers:** Deepgram (live-WS + pre-recorded fallback); OpenAI translation (streamed); OpenAI TTS (streamed); error mapping; streaming WebSocket endpoint + blob fallback endpoint.
- **D — Frontend core:** session setup + model selectors; mode/direction; recording controls; transcript/metrics/cost panels; cascade streaming client + playback.
- **E — Realtime mode:** client-secret broker; WebRTC client; event normalization; metrics persistence.
- **F — Evaluation & summary:** phrases; transcribe + WER API/UI (Evaluation panel); comparison summary.
- **G — Docs & demo:** README; CLAUDE.md/AGENTS.md; demo script; comparison write-up; optional deployment notes.

---

## Appendix C — Gap-Audit Record (provenance)

This document is the finalized output of a gap audit against the PRD (handoff `CLAUDE_CODE_HANDOFF.md`). Current external-API facts (OpenAI Realtime GA `client_secrets` + `/v1/realtime/calls`, VAD-off manual turns, Responses-API streamed translation with `gpt-5.4` small tier, OpenAI TTS chunk streaming, Deepgram `nova-3`+`multi` .NET SDK + no-transcoding, browser MediaRecorder/AudioWorklet, May-2026 pricing) were verified against current provider documentation during the audit; all values remain configurable and are re-confirmed at build time per §16.
