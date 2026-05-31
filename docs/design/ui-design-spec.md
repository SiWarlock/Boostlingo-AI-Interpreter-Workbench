# AI Interpreter Workbench — UI Design Spec

> Input for a Claude Design pass. Compiled from the real codebase (`web/src/`) so the design maps 1:1 onto the actual components, state shape, and state machine. **Nothing here is aspirational — every component, field, and state below exists and is wired today.** The app is functionally complete; it has **zero CSS**. This spec is the contract a visual design must satisfy.

---

## 1. What the app is

A **single-screen instrumented "workbench" dashboard** for running live speech-interpretation turns in two modes and comparing them on **latency, cost, and quality (WER)**. It is an *evidence/comparison tool*, not a consumer interpreter — the hero content is the **measured numbers**, not the conversation.

- **Two modes:** **Realtime** (OpenAI Realtime over WebRTC) vs **Cascade** (STT → Translation → TTS pipeline). One mode-agnostic UI; the user toggles between them within a session.
- **Language pair:** EN ↔ ES, both directions.
- **A "turn"** = press Start → speak → press Stop → see source + target transcripts, per-stage latency, estimated cost, and hear translated audio.
- **Clean-separation invariant:** every component renders **only** from one state store (`UiSessionState`). No component holds wire/transport detail. So the design is purely a function of the state below.

---

## 2. Component inventory (11 regions)

Render today is a flat vertical stack in this order. Each is a real component in `web/src/components/`.

| # | Region | Purpose | Reads (state) | User interactions |
|---|---|---|---|---|
| 1 | **App header** | Title + **global session status** | `sessionStatus` | — |
| 2 | **Provider health** | Readiness of each provider (config-gating) | `providerHealth` (config) | — |
| 3 | **ModeToggle** | Pick **Realtime** vs **Cascade** | `mode`, `providerHealth`, `turnStatus` | Click a mode (disabled if unconfigured or a turn is in flight) |
| 4 | **SessionSetup** | Label, direction, model selectors, **Start/End session** | `label`, `direction`, `realtimeModel`, `translationModel`, `sessionStatus`, config | Text input, 3 dropdowns, Start session, End session |
| 5 | **RecordingControls** | **Start/Stop recording** a turn | `sessionStatus`, `turnStatus` | Start recording, Stop |
| 6 | **TranscriptPanel** | **Source + target transcripts**, live | `currentTurn.sourceTranscript[]`, `currentTurn.targetTranscript[]` | — (read-only, streams) |
| 7 | **MetricsPanel** | Per-turn latency + **per-stage breakdown** + session averages | `currentTurn.latency`, `summary` | "Refresh summary" button |
| 8 | **CostPanel** | **Estimated $/min** + model + assumptions | `currentTurn.cost` / `estimatedCostPerMinuteUsd` | — (assumptions tooltip) |
| 9 | **EvaluationPanel** | **WER eval** (phrase → record → score) | phrase list + local result | Phrase selector, Record & evaluate |
| 10 | **ComparisonSummary** | **Realtime-vs-Cascade** by mode + **by model variant** | `summary` + persisted turns | — (the "money" view) |
| 11 | **ErrorBanner** | Sanitized errors + actionable copy | `errors[]` | (dismiss, if added) |

---

## 3. The two state machines (this is what the UI must visualize)

### 3a. Session lifecycle — `sessionStatus`
```
idle ──(config loads / first setting change)──▶ configured
configured ──(Start session)──▶ starting ──(backend created)──▶ active
active ◀──▶ readyForTurn        (between turns)
active/readyForTurn ──(End session)──▶ ended
```
| Value | Meaning | UI implication |
|---|---|---|
| `idle` | initial; before config/selection | Start disabled |
| `configured` | config loaded or a setting edited | Start **enabled** |
| `starting` | Start clicked, awaiting backend | Start → spinner/disabled |
| `active` | session running | Recording allowed; **End session** shown |
| `readyForTurn` | between turns | Recording allowed |
| `ended` | session over | Controls disabled / offer reset |

### 3b. Turn lifecycle — `turnStatus`
```
ready ──(Start recording)──▶ recording ──(Stop)──▶ processing ──▶ playing ──▶ completed
                                   └────────────────(error)────────────────▶ failed
```
| Value | Meaning | UI implication |
|---|---|---|
| `ready` | no turn / ready to record | Start recording enabled |
| `recording` | capturing mic | Stop enabled; **mode toggle locked**; pulse/“REC” indicator |
| `processing` | STT→translation→TTS in flight | spinner; toggle locked |
| `playing` | TTS audio playing | playing indicator; toggle locked |
| `completed` | turn done | Start recording re-enabled |
| `failed` | turn errored | error surfaced; Start re-enabled (retry) |

---

## 4. State-driven UI behavior — the "what changes when" (all real, from `selectors.ts`)

1. **Mode availability (config-gating):** a mode is **disabled** unless its providers are configured. **Realtime** needs the realtime key; **Cascade** needs **STT + Translation + TTS all** configured. (Selector: `modeAvailability`.)
2. **Active mode** is tracked via `aria-pressed` today — **needs a visible highlight** (currently invisible — this is the #1 "looks broken" cause).
3. **Mode toggle is locked** during `recording` / `processing` / `playing` (`canToggleMode`). Clicking the already-active mode is a deliberate no-op.
4. **Model selectors** populate from config (`availableModels`): realtime models + translation models. (Catalogs always present; `configured` gates the *mode*, not the list.)
5. **Start session** enabled when `sessionStatus !== idle`; on click → `starting` → `active`, and **End session** appears.
6. **Start recording** enabled only when `sessionStatus ∈ {active, readyForTurn}` **AND** `turnStatus ∈ {ready, completed, failed}` (`canStartRecording`). **Stop** only while `recording` (`canStopRecording`).
7. **Transcripts** stream: a non-final **partial** is replaced in place by later partials, then **finalized**; a new running entry starts after a final. (source + target independently.)
8. **Metrics / Cost** fill in as latency events + cost arrive; **`n/a`** wherever a value is unavailable (never a fake 0). Latency has **per-stage** (STT/translation/TTS) + **top-level** (speech-end→first-audio, total turn).
9. **Errors** render as sanitized `UiError`s with **actionable copy** (mic-denied → re-allow; provider failure → retry/switch-mode).

---

## 5. Data each panel renders (field-level — the design's data model)

**`UiSessionState`** (the whole store):
`sessionId`, `label?`, `mode` (`cascade`|`realtime`), `direction` ({source,target} of `en`/`es`), `realtimeModel` (`gpt-realtime`|`gpt-realtime-mini`), `translationModel` (`gpt-5.4-nano`|`gpt-5.4-mini`), `sessionStatus`, `turnStatus`, `providerHealth?`, `turns[]`, `currentTurn?`, `summary?`, `errors[]`.

**`TurnViewModel`** (`currentTurn` + each in `turns[]`):
`turnId`, `mode`, `direction`, `status`, `startedAt`, `completedAt?`, `audioDurationMs?`, `sourceTranscript[] {text,isFinal}`, `targetTranscript[] {text,isFinal}`, `latency { speechEndToFirstAudioMs?, speechEndToPlaybackMs?, totalTurnMs?, stages? }`, `latencyEvents[]`, `estimatedCostUsd?`, `estimatedCostPerMinuteUsd?`, `translationModelUsed?`, `cost? (full estimate w/ assumptions)`.

**`UiError`** (each in `errors[]`): `code`, `safeMessage`, `stage?`, `retryable`, `turnId?`.

**`summary`** (from backend `GET /summary`): per-mode aggregates (avg latency, avg cost/min, error count, turn count) for **realtime** and **cascade**, plus a **WER summary** ({sampleCount, avgWer}).

---

## 6. Proposed dashboard layout

Today it's a single vertical scroll. A dashboard arrangement that fits the data:

```
┌────────────────────────────────────────────────────────────────────────┐
│ HEADER:  AI Interpreter Workbench        [ session status pill ]         │
│          provider-health chips:  ● Realtime  ● STT  ● Translation  ● TTS │
├──────────────┬─────────────────────────────────────┬────────────────────┤
│ CONTROLS     │ TRANSCRIPTS (the live stage)         │ METRICS            │
│ (left rail)  │ ┌─────────────┬─────────────┐        │ ┌────────────────┐ │
│ • ModeToggle │ │ Source (EN) │ Target (ES) │ live   │ │ This turn:     │ │
│   [Cascade]  │ │ partial→fin │ partial→fin │ stream │ │  speech→audio  │ │
│   [Realtime] │ └─────────────┴─────────────┘        │ │  per-stage:    │ │
│ • SessionSetup                                       │ │   STT/Trans/TTS│ │
│   label,dir, │                                       │ │ Session avg…   │ │
│   models,    │                                       │ ├────────────────┤ │
│   Start/End  │                                       │ │ COST           │ │
│ • Recording  │                                       │ │ Est. $X.XX/min │ │
│   [● Rec][■] │                                       │ │ model + assump │ │
├──────────────┴─────────────────────────────────────┴────────────────────┤
│ COMPARISON SUMMARY (Realtime vs Cascade, by mode AND by model variant)   │
│   latency · cost/min (4 variants) · error counts · WER · turn counts     │
├──────────────────────────────────────────────────────────────────────────┤
│ EVALUATION (WER): phrase selector · reference · Record&Score · WER %      │
├──────────────────────────────────────────────────────────────────────────┤
│ ERROR BANNER (sanitized, actionable) — top toast or inline strip         │
└──────────────────────────────────────────────────────────────────────────┘
```

**Layout principles for the design:**
- **Controls left, live stage center, metrics right** — the operator's eye path: configure → speak → read numbers.
- **Comparison Summary is the deliverable** — give it visual weight (it's what the evaluation is *for*).
- The whole thing is **one operator session**; no routing/multi-page.

---

## 7. Key user flows (step-by-step UI states)

- **A — Load:** mount → `GET /config` → provider-health chips light up; unconfigured modes disabled.
- **B — Configure:** pick mode, direction, models, label → `configured`.
- **C — Cascade turn:** Start session → `active`; Start recording → `recording` (mic, REC indicator, toggle locked) → Stop → `processing` (transcripts stream, per-stage latency fills) → `playing` (ES audio) → `completed` (metrics + cost finalize).
- **D — Realtime turn:** same controls; speech-end→first-audio is the headline metric; cost is input-priced (output disclosed-unavailable).
- **E — Mode toggle (Flow G):** between turns, switch Realtime↔Cascade; active mode re-highlights; next turn runs in the new mode.
- **F — WER eval:** EvaluationPanel → pick scripted phrase → record → WER % + S/I/D; "WER is STT-only" note.
- **G — Comparison:** after turns in both modes, ComparisonSummary shows the by-mode + by-variant grid.

---

## 8. What the current build visually lacks (design priorities)

1. **Active-mode highlight** + selected-state for all toggles/segmented controls (the #1 "is it broken?" fix).
2. **Status pills** for `sessionStatus` + `turnStatus` (color-coded; a live "REC"/"processing"/"playing" indicator).
3. **Enabled/disabled affordances** on every button (Start/Stop/Start-session/End reflect the gating rules in §4).
4. **Streaming transcript treatment** — partials visually distinct from finalized (e.g. dimmed/italic partial → solid final), source vs target columns.
5. **Latency presentation** — per-stage bars/timeline; color vs the targets (Cascade end-to-end **<3s**, Realtime speech→first-audio **<1.5s**).
6. **Cost** — always-qualified "Estimated $X.XX/min" label; model used; assumptions on hover.
7. **Comparison grid** — the by-mode × by-variant table as the visual centerpiece; `n/a` styled, not blank.
8. **Loading / empty / error states** for every panel (config loading, no-turn-yet, sanitized error).
9. Provider-health chips, secure-context/mic-permission prompts, a headset hint.

> Design constraint: **CSS/layout only — no logic changes.** Components already render from the store; a design pass restyles + rearranges them. Keep the component boundaries in §2 (they map to the state in §3–§5).
