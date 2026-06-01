# /tdd brief — wer_pushtotalk_recording_ux

## Feature
Fix the interactive WER EvaluationPanel's recording affordance (Finding 3): replace the silent fixed-6s auto-record with **push-to-talk** (click-to-start → visible "recording…" → user reads at their own pace → click "Stop" → evaluate), plus a short "get ready" countdown lead-in, a visible recording state, and an empty/silent-capture guard that renders "no speech detected — n/a" instead of a misleading "100%".

## Use case + traceability
- **Task ID:** Finding-3 / WER-UX (long-pending Carry-forward; user-confirmed live repro 2026-06-01)
- **Architecture sections it implements:** `ARCH-015` (WER evaluation), `ARCH-017` (Flow D eval user flow), `ARCH-007` (frontend component/state separation), `ARCH-030` (audio capture)
- **Related context:** the live-confirmed repro — "Record & evaluate" silently records ~6s with NO recording indicator / NO "speak now" prompt / NO countdown, so the user speaks late or not at all → STT mis-hears → a *mathematically-correct-but-misleading* high WER (screenshot: ref "Please describe your symptoms to the doctor" / hypothesis "I think he understood" / 4 subs + 3 del = 7/7 = 100%). This is a **recording-affordance UX bug, NOT a WER-math or empty-hypothesis bug** (lesson §34 — verified against the live repro, not code-only). **User chose Option A (push-to-talk)** over a fixed-window countdown or VAD-autostop (lead AskUserQuestion, 2026-06-01). web LESSONS §8 (capture = manual-smoke shell over pure helpers), §20 (reuse `recordBlob`; `recordBlob` returns null silently on mic-fail), §7 (DI'd actions module), §11 (inFlight guard), §14 (jsdom component tests + `errorCopy`-style copy).

## Current behavior
`EvaluationPanel.handleEvaluate` → `runEvaluation` (`evaluationActions.ts`) calls `capture.recordBlob(EVAL_RECORD_DURATION_MS=6000)` — a fixed-duration auto-stop (`audioCaptureController.recordBlob`: getUserMedia → MediaRecorder → `setTimeout(stop, clamped)`); no visible recording state; the window is not user-controlled. `recordBlob`'s only caller is `evaluationActions` (confirm at Step 1 — the soak harness injects at `getUserMedia`, doesn't use `recordBlob`).

## Acceptance criteria (what "done" means)
- [ ] The panel is a small state machine: `idle → countdown → recording → evaluating → result` (+ back to idle). Each state has a distinct, visible affordance.
- [ ] **Push-to-talk:** click "Record" → a short countdown lead-in ("Get ready… 3·2·1") → recording starts with a **visible "Recording — read the phrase, click Stop when done"** state → click "Stop" → evaluate (transcribe → score). The window is fully user-controlled (no fixed auto-cut).
- [ ] A **safety max-duration auto-stop** still bounds the recording (reuse the existing `clampBlobDurationMs` cap, ~60s) so a stuck recording can't hold the mic open unbounded.
- [ ] **Empty/silent-capture guard:** when the transcribe returns an empty/whitespace hypothesis (no speech detected), the panel renders **"No speech detected — n/a"** (a distinct outcome), NOT a WER score and NEVER a confident "100%". The existing zero-byte-blob guard (`capture.empty`) is preserved.
- [ ] Mic-denied / unsupported-mime / recorder-error still surface the existing sanitized `capture.failed` (web §20); the recording state resets to idle on any failure.
- [ ] The `inFlight` double-click guard (web §11) is preserved across the new states (can't start a second recording / a second evaluate).
- [ ] Component renders only from local state + the store (errors); no transport internals (clean separation, forbidden-pattern #3). Existing aria-labels/role hooks (the phrase `<select>`, `reference-text`, `wer-result`, `hypothesis`, the WER explanation) preserved; new states get queryable hooks (e.g. an accessible "recording" status + the Stop button).
- [ ] All unit tests in `web/src/state/evaluationActions.test.ts` (+ any pure capture-helper test) pass; the jsdom `EvaluationPanel` component test covers the state transitions + the no-speech outcome.
- [ ] `/preflight` clean (`format:check && lint && typecheck && test`).

## Wiring / entry point (Step 7.5)
`EvaluationPanel` (rendered in `App.tsx`) → the new push-to-talk handlers → the (refactored) evaluation flow → `POST /transcribe` + `POST /wer`. Confirm the new capture seam + the split evaluate flow are reached from the real panel buttons, not just tests.

## Files expected to touch
**New:**
- (likely) a pure capture-state or countdown helper + its test, IF the state machine has TDD-able pure logic worth isolating (implementer's call at Step 2.5).

**Modified:**
- `web/src/audio/audioCaptureController.ts` — ADD a manual-stop capture seam (e.g. `startBlobRecording(): Promise<{ stop: () => Promise<BlobCapture | null> } | null>`) alongside the existing `recordBlob` (do NOT change `recordBlob`'s fixed-duration signature — preserve any other caller). The new seam starts the MediaRecorder + returns a `stop()` that triggers `recorder.stop()` and resolves the blob; keep the safety max-duration auto-stop + the mic release on stop/error.
- `web/src/state/evaluationActions.ts` — split the record-then-evaluate flow so the panel owns the recording lifecycle: a flow that takes a **pre-captured blob** (`transcribe → createTurn → computeWer`) + returns a `no-speech` outcome when the hypothesis is empty (vs a WerResult). Keep errors → store (single sink, §7).
- `web/src/components/EvaluationPanel.tsx` — the push-to-talk state machine + visible recording/countdown states + the no-speech rendering.
- test files for the above.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
**`evaluationActions` (the split evaluate-from-blob flow):**
1. **`evaluate_from_blob_empty_hypothesis_returns_no_speech`** — transcribe yields `""`/whitespace → returns a distinct `no-speech` outcome (NOT a WerResult; no fabricated 100%). Why: the honest-degrade core of the fix.
2. **`evaluate_from_blob_happy_path`** — non-empty hypothesis → `transcribe → createTurn → computeWer` → returns `{hypothesis, werResult}`. Why: the real path still works.
3. **`evaluate_from_blob_transcribe_failure_aborts`** — transcribe throws → sanitized `evaluation.transcribe_failed` to the store, no createTurn/score. Why: preserve the §20 abort arm.
4. **(if a pure state/countdown helper is extracted)** transitions: `idle→countdown→recording→evaluating→result`; a stop during countdown cancels cleanly; double-start guarded.

**`audioCaptureController.startBlobRecording`** is a manual-smoke shell over the MediaRecorder (browser-exempt, web §8/§20) — pin only any pure helper (e.g. the duration clamp reuse); the start/stop wiring is smoke-verified.

**`EvaluationPanel` (jsdom, web §14):** clicking Record enters countdown→recording (asserts the visible recording status + the Stop button); clicking Stop evaluates; the no-speech outcome renders "No speech detected — n/a" (not a `%`); a mic-fail resets to idle + surfaces `capture.failed`.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none expected (no wire-contract change — `/transcribe` + `/wer` unchanged; this is FE capture/flow/UI). The `EvaluationOutcome` type may gain a `no-speech` variant (frontend-internal, not a wire mirror).
- **Orchestrator doc rows to write hot:** none anticipated (confirm at Step 9). A convention candidate (push-to-talk capture seam + honest no-speech degrade) is likely → I write the web LESSONS entry.

## Things to flag at Step 2.5
1. **Countdown lead-in — keep it, and how long?** Push-to-talk usually records immediately on click, but the user complaint ("didn't know when") + the lead's spec call for a short "get ready" beat. My default vote: **a 3·2·1 (~3s) countdown after clicking Record, THEN recording starts (visible state) until the user clicks Stop.** Alternative: record immediately on click with just a prominent "Recording — speak now" state (no countdown). Lean: include the short countdown — it directly answers "it never asked me to record."
2. **New capture seam vs refactor `recordBlob`.** My default vote: **ADD `startBlobRecording` (manual-stop), leave `recordBlob` intact** — confirm `recordBlob` has no other caller (Step 1); if truly only `evaluationActions` uses it, the implementer MAY instead refactor, but adding is the lower-risk default.
3. **Empty/no-speech detection — where + threshold.** My default vote: **detect an empty/whitespace hypothesis in the evaluate-from-blob flow** (after transcribe) and return a `no-speech` outcome → the panel renders "No speech detected — n/a". (Don't try to detect silence client-side from the blob — let the real STT be the judge; an empty transcript IS the signal.) Keep the existing zero-byte-blob `capture.empty` guard for a truly empty recording.
4. **Safety max-duration.** My default vote: **keep a max auto-stop** (reuse `clampBlobDurationMs` ~60s) as a backstop so push-to-talk can't hold the mic open forever; the user's Stop is the normal path.

## Dependencies + sequencing
- **Depends on:** nothing technical. **Sequencing (lead directive):** slot AFTER the cost re-run + G.5 cost finalize — do NOT dispatch/interrupt the re-run. Authored now; dispatched on the lead's go.
- **Blocks:** nothing; closes the long-pending Finding 3 (the WER quality axis becomes trustworthy for a live demo).

## Estimated commit count
**1** (possibly 2 if the capture-seam addition is cleanly separable from the panel/flow change and the implementer prefers to land the seam first). Not a safety-invariant slice; reviewer fan-out stays disabled per standing directive. The implementer may take an on-demand single review at their discretion (UX state machine + a capture seam — not a Key-safety-rule surface).

## Lessons-logged candidates anticipated
- **Convention candidate** — "an evaluation/measurement capture must make its recording window USER-VISIBLE + user-controlled (push-to-talk); a silent fixed auto-window produces mathematically-correct-but-misleading scores; degrade an empty/no-speech capture to an explicit n/a, never a confident 100%."
- **Architecture-doc note candidate** — ARCH-017 Flow D: the eval capture is push-to-talk with a no-speech honest-degrade.

## How to invoke
> Do NOT prescribe `/session-start` (the FE session is oriented). Jump to pre-flight + `/tdd`.
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd wer_pushtotalk_recording_ux` in the frontend (`web/`) implementer session — **only after the orchestrator dispatches it** (post cost re-run + G.5 finalize).
3. Step 0 restate → Step 1 file list (confirm `recordBlob` callers) → Step 2 RED → **Step 2.5 ping the orchestrator** with test designs + answers to the 4 questions → GREEN after approval.
4. Step 9: surface any cross-doc/lesson candidates + the draft commit message.
