# /tdd brief — session_label_auto_derive

## Feature
When a session's `Label` is blank, auto-derive a meaningful label at persist time so the history list stops showing raw session ids. Derived label = the session's **first source-final transcript snippet** (truncated ~40 chars + "…"), with a **mode + direction fallback** (e.g. `Cascade · EN→ES`, `Realtime+Cascade · EN→ES`) when the session has no source transcript. A user-typed label always wins (only fill when blank).

## Use case + traceability
- **Task ID:** user enhancement (history UX) — backend half. Low/normal priority; slots into the metrics/cost round (BE is idle; doesn't compete with the FE's 076).
- **Architecture sections it implements:** `ARCHITECTURE.md` ARCH-016 (session persistence / history read tier), ARCH-017 (Flow H end/persist), ARCH-009 (`GET /api/sessions` / `SessionListItem`).
- **Related context:** user request (via the lead). `SessionListItem.FromSession` (SessionListItem.cs:25-27) reads `session.Label` directly; the FE history row already renders `item.label ?? item.sessionId` (SessionHistory.tsx:116) + mode chips + timestamp **separately** — so the label string is JUST the utterance snippet (do NOT stuff mode/time into the derived label; they're already in the row). **FE needs NO change** (confirm only). `Label` is `[MaxLength(512)]` (SessionDtos.cs:14) on `InterpretationSession` (SessionModels.cs:50). `TranscriptSegment` has `Role` (string) + `IsFinal` (bool) (SessionModels.cs:89).

## Acceptance criteria (what "done" means)
- [ ] A pure `DeriveSessionLabel(InterpretationSession) → string` (or equivalent helper):
  - Returns the existing `Label` **unchanged** when it is non-blank (after trim) — a user-typed label always wins.
  - Else, when a first **source-role final** transcript exists across the session's turns, returns its text **truncated to ~40 chars + "…"** (only append "…" when actually truncated).
  - Else (no source transcript), returns the **mode + direction fallback**: the session's distinct modes joined (`Cascade` / `Realtime` / `Realtime+Cascade`) + ` · ` + `SOURCE→TARGET` (e.g. `Cascade · EN→ES`).
  - Treats empty/whitespace `Label` as blank.
  - Result always within `MaxLength(512)` (trivially true given the ~40-char truncation + short fallback — assert it as a guard).
- [ ] The derivation is applied at the **session-end persist** path (`SessionService.EndAsync`): when `Label` is blank, the finalized session (in-memory update + persisted JSON) gets the derived label → `GET /api/sessions` shows it, `GET /{id}` shows it, the raw file carries it.
- [ ] **No FE change** — `SessionListItem.FromSession` keeps reading `session.Label` (now populated); confirm-only.
- [ ] **Quick `security-reviewer` sanity** (per the lead): the snippet is transcript text already persisted in the same session JSON (turns[].transcripts) — confirm no new data exposure / no secret/PII-handling change. (Single-trusted-user, own data — ARCH-002.)
- [ ] `/preflight` clean (backend suite green).

## Files expected to touch
**New:**
- `Sessions/SessionLabelDeriver.cs` (or a static helper co-located with the session models) — the pure `DeriveSessionLabel`.
- `SessionLabelDeriverTests.cs` (`server/AiInterpreter.Tests/`) — the pure-function tests.

**Modified:**
- `Sessions/SessionService.cs` — `EndAsync`: when `Label` is blank, set `session = session with { Label = DeriveSessionLabel(session) }` before/at persist.
- An existing `SessionService`/`EndAsync` test file — the wiring test (blank label + a source transcript → persisted label = snippet; user label preserved).

If implementation needs files beyond this list (e.g. the source `Role` constant lives elsewhere, or `EndAsync` makes the in-memory update awkward), **flag at Step 2.5** before going GREEN.

## RED test outline (Step 2)

**Pure `DeriveSessionLabel` (`SessionLabelDeriverTests.cs`):**
1. **`user_label_wins`** — non-blank `Label` ("My demo") → returned unchanged (even when a transcript exists). Why: user intent always wins.
2. **`derives_from_first_source_final_transcript`** — blank label, a source-final transcript "Hello, how are you today and beyond" → label = first ~40 chars + "…" (truncated). Why: the core feature.
3. **`no_truncation_marker_when_short`** — blank label, short source-final transcript "Hello there" → label = "Hello there" (no "…"). Why: only append "…" when truncated.
4. **`skips_non_source_and_non_final`** — target-role or non-final segments are skipped; the FIRST source-**final** wins. Why: pin the selection.
5. **`mode_direction_fallback_when_no_transcript`** — blank label, no source transcript, modes {Cascade}, direction EN→ES → "Cascade · EN→ES"; modes {Realtime, Cascade} → "Realtime+Cascade · EN→ES". Why: graceful fallback.
6. **`whitespace_label_treated_as_blank`** — `Label = "   "` → derives (not returned as-is). Why: edge case.
7. **`result_within_maxlength`** — derived value length ≤ 512. Why: guard the persistence constraint.

**Wiring (`EndAsync` test):**
8. **`end_fills_blank_label_with_derived`** — end a session with a blank label + a source-final transcript → the finalized/persisted session's `Label` == the derived snippet. Why: the persist-time application.
9. **`end_preserves_user_label`** — end a session with a user-typed label → `Label` unchanged. Why: regression guard.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** NONE — `Label` already exists on `InterpretationSession` + `SessionListItem`; this populates it, no shape change.
- **Orchestrator doc rows to write hot:** none (no cross-doc table row, no Appendix A change). Possible ARCH-016/017 realization note (blank session labels are auto-derived from the first source utterance at end-persist, mode+direction fallback) — orchestrator folds at round-seal.

## Things to flag at Step 2.5
1. **Where to apply the derivation — `EndAsync` (session-end) vs the `SessionPersistenceWriter` chokepoint?** My default vote: **`EndAsync`** — the canonical finalize+persist where the full session + all transcripts exist, and the label "finalizes" at end (a mid-flight per-turn persist can keep `null`; the user looks at ended sessions). The writer-chokepoint alternative would derive on every write (mid-flight too) but is more invasive. Confirm.
2. **Source-role value.** The "source" transcript role — confirm the exact `Role` string/constant (`"source"`?) used by the cascade + realtime producers; select the first segment matching source-role AND `IsFinal`.
3. **Truncation length + boundary.** ~40 chars + "…". Default: a hard 40-char cut (don't over-engineer word-boundary trimming) + "…" only when the text exceeds 40. Confirm the exact cap (40 vs 50) — my lean 40.
4. **Fallback mode ordering / formatting.** `Realtime+Cascade` (order? dedup?). Default: distinct modes in a stable order (Realtime before Cascade), `+`-joined, ` · `, then `SOURCE→TARGET` uppercased. Confirm.

## Dependencies + sequencing
- **Depends on:** nothing — self-contained backend at the persist seam.
- **Blocks:** nothing. Independent of 076 (FE) — can land in parallel.

## Estimated commit count
**1.** One focused backend feature (pure deriver + the end-persist wiring). Not a safety invariant (light security-reviewer sanity only, per the lead). Bisectable as one unit.

## Lessons-logged candidates anticipated
- **Architecture-doc note candidate** — ARCH-016/017: a blank session label is auto-derived at end-persist from the first source-final utterance (≤40-char snippet), with a mode+direction fallback; a user-typed label always wins.

## How to invoke
1. Read this brief end-to-end.
2. Run `/tdd session_label_auto_derive`.
3. Step 0 (Restate) — confirm against the Feature line.
4. Step 2.5 — send the per-test write-up + your read on the 4 design questions; I reply `APPROVED.`/`TWEAK:`/`ADD:`.
5. Step 7→8 — **quick `security-reviewer` sanity** (per the lead — transcript text already persisted; confirm no new exposure).
6. Step 9 — categorized summary + ship/no-ship + draft commit message.
