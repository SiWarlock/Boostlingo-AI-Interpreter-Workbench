# AI Interpreter Workbench — Platform Overview

> A guided tour of how this platform works: what it is, how it's built, how data flows through it, and what it's like to use. Read the **Executive Summary** for the whole picture in two minutes; the numbered sections go deeper without losing the plot. For the binding, anchored design contract see `ARCHITECTURE.md`; for the everyday "get it running" steps see `docs/runbooks/local-dev-and-test-runbook.md`; for the measured head-to-head results see `docs/COMPARISON_WRITEUP.md`.

**Audience:** an engineer, reviewer, or technical stakeholder who wants to understand the system end to end.

---

## Executive Summary

The AI Interpreter Workbench is a **browser-based architecture-evaluation tool**. It builds the two dominant ways to do live AI speech interpretation, runs them side by side through the same UI, and **measures them head-to-head** — latency, cost, and transcription quality — so a team can decide which approach fits which use case.

It is **not** a production interpreter. Its *product is evidence*: real measured numbers from a real working build of both approaches.

The two approaches it pits against each other:

- **Realtime mode** — one vertically-integrated voice-to-voice model (OpenAI's Realtime API, `gpt-realtime`), reached directly from the browser over **WebRTC**. You speak; it speaks the translation. Fast, conversational, but a black box — there are no internal stages to inspect.
- **Cascade mode** — a **composable, fully streaming pipeline** of three swappable services: **Deepgram** transcribes speech → **OpenAI** translates the text → **OpenAI TTS** speaks the translation. Slower, but every stage is observable and any vendor can be swapped behind a clean interface.

The whole system in one line: **one UI, two transports, one normalized session + metrics model, one persisted evidence trail, one comparison write-up.**

**What the measurements found** (from a live 5-minute run of each mode):

| | Realtime | Cascade |
|---|---|---|
| Speech-end → first audio | ~0.67 s | ~1.9 s (**~2.9× slower**) |
| Cost / minute | ~$0.24, **and it climbs** through a session | ~$0.012, **flat** (**~20× cheaper**) |
| Word error rate (synthetic audio) | 0.174 | 0.025 |
| 5-minute stability | clean (no disconnect / drift / leak) | clean |

The headline tradeoff: **Realtime buys responsiveness at a steep, *compounding* cost** (it re-bills the entire growing conversation every turn), while **Cascade is cheaper, flatter, more observable, and provider-flexible** — but slower. The rest of this document explains how the platform is built to produce those numbers honestly.

---

## 1. The Big Picture — One UI, Two Transports, One Evidence Trail

The guiding principle is **fair comparison**: every design choice exists to make the two modes measurable on the same terms. The system is deliberately split so that the *only* thing that differs between a Realtime turn and a Cascade turn is the interpretation transport itself — everything around it (session model, metrics schema, cost estimator, persistence, UI) is shared.

```
                          ┌─────────────────────────────────────────────┐
                          │   Browser SPA (React + Vite + TypeScript)    │
                          │   mic capture · playback · both transports · │
                          │   all rendering (one mode-agnostic UI)       │
                          └───────────────┬──────────────┬──────────────┘
                  Realtime path           │              │      Cascade path
            (browser ↔ OpenAI direct)     │              │  (fully streamed through backend)
                          ┌───────────────┘              └───────────────┐
                          ▼                                              ▼
              ┌───────────────────────┐                  ┌──────────────────────────────┐
              │  OpenAI Realtime API   │                  │   .NET Backend (ASP.NET Core)  │
              │  gpt-realtime (WebRTC) │                  │   cascade orchestrator +       │
              └───────────┬───────────┘                  │   provider abstractions        │
                          │                              └───┬──────────┬─────────┬───────┘
        backend mints an ephemeral                          ▼          ▼         ▼
        credential so the real key                      Deepgram    OpenAI    OpenAI
        never reaches the browser ──────►               STT(nova-3) translate  TTS
                          │                              (gpt-5-nano/mini) (gpt-4o-mini-tts)
                          ▼
              ┌─────────────────────────────────────────────────────────────────┐
              │  Shared evidence layer (backend): one LatencyEvent schema,        │
              │  cost estimator, WER utility, local JSON session files            │
              │  (no raw audio, no secrets) → the comparison write-up             │
              └─────────────────────────────────────────────────────────────────┘
```

The architecture principle, stated as a tradeoff the build is designed to expose:

```
Realtime = less app orchestration, fewer stage-level signals, lower latency, more vendor lock-in.
Cascade  = more moving parts, more observability/control, more provider flexibility, more latency.
```

**Three major subsystems** do the work:

1. **A TypeScript SPA** — owns the microphone, audio playback, *both* transports (the WebRTC client and the cascade WebSocket client), and every pixel of the UI.
2. **A .NET/C# backend** — owns secrets, the ephemeral-credential broker, the streaming cascade orchestrator, the provider abstractions (with real + fake implementations), metrics normalization, cost estimation, WER scoring, and JSON persistence.
3. **The evidence layer** — a shared latency-event schema, a config-driven cost estimator, a scripted WER utility, and local JSON session files. This is the part that turns "it works" into "here are the numbers."

---

## 2. The Two Interpretation Modes (the heart of the system)

### 2a. Realtime mode — one model, voice to voice

Realtime mode hands the entire job to a single OpenAI model that takes speech in and produces speech out. The browser talks to OpenAI **directly** over a WebRTC peer connection — audio never round-trips through our backend.

**The data flow of one realtime turn:**

1. The browser asks our backend for a short-lived credential: `POST /api/realtime/client-secret`.
2. The backend calls OpenAI (`POST /v1/realtime/client_secrets`) using the **standard API key**, and returns only a **15-minute ephemeral token** (`ek_…`). *The real key never leaves the server.*
3. The browser opens an `RTCPeerConnection`, adds the mic track, and a data channel named `oai-events`, then completes the SDP handshake directly with OpenAI.
4. You speak. Translated **audio comes back on the media track** and plays. **Events** (transcripts, token usage, turn boundaries) arrive as JSON on the `oai-events` data channel.
5. The browser normalizes those events and reports them to the backend (`POST …/turns/{id}/events` and `…/complete`) so the turn — its latency stamps, transcripts, and cost — gets persisted.

**What you can observe:** end-to-end latency, the source + target transcripts (OpenAI returns input transcription too), and exact token usage for cost. **What you can't:** internal stages — there's no separate "transcription" or "translation" step to time or swap. That opacity is the point of the comparison.

The interpreter behavior is set entirely by the **instruction prompt** baked into the minted session (server-side). A late-2026 hardening lesson: a gentle "translate, don't comment" instruction isn't enough — the model will answer a direct question ("What is your name?") instead of translating it, so the instruction is now an emphatic *conduit* framing with a target-language lock and explicit "translate the question, never answer it."

### 2b. Cascade mode — three streaming stages you can see

Cascade mode rebuilds interpretation out of three independent, swappable services, wired together as a **fully streaming** pipeline (no waiting for a complete utterance before starting the next stage).

**The data flow of one cascade turn:**

1. The browser captures mic audio, converts it to **linear16 PCM** in an `AudioWorklet`, and streams ~20–50 ms binary frames over a WebSocket: `WS /api/cascade/stream`.
2. The backend's **`CascadeStreamingOrchestrator`** forwards audio to **Deepgram** (`nova-3`, `language=multi`), which streams back interim and final transcript segments.
3. For **each finalized source segment**, the orchestrator streams it to **OpenAI translation** (`gpt-5-nano` by default, `gpt-5-mini` switchable) and consumes the translated tokens as they stream.
4. The translated text streams into **OpenAI TTS** (`gpt-4o-mini-tts`), which streams back audio chunks.
5. Throughout, the backend streams a typed event feed back to the browser over the same WebSocket: source/target transcript partials and finals, per-stage `LatencyEvent`s, TTS audio chunks, the cost estimate, any errors, and a final `done`.

**What you can observe:** *everything* — per-stage timings (`stt.final`, `translation.final`, `tts.first_audio`), which exact models ran, and a stage-by-stage cost. If a turn is slow or wrong, you can see precisely where. And because every stage sits behind an interface (`ISttProvider`, `ITranslationProvider`, `ITtsProvider`), swapping Deepgram for another STT vendor — or adding a new language pair — is a configuration change, not a rewrite.

**Streaming honesty is a hard rule here:** the backend never fabricates a stage boundary or back-dates a timestamp. Each `first_*` marker is stamped on the *real* first arrival from the provider stream. A buffered single response (the documented blob fallback) is explicitly labeled as *not* streaming.

**A note on the contrast:** the same spoken sentence might come back in ~0.67 s on Realtime and ~1.9 s on Cascade — but Cascade tells you it spent, say, 300 ms in STT, 200 ms in translation, and 400 ms to first TTS audio, and lets you price each. That observability-vs-speed tension is exactly what the workbench exists to quantify.

---

## 3. Architecture — The Layers and Components

### 3a. Frontend — the TypeScript SPA (`web/`)

The SPA is strictly layered, with a one-directional dependency rule: **components render only from a single store; they never import transport internals.**

```
Components (render only from store state)
  → state/sessionStore  (UiSessionState — the one source of UI truth)
    → api/ + transport clients (cascadeStreamClient, realtimeWebRtcClient)
      → audio/ capture + playback controllers
  → types/  (shared contracts, importable anywhere)
```

Key pieces:

- **`sessionStore`** — a minimal external store holding `UiSessionState` (mode, direction, models, session/turn status, the list of turns, the live summary, and an errors list). It is the **single error sink** and the single place the UI reads from.
- **Transport clients** — `realtimeWebRtcClient` (the WebRTC peer connection + `oai-events` data channel) and `cascadeStreamClient` (the WebSocket). Each owns all of its wire detail and feeds normalized results into the store.
- **`audio/`** — `audioCaptureController` (the streaming PCM path + a `MediaRecorder` blob fallback), `pcmWorklet` (Float32 → linear16 conversion off the main thread), and `playbackController` (progressive MSE playback with a blob fallback).
- **Components** — `SessionSetup`, `ModeToggle`, `RecordingControls`, `TranscriptPanel`, `MetricsPanel`, `CostPanel`, `EvaluationPanel`, `ComparisonSummary`, `ErrorBanner`. Each is a thin renderer that reads the store and dispatches intents.

The UI carries a **load-bearing visual contract**: Realtime is always blue, Cascade always violet — through the toggle, the status accents, and the comparison — so the two modes are never visually confused.

The frontend **must not** own: provider API keys, the authoritative persisted session state, the cost pricing source of truth, or the canonical WER calculation. Those live on the backend by design.

### 3b. Backend — the .NET API (`server/`)

Also strictly layered, with the rule that **provider logic never lives in a controller**:

```
Controllers / WebSocket endpoint
  → Application services (orchestrators, SessionStore, SessionSummaryService, EvaluationService)
    → Provider abstractions (ISttProvider / ITranslationProvider / ITtsProvider)
      → Provider implementations (Deepgram / OpenAI / Fakes)
  → Utilities (Metrics, Cost, Evaluation/WER, Persistence, ErrorSanitizer, Common)
```

Key services:

| Service | Responsibility |
|---|---|
| `SessionStore` | In-memory active session state |
| `SessionPersistenceWriter` | Writes session JSON (after each turn + on end) |
| `SessionSummaryService` | Computes the `SessionSummary` on demand (pure — never writes) |
| `RealtimeClientSecretService` | Mints OpenAI Realtime ephemeral credentials |
| `CascadeStreamingOrchestrator` | Drives the live STT→translation→TTS stream over the WebSocket |
| `CascadeOrchestrator` | The blob-fallback path (pre-recorded STT → streamed translation → TTS) |
| `MetricsAggregator` | Latency/stage summaries from absolute-timestamp math |
| `CostEstimator` | Model-aware estimated costs (branches on pricing basis) |
| `WerCalculator` / `EvaluationService` | Deterministic WER scoring |
| `ErrorSanitizer` | Internal/provider errors → safe `UiError` / `ProviderError` |
| `ConfigService` | Reports which capabilities are configured (from key *presence*, never values) |

**The provider abstraction is the spine of cascade flexibility.** Each stage is an interface emitting normalized streaming events; there's a real implementation (Deepgram / OpenAI) *and* a fake. The fakes are what make the cascade orchestrator, error mapping, and metrics testable without ever calling a live API — the entire deterministic core is unit-tested against fakes, and real-provider calls are exercised only in manual smoke runs.

State is **in-memory + local JSON files — no database**. The session is created in memory on `POST /api/sessions`, each turn is appended as it completes, and JSON is written best-effort after every turn and (as a MUST) on session end. A failed write keeps the session running and surfaces a `persistence.failed` warning rather than crashing the turn.

### 3c. The boundary — who owns what

| The frontend owns | The backend owns |
|---|---|
| Mic capture, playback, the two transports | Secrets + the ephemeral-credential mint |
| All interactive UI state + rendering | The authoritative session + turn state |
| Reporting realtime turn events | The cascade pipeline orchestration |
| | The cost pricing config + the WER calculation |
| | Metrics normalization + JSON persistence |

A clean line: anything secret, authoritative, or comparison-defining lives on the backend; anything interactive or device-bound lives in the browser.

---

## 4. Data Flow — Following a Turn End to End

A **session** contains many **turns**; a turn is one recorded input→output cycle. Both flow through the same normalized model so the two modes stay comparable.

### 4a. A realtime turn, step by step

```
Browser ──POST /api/realtime/client-secret──► Backend ──POST /v1/realtime/client_secrets──► OpenAI
Browser ◄──── ek_… ephemeral token ──────────  Backend  (standard sk-… key stays server-side)
Browser ══WebRTC (mic track)══════════════════════════════════════════════════════════════► OpenAI
Browser ◄═══════════════════════ translated audio track + oai-events (JSON) ════════════════ OpenAI
Browser ──normalized events / complete──► Backend ──► in-memory turn ──► JSON persistence
```

The browser is the timing authority for realtime (it stamps `recording.started/stopped`, first-audio, playback). It forwards the exact `response.done.usage` token counts so the backend can price the turn precisely.

### 4b. A cascade turn, step by step

```
Browser mic → AudioWorklet (linear16 PCM) ══WebSocket binary frames══► Backend orchestrator
                                                                          │
                                          ┌───────────────────────────────┘
                                          ▼
                              Deepgram live WS (interim + final source transcript)
                                          │   on each finalized segment ▼
                                          │              OpenAI translation (streamed target tokens)
                                          │                          │ streamed text ▼
                                          │                                OpenAI TTS (streamed audio)
                                          ▼
Backend ══WebSocket event stream══► Browser  (source/target partials, per-stage LatencyEvents,
                                              TTS audio chunks, cost, errors, done)
Backend ──► in-memory turn (per segment) ──► JSON persistence (per turn + on end)
```

The backend is the timing authority for cascade (it can see each provider stage), and it computes per-stage durations from absolute timestamps — cross-clock differences are *disclosed*, never silently clamped.

### 4c. Where the data lands — the session model + persistence

Every turn — regardless of mode — is normalized into the same records (camelCase JSON on the wire and on disk):

- **`InterpretationSession`** → id, label, config (mode + direction + provider profile), a list of **`InterpretationTurn`**s, mode-transition events, and a computed **`SessionSummary`**.
- **`InterpretationTurn`** → mode, direction, status, transcripts, **`LatencyEvent[]`**, a **`CostEstimate`**, an optional **`WerResult`**, and errors.
- **`SessionSummary`** → per-mode **`ModeSummary`** (average latencies, cost/min, error count, and — cascade only — per-stage averages) plus a **`WerSummary`**. The summary is computed *on demand* and snapshotted on end; `ComputedAt` disambiguates staleness.

The session lifecycle is an explicit state machine — `Idle → Configured → Starting → Active → ReadyForTurn ⇄ (turn cycle) → Ending → Ended` — with a nested turn machine (`Ready → Recording → Captured → Processing → Playing → Completed`, or `→ Failed`). Mode switching is forbidden mid-turn and records a transition event between turns, so by-mode comparison stays valid.

**Persistence is deliberately minimal and inspectable:** local JSON under `data/sessions/`, holding transcripts, metrics, costs, and errors — **never raw audio, never any key, never the ephemeral token**. You can open a session file in a text editor and read the entire evidence trail.

---

## 5. The Evidence Layer — How It Measures (the actual product)

This is what separates the workbench from a demo. Three measurement systems, plus a posture.

**Latency** — a single shared `LatencyEvent` schema records named, stamped events (`stt.final`, `tts.first_audio`, `realtime.first_audio_delta`, `playback.started`, …) on a known clock. The headline metric is **speech-end → first audio** (responsiveness), reported for both modes; Cascade additionally reports **per-stage** durations. All aggregation is done from *absolute timestamps*, so it's safe across the browser/server clock split. Unavailable metrics show **"n/a"**, never a fabricated zero.

**Cost** — a config-driven estimator (`config/pricing.json`, versioned) prices each turn by its **pricing basis** (audio-minutes, tokens, characters). It's clearly labeled an *estimate*, not billing-grade. A subtle but load-bearing finding lives here: **Realtime re-bills the entire accumulating conversation context every turn**, so its per-turn cost *climbs* through a session, while Cascade is stateless and flat. (An earlier version over-counted Realtime by ~1.5× because it priced cached audio tokens at the full rate; that was found via a live run and corrected — cached audio is now priced at the discounted cached rate.)

**WER (Word Error Rate)** — a deterministic backend Levenshtein utility scores STT transcript accuracy against **scripted reference phrases**. It is explicitly **STT quality only**, *not* a measure of translation quality. The standalone Evaluation panel uses a **push-to-talk** capture (countdown → "recording" → you read the phrase → stop), and an empty/silent capture honestly reports **"no speech detected — n/a"** rather than a misleading 100% error.

**The comparison summary** aggregates all of the above by mode and by model variant — the deliverable that answers "which approach, when."

**The honest-numbers posture** runs through everything: estimates are labeled as estimates; unmeasurable values are disclosed as "n/a" or "unavailable," never invented; cross-clock skew is disclosed, not hidden; and a degraded turn says so. The whole point is *trustworthy* evidence.

---

## 6. User Experience — What It's Like to Use

**Setting up.** You open the app, give the session a label, pick a **direction** (English → Spanish or Spanish → English), choose a **mode** (Realtime or Cascade), and pick the **model variants** (which realtime model, which translation model). Modes whose provider keys aren't configured are automatically disabled — the UI reads capabilities from `GET /api/config`.

**Taking a turn.** Press **Start recording**, speak, press **Stop** — the click delimits the turn while audio streams within it. The translated speech plays back, and the **transcript panel** shows source and target text *as it's produced* (partials replaced by finals), not just at the end.

**Hands-free options.** Beyond manual click-to-talk, there's **Auto-VAD** (voice-activity detection ends the turn automatically when you stop speaking) and **Bidirectional** mode (it auto-detects which language you spoke and translates to the other, flipping direction per turn with a badge) — so two people can converse without touching the controls.

**The panels** give you the live evidence as you go:
- **Transcript** — source + target, streaming.
- **Metrics** — current-turn latency; for Cascade, the per-stage breakdown; session averages by mode.
- **Cost** — estimated cost/min (always labeled "estimated"), per turn and per session, with the model shown.
- **Comparison summary** — the apples-to-apples latency/cost/WER across both modes once you've run turns in each.
- **Evaluation/WER** — the push-to-talk STT-accuracy check against scripted phrases.
- **Error banner** — actionable, sanitized messages ("Microphone permission denied — enable mic access and retry"), never a stack trace.

**Errors are friendly by construction.** A failed turn degrades honestly (an `n/a` is honest degrade, not a bug); the user never sees raw provider text or secrets.

**The soak harness (dev only).** Open the app with `?soak=1` and you get a panel that drives a scripted 5-minute synthetic EN↔ES conversation through the *real* pipeline for a chosen mode — no microphone needed, because synthetic audio is injected at the capture boundary. It produces a `SoakReport` with three stability checks (no disconnect / no drift / no memory leak) plus aggregate latency, cost, and WER. This is how the 5-minute stability numbers in the comparison were produced.

---

## 7. Safety, Privacy & Trust Boundaries

Five invariants are enforced (each backed by a test), because this system handles live audio and real provider keys:

1. **Standard provider keys are server-side only.** The OpenAI and Deepgram keys live only in backend config/env — never in `web/`, never in any persisted JSON. The browser only ever receives the short-lived ephemeral Realtime credential.
2. **The ephemeral Realtime credential is never persisted.** It exists only in the browser's live WebRTC session.
3. **Raw audio is never persisted.** Session JSON holds transcripts, metrics, and errors only.
4. **Provider errors are sanitized before reaching the UI.** No stack traces, no secrets, no raw provider payloads cross the boundary — only a normalized error with a safe message and a code.
5. **Session file paths derive only from a server-generated id** (matching a strict allowlist and resolved under the data directory — a path-traversal guard), and audio uploads are size- and content-type-validated before any provider call.

A useful mental model: the backend is a **trust boundary**. Secrets and authoritative state stay behind it; only sanitized, non-sensitive data crosses to the browser. The cascade WebSocket additionally validates its `Origin` (a WS upgrade bypasses CORS), and the WER endpoint caps hypothesis length before allocating its scoring matrix (a denial-of-service guard).

---

## 8. How It's Built & Run

**Stack.** Backend: .NET 8 / C# 12, ASP.NET Core, immutable `record` domain types, xUnit. Frontend: Node 22 / TypeScript 5, React 19 + Vite, Vitest. Both run strict typing (nullable-as-errors on the backend; `tsc` strict, no `any` on exported surfaces on the frontend).

**Run it locally.** Backend on `:5179`, frontend on `:5173` (the Vite dev proxy forwards `/api` — REST + the cascade WebSocket — to the backend). Provider keys come from a repo-root `.env`, auto-loaded at startup. Full step-by-step, smoke tests, and troubleshooting are in **`docs/runbooks/local-dev-and-test-runbook.md`**.

**Testing posture.** Deterministic code is **test-first (TDD)**: the cascade orchestrator, provider boundaries (against fakes), error→`ProviderError` mapping, metrics math, cost estimation, WER, and persistence (including the no-secret/no-audio sentinel checks). Inherently non-deterministic surfaces — browser mic/WebRTC/playback internals, real-provider network calls, and LLM output *quality* — are covered by manual smoke runs and the soak harness, never by brittle unit assertions. Real provider APIs are never called in tests; the fakes stand in.

---

## 9. Where to Go Deeper

| You want… | Read… |
|---|---|
| The binding, anchored design contract | `ARCHITECTURE.md` (cite the `ARCH-###` anchors) |
| The measured head-to-head results + recommendation | `docs/COMPARISON_WRITEUP.md` |
| How to start + smoke-test locally | `docs/runbooks/local-dev-and-test-runbook.md` |
| Frontend conventions + the contract-model map | `web/CLAUDE.md` (+ `web/LESSONS.md` for the "why" behind each pattern) |
| Backend conventions + the contract-model map | `server/CLAUDE.md` (+ `server/LESSONS.md`) |
| The phase-by-phase build state | `MVP_TASKS.md` |

**The one-sentence takeaway:** the AI Interpreter Workbench builds Realtime and Cascade interpretation side by side behind one UI, instruments both with a shared, honest evidence layer, and persists an inspectable trail — so the question "vertically-integrated voice model vs composable pipeline?" gets answered with measurements instead of opinions.
