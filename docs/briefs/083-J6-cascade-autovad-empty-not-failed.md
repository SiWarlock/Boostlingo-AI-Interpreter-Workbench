# /tdd brief — cascade_autovad_empty_not_failed

## Feature
An auto-VAD cascade turn that captures **no speech** (empty-only Deepgram finals) must finalize as **Completed-silence, NOT failed** — so a continuous-loop (J.5) re-armed turn that catches a silent gap or is ended-while-waiting does not produce a spurious `cascade.empty_transcript` failure that pollutes the comparison `errorCount` + shows the session "failed" even though the conversation worked. Manual-mode empty turns stay `empty_transcript`-failed (the user deliberately recorded silence — a real signal).

## Use case + traceability
- **Task ID:** J.6 (Phase-J smoke Finding 1 — the visible false-error regression; BE half)
- **Architecture sections it implements:** ARCH-011 (cascade streaming status), ARCH-013 (summary errorCount)
- **Related context:** Phase-J cascade-continuous smoke (user, 2026-06-01, session `data/sessions/session_20260601T025341Z_ed23aff3.json` turn 4). SAME class as **052/069** (don't-false-fail-on-trailing-empty, §31/§22) now resurfacing in the J.5 continuous loop. FE half = brief 084 (prevent the phantom empty re-armed turn).

## ⭐ Root (orchestrator-confirmed)
`CascadeStreamingOrchestrator.cs:99-102` — `TerminalFailure()` returns `cascade.empty_transcript` on `sawEmptyFinal && !sawNonEmptyFinal` (the §31 two-flag). `p.AutoVad` IS in scope (used at the `SttUtteranceEnd when p.AutoVad` case). In auto-VAD, an empty-only turn = silence/gap (the VAD-delimited continuous loop), NOT a user error. Scope the failure to manual mode. `SessionSummaryService` counts errors via `Sum(t.Errors.Count)` → a Completed-silence turn (no errors) contributes 0.

## Acceptance criteria
- [ ] An **auto-VAD** turn with empty-only finals (`sawEmptyFinal && !sawNonEmptyFinal`) → **`Done(Completed)`**, no `cascade.empty_transcript` Error emitted.
- [ ] A **manual** turn with empty-only finals → **still** `cascade.empty_transcript` + `Done(Failed)` (regression preserved — deliberate silence is a real signal).
- [ ] The §31 three-outcome is otherwise preserved (pure-silence→Completed; has-content→Completed; dangling-partial→`cascade.unknown` — for BOTH modes).
- [ ] The auto-VAD utterance-end terminal path AND the stream-end path both honor the new scoping (the shared `TerminalFailure()` covers both).
- [ ] `/preflight` clean.

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Cascade/CascadeStreamingOrchestrator.cs` — `TerminalFailure()`: `&& !p.AutoVad` on the `empty_transcript` arm.
- (Conditional — see Step-2.5 Q2) `server/AiInterpreter.Api/Sessions/SessionSummaryService.cs` — exclude 0-transcript turns from the per-mode aggregation.
- Test files: `CascadeOrchestratorTests.cs` (+ `SessionSummaryServiceTests.cs` if Q2 taken).

## RED test outline (Step 2)
1. **`autovad_empty_only_finals_completes_not_failed`** — `p.AutoVad=true`, scripted STT yields empty-only final(s) ⇒ `Done(Completed)`, NO `Error`/`empty_transcript`. Why: the fix.
2. **`manual_empty_only_finals_still_fails`** — `p.AutoVad=false`, empty-only final(s) ⇒ `Error(empty_transcript)` + `Done(Failed)`. Why: regression preserved (deliberate silence).
3. **`autovad_pure_silence_completes`** — `p.AutoVad=true`, no finals at all ⇒ `Done(Completed)` (unchanged, §31 Q4). Why: no regression.
4. **`autovad_has_content_completes`** — `p.AutoVad=true`, a non-empty final ⇒ `Done(Completed)` with the content. Why: the loop's normal turn unaffected.
5. **`autovad_dangling_partial_still_unknown`** — `p.AutoVad=true`, partial-then-no-final ⇒ `cascade.unknown` + Failed (the lost-final case is a real failure, NOT silenced by the auto-VAD scoping). Why: don't over-broaden the silencing.
6. *(if Q2)* **`summary_excludes_empty_silence_turn`** — a 0-transcript Completed turn is excluded from `ModeSummary.TurnCount`/averages. Why: no phantom-turn inflation.

## Cross-doc invariant impact
- **Model field changes:** none. (If a new `TurnStatus` were added — it is NOT; Completed reused.)
- **Architecture note (orchestrator writes):** ARCH-011/013 — auto-VAD empty = silence→Completed (not failed); pairs with §31/§22.

## Things to flag at Step 2.5
1. **`&& !p.AutoVad` scoping** — auto-VAD empty → Completed-silence; manual → empty_transcript-failed. **Default vote: yes** — in VAD-delimited continuous mode an empty turn is a gap, not a user error; manual silence is deliberate. The dangling-partial→`cascade.unknown` path stays for BOTH modes (test 5) — a lost final is a real failure regardless.
2. **Also exclude 0-transcript turns from `ModeSummary`?** (belt-and-suspenders — a phantom Completed-silence turn would still inflate `TurnCount`.) **Default vote: YES, add the exclusion** (skip turns with no transcripts from the per-mode TurnCount/averages, like §29's IsEvaluation exclusion) — so even if the FE-084 prevention misses one, the comparison stays clean. Confirm this doesn't hide a real content-losing bug (the §31/§22 failure paths still flag genuine losses).
3. **Persist the empty Completed turn at all?** The BE doesn't control turn creation (the FE does, brief 084). The BE just declines to fail it. **Default vote: BE declines-to-fail + excludes-from-summary; the FE prevents the phantom (084).**

## Dependencies + sequencing
- **Depends on:** J.5 (082, landed — the continuous loop that surfaces this). Pairs with FE-084.
- **Blocks:** a clean cascade-continuous comparison (no false "Errors: 1").

## Estimated commit count
**1–2.** The orchestrator scoping (1); the summary exclusion (Q2) may be a sibling commit. No safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — auto-VAD/continuous empty = silence→Completed (the §31/§22/§52/§69 empty-final lineage extended: deliberate-manual-silence fails, VAD-gap-silence completes); 0-transcript turns excluded from the comparison.
