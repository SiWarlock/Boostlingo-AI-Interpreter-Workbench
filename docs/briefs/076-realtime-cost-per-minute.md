# /tdd brief — realtime_cost_per_minute

## Feature
Make the realtime per-turn cost render a **$/min** figure (today it shows a total `estimatedUsd` but `estimatedUsdPerMinute` is `n/a`, blanking the cost comparison). The realtime `/complete` finalize sends the turn's **recording (source-speech) duration** as `audioDurationMs`; the backend already divides cost by that duration to produce `estimatedUsdPerMinute`. Plus a disclosed assumption that both modes use the same `$/min` basis.

## Use case + traceability
- **Task ID:** metrics/cost-accuracy round (feeds G.5 write-up) — FE half. **⭐ COMPARISON-CRITICAL** (the cost axis of the central deliverable).
- **Architecture sections it implements:** `ARCHITECTURE.md` ARCH-014 (estimated cost/min), ARCH-010 (realtime turn lifecycle), ARCH-009 (`/complete`).
- **Related context:** Lead-confirmed denominator = **input/recording audio-duration** (cascade-consistent — both modes read "$ per minute of SOURCE speech"; the input+output basis was rejected as it distorts the headline ~2×). web LESSONS §17 (realtime turn controller stamps `recording.started`/`stopped`), §26 (the `/complete` finalize forwards exact DC token counts), §21 (cost read via `GET /session`, `costEstimate`≠`cost`). Carry-forward "realtime cost-UNIT difference (G.5)" — THIS slice resolves it.

## Root cause (code-traced — confirms the small surface)
`CostEstimator.Build()` already computes `estimatedUsdPerMinute = usd / (audioDurationMs/60000)` for every estimate; `SessionService.EstimateRealtimeCost` already calls `EstimateRealtime(model, usage, turn.AudioDurationMs)`. The realtime `$/min` is `null` for ONE reason: `finalizeTurn` (`realtimeTurnController.ts:142`) builds the `/complete` body as `{ status:'completed' }` + audio-token fields and **never sends `audioDurationMs`** → `turn.AudioDurationMs` stays 0 → `perMinute` null. **No BE change needed** (confirmed with the lead). The `CompleteTurnRequest.audioDurationMs` field already exists (`domain.ts:224`).

## Acceptance criteria (what "done" means)
- [ ] On `responseDone`, `finalizeTurn` includes `audioDurationMs` in the `/complete` `CompleteTurnRequest`, computed from the finalized turn's `turn.recording.started` + `turn.recording.stopped` markers (duration = `stopped.timestamp − started.timestamp`, rounded to ms).
- [ ] `audioDurationMs` is sent **only when both markers are present and the duration is > 0**; otherwise the field is **omitted** (never a synthesized 0 — honest-degrade, web §25/§26). Backend then leaves `perMinute` null + discloses-unavailable, exactly as today.
- [ ] The existing audio-token fields + `status:'completed'` are unchanged (the cost is still priced from exact tokens; only the per-minute denominator is newly supplied).
- [ ] A live realtime turn with a real recording duration now persists a non-null `estimatedUsdPerMinute` (read back via `GET /session`, web §21) → the MetricsPanel / CostPanel / comparison view show realtime `$/min` on the SAME source-speech-minute basis as cascade.
- [ ] **Disclosed assumption:** the cost display surfaces a disclosure that `$/min = estimated cost ÷ source-speech minutes` (same basis for cascade + realtime). Placement + FE-static-vs-BE-assumption resolved at Step 2.5.
- [ ] The `domain.ts` CompleteTurnRequest comment (currently "the realtime path populates ONLY the audio-token fields … + status") is updated to include `audioDurationMs` (source code comment — implementer edits).
- [ ] `/preflight` clean.

## Files expected to touch
**Modified:**
- `web/src/realtime/realtimeTurnController.ts` — `finalizeTurn`: compute + include `audioDurationMs` from the finalized turn's recording markers (read the turn's `latencyEvents` like `reportTurnEvents` does at line 117).
- `web/src/realtime/realtimeTurnController.test.ts` — RED tests (duration computed + sent; omitted when a marker is absent).
- `web/src/types/domain.ts` — update the CompleteTurnRequest comment (not the type — the field already exists).
- Cost display component for the disclosure (e.g. `web/src/components/CostPanel.tsx` and/or `ComparisonSummary.tsx`) — exact target confirmed at Step 2.5.

If implementation needs files beyond this list, **flag at Step 2.5** before going GREEN.

## RED test outline (Step 2)
Tests in `web/src/realtime/realtimeTurnController.test.ts` (against the real store + mocked api, the §17 pattern):

1. **`finalize_sends_recording_duration_as_audioDurationMs`** — a finalized turn whose `latencyEvents` carry `turn.recording.started`@T0 and `turn.recording.stopped`@T0+5000; on `responseDone`, `api.completeTurn` is called with a body where `audioDurationMs === 5000` (alongside the existing token fields + `status:'completed'`).
   - Why: ARCH-014 — supplies the per-minute denominator; pins the comparison-critical fix.
2. **`finalize_omits_audioDurationMs_when_stopped_marker_absent`** — a finalized turn missing `turn.recording.stopped`; the `/complete` body has NO `audioDurationMs` key (honest-degrade, never 0).
   - Why: web §25/§26 — absent data is omitted, never synthesized; backend keeps `perMinute` null.
3. **`finalize_omits_audioDurationMs_when_duration_not_positive`** — markers present but `stopped ≤ started` (clock edge); `audioDurationMs` omitted.
   - Why: never send a 0/negative denominator → no divide-by-~0 garbage; disclosed-unavailable instead.
4. **`finalize_preserves_token_fields_and_status`** — the token fields + `status:'completed'` still present alongside `audioDurationMs` (regression guard for §26).
   - Why: the cost is still token-priced; only the denominator is added.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** NONE — `CompleteTurnRequest.audioDurationMs` already exists (a wire field on the documented mirror). This slice changes which fields the realtime path POPULATES (a behavior change), not the shape.
- **Orchestrator doc rows to write hot (Step 9):** the `web/CLAUDE.md` cross-doc row for `CompleteTurnRequest`/`CompleteTurnResponse` (053-C2b) currently says "the FE populates only the `*AudioTokens` + `status:'completed'`" — update to "+ `audioDurationMs` (recording/source-speech duration, for the realtime `$/min` denominator)." **No ARCHITECTURE.md contract change** (the field is already in Appendix A). Possible ARCH-014 realization note (realtime `$/min` uses the source-speech-minute basis, uniform with cascade).

## Things to flag at Step 2.5
1. **Disclosure placement + ownership.** Options: **(a)** a static FE disclosure line in the cost display ("$/min = cost ÷ source-speech minutes; same basis for cascade + realtime") [**my default vote** — honors the lead's "no BE change"; the basis is global/uniform so a single static line fits]; **(b)** add the assumption string in the backend `CostEstimator.Build` when `perMinute` is computed (more uniform — rides the existing `Assumptions` array — but is a BE change). Lean **(a)**; if the FE-static line looks inconsistent beside the backend-sourced assumptions, flag and I'll authorize the tiny BE add.
2. **Duration source — read the turn's `latencyEvents`, or thread the markers' timestamps directly?** My default vote: **read the finalized turn's `latencyEvents`** (single source, mirrors `reportTurnEvents` at line 117; the turn is already in `turns[]` when `finalizeTurn` runs). Confirm the markers are present at `finalizeTurn` time for BOTH auto-VAD (3A anchor on `speech_stopped`) and manual (Stop) paths.
3. **Auto-VAD multi-segment edge.** Each auto turn has its own `recording.started`/`stopped`. Confirm the duration reads THIS turn's markers (not a stale prior-segment value — the 070 `pendingRecordingStoppedTs` reviewer-MED is adjacent). If a stale-anchor risk surfaces, omit rather than send a wrong duration.

## Dependencies + sequencing
- **Depends on:** nothing hard. Sibling to 074 (FE display polish) — both FE; dispatch after 074's slice lands (FE works one slice at a time).
- **Blocks:** the G.5 write-up's cost axis (realtime `$/min` now comparable to cascade).

## Estimated commit count
**1.** One focused FE feature (send the denominator + the disclosure). No safety invariant. The disclosure is a small co-located display add — bundles cleanly with the send.

## Lessons-logged candidates anticipated
- **Architecture-doc note candidate** — ARCH-014: realtime `$/min` uses the source-speech-minute denominator (recording duration), uniform with cascade; supplied by the FE at `/complete`, computed by the existing `Build`.
- **Convention candidate (maybe)** — "a fair cross-mode `$/min` requires the SAME denominator basis on both sides; supply the realtime denominator from the recording duration, omit (never 0) when absent."

## How to invoke
1. Read this brief end-to-end (note the lead-confirmed denominator + the honest-omit rule).
2. Run `/tdd realtime_cost_per_minute`.
3. Step 0 (Restate) — confirm against the Feature line.
4. Step 2.5 — send the per-test write-up + your read on the three design questions; I reply `APPROVED.`/`TWEAK:`/`ADD:`.
5. Step 9 — categorized summary (flag the cross-doc `CompleteTurnRequest` row update) + ship/no-ship + draft commit message.
