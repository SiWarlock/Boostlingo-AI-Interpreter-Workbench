# Session 029 — Frontend: live-Finding fixes (cached-audio cost, push-to-talk WER, display-turn) (095/096/099)

- **Date:** 2026-06-01
- **Phase:** G.5-adjacent cost-accuracy + live-test Finding closure (frontend)
- **Predecessor:** [025 — frontend G.4 synthetic soak-harness](025-2026-06-01-frontend-g4-soak-harness.md)
- **Successor:** _(TBD — next FE session)_

## Why this session existed

A fresh frontend implementer cycled in post-G.4-soak to (a) land the FE half of a cost-accuracy correction surfaced by the live soak run, and (b) close the user's live-test Findings. Three slices landed this round:

- **095** — forward the cached input-**AUDIO** subset to `/complete` (pairs with BE **094**'s realtime cached-audio repricing); a soak-surfaced cost-accuracy fix.
- **096** — replace the WER panel's silent fixed-6 s auto-record with **push-to-talk** + a no-speech honest-degrade (**Finding 3**, long-pending, live-confirmed).
- **099** — one shared `selectDisplayTurn` selector so a trailing empty auto-VAD turn no longer blanks the per-turn Latency/Cost cards (**Finding C**; the FE-display sibling of the backend `IsEmptySilence` work in 097/098).

All three are deterministic FE logic, driven through `/tdd` (RED → Step-2.5 orch review → GREEN). Web suite **394 → 406 green** across the round.

## What was built (by slice)

### 095 — forward cached AUDIO subset to /complete (`5237820`)

**Modified:**
- `web/src/realtime/realtimeEvents.ts` — `extractRealtimeUsage` now reads `input_token_details.cached_tokens_details.audio_tokens` (the cached input-AUDIO subset) into `cachedAudioInputTokens`, instead of the aggregate `cached_tokens` (text+audio) it forwarded before. Guarded via `asObject(inputDetails?.cached_tokens_details)?.audio_tokens`; present-0 kept, absent-breakdown omitted (no aggregate fallback). Updated 3 comment blocks + corrected a pre-existing `§25→§26` lesson citation at lines 11 + 74 (code-comment fix, orch-authorized).
- `web/src/realtime/realtimeEvents.test.ts` — 2 new tests (the discriminator: `cached_tokens:512` + `cached_tokens_details:{text:192,audio:320}` → `320`; present-0 kept) + 1 repurposed (no-fallback-to-aggregate, pins the drop) + 2 fixture refreshes.
- `web/src/realtime/realtimeTurnController.test.ts` — 3 cached-0 fixtures gained `cached_tokens_details:{audio_tokens:0}` to keep the real-0-forwarded assertions green.

CF76 live evidence confirmed the GA shape (`cached_tokens_details:{text_tokens,audio_tokens}`). Wire SHAPE unchanged (`number|null`); documented semantics only (aggregate → audio-subset), coordinated with BE 094.

### 096 — push-to-talk WER recording + no-speech honest-degrade (`ab93fbd`, Finding 3)

**Modified:**
- `web/src/audio/audioCaptureController.ts` — ADD `startBlobRecording(): Promise<BlobRecordingHandle|null>` (manual-stop seam: opens the mic, returns `{stop}` that ends + resolves the blob; 60 s safety auto-stop backstop via `MAX_BLOB_DURATION_MS`). REMOVED the now-orphaned `recordBlob` (+ dropped it from the `AudioCaptureController` type).
- `web/src/state/evaluationActions.ts` — `runEvaluation` → `evaluateFromBlob(deps, blob, input)` (the panel owns the recording lifecycle; dropped the record step, the `capture` dep, and `EVAL_RECORD_DURATION_MS`). ADD the no-speech guard (empty/whitespace hypothesis → `{kind:'no-speech'}`, NO eval turn created). `EvaluationOutcome` → discriminated union `{kind:'scored';…} | {kind:'no-speech'}`.
- `web/src/components/EvaluationPanel.tsx` — push-to-talk state machine `idle → countdown (3·2·1) → recording (visible "click Stop") → evaluating → result`; renders "No speech detected — n/a" for the no-speech outcome (never a `%`); Record available again after a result (retry); mic-fail resets to idle + surfaces `capture.failed`.
- `web/src/state/evaluationActions.test.ts` — retargeted to `evaluateFromBlob` (9 tests incl. the no-speech core).
- `web/src/components/EvaluationPanel.test.tsx` — push-to-talk state-machine + no-speech render + retry + mic-fail (8 tests, fake timers for the countdown).

Removed symbols (`recordBlob`/`runEvaluation`/`EVAL_RECORD_DURATION_MS`) — grep-confirmed zero remaining refs (Step-1 confirmed `recordBlob`'s only caller was `runEvaluation`).

### 099 — selectDisplayTurn skips trailing empty-silence (`692fea4`, Finding C)

**New:**
- `web/src/components/CostPanel.test.tsx` — the Finding-C cost-sibling jsdom test (good priced turn stays displayed after a trailing empty turn).

**Modified:**
- `web/src/state/selectors.ts` — ADD `selectDisplayTurn(state)` + the `isEmptySilenceTurn` predicate (`sourceTranscript.length===0 && targetTranscript.length===0 && cost == null`). Selection: prefer a non-empty `currentTurn` → most recent non-empty turn in `turns[]` → today's `currentTurn ?? turns[last]` fallback.
- `web/src/components/MetricsPanel.tsx` / `web/src/components/CostPanel.tsx` — both swap the duplicated inline `currentTurn ?? turns[last]` → `selectDisplayTurn(state)` (dropped the now-unused `TurnViewModel` import in each).
- `web/src/state/selectors.test.ts` — `selectDisplayTurn` describe (5 tests, incl. the AND-not-OR cost-bearing-turn-is-meaningful pin).
- `web/src/components/MetricsPanel.test.tsx` — the Finding-C trailing-empty-turn describe (1 test) + a `TranscriptSegment` fixture fix.

## Decisions made

- **095:** read the cached-AUDIO subset, not the aggregate (the BE discounts only cached audio at the cached rate — sending the aggregate would over-subtract); drop the aggregate `cached_tokens` read entirely (no consumer); absent breakdown → omit (never fabricate 0).
- **096:** push-to-talk (Option A, user choice) with a 3·2·1 countdown lead-in (directly answers the "it never told me when" repro); no-speech detected at the empty/whitespace hypothesis AFTER transcribe (let the real STT be the judge; an empty transcript IS the signal); no eval turn created for a no-speech capture (avoids an orphan turn / WerSummary pollution); REMOVE the orphaned `recordBlob`/`runEvaluation` (Q2 — no other caller, TIMEBOX-OFF clean removal over keeping production-dead code).
- **099:** keep-last-meaningful (Option A) over a placeholder; the empty-silence predicate is AND-not-OR so a cost-bearing 0-transcript realtime turn stays meaningful (the FE mirror of the backend 097 cost-bearing semantics); one shared selector both panels use (no whack-a-mole).

## Decisions explicitly NOT made

- **096 — no separate pure reducer/state-machine helper.** The 4-state transitions are thin React state, fully covered by the jsdom test; extracting a reducer would be over-engineering (orch-endorsed).
- **096 — no client-side silence detection from the blob.** The STT (empty transcript) is the no-speech judge; the zero-byte-blob `capture.empty` guard is kept for a truly empty recording.
- **099 — no "currentTurn is recording" override.** During a new turn's first instant (before transcripts arrive) the display briefly shows the prior meaningful turn, updating the moment content lands — accepted as far better than a persistent n/a.

## TDD compliance

**Clean — all three slices RED-first.** Each: tests written → Step-2.5 orch review (`APPROVED.`) → confirmed RED for the right reason → minimum GREEN → full suite → gates. No TDD violations.
- 095: 3 tests failed (320≠512, present-0 vs omit, no-fallback) before the source swap.
- 096: 14 tests failed (missing `evaluateFromBlob` export, no `status`/Stop, no `capture.failed`) before the state machine.
- 099: 7 tests failed (`selectDisplayTurn is not a function` ×5 + both panels landing on the empty turn) before the selector.

## Reachability (Step 7.5 — all wired)

- **095:** realtime `response.done` DC handler → `normalizeRealtimeEvent` → `responseDone.usage` → `finalizeTurn` (`realtimeTurnController.ts:166-167`) → `POST …/turns/{turnId}/complete`. Field-source change in an already-wired extractor.
- **096:** `App.tsx` → `EvaluationPanel` → Record button → `audioCaptureController.startBlobRecording()`; Stop → `evaluateFromBlob` → `/transcribe` + createTurn + `/wer`.
- **099:** `App.tsx` → `MetricsPanel` + `CostPanel` → `selectDisplayTurn(useSessionState())`. **Grep-confirmed the old `turns[length-1]` expression is gone from both panels.**

No tested-but-unwired gaps.

## Open follow-ups (Step-9 categorized — orchestrator routes/verifies at `/orchestrate-end`)

All items were hot-routed by the orchestrator during the round; surfaced here for the `/orchestrate-end` verification pass:

- **Convention candidates (→ web LESSONS + CLAUDE.md index) — ROUTED:** §36 (095 cached-audio-subset), §37 (096 push-to-talk + no-speech degrade), §38 (099 selectDisplayTurn). Confirmed present in `web/CLAUDE.md` lessons index.
- **Architecture-doc notes (→ ARCHITECTURE.md) — orch hot-routing in the working tree (verify):** ARCH-014 (095 cached-audio semantics), ARCH-017/ARCH-015 (096 push-to-talk eval capture + no-speech n/a), ARCH-013 (099 per-turn display = last meaningful turn).
- **Cross-doc invariant note (→ web/CLAUDE.md `CompleteTurnRequest` row) — ROUTED:** row 136 carries the 094/095 `cachedAudioInputTokens` aggregate→audio-subset semantic note (wire shape unchanged).
- **Lesson-supersession to consider (orch's call):** §20 ("reuse `recordBlob`; `EVAL_RECORD_DURATION_MS`") is now partly superseded by §37 (096 removed `recordBlob` for `startBlobRecording`). Not edited by me — flag for the orch to annotate §20 if desired.
- **No cross-doc invariant DRIFT:** 095 = documented-semantics only (shape unchanged); 096's `EvaluationOutcome`/`AudioCaptureController` are frontend-internal (not wire mirrors); 099 is a pure selector — none require a new table row.

## How to use what was built

- The WER panel is now push-to-talk: click **Record**, wait for the 3·2·1 countdown, read the phrase, click **Stop**. An empty/no-speech capture shows "No speech detected — n/a" (retry with Record).
- Per-turn Latency + Cost cards now stay on the last meaningful turn through cascade continuous/auto-VAD silence turns.
