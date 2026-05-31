# Boostlingo AI Interpreter Workbench — UX Spec (verbatim brief)

> Source of truth for layout & UX. Reproduced from the project brief. The aesthetic reference image
> (`uploads/Original Image 2048x1536.webp`) informs **visual style only**, not this layout.

## 1. What the app is

A **single-screen instrumented "workbench" dashboard** for running live speech-interpretation turns
in two modes and comparing them on **latency, cost, and quality (WER)**. It is an
*evidence/comparison tool*, not a consumer interpreter — the hero content is the **measured numbers**.

- **Two modes:** **Realtime** (OpenAI Realtime over WebRTC) vs **Cascade** (STT → Translation → TTS).
  One mode-agnostic UI; the user toggles between them within a session.
- **Language pair:** EN ↔ ES, both directions.
- **A "turn"** = press Start → speak → press Stop → see source + target transcripts, per-stage
  latency, estimated cost, and hear translated audio.
- **Clean-separation invariant:** every component renders **only** from one state store
  (`UiSessionState`). No component holds wire/transport detail.

## 2. Component inventory (11 regions)

1. **App header** — Title + global session status. Reads `sessionStatus`.
2. **Provider health** — Readiness of each provider (config-gating). Reads `providerHealth`.
3. **ModeToggle** — Pick Realtime vs Cascade. Reads `mode`, `providerHealth`, `turnStatus`.
4. **SessionSetup** — Label, direction, model selectors, Start/End session.
5. **RecordingControls** — Start/Stop recording a turn.
6. **TranscriptPanel** — Source + target transcripts, live.
7. **MetricsPanel** — Per-turn latency + per-stage breakdown + session averages.
8. **CostPanel** — Estimated $/min + model + assumptions.
9. **EvaluationPanel** — WER eval (phrase → record → score).
10. **ComparisonSummary** — Realtime-vs-Cascade by mode + by model variant. (the "money" view)
11. **ErrorBanner** — Sanitized errors + actionable copy.

## 3. State machines

### 3a. Session lifecycle — `sessionStatus`
`idle → configured → starting → active ⇄ readyForTurn → ended`
- `idle` — Start disabled
- `configured` — Start enabled
- `starting` — Start → spinner/disabled
- `active` — recording allowed; End session shown
- `readyForTurn` — between turns
- `ended` — controls disabled / offer reset

### 3b. Turn lifecycle — `turnStatus`
`ready → recording → processing → playing → completed` (or `→ failed` on error)
- `ready` — Start recording enabled
- `recording` — Stop enabled; mode toggle locked; pulse/REC
- `processing` — spinner; toggle locked
- `playing` — playing indicator; toggle locked
- `completed` — Start recording re-enabled
- `failed` — error surfaced; Start re-enabled (retry)

## 4. State-driven UI behavior (from selectors.ts)

1. **Mode availability:** mode disabled unless its providers configured. Realtime needs realtime key;
   Cascade needs STT + Translation + TTS all configured. (`modeAvailability`)
2. **Active mode** tracked via `aria-pressed` today — **needs a visible highlight** (#1 "looks broken").
3. **Mode toggle locked** during recording/processing/playing (`canToggleMode`).
4. **Model selectors** populate from config (`availableModels`).
5. **Start session** enabled when `sessionStatus !== idle`; click → starting → active; End appears.
6. **Start recording** enabled only when `sessionStatus ∈ {active, readyForTurn}` AND
   `turnStatus ∈ {ready, completed, failed}`. Stop only while `recording`.
7. **Transcripts** stream: non-final partial replaced in place → finalized; new running entry after.
8. **Metrics / Cost** fill in as events arrive; **n/a** wherever unavailable (never fake 0).
   Latency has per-stage (STT/translation/TTS) + top-level (speech-end→first-audio, total turn).
9. **Errors** render as sanitized `UiError`s with actionable copy.

## 5. Data model (field level)

**`UiSessionState`:** `sessionId`, `label?`, `mode` (cascade|realtime),
`direction` ({source,target} of en/es), `realtimeModel` (gpt-realtime|gpt-realtime-mini),
`translationModel` (gpt-5.4-nano|gpt-5.4-mini), `sessionStatus`, `turnStatus`, `providerHealth?`,
`turns[]`, `currentTurn?`, `summary?`, `errors[]`.

**`TurnViewModel`:** `turnId`, `mode`, `direction`, `status`, `startedAt`, `completedAt?`,
`audioDurationMs?`, `sourceTranscript[] {text,isFinal}`, `targetTranscript[] {text,isFinal}`,
`latency { speechEndToFirstAudioMs?, speechEndToPlaybackMs?, totalTurnMs?, stages? }`,
`latencyEvents[]`, `estimatedCostUsd?`, `estimatedCostPerMinuteUsd?`, `translationModelUsed?`, `cost?`.

**`UiError`:** `code`, `safeMessage`, `stage?`, `retryable`, `turnId?`.

**`summary`:** per-mode aggregates (avg latency, avg cost/min, error count, turn count) for realtime
and cascade, plus WER summary ({sampleCount, avgWer}).

## 6. Proposed dashboard layout

```
HEADER:  AI Interpreter Workbench        [ session status pill ]
         provider-health chips:  ● Realtime  ● STT  ● Translation  ● TTS
─────────────┬────────────────────────────────┬──────────────────
CONTROLS     │ TRANSCRIPTS (live stage)        │ METRICS
(left rail)  │  Source (EN) | Target (ES)      │  This turn: speech→audio
• ModeToggle │  partial→final  partial→final   │  per-stage STT/Trans/TTS
• SessionSetup                                 │  Session avg…
• Recording                                    │ COST  Est. $X.XX/min
─────────────┴────────────────────────────────┴──────────────────
COMPARISON SUMMARY (Realtime vs Cascade, by mode AND by model variant)
  latency · cost/min (4 variants) · error counts · WER · turn counts
──────────────────────────────────────────────────────────────────
EVALUATION (WER): phrase selector · reference · Record&Score · WER %
──────────────────────────────────────────────────────────────────
ERROR BANNER (sanitized, actionable) — top toast or inline strip
```

Principles: Controls left, live stage center, metrics right (configure → speak → read numbers).
Comparison Summary is the deliverable — give it weight. One operator session; no routing/multi-page.

## 7. Key flows
- **A Load:** mount → GET /config → provider-health chips light; unconfigured modes disabled.
- **B Configure:** mode, direction, models, label → configured.
- **C Cascade turn:** Start session → active; Start recording → recording (mic, REC, toggle locked)
  → Stop → processing (transcripts stream, per-stage latency fills) → playing (ES audio) → completed.
- **D Realtime turn:** same controls; speech-end→first-audio is headline; cost input-priced.
- **E Mode toggle:** between turns switch Realtime↔Cascade; active re-highlights.
- **F WER eval:** pick scripted phrase → record → WER % + S/I/D; "WER is STT-only".
- **G Comparison:** after turns in both modes, by-mode + by-variant grid.

## 8. Design priorities (what current build lacks)
1. Active-mode highlight + selected-state for all toggles.
2. Status pills for sessionStatus + turnStatus (color-coded; live REC/processing/playing).
3. Enabled/disabled affordances on every button.
4. Streaming transcript treatment (partial vs final; source vs target).
5. Latency presentation (per-stage bars/timeline; color vs targets: Cascade <3s, Realtime <1.5s).
6. Cost — always-qualified "Estimated $X.XX/min"; model used; assumptions on hover.
7. Comparison grid — by-mode × by-variant; n/a styled, not blank.
8. Loading / empty / error states for every panel.
9. Provider-health chips, secure-context/mic prompts, headset hint.

> Design constraint: **CSS/layout only — no logic changes.**
