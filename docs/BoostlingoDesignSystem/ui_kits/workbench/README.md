# Workbench UI kit

A high-fidelity, interactive recreation of the **AI Interpreter Workbench** — the single-screen
instrumented dashboard for comparing **Realtime** vs **Cascade** speech interpretation on latency,
cost, and quality (WER). Built to the layout & state contract in `../../UX_SPEC.md` and the visual
foundations in `../../README.md`.

> This is a **UI kit** — a cosmetic, mainly-visual recreation. The state machine + turn flow are
> faked in `store.jsx`; there's no real audio/network. The component boundaries map 1:1 to the 11
> regions in the spec so the styling transfers cleanly onto the real app.

## Run
Open `index.html`. Use the **Demo states** bar to jump between canonical states
(Idle · Recording · Streaming · Completed · Comparison · Error), or just press **Start recording**
to run a live simulated turn (transcripts stream in, per-stage latency fills, cost finalizes, the
turn lands in the comparison grid). Switch modes between turns; the toggle locks mid-turn.

## Files
| File | Role |
|---|---|
| `index.html` | Loads React + Babel + all scripts in order. |
| `workbench.css` | All kit styles (consumes tokens from `../../colors_and_type.css`). |
| `store.jsx` | Mock `UiSessionState` store + the two state machines + turn simulation + helpers (`fmtLatency`, `latClass`, `werClass`, `computeSummary`). Exposes `window.useWorkbench()` + `window.WB`. |
| `scenarios.jsx` | `window.wbScenario(name, prev)` — canonical demo states. |
| `icons.jsx` | Inlined Lucide (MIT) icon set → `window.Icon`. |
| `components.jsx` | Primitives (`Card`, `StatusPill`, `Button`, `Eyebrow`) + Header, ProviderChips, ModeToggle, SessionSetup, RecordingControls. |
| `panels.jsx` | TranscriptPanel, MetricsPanel, CostPanel, ComparisonSummary, EvaluationPanel, ErrorToasts. |
| `app.jsx` | Composes the 3-column shell + comparison/eval bands + demo switcher. |

## Components ↔ spec regions
Header + status pill (1) · ProviderChips (2) · ModeToggle (3) · SessionSetup (4) ·
RecordingControls (5) · TranscriptPanel (6) · MetricsPanel (7) · CostPanel (8) ·
EvaluationPanel (9) · ComparisonSummary (10) · ErrorToasts (11).

## State visualization (the point of the design)
- **Active-mode highlight** — segmented control fills with the mode's color + ring (blue / violet).
  Locked (dimmed, non-interactive) during recording/processing/playing.
- **Status pills** — color-coded for `sessionStatus` + `turnStatus`, with live indicators
  (REC pulse / spinner / equalizer).
- **Enabled/disabled** — every button reflects the gating selectors (`canStartRecording`, etc).
- **Streaming transcript** — partials dimmed + italic with a caret; finalize → solid; EN / ES columns.
- **Latency vs target** — headline mono number colored good/warn/over; per-stage segmented bar (Cascade).
- **Cost** — always "Estimated $X.XX / min" + model + assumptions; output pricing shown as `n/a`.
- **Comparison grid** — by-mode rows + indented by-variant rows; `n/a` muted, never blank.
- **Errors** — calm, sanitized, actionable toasts.

## Notes
- Layout: 3-column grid (controls / transcript / metrics+cost) collapsing to one column under 1180px,
  then the full-width Comparison band, then Evaluation.
- The brand mark is the placeholder from `../../assets/` — swap when the real asset arrives.
