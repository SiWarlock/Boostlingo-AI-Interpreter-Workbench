# /tdd brief — realtime_auto_vad_wiring (Phase-I Bug C; realtime auto-VAD doesn't finalize)

> **Frontend slice (`web/`).** Phase I (062/063/066) was supposed to deliver both-modes auto-VAD; the live smoke shows
> **cascade auto-VAD works but realtime does NOT auto-finalize** on end-of-speech. Realtime VAD is browser-side
> (`session.update` `turn_detection` / server-VAD), so there's no backend signal — this is a FE wiring fix. No safety
> invariant. (The realtime-cost-n/a half is SEQUENCED AFTER this — the lead captures realtime `response.done.usage`
> once auto-VAD works OR a manual-stop fires.)

## Feature
In auto turn-control mode, the realtime transport must (1) apply server-VAD via `turn_detection` in `session.update`
AND (2) auto-finalize the turn on end-of-speech — mirroring the cascade utterance-end auto-finalize. Today the toggle
exists (063-s1) but realtime never auto-closes the turn.

## Use case + traceability
- **Task ID:** Phase-I Bug C (realtime auto-VAD). **Architecture:** ARCH-003 (turn-control, REVISED for auto-VAD),
  ARCH-010 (realtime transport / GA events), ARCH-007 (store + controller).
- **Related:** **063-s1** (`TurnControlMode`/`turnControlMode` + the realtime `session.update` `turn_detection` branch
  — auto→`server_vad`, manual→`null`), **063-s2/066** (the cascade auto-VAD that WORKS — the parallel to mirror),
  **053-C** (the realtime GA event shapes: `session.created turn_detection:null` confirmed manual; the captured DC
  event sequence). Web cross-doc row for `TurnControlMode` (063-s1).
- **User report (lead, live smoke):** realtime does NOT auto-finalize on end-of-speech despite the toggle; cost is
  n/a on BOTH modes. Realtime cost rides the DC `response.done.usage` (a separate path from cascade) → the lead can't
  capture realtime cost until auto-VAD works or a manual-stop → **fix C first**, then the lead re-captures.

## ⭐ Pre-orient at Step 1 (INVESTIGATE the root cause — confirm the surfaces)
- The realtime transport client (the WebRTC/DC controller) — where `session.update` is built + sent. **Confirm: does
  auto mode actually EMIT `turn_detection:{type:'server_vad', …}` (the GA shape) in `session.update`?** (063-s1 claims
  the branch exists — verify it fires live, not just in a unit.)
- **The server-VAD event handling** — in auto mode OpenAI server-VAD fires speech-lifecycle events on the DC
  (`input_audio_buffer.speech_started`/`.speech_stopped`/`.committed`, `response.created`/`.done`). Confirm whether the
  controller HANDLES those to FINALIZE the turn — or whether it only finalizes on a manual `Stop` (the likely gap:
  audio flows but the turn never closes because nothing routes the server-VAD terminal to the finalize seam).
- **The toggle path:** `turnControlMode` (store) → the realtime controller — confirm the toggle REACHES the controller
  + is read at `session.update` time (not stale).

## Design (verify-then-fix; the live auto-finalize is the lead's re-capture)
- **Deterministic wiring (TDD'able):** auto mode → the realtime `session.update` payload carries
  `turn_detection:{type:'server_vad'}` (GA shape — confirm vs the 053-C capture + Context7, the §18/§20
  verify-the-real-shape discipline); the controller, on the server-VAD terminal event, routes through the **SAME
  turn-finalize seam the manual `Stop` uses** (one seam, idempotent — mirror the cascade single-finalize). Manual mode
  → `turn_detection:null` (unchanged).
- **The likely bug (diagnose at Step 1, report at Step 2.5):** (i) the toggle isn't applied to `session.update`
  (stale / not sent), or (ii) `session.update` is sent but the controller doesn't handle the server-VAD events to
  finalize (audio plays, turn never closes), or (iii) the GA `type` strings for the server-VAD lifecycle differ from
  what the controller expects (the 053-C/§18/§20 class). Fix the deterministic part; flag the live-confirm.
- **Browser-exempt boundary:** the actual WebRTC/mic/server-VAD round-trip is manual-smoke (ARCH-020). TDD the
  deterministic parts (the `session.update` payload shape; the event→finalize routing GIVEN a server-VAD event); the
  LIVE auto-finalize is the lead's re-capture.

## Acceptance (RED-first deterministic; live-confirm flagged)
- [ ] Auto mode → the realtime `session.update` payload includes `turn_detection:{type:'server_vad'}` (GA shape);
      manual → `turn_detection:null`. Pin the payload builder.
- [ ] Given a server-VAD terminal event (the GA `type`, fed to the controller's event handler), the controller
      finalizes the turn through the SAME seam as a manual `Stop` (one finalize path; idempotent — no double-finalize).
      Pin via a scripted-event test.
- [ ] The toggle is read at `session.update` time (not stale) — auto set before connect applies; a mid-session toggle
      updates. Pin the read.
- [ ] **(Flag, not unit-pinned)** Live auto-finalize on end-of-speech — the lead's re-capture confirms; re-pin any
      divergent GA `type` to the web realtime event map.
- [ ] `/preflight` (web) clean.

## Cross-doc invariant impact (flag at Step 9)
- Likely none (no new wire model). IF the realtime event map gains a server-VAD `type` constant, that's an internal
  constant (no wire-mirror change). Confirm at Step 1.

## Things to flag at Step 2.5
1. **⭐ Root cause (from the Step-1 investigation):** toggle-not-applied vs events-not-handled vs GA-type-mismatch —
   report which.
2. **The server-VAD GA event `type` strings** — confirm against the 053-C capture + Context7 (§18/§20).
3. **Finalize seam:** route the server-VAD terminal through the SAME finalize as manual `Stop` (my vote — single
   idempotent seam, mirrors cascade §34) vs a separate auto path.
4. **Manual-stop coexists in auto mode** (a user can still Stop early) — confirm the manual path still works.

## Dependencies + sequencing
- **Depends on:** 063-s1 (the toggle + branch — landed). **Blocks:** the realtime-cost capture (the lead captures
  realtime `response.done.usage` once auto-VAD works OR a manual-stop) → which feeds the realtime-cost-null half + the
  G.5 real numbers.
- **Pairs with:** the lead's realtime re-capture (the live confirm).

## Estimated commit count
**1** (the realtime auto-VAD wiring fix). If the root cause splits into payload-fix + event-handling-fix → possibly 2.
No safety invariant.

## Lessons-logged candidates
- **Convention candidate** — possibly "realtime server-VAD auto-finalize routes through the same single idempotent
  turn-finalize seam as manual `Stop`, mirroring the cascade utterance-end seam (§34)."
- **Architecture-doc note** — ARCH-003/010 (the realtime auto-VAD realization + the confirmed server-VAD GA `type`s).

## How to invoke
1. Read this brief + 063-s1/s2 + the 053-C realtime GA capture + the web realtime controller.
2. Step 1: INVESTIGATE the root cause (toggle-applied? events-handled? GA-type match?) — report at Step 2.5.
3. `/tdd realtime_auto_vad_wiring` — RED the `session.update` payload (auto→`server_vad`) + the event→finalize routing.
4. Step 2.5: report the root cause + answer Q1–Q4. Step 9: categorized summary + ship/no-ship + draft commit message.
