# /tdd brief — realtime_cost_summary_emptysilence_fix

## Feature
Fix the realtime per-mode cost (+ all `ModeSummary` aggregates) not displaying in the live UI: `SessionSummaryService.SummarizeMode`'s `IsEmptySilence` rule excludes EVERY realtime turn from the summary (because live realtime turns legitimately persist with 0 transcripts but a real cost), blanking the realtime Cost/min surfaces. Refine the rule so a **cost-bearing** turn is never treated as empty silence.

## Use case + traceability
- **Task ID:** Finding-A (user live-test 2026-06-01; realtime cost no longer displays in the interactive flow)
- **Architecture sections it implements:** `ARCH-009` (`SessionSummary`/`ModeSummary`), `ARCH-013` (metrics), `ARCH-014` (cost)
- **Related context:** server LESSONS **§39** (083 / Phase J — `IsEmptySilence = Status==Completed && Transcripts.Count==0`, added to drop phantom cascade auto-VAD silence turns). **This brief refines §39.** Investigated + confirmed against the user's real persisted JSON (lesson §34 — verified, not code-only): NOT a 094/095 regression (see below).

## Root cause (CONFIRMED against the user's live session JSON + the full corpus)
Live realtime turns persist as `status:completed`, **`transcripts:[]` (0 transcripts)**, with a **real `costEstimate` (both `estimatedUsd` and `estimatedUsdPerMinute` populated)** — e.g. `session_20260601T170454Z_20dfbf39.json`: 5 completed realtime turns, each ~$0.005–0.009 / ~$0.12–0.36/min, all 0 transcripts. But `summary.realtime.estimatedCostPerMinuteUsd = null` and `realtime.turnCount` is empty. Why: `SummarizeMode` filters `!IsEmptySilence(t)` and `IsEmptySilence = Completed && Transcripts.Count==0` (SessionSummaryService.cs:69-70) — which matches EVERY realtime turn (realtime transcripts live FE-store-side and are not persisted on the turn), so all realtime turns are excluded → the realtime ModeSummary is empty → CostPanel "Session · per mode" + ComparisonSummary per-mode realtime Cost/min render n/a.

**Corpus-validated discriminator** (scanned every persisted session): a Completed + 0-transcript turn is `hasCost` for **realtime** (100% of cases — tokens billed) and `costNULL` for **cascade** (100% — genuine silence: no STT final → no translation → composite unavailable). So `CostEstimate == null` cleanly separates "real realtime turn" from "cascade phantom silence."

**094/095 are CLEARED** (definitive mechanism): 095's `extractRealtimeUsage` change only narrows the `cached` field (input/output reads untouched → never newly returns null); 094's `EstimateRealtimeFromTokens` always reaches `Build()` with a valid cost. Neither touches the summary-exclusion path. Per-turn cost compute + persistence work (so the G.5/demo numbers stand). The regression is §39 (083, landed 2026-06-01) — before it, realtime turns were summarized; "no longer displays" fits exactly.

## Acceptance criteria (what "done" means)
- [ ] `IsEmptySilence` excludes a turn ONLY when `Status==Completed && Transcripts.Count==0 && CostEstimate==null` — i.e. a cost-bearing turn is NEVER excluded.
- [ ] A completed realtime turn with 0 transcripts + a real `CostEstimate` is INCLUDED in `ModeSummary` (counted in `TurnCount`; its cost feeds `EstimatedCostPerMinuteUsd`).
- [ ] A completed cascade silence turn (0 transcripts, `CostEstimate==null`) is STILL excluded (§39 intent preserved — no `errorCount`/count pollution from phantom re-armed silence).
- [ ] A failed-empty turn (Status==Failed, 0 transcripts, a real error) is STILL counted (the §39 `Completed`-scope, unchanged — its error stays in `errorCount`).
- [ ] Top-level `SessionSummary.TurnCount` still counts ALL turns (unchanged).
- [ ] All tests in `server/AiInterpreter.Tests/SessionSummaryServiceTests.cs` (or wherever `SummarizeMode`/`IsEmptySilence` is tested) pass; the existing §39 silence-exclusion test is preserved + a new realtime-cost-bearing-inclusion test added.
- [ ] `/preflight` clean.

## Wiring / entry point (Step 7.5)
Already wired — `GET /api/sessions/{id}/summary` → `SessionSummaryService.Summarize` → `SummarizeMode` → the FE `refreshSummary` → `CostPanel`/`ComparisonSummary`. This slice changes the filter predicate inside the existing reachable path; no new caller. Confirm the realtime ModeSummary now populates (a characterization test mirroring the user's session shape: completed realtime turns, 0 transcripts, real cost → non-null `EstimatedCostPerMinuteUsd`).

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Sessions/SessionSummaryService.cs` — `IsEmptySilence` predicate (+ a clarifying comment that a cost-bearing realtime turn is real evidence, not silence).
- `server/AiInterpreter.Tests/SessionSummaryServiceTests.cs` — preserve the §39 cascade-silence-exclusion test; add the realtime-cost-bearing-inclusion test (+ the cascade-silence-stays-excluded + failed-empty-stays-counted pins).

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`summarize_includes_completed_realtime_turn_with_cost_and_no_transcripts`** — a Completed realtime turn, `Transcripts=[]`, a real `CostEstimate` (non-null `EstimatedUsdPerMinute`) → asserts `ModeSummary.Realtime` is non-null, `TurnCount==1`, `EstimatedCostPerMinuteUsd` == that turn's value. Why: the Finding-A fix (the user's exact session shape).
2. **`summarize_still_excludes_cascade_empty_silence`** (preserve/rename the existing §39 test) — a Completed cascade turn, `Transcripts=[]`, `CostEstimate==null` → still excluded (not in `TurnCount`, no avg pollution). Why: §39 intent intact.
3. **`summarize_still_counts_failed_empty_turn`** — a Failed turn, `Transcripts=[]`, a real error → still counted (`errorCount` includes it). Why: the §39 `Completed`-scope (don't hide real failures).
4. **(if helpful)** a mixed session (realtime cost turns + a cascade silence turn + a failed turn) → the per-mode splits + top-level `TurnCount` all reconcile.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (predicate-only change; no model/DTO shape change).
- **Orchestrator doc rows to write hot:** none structural. A server LESSONS update to §39 (the cost-bearing refinement) is a lesson edit I write at Step 9; flag it categorized.

## Things to flag at Step 2.5
1. **Discriminator: `CostEstimate==null` vs mode-scoping.** My default vote: **`&& CostEstimate==null`** — semantically precise ("a turn that produced billable work isn't silence"), mode-agnostic, and validated across 100% of the persisted corpus (cascade silence = costNULL; realtime = hasCost). Alternative: `&& Mode != Realtime` (simpler but couples the silence concept to a mode; and a future genuinely-silent cascade-with-cost edge wouldn't be handled). Lean: cost-based.
2. **A $0-cost realtime turn (a barge-in/cancelled turn with a non-null `CostEstimate` of $0).** Under the cost==null discriminator it'd be INCLUDED (CostEstimate present). My default vote: **include it** — a present CostEstimate (even $0) means a `response.done` fired; it's a real, cheap turn, not silence. (If we'd rather exclude $0 turns, that's a separate, more complex rule — out of scope.) Flag if you disagree.
3. **Don't fix the deeper "realtime transcripts aren't persisted" here.** This brief is summary-inclusion only. Whether realtime turns SHOULD persist their transcripts (so the history drill-in shows them) is a separate, larger question (a new wire path at `/complete`) — I'm flagging it to the lead as a follow-up, NOT bundling it. My default vote: **keep this slice predicate-only.**

## Dependencies + sequencing
- **Depends on:** nothing.
- **Blocks:** the realtime cost displaying live (Finding A). Independent of Finding B (interpreter-answers-not-translates), which is a separate slice pending the lead's mode-narrowing.

## Estimated commit count
**1.** Focused predicate refinement + tests. Not a safety-invariant slice; reviewer fan-out stays disabled per standing directive.

## Lessons-logged candidates anticipated
- **Convention candidate / §39 refinement** — "`IsEmptySilence` must also require `CostEstimate==null`: a cost-bearing turn (every realtime turn — which persists 0 transcripts but real billed tokens) is real evidence, not phantom silence; excluding it blanks the per-mode summary. The summary filter that drops cascade auto-VAD silence must not also drop realtime."
- **Architecture-doc note candidate** — ARCH-009/013: realtime turns persist with 0 transcripts (FE-store-side transcripts) but contribute cost to `ModeSummary`.

## How to invoke
> Don't prescribe `/session-start` (the BE session is oriented). Jump to pre-flight + `/tdd`.
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd realtime_cost_summary_emptysilence_fix` in the backend (`server/`).
3. Step 0 restate → Step 1 file list → Step 2 RED → **Step 2.5 ping the orchestrator** with test designs + answers to the 3 questions → GREEN after approval.
4. Step 9: surface the §39-refinement lesson candidate + the draft commit message.
