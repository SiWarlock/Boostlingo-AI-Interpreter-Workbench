# DIAGRAM_PLAN.md — AI Interpreter Workbench

> **Status:** Diagram planning artifact derived from `ARCHITECTURE.md`.
>
> **Purpose:** Define diagrams that clarify system shape, data flow, trust boundaries, and implementation seams.

---

## Full-Scope Architecture Diagram

### Purpose

Show the entire MVP architecture in one view so a reviewer can understand how the browser, backend, providers, metrics, evaluation, and persistence connect.

### Must Show

- Browser SPA.
- Audio capture and playback.
- Realtime WebRTC client.
- Cascade API client.
- .NET backend.
- Realtime client-secret broker.
- Cascade orchestrator.
- Provider interfaces.
- Deepgram STT.
- OpenAI translation.
- OpenAI TTS.
- OpenAI Realtime.
- Metrics normalizer.
- Cost estimator.
- WER evaluator.
- JSON session persistence.
- Trust boundary: browser ↔ backend ↔ providers.
- Secret boundary: standard API keys only on backend; ephemeral credential to browser.

### Spec Anchors

- `ARCH-004`
- `ARCH-007`
- `ARCH-008`
- `ARCH-010`
- `ARCH-011`
- `ARCH-019`

### Recommended Format

Excalidraw-style or Mermaid architecture diagram.

---

## Sub-Diagram 1 — Realtime Mode Sequence

### Purpose

Show how Realtime mode works with browser WebRTC and backend ephemeral credential.

### Must Show

1. Browser requests client secret from backend.
2. Backend uses standard OpenAI key server-side.
3. Browser connects to OpenAI Realtime via WebRTC.
4. Browser sends/receives audio/events.
5. Browser/backend persist normalized metrics.

### Spec Anchors

- `ARCH-010`
- `ARCH-013`
- `ARCH-019`

### Priority

High.

---

## Sub-Diagram 2 — Cascade Pipeline Sequence

### Purpose

Show STT → Translation → TTS execution and stage-level metrics.

### Must Show

1. Browser sends recorded turn to backend.
2. Backend calls `ISttProvider` / Deepgram.
3. Backend calls `ITranslationProvider` / OpenAI.
4. Backend calls `ITtsProvider` / OpenAI.
5. Metrics emitted at each stage.
6. Result returned to browser for playback.
7. Session JSON persistence.

### Spec Anchors

- `ARCH-011`
- `ARCH-012`
- `ARCH-013`
- `ARCH-016`

### Priority

High.

---

## Sub-Diagram 3 — Domain Model / Session Lifecycle

### Purpose

Clarify core entities and lifecycle states.

### Must Show

- `InterpretationSession`.
- `InterpretationTurn`.
- `TranscriptSegment`.
- `LatencyEvent`.
- `CostEstimate`.
- `WerResult`.
- Session lifecycle.
- Turn lifecycle.

### Spec Anchors

- `ARCH-005`
- `ARCH-016`
- `ARCH-017`

### Priority

Medium.

---

## Sub-Diagram 4 — Metrics and Comparison Model

### Purpose

Show how different provider events become comparable metrics.

### Must Show

- Realtime events.
- Cascade stage events.
- Normalized `LatencyEvent` schema.
- Metrics aggregator.
- Comparison summary.
- JSON session output.

### Spec Anchors

- `ARCH-013`
- `ARCH-014`
- `ARCH-016`

### Priority

High.

---

## Sub-Diagram 5 — Security / Trust Boundary Diagram

### Purpose

Show where secrets live and what crosses boundaries.

### Must Show

- Browser receives only ephemeral OpenAI credential.
- Backend owns standard provider API keys.
- Backend calls OpenAI/Deepgram.
- Session JSON excludes raw audio and secrets.
- External provider boundary.

### Spec Anchors

- `ARCH-019`
- `ARCH-010`
- `ARCH-016`

### Priority

Medium-high.

---

## Sub-Diagram 6 — Testing Architecture

### Purpose

Show how fake providers allow deterministic cascade tests.

### Must Show

- Cascade orchestrator.
- Provider interfaces.
- Fake STT/translation/TTS providers.
- WER calculator.
- Cost estimator.
- Persistence writer.
- Test suite boundaries.

### Spec Anchors

- `ARCH-012`
- `ARCH-020`

### Priority

Medium.

