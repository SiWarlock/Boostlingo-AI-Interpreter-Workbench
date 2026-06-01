# /tdd brief — cascade_continuous_no_phantom_turn

## Feature
Harden the J.5 continuous-loop end-path so it doesn't spawn a **phantom/orphan** re-armed turn: when the user ends the conversation (or the session goes not-live) the loop must NOT `createTurn` a new backend turn that never captures speech, and a re-armed turn already waiting must be finalized cleanly (no hang, no surfaced error). Pairs with BE-083 (which makes an unavoidable empty auto-VAD turn Complete-silent + excluded from the comparison).

## Use case + traceability
- **Task ID:** J.6 (Phase-J smoke Finding 1 — FE half)
- **Architecture sections it implements:** ARCH-007 (capture lifecycle), ARCH-011 (cascade turns)
- **Related context:** Phase-J cascade-continuous smoke (Finding 1). BE half = brief 083. **Note (orchestrator-confirmed):** cascade CANNOT defer turn-creation until first speech (Deepgram — the speech detector — needs the turn's WS open first), so a speechless turn is sometimes unavoidable; BE-083 handles that one gracefully. THIS brief is hygiene: don't *create* orphan turns on end + finalize a waiting one cleanly.

## ⭐ Root (orchestrator-confirmed)
`recordingActions.ts` `rearmCascadeTurn` (≈157-194): on re-arm it `createTurn`s (POST → a backend turn) immediately, THEN checks `userEnded || !sessionLive` AFTER the async createTurn (≈184) — by which point the orphan backend turn already exists (created but never started/finalized). And a user-end while a re-armed turn is *waiting* (WS started, no speech) finalizes it → empty (→ failed pre-083).

## Acceptance criteria
- [ ] When `userEnded` (or the session is not-live) is true **before** the re-arm's `createTurn`, the loop does NOT `createTurn` — no orphan backend turn.
- [ ] When `userEnded`/not-live becomes true **during** the async `createTurn` (the existing ≈184 race window), the created turn is finalized cleanly (`client.stop()`) so it doesn't hang un-finalized; no error surfaced to the user.
- [ ] A re-armed turn that is *waiting* (started, no speech) when the user ends is finalized cleanly (relies on BE-083 → Completed-silence, not failed).
- [ ] The normal continuous loop (speech → finalize → re-arm) is UNCHANGED (no regression in J.5's 6 tests).
- [ ] `tsc`/eslint/`/preflight` clean.

## Files expected to touch
**Modified:**
- `web/src/state/recordingActions.ts` — guard `createTurn` on `userEnded`/liveness BEFORE the POST; finalize an orphan created during the race; ensure the waiting-turn end path is clean.
- `web/src/state/recordingActions.test.ts` — the new tests.

If implementation needs files beyond this, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`no_createTurn_when_already_ended_before_rearm`** — `userEnded` true before re-arm ⇒ `createTurn` NOT called (no orphan). Why: the primary hygiene fix.
2. **`orphan_finalized_when_end_races_createTurn`** — `userEnded`/not-live flips true DURING the async `createTurn` ⇒ the created turn is finalized cleanly (`client.stop` or equivalent), not left hanging. Why: the ≈184 race.
3. **`waiting_turn_clean_end_no_error`** — user ends with a started-but-speechless re-armed turn ⇒ `client.stop()`, no surfaced error, mic stopped. Why: clean end (works with BE-083).
4. **`normal_rearm_loop_unchanged`** — speech → finalize → re-arm still begins the next turn (J.5 regression). Why: no regression.

## Cross-doc invariant impact
- **Model field changes:** none (FE lifecycle only). No cross-doc.

## Things to flag at Step 2.5
1. **Guard placement** — check `userEnded`/liveness BEFORE `createTurn` (avoid the orphan) AND keep the post-createTurn re-check (the existing race guard)? **Default vote: both** — a pre-check avoids the common case (don't POST when ending); the post-check + clean-finalize covers the race.
2. **Orphan finalize mechanism** — `client.stop()` on the orphan vs a backend turn-cancel endpoint? **Default vote: `client.stop()`** (no new backend surface; the orchestrator closes the turn; BE-083 makes it Completed-silence). A turn-cancel endpoint is out of scope.
3. **Does the loop ever re-arm into pure silence repeatedly** (spawning back-to-back empty turns mid-conversation)? Per the orchestrator scope note, Deepgram utterance-end doesn't fire on pure silence, so a re-armed turn just waits (no repeated empties). **Default vote: no extra guard needed**; if the smoke shows repeated empties, flag at Step 9.

## Dependencies + sequencing
- **Depends on:** J.5 (082, landed). Pairs with BE-083 (the not-failed/excluded handling).
- **Blocks:** a fully-clean cascade-continuous session (no orphan/phantom turns).

## Estimated commit count
**1.** Focused end-path hygiene; FE-only, no safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — continuous-loop end-path hygiene: guard turn-creation on the user-end/liveness flag BEFORE the POST + cleanly finalize a race-created orphan; cascade can't create-on-speech (Deepgram needs the turn open), so the empty turn is handled (BE-083) not prevented.
