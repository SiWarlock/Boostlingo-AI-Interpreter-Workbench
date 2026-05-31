# Session 014 — H.1 UI baseline + G.4 frontend bug-fix round

- **Date:** 2026-05-31
- **Phase:** H.1 (UI/UX styling baseline) + G.4 (real-key-smoke frontend bug-fixes)
- **Area:** frontend (`web/`)
- **Predecessor:** [012 — Phase F.4 eval-turn exclusion](012-2026-05-30-phase-f-f4-eval-turn-exclusion.md)
- **Sibling (parallel backend close-out, same wholesale cycle):** [013 — backend G.4 bug-fix cycle](013-2026-05-31-smoke-bugfix-backend.md)
- **Successor:** [015 — frontend metrics-correctness, realtime cost, Phase-I realtime slice 1](015-2026-05-31-frontend-metrics-realtime-cost-phase-i-slice1.md)

> Implementer session doc (technical close-out only). `MVP_TASKS.md` / `LESSONS.md` / `ARCHITECTURE.md` are orchestrator-owned and untouched here. Cross-doc + lesson items are **surfaced** below for `/orchestrate-end`.

## Why this session existed

Two threads: (1) apply the user's delivered Boostlingo design system as the H.1 CSS baseline (the frontend was functionally complete but had ZERO CSS — the unstyled ModeToggle read as "broken"); (2) fix the frontend bugs the **real-key smoke** surfaced (G.4 round): garbled realtime metrics, fictional model names, the mid-session-mode-switch comparison-validity bug (2c), and the realtime transcript/metrics gap.

## What was built (per slice)

**H.1a — UI baseline foundation + headline fix** (`1d99eaf`, brief 047, styling/manual-smoke-exempt)
- NEW: `web/src/styles/tokens.css` (vendored design tokens + Google-Fonts `@import`), `web/src/styles/workbench.css` (adapted kit styles), `web/src/components/StatusPill.tsx` (presentational session/turn pill + live indicators), `web/public/mark.svg` (placeholder brandmark).
- MOD: `main.tsx` (import the two stylesheets), `App.tsx` (3-column shell + header + provider chips), `ModeToggle.tsx` (the headline fix — `.seg` segmented control with SOLID active-mode highlight, blue=Realtime/violet=Cascade), `SessionSetup.tsx` + `RecordingControls.tsx` (card/control styling), `ErrorBanner.tsx` (inline `.toast.err`), `package.json`/lock (`lucide-react@1.17.0`).

**H.1b — data-panel legibility** (`c753539`, brief 047)
- NEW: `web/src/components/latencyTarget.ts` (pure latency-vs-target tier helper) + `latencyTarget.test.ts` (the one deterministic bit, unit-TDD'd).
- MOD: `TranscriptPanel.tsx` (EN|ES `.tx-cols`), `MetricsPanel.tsx` (big-mono headline + per-stage bar), `CostPanel.tsx`, `EvaluationPanel.tsx`, `ComparisonSummary.tsx` (Q2(b): blue/violet mode-identity cards + `.cmp-table` cost-by-variant), `workbench.css`.

**049 — realtime metrics + CSS bug-fixes** (`27cab1f` A+B, `ccfcc17` C; brief 049, `/tdd`)
- A: realtime `playback.started` now stamped **per-turn** in the sink (on first post-stop `audioDelta`), dropping the leaky session-`<audio>` once-stamp; `deriveTurnMetrics` implements ARCH-013's `tts.first_audio ?? realtime.first_audio_delta ?? playback.started` chain (fixed the negative playback delta + the permanent-n/a realtime headline).
- B: `MetricsPanel` "Cascade stages" section gated to `mode==='cascade'`.
- C: `.session-actions .btn{min-width:0}` (button overflow).

**051-frontend — model-name fix** (`1b352e0`, `/tdd`-ish, typecheck-pinned)
- `TranslationModel` union `gpt-5.4-*` → `gpt-5-*` (real, broadly-available) + `sessionStore` default + ~18 test fixtures. (Backend half `8c46d13`.)

**050-frontend — mid-session mode switch → backend (Finding 2c)** (`7dc398e`, `/tdd`)
- `sessionsApi.setMode` (`POST /api/sessions/{id}/mode`); `sessionActions.switchMode` (DI'd — no-op / pre-session-store-only / active-await-then-finalize; realtime teardown gated on POST success; failure keeps prior mode = no divergence); `ModeToggle` dispatches it; `SetModeRequest` + `ModeTransitionEvent` TS mirrors added (`modeTransitions` stays opaque until H.3).

**053-B — realtime input-transcription clobber + DC diagnostic** (`e777a31`, `/tdd` + manual-smoke)
- Client `session.update` re-asserts `transcription: {model:'gpt-4o-transcribe'}` so the partial `audio.input` can't clobber the mint's config; DEV-only raw `oai-events` DC-frame logger in `realtimeWebRtcClient.ts`.

## Decisions made
- **H.1 Q2 → (b)**: ComparisonSummary stays div-structured (blue/violet mode cards) + `.cmp-table` only for cost-by-variant — a full by-mode table would have forced rewriting labeled assertions (`ComparisonSummary.test`), crossing the "adjust a query, never an assertion" line. Zero test churn.
- **049 Fix A** touched `deriveTurnMetrics` (the brief assumed it wouldn't) — justified by ARCH-013 line 1043 documenting the realtime fallback the code under-implemented. The −2102ms was a within-browser-clock stamp leak, distinct from the intentionally-unclamped −50ms cross-clock disclosure (`selectors.test.ts:214`).
- **050 Q1/Q4**: await the POST before finalizing (no divergence on failure); resync mode from the authoritative response. Teardown moved INTO `switchMode` (success-gated).
- **053 Fix B = frontend-only**: the mint already enables `input.transcription`; the client `session.update` was clobbering it. No backend slice.

## Decisions explicitly NOT made (deferred — fresh team)
- **053 gaps A + C** — NOT fixed (need the live `oai-events` DC stream to pin). See the investigation capture below.
- **`ModeTransitionEvent` not wired into `InterpretationSession.modeTransitions`** — stays opaque (`unknown[]`) until H.3 consumes the timeline (pragmatic-accrete; the type is registered + ready).

## ⭐ 053 realtime transcript/metrics — investigation capture (FOR THE FRESH TEAM)
The user's first live realtime test: **audio plays, but no source/target transcripts + no metrics/summary** (Realtime stays "n/a").
- **KEY REFRAME:** realtime audio plays via the **WebRTC media track** (`pc.ontrack` → `<audio>` in `realtimeWebRtcClient.ts`), **NOT** the data-channel `audioDelta`. So **audio working does NOT prove the `oai-events` data channel is delivering events.** Transcripts + metrics ALL hinge on DC events.
- **(A) Transcript mapping** (`realtimeEvents.ts`): **spec-correct** — target=`response.output_audio_transcript.delta`, source=`conversation.item.input_audio_transcription.delta`/`.completed`, all mapped + accumulated (§16). NOT obviously broken; the exact **live `type` strings are unconfirmed** (ARCH-010 §7 smoke-confirm carry-forward).
- **(B) input transcription** — FIXED this session (the `session.update` clobber).
- **(C) metrics reporting** (`realtimeTurnController.ts`): the path **exists** (`responseDone`→`completeTurn`→`reportTurnEvents`→`appendTurnEvents`). Hinges on whether `response.done` + transcript deltas actually arrive on the DC over WebRTC.
- **What pins A/C next:** the **DEV diagnostic** (now logging every raw DC frame) on the next live realtime smoke, OR the **user's live `oai-events` console log** from the failed turn (the orchestrator is requesting it). Re-pin any divergent GA `type` string → ARCH-010 §7 note.

## TDD compliance
**Clean.** Deterministic logic was RED-first: `latencyTarget` (H.1b), 049 A+B (deriveTurnMetrics chain, sink per-turn `playback.started`, MetricsPanel gate), 050 `switchMode` flow + ModeToggle dispatch, 053 Fix B (`session.update` transcription assertion). **Manual-smoke-exempt (per posture):** all H.1 styling (verified via headless-browser smoke), the 053 DC diagnostic (dev-only logging), and the live realtime/cascade behavior (real-key smoke). 051 rename pinned by typecheck (the union) + a RED test caught one escaped-dot regex assertion. No violations.

## Reachability
All features reachable from production entry points: H.1 components render from `App`; `latencyTarget` ← MetricsPanel/ComparisonSummary; `switchMode` ← ModeToggle `onClick` (UI handler) → `sessionsApi.setMode`; 053 `session.update` transcription ← `startTurn` (via RecordingControls); the DC diagnostic ← the client's `dataChannel.onmessage`. **No tested-but-unwired code.** Note: 050's `POST /mode` 404s until the backend half ships (confirmed live — frontend correctly keeps prior mode); 053 A/C are wired but await live confirmation (not a wiring gap).

## Open follow-ups

**Step-9 items (surfaced for `/orchestrate-end` — orchestrator writes; flagged hot during the session):**
- H.1: styling-slice lesson + ARCH-007 styling-convention note (vendored `tokens.css`+`workbench.css`).
- 049: ARCH-013 realization note (`deriveTurnMetrics` now implements the documented first-audio chain) + lesson refinement (realtime first-audio/playback stamps are per-turn in the sink, not a session-`<audio>` once-stamp; refines §16/§17).
- **051 [cross-doc]:** `TranslationModel` union `gpt-5.4-*`→`gpt-5-*` → ARCH-005 / Appendix A / `web/CLAUDE.md` row.
- **050 [cross-doc]:** new route `POST /api/sessions/{id}/mode` → ARCH-009 + Appendix A `SetModeRequest`; `SetModeRequest` + `ModeTransitionEvent` TS mirrors → `web/CLAUDE.md` rows; `config.currentMode` mutable-mid-session + `ModeTransitionEvent` wired (Flow G) → ARCH-010/017 realization notes.
- 053: none (a re-pinned GA `type` string, pending the live log → ARCH-010 §7 note, fresh team).

**Cross-doc invariant audit:** 2 model-surface changes this session (051 `TranslationModel` union; 050 `SetModeRequest`/`ModeTransitionEvent` mirrors + new route). Both **flagged at Step-9**; the orchestrator writes the ARCHITECTURE.md/CLAUDE.md rows at round-seal (confirmed in-thread). No silent drift.

**Fresh-team work (not started this cycle, per orchestrator):** 053 gaps A/C (live DC confirmation); 050-backend (`/mode` endpoint — until it ships the toggle POST 404s); H.3 (session-history timeline, consumes `ModeTransitionEvent`); the metrics-framing items.

## How to use what was built
- Run `npm run dev` (web) + the backend with `.env` sourced; the workbench now renders the full Boostlingo design baseline. Toggle modes to see the active highlight (blue/violet).
- For the next realtime live smoke: open the browser console — the DEV diagnostic logs `[realtime oai-events] …` for every DC frame. Capture those to pin 053 A/C.
