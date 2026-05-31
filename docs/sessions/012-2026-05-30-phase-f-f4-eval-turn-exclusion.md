# Session 012 — Phase F.4: eval-turn exclusion (backend)

- **Date:** 2026-05-30
- **Phase:** F (F.4 — the user-requested backend close-out that finalizes Phase F)
- **Predecessor:** [011-2026-05-30-phase-f-f2-f3-frontend.md](011-2026-05-30-phase-f-f2-f3-frontend.md)
- **Successor:** [013-2026-05-31-smoke-bugfix-backend.md](013-2026-05-31-smoke-bugfix-backend.md)
- **Slice commit:** `1e8a655` `feat(evaluation): exclude WER-eval turns from per-mode summary (F.4)`

## Why this session existed

F.2 persists WER by creating a **dedicated evaluation turn** then scoring it via `POST /api/evaluation/wer`. That eval turn was created with the session's current mode, so it inflated `ModeSummary.TurnCount` (and would skew the per-mode latency/cost averages + error count) — F.3's Realtime-vs-Cascade comparison counted evaluation turns as if they were interpretation turns. The user (decision 2026-05-30, F.2 Q1) **required the clean backend fix**, not a document-only caveat. F.4 marks eval turns and excludes them from the per-mode comparison while keeping them in the WER summary — so F.3's counts become exact with **zero frontend change**.

## What was built

### Files created
- `docs/sessions/012-2026-05-30-phase-f-f4-eval-turn-exclusion.md` — this session doc.

### Files modified (production — committed in `1e8a655`)
- `server/AiInterpreter.Api/Sessions/SessionModels.cs` — `InterpretationTurn` gains a **trailing** `bool IsEvaluation = false`. Trailing + defaulted so every existing positional construction / `with` site compiles unchanged and old session JSON (no `isEvaluation` key) deserializes to `false`.
- `server/AiInterpreter.Api/Evaluation/EvaluationService.cs` — the `/wer` turn-attach `UpdateTurn` transform now sets the marker **atomically with the score**: `turn => turn with { WerResult = wer, IsEvaluation = true }`. No signature/flow change beyond the transform → F.2's frontend stays byte-identical (it already calls `/wer` with the `turnId`).
- `server/AiInterpreter.Api/Sessions/SessionSummaryService.cs` — `SummarizeMode` now filters `t.Mode == mode && !t.IsEvaluation`, so the per-mode `ModeSummary` `TurnCount`, latency/cost averages, **and** `ErrorCount` reflect interpretation turns only (one filter covers every per-mode field). `SummarizeWer` is **unchanged** (`WerResult is not null`) — eval turns' WER stays in `WerSummary`.

### Files modified (tests — committed in `1e8a655`)
- `server/AiInterpreter.Tests/SessionSummaryServiceTests.cs` — **+2 new** (`summarize_mode_excludes_evaluation_turns` with skewing eval-turn values that prove exclusion across count/averages/errors; `summarize_wer_still_includes_evaluation_turns`) **+1 strengthened** (top-level `summary.TurnCount == 3` assertion — pins the Q1 "top-level counts all" semantic at the unit level).
- `server/AiInterpreter.Tests/EvaluationServiceTests.cs` — **+1 new** (`wer_with_turn_id_marks_turn_is_evaluation` — the marker rides the attach atomically) **+1 strengthened** (the no-`turnId` test now asserts `IsEvaluation` stays false — pins "marker set ONLY on the attach path").
- `server/AiInterpreter.Tests/SessionPersistenceTests.cs` — **+1** (`created_turn_defaults_is_evaluation_false` — `SessionStore.CreateTurn` defaults the marker false).
- `server/AiInterpreter.Tests/DomainSerializationTests.cs` — **+1** (`is_evaluation_round_trips_through_json` — camelCase `isEvaluation` key, default false, `true` round-trips via `JsonDefaults`; back-compat).
- `server/AiInterpreter.Tests/SessionsControllerTests.cs` — **+1 e2e** (`summary_endpoint_excludes_eval_turn_e2e` — real HTTP: create cascade session → POST /turns ×3 → POST /wer on turn 3 → GET /summary → top-level `turnCount==3`, `cascade.turnCount==2`, `wer.sampleCount==1`).

`SessionStore.CreateTurn` needed **no change** (named-arg construction + the field default cover it).

**Backend suite 339 → 345 green.** `dotnet build` 0 warnings/0 errors. `dotnet format --verify-no-changes` clean.

## Decisions made
- **Q1 — top-level `SessionSummary.TurnCount` counts ALL turns (incl. eval); only per-mode excludes.** Honest "total turns in the session"; the per-mode `ModeSummary` is where F.3's comparison reads, so per-mode is the load-bearing exclusion. Consequence: top-level total ≠ sum of per-mode counts (delta = eval turns) — the orchestrator documents this in the ARCH-013 note + the G.5 write-up presentation.
- **Q2 — `ModeSummary.ErrorCount` excludes eval turns too.** The single `&& !t.IsEvaluation` filter covers it; an eval turn isn't an interpretation turn, so its errors don't belong in the mode's interpretation error count.
- **Q3 — set the marker at `/wer`, not at turn creation.** Keeps F.2's frontend byte-identical (the user's constraint). Set-at-create would need an F.2 create-time signal (rejected).
- **Code-quality MEDIUMs fixed in-slice** — both review findings were missing assertions on the Q1 (top-level) and Q3 (marker-only-on-attach) decisions; added one line each so the decisions are pinned at the unit level, not just e2e.

## Decisions explicitly NOT made (deferred)
- **The orphaned-eval-turn (failure path) remains open** — a `computeWer` failure *after* `createTurn` succeeds leaves a valid turn with no `WerResult` and (post-F.4) no `IsEvaluation` marker, so it would still be counted in a mode. By design: set-at-`/wer` closes the **success path**; this rare failure-path orphan stays the documented bounded carry-forward (origin F.2, single-trusted-user demo). A backend turn-cancel/cleanup endpoint (or a frontend `computeWer` retry against the same turn) would close it — a Phase-G hardening item.
- **Comment-overlap LOW** (the `SessionModels` field doc + the `SummarizeMode` use-site comment partially overlap) — kept both deliberately; the use-site comment aids local readability and matches this file's heavily-commented idiom.

## TDD compliance
**Clean.** All 6 tests written before any production change (Step 2), RED confirmed as a compile-level `'InterpretationTurn' does not contain a definition for 'IsEvaluation'` error for the right reason (Step 3), then the minimal 3-edit implementation went GREEN (Step 4–5). The 2 review-driven assertions tighten already-correct behavior on green tests — not implementation-before-test. No violations.

## Reachability
- **`IsEvaluation` set** — reachable from `POST /api/evaluation/wer` (with a `turnId`) → `EvaluationController` → `EvaluationService.ComputeWerAsync` → `SessionStore.UpdateTurn` transform. (Already-wired F.1a path; F.4 only changed its internals.)
- **Exclusion observed** — `GET /api/sessions/{id}/summary` → `SessionsController` → `SessionService.Summary` → `SessionSummaryService.SummarizeMode`. (Already-wired B-phase summary path.)
- Proven end-to-end by the passing e2e test `summary_endpoint_excludes_eval_turn_e2e`. **No tested-but-unwired gap.**

## Open follow-ups

### Step-9 categorized items (orchestrator routes via `/orchestrate-end`; surfaced here for verification — NOT re-routed)
- **Cross-doc invariant change** → `ARCHITECTURE.md` ARCH-005 `InterpretationTurn` field list + `IsEvaluation`; Appendix A `InterpretationTurn` row; `server/CLAUDE.md` ARCH-005 cross-doc note; the **ARCH-013 semantic note** (top-level `TurnCount` counts all incl. eval / per-mode `ModeSummary` excludes / `WerSummary` includes → top-level ≠ sum-of-per-mode, delta = eval turns). _(Orchestrator's edits are in the shared tree — `ARCHITECTURE.md`/`MVP_TASKS.md`/`README.md` show modified; committed at the round seal. Staggered-commit per protocol, not drift.)_ No `web/` mirror change (the frontend turn is opaque; F.3 reads `costEstimate`/`turnCount`, not `isEvaluation`).
- **Convention candidate** → `server/LESSONS.md` §29 (orchestrator-confirmed) + `server/CLAUDE.md` index row: *Distinguish auxiliary/synthetic turns (eval turns) from interpretation turns with an explicit domain marker set at the defining moment (the `/wer` attach), atomic with the metric in ONE `UpdateTurn` transform; exclude them from the per-mode comparison aggregation while keeping them in the metric they exist for (WER) — don't overload "has a WerResult" as the discriminator.* Sub-point (from the security review): keep the persistence sentinel fixture (`BuildFullSession`) current with the model's full field set so future non-bool field additions are exercised under the inject-and-assert scan (this slice's `true`-case is covered by `is_evaluation_round_trips_through_json`, just not inside the sentinel family).
- **Already-tracked residual** — the orphaned-eval-turn (above). No new tracker action; already in Carry-forward (origin F.2).

### Wiring follow-ups
- None. The feature is reachable from production HTTP entry points (verified by e2e test 6).

## Security
Security review: **PASS** on all 5 Key safety invariants. `IsEvaluation` is a plain `bool` on the persisted model — introduces no secret/ephemeral-secret/raw-audio/PII/path surface; serializes as `"isEvaluation"` (collides with none of the `sk-`/`ek_`/`apikey`/`clientsecret`/`"audio":` sentinel patterns). The 3 `SessionPersistenceTests` sentinel scans still pass with the new field.
