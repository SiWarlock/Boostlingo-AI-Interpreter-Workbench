# /tdd brief — wer_explicit_reference_path

## Feature
Extend `POST /api/evaluation/wer` with an **additive explicit-reference path** `{reference, hypothesis}` — the canonical backend `WerCalculator` scores a caller-supplied reference, parallel to (and NOT replacing) the existing `phraseId` reference-from-store path. This lets the G.4 soak-harness score its committed FE script against the **one canonical WER algorithm** (user decision Option iii), instead of a duplicate client-side calc.

## Use case + traceability
- **Task ID:** G.4-BE-wer-ref (the 5-min synthetic soak-harness — the WER wiring half).
- **Architecture sections it implements:** `ARCHITECTURE.md §12 / ARCH-015` (WER), `§15 / ARCH-019` (boundary input-validation — the new external `reference`/`hypothesis` are bounded), `§6 / ARCH-009` (the `/wer` contract). Lesson **§27** (the WER endpoint chokepoint — extended additively here).
- **Related context:** `docs/g4-harness-design.md` → "WER-wiring sub-decision (089b)" (the user picked Option iii via the lead). The FE runner (089a) wires its abstract `computeWer` seam to `POST /wer {reference, hypothesis}` in 089b once this lands.

## ⚠️ User decision + lead guardrails (bake in)
1. **Strictly additive.** The `phraseId` reference-from-store path is **untouched + remains the DEFAULT**; the explicit-reference path is a new branch. **Byte-identical** behavior for existing callers (regression-pinned).
2. **Validate + cap at the boundary (SECURITY).** `reference` and `hypothesis` are external inputs → cap their length + reject empties per ARCH-019. **The reference is the `n` dimension of the n×m DP matrix that §27 only capped on `m` (hypothesis)** — so an unbounded reference reopens the same memory-DoS surface. Cap BOTH, in-service, before the allocation (the service stays the single WER chokepoint, §27).
3. **Update lesson §27** (orchestrator writes at routing): the explicit-reference path was added additively for the measurement-workbench/soak use case; store-by-`phraseId` remains the default for the phrase flow. Keep §27's original intent documented alongside the additive exception.
4. **Security-reviewer ON** for this slice (lead-recommended + warranted): it touches an input-validation boundary + reopens a deliberate design. Run it at the Step 7→8 boundary.

## ⭐ Correctness pin — the soak path does NOT mark turns as evaluation
The soak posts `{reference, hypothesis}` with **NO `TurnId`** → the existing `IsNullOrEmpty(TurnId)` branch returns `Computed(wer, persist: null)` (no turn-attach). This is REQUIRED: the soak's turns are the REAL interpretation turns being measured — they must stay IN the per-mode comparison (§29/§39 exclude `IsEvaluation` turns). The explicit-reference path must **not** force a turn-attach; the optional `TurnId`-attach stays orthogonal (unchanged). Pin this.

## Acceptance criteria (what "done" means)
- [ ] `WerRequest` gains optional `Reference` (`string?`). When `Reference` is non-empty, `ComputeWerAsync` uses it as the reference (capped) and computes via the canonical `_compute` delegate (`WerCalculator`), **bypassing the phrase-store lookup**. `PhraseId` stays optional.
- [ ] **Cap the reference** (in-service, before the DP allocation) — over-cap → `Invalid` → sanitized `400 evaluation.invalid_phrase` (reuse the existing code/path); **reject empty/whitespace reference** → `400` (the calculator throws `ArgumentException` on empty reference per ARCH-015 — don't reach it). The hypothesis cap (`MaxHypothesisChars`) still applies.
- [ ] **Existing `phraseId` path byte-identical** (regression test): `{phraseId, hypothesis}` → store lookup → unchanged outcomes (Computed/PhraseNotFound/Invalid).
- [ ] **No-TurnId explicit path returns WER with no side effects** — no turn-attach, no `IsEvaluation`, no persist (the soak path).
- [ ] **Optional `TurnId`-attach still works** on the explicit path (orthogonal — unchanged behavior).
- [ ] Precedence when BOTH `phraseId` and `reference` are present — defined + pinned (Step-2.5 Q1).
- [ ] WAF wire test: `POST /wer {reference, hypothesis}` → 200 + `WerResponse`; over-cap/empty → 400 sanitized.
- [ ] Security-reviewer pass clean (or findings triaged). All unit tests pass; `/preflight` clean.

## Wiring / entry point (Step 7.5)
`POST /api/evaluation/wer` (`EvaluationController.ComputeWer` → `EvaluationService.ComputeWerAsync`). Confirm the explicit-reference branch is reachable from the route + the existing phraseId branch is unchanged. The FE soak runner (089b) is the production caller; the existing WER panel is the phraseId caller (must stay working).

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Evaluation/EvaluationService.cs` — the explicit-reference branch + the reference cap (before allocation).
- the `WerRequest` DTO (wherever it lives) — add optional `Reference`.
- `server/AiInterpreter.Tests/…EvaluationService…` (+ the WAF wire test file) — the new cases.

If more files are needed (e.g. a new `MaxReferenceChars` const placement), **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`explicit_reference_computes_against_supplied_reference`** — `{reference:"hello world", hypothesis:"hello word"}` (no phraseId) → WER computed against the supplied reference (NOT the store); assert the `_compute` delegate got the explicit reference (arg-order pin, §27 ADD-1 style — asymmetric reference≠hypothesis). Why: the canonical-WER use case.
2. **`explicit_reference_over_cap_rejected`** — reference length > cap → `Invalid` (400), `_compute` never called (spy). Why: the `n`-dimension DoS guard (guardrail 2).
3. **`explicit_reference_empty_rejected`** — empty/whitespace reference (with hypothesis present) → `Invalid` (400), calculator never reached. Why: ARCH-015 empty-reference precondition at the boundary.
4. **`hypothesis_cap_still_applies_on_explicit_path`** — explicit reference + over-cap hypothesis → `Invalid`. Why: §27 `m`-dimension guard preserved.
5. **`phraseid_path_unchanged`** — `{phraseId, hypothesis}` → store lookup → Computed (regression; reference branch not taken). Why: strictly-additive guardrail 1.
6. **`explicit_no_turnid_no_attach`** — `{reference, hypothesis}` no TurnId → `Computed(wer, persist:null)`, store `UpdateTurn` NEVER called (spy), no `IsEvaluation`. Why: the ⭐ soak-correctness pin (turns stay in the comparison).
7. **`explicit_with_turnid_attaches`** — `{reference, hypothesis, turnId, sessionId}` → attaches + marks `IsEvaluation` (orthogonal, unchanged). Why: the attach path still works on the explicit branch.
8. **`both_phraseid_and_reference_precedence`** — defined behavior (Q1). Why: ambiguous-input discipline.
9. **(WAF wire)** `POST /wer {reference, hypothesis}` → 200 `WerResponse`; empty/over-cap → 400 sanitized `evaluation.invalid_phrase` (no payload leak). Why: the boundary contract.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** `WerRequest` gains `Reference?` → **Cross-doc invariant change.** Flag at Step 9; the orchestrator writes the `server/CLAUDE.md` cross-doc row + the `ARCHITECTURE.md` Appendix A / ARCH-009 row + the **TS `WerRequest` mirror** note (web side, consumed by 089b) + the **§27 additive-exception note** (guardrail 3). Implementer does NOT edit those.

## Things to flag at Step 2.5
1. **Precedence when BOTH `phraseId` and `reference` are present.** (a) **reference-wins** (explicit overrides the store lookup); (b) **reject as ambiguous** (`400 invalid`); (c) phraseId-wins. My default vote: **(a) reference-wins** — the explicit path is the more specific intent; simplest for the soak (which never sends phraseId). Pin whichever with a test.
2. **Reference cap constant.** Reuse `MaxHypothesisChars` for the reference, or a dedicated `MaxReferenceChars`? Default: **reuse `MaxHypothesisChars`** (the DP is symmetric in n,m; one bound suffices; soak refs are short). Vote: **reuse** unless you see a reason to size them differently.
3. **Reject-empty mechanism.** Default: treat empty/whitespace `reference` (on the explicit branch) as `Invalid` → 400 (don't reach the calculator's `ArgumentException`). Vote: **boundary-reject → Invalid/400** (consistent with the cap path; never surface the calculator throw).
4. **DTO shape.** Add `Reference?` to the existing `WerRequest` (one endpoint, branch in-service) vs a separate endpoint. Default: **one endpoint, optional `Reference`, branch in-service** — additive, one route, the §27 chokepoint stays single. Vote: **optional field on `WerRequest`.**

## Dependencies + sequencing
- **Depends on:** the existing `/wer` (`EvaluationService.ComputeWerAsync`, §27) — present.
- **Blocks:** **089b** (the FE production WER wiring — POSTs `{reference, hypothesis}` per soak turn). 089a (FE runner orchestration) is in flight in parallel against the abstract seam.

## Estimated commit count
**1.** One additive endpoint-path extension + its validation. It touches an **input-validation boundary** (ARCH-019) → its OWN commit (not bundled — already standalone), with the **security-reviewer pass**. The cross-doc `WerRequest` field change rides the orchestrator's round commit (staggered per convention).

## Lessons-logged candidates anticipated
- **Convention candidate / §27 amendment** — "the WER chokepoint gained an additive explicit-reference path for the measurement-workbench/soak: when an external reference is supplied it is capped (the `n` dimension) alongside the hypothesis (`m`), bypassing the store; store-by-`phraseId` remains the default. Both DP dimensions must be bounded once the reference is external." (Orchestrator writes as a §27 note.)
- **Architecture-doc note candidate** — ARCH-009 `/wer` now accepts `{phraseId | reference}` + hypothesis; the soak uses the no-TurnId explicit path so its measured turns stay in the comparison.

## How to invoke
1. Read this brief + `docs/g4-harness-design.md` (the WER sub-decision) + skim lesson §27.
2. Run `/tdd wer_explicit_reference_path`.
3. Step 0 (Restate) → confirm against the Feature line + the strictly-additive + soak-no-attach pins.
4. Step 2.5 → send the test write-up + answers to the 4 questions.
5. Step 7→8 → run the **security-reviewer** (input-validation boundary).
6. Step 9 → flag the `WerRequest.Reference` cross-doc change + the §27 amendment.
