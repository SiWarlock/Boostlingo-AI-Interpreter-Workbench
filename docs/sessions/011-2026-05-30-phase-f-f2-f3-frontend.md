# Session 011 — Phase F: F.2 EvaluationPanel + F.3 ComparisonSummary (frontend) — closes Phase F

- **Date:** 2026-05-30
- **Phase:** F (Evaluation, Summary & Comparison) — the frontend closing slices
- **Predecessor:** [010-2026-05-30-phase-f-f1-evaluation-endpoints.md](010-2026-05-30-phase-f-f1-evaluation-endpoints.md)
- **Successor:** _(next session)_
- **Area:** `web/` (frontend implementer)
- **Slice commits:** F.2 `f1697bc` · F.3 `f7daae1`
- **Web suite:** 157 → **193 green** (+36: F.2 +17, F.3 +19); `format:check` + ESLint + `tsc --noEmit` clean at close.
- **Preflight at close:** GREEN — `format:check` clean · ESLint no issues · `tsc --noEmit` clean · Vitest 193/193 (31 files).

## Why this session existed

F.1 (`7e29f35`/`f9cf6a9`, session 010) landed the three backend evaluation endpoints. Phase F's frontend half consumes them:
- **F.2 — the standalone `EvaluationPanel`** (Flow D): select a scripted phrase → record → `POST /transcribe` (STT-only hypothesis) → create a dedicated evaluation turn → `POST /wer` WITH that `turnId` to score **and persist** the `WerResult` on the session, rendering the result + the "WER is STT-only" explanation.
- **F.3 — the `ComparisonSummary`** (Flow E): the Realtime-vs-Cascade comparison — per-mode avg latency / cost-per-min / errors / turn counts + WER + total (from `GET /summary`), plus a per-model-variant cost/min breakdown derived frontend-side from each persisted turn's `costEstimate` (`GET /session`). F.3 depends on F.2's WER-persist path populating `GET /summary`'s `WerSummary`.

Both reuse landed frontend seams: the `http`/`request` boundary (§3/§4), the DI'd-actions pattern (§7), the jsdom/Testing-Library env (§14), the single-error-sink store (§2), `cascadeApi`'s multipart pattern, `audioCaptureController.recordBlob()`, `sessionsApi.getSummary`/`getSession`, and `selectors.formatUsdPerMinute`/`canStartRecording`.

## What was built

### Files created (F.2)
- `web/src/api/evaluationApi.ts` — the 3-endpoint client over the shared `http` boundary: `getPhrases()` (GET), `transcribe(params, blob)` (multipart — FormData body, no content-type header so fetch sets the boundary), `computeWer(req)` (JSON). (+ `evaluationApi.test.ts` — 4 tests, §4 fetch-mock.)
- `web/src/state/evaluationActions.ts` — the DI'd Flow-D flow `runEvaluation(deps, input)`: record → transcribe → createTurn → computeWer; errors route to the store (single sink); returns `{hypothesis, werResult} | null`. Exports `EVAL_RECORD_DURATION_MS = 6000`. (+ `evaluationActions.test.ts` — 8 tests, §7.)
- `web/src/components/EvaluationPanel.tsx` — the panel: phrase selector, reference text, "Record & evaluate" (gated on an active session via `canStartRecording`), the rendered WER result (% / S·I·D·N / hypothesis), and the verbatim ARCH-015 explanation. A `useRef` inFlight guard blocks a concurrent double-dispatch. (+ `EvaluationPanel.test.tsx` — 5 tests, §14.)

### Files created (F.3)
- `web/src/state/comparisonAggregation.ts` — the pure per-variant aggregation: `toComparisonTurn(wireTurn)` (projects the opaque wire turn, reading `costEstimate` NOT the viewmodel `cost`) + `aggregateCostByVariant(ComparisonTurn[])` (groups by `(mode, model)`, averages `estimatedUsdPerMinute`, skips null/non-finite cost — never a synthetic 0). (+ `comparisonAggregation.test.ts` — 6 tests, §13.)
- `web/src/state/comparisonActions.ts` — the DI'd `loadComparison(deps)` flow: `getSummary` (per-mode + WER headline) + `getSession` (per-variant cost), the two sources degrading INDEPENDENTLY (`byVariant: VariantCost[] | null` — null = source failed, [] = no priced turns). (+ `comparisonActions.test.ts` — 5 tests, §7.)
- `web/src/components/ComparisonSummary.tsx` — the Flow-E view: per-mode columns (latency/cost/errors/turns, `n/a` for null fields), WER (unbounded — never clamped past 100%), total, and the per-variant cost rows (null → "unavailable", [] → "no priced turns"). (+ `ComparisonSummary.test.tsx` — 8 tests, §14.)

### Files modified
- `web/src/types/domain.ts` — F.2 added 5 wire mirrors (`EvaluationPhrase`, `WerResult`, `TranscribeResponse`, `WerRequest`, `WerResponse`) + the `TranscribeParams` request shape; F.3 added the `ComparisonTurn` projection (`{mode, cost: CostEstimate|null}`). All camelCase mirrors / focused projections; zero drift vs the backend records (verified against `Evaluation/EvaluationModels.cs` + `Sessions/SessionModels.cs`).
- `web/src/App.tsx` — mounted `<EvaluationPanel/>` (F.2) and `<ComparisonSummary/>` (F.3) in the shell.

## Decisions made

- **F.2 — WER persists via a DEDICATED evaluation turn (Q1=a).** The panel creates a turn (`sessionsApi.createTurn`) per measurement and passes its `turnId` to `POST /wer` so the `WerResult` reaches the session JSON for F.3's `WerSummary`. The eval turn is a **backend-only artifact** — the store's interpretation-turn machine (`currentTurn`/`turns[]`) stays untouched (no `beginTurn`/`completeTurn`). Known limitation: the eval turn inflates `ModeSummary.TurnCount` (quality averages are null-skipping + unaffected; `WerSummary` exact) → documented for G.5.
- **F.2 ordering** record → transcribe → createTurn → computeWer (createTurn AFTER a successful transcribe so a failed transcribe doesn't orphan a turn). `recordBlob` returns null silently (no `onError` on the blob path) → the flow surfaces its own `capture.failed` error.
- **F.2 result lives in local panel state** (the flow returns `{hypothesis, werResult}`); only errors route to the store. Transient display state doesn't belong in `UiSessionState`.
- **F.3 — two data sources, each authoritative for its slice (no double-truth):** `GET /summary` owns per-mode + WER; `GET /session`'s canonical persisted turns own the per-variant cost split (derived frontend-side, the §13 precedent, since `ModeSummary` is per-mode only). The wire cost field is **`costEstimate`** (C# `InterpretationTurn.CostEstimate`), distinct from the frontend `TurnViewModel.cost` — reading the wrong one silently empties the breakdown.
- **F.3 independent degradation:** a `GET /session` failure leaves `byVariant` null (the per-mode headline still renders); a `GET /summary` failure returns null (no comparison). Honest `n/a` for null `ModeSummary` fields; cascade `avgSpeechEnd*` stays `n/a` (the documented no-client-latency-channel limitation).
- **F.3 — only-used model variants appear** (the aggregation emits a row only for a `(mode, model)` with ≥1 priced turn — no fabricated 0-turn rows).

## Decisions explicitly NOT made (deferred)

- **Q5 — a cascade client→server latency channel** for the comparison's cascade end-to-end latency. Accepted `n/a` + document (G.5). Adding a channel is a cross-area cascade-WS backend+frontend slice — out of scope; NOT escalated (the comparison stays rich: realtime end-to-end + cascade per-stage + cost + WER + errors + turns).
- **Orphaned-eval-turn cleanup** (F.2). A `computeWer` failure AFTER `createTurn` leaves a valid turn with no `WerResult`. Bounded for the single-trusted-user demo; documented in-code; a backend turn cancel/cleanup endpoint would close it — Carry-forward (→ F.3/G).
- **A backend eval-turn marker** so `ModeSummary` excludes eval turns from `turnCount` — deferred (a later backend slice); documented limitation suffices for the MVP.

## TDD compliance

- **F.2 + F.3: TDD-clean.** Each feature (api / flow / component / aggregation) was written test-first — Step 2 tests, Step 3 RED confirmed for the right reason (module-not-found), then GREEN. Step-2.5 orchestrator review on both slices (both added required tests: F.2 createTurn-error + gate-closed; F.3 `toComparisonTurn` projection + null-vs-`[]` rendering).
- **One minor note (F.2):** the `useRef` inFlight guard on `handleEvaluate` was added at the Step-8 code-quality review as **defensive hardening** (the §11 `recordingActions` pattern) WITHOUT a dedicated failing test — the concurrent-double-click window is not deterministically reproducible in jsdom (same exemption as `recordingActions`'s own inFlight guard, which is also not race-unit-tested). The disabled-button path + all existing tests remain green. Not a behavior change a test could pin without flakiness.
- The other review-driven test additions (F.3 NaN-skip, `sessionId===null`, non-ApiError fallback, garbage-mode default, `turnCount===0` gate, null-mode column) pin already-implemented guards — post-hoc coverage of existing behavior, not back-filled-after-seeing-green new behavior.

## Reachability

- **F.2 `EvaluationPanel`** — reachable from `App.tsx` via `<EvaluationPanel/>` → "Record & evaluate" button → `runEvaluation({store, api: evaluationApi, createTurn: sessionsApi.createTurn, capture: audioCaptureController}, …)`; phrases load on mount via `evaluationApi.getPhrases()`. Wired this slice. ✓
- **F.3 `ComparisonSummary`** — reachable from `App.tsx` via `<ComparisonSummary/>` → on-mount `loadComparison({store: sessionStore, api: sessionsApi})` → `getSummary` + `getSession` → `aggregateCostByVariant`. Wired this slice. ✓
- **No tested-but-unwired gaps.**

## Open follow-ups

### Step-9 categorized items (orchestrator routes hot via `/orchestrate-end` — surfaced here for verification, NOT re-routed)
- **Cross-doc invariant change → `web/CLAUDE.md` rows** (mirror-registration; NO `ARCHITECTURE.md` change — F.2's 5 mirrors + `TranscribeParams` mirror the already-shipped F.1 DTOs in Appendix A; F.3's `ComparisonTurn` reads the already-mirrored wire turn). Orchestrator's `web/CLAUDE.md` edit was observed in the working tree (` M`) at commit time.
- **Architecture-doc note → G.5 limitations** — (a) the cascade speechEnd→playback `n/a` (no client-latency channel); (b) the eval-turn `turnCount` inflation (F.2).
- **Convention candidate(s) → `web/LESSONS.md`** — (F.2) the dedicated-eval-turn WER-persist pattern (eval turn = backend-only artifact; turnCount-inflation cost) + the `recordBlob` reuse / DI'd-flow returns-transient-result pattern; (F.3) the cost-by-variant frontend aggregation over the wire `costEstimate.model` (two-source-no-double-truth; the `costEstimate`-not-`cost` gotcha). Orchestrator's `web/LESSONS.md` edit observed in the tree (` M`).
- **Future TODO — next-brief working set (Carry-forward → F.3/G)** — the orphaned-eval-turn cleanup (above).

### Wiring follow-ups
- None. Both panels are mounted + reachable from `App.tsx`.

## How to use what was built

- **WER evaluation (demo):** start a session → in the Evaluation panel, pick a scripted phrase, read it aloud, click "Record & evaluate" → the panel shows the WER % + S·I·D·N + hypothesis, and persists the score on the session.
- **Comparison (demo):** run a few turns in both modes (and across model variants) → the Comparison panel shows per-mode latency/cost/errors/turns + WER + the per-model-variant cost/min breakdown. Run the variants you want compared — only-used variants appear.
- Both panels degrade honestly: missing metrics render `n/a` (never a fabricated 0); fetch failures surface via the error banner without crashing the view.
