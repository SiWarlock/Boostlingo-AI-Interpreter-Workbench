# /tdd brief — realtime_dc_open_gate (P0 regression from 070; session.update before DC open)

> **Frontend slice (`web/`). P0 REGRESSION — realtime is 100% DEAD after 070 (`adc9fa8`).** PREEMPTS 071.
> A definitive browser stack trace (lead, from the user): `session.update` is sent before the RTCDataChannel is
> `open` → `InvalidStateError` → `startTurn` rejects → the turn never initializes (BOTH auto-VAD AND manual stop
> produce nothing). Transport-timing bug (browser-exempt per ARCH-020 — the live smoke caught what 070's unit tests
> structurally couldn't). No safety invariant.

## Feature
Gate the realtime `session.update` (and any client event) on the RTCDataChannel being `open` — so `startAutoSession`
no longer sends before the channel is ready. Fixes the P0 where the premature send throws `InvalidStateError`, rejects
`startTurn`'s promise, and leaves realtime fully non-functional.

## Use case + traceability
- **Task ID:** Phase-I P0 regression (070 follow-up). **Architecture:** ARCH-010 (realtime transport / DC lifecycle),
  ARCH-007.
- **Related:** 070 (`adc9fa8` — decoupled the auto-segment begin from `speech_started`; its reordering surfaced this
  DC-open race). Web realtime controller + the WebRTC client.
- **⭐ EXACT ERROR + STACK (lead capture):**
  ```
  Uncaught (in promise) InvalidStateError: Failed to execute 'send' on 'RTCDataChannel':
    RTCDataChannel.readyState is not 'open'
      at Object.sendClientEvent (realtimeWebRtcClient.ts:103:46)
      at sessionUpdateInput (realtimeTurnController.ts:179:12)
      at startAutoSession (realtimeTurnController.ts:286:5)
      at async Object.startTurn (realtimeTurnController.ts:203:9)
  ```
  Backend clean (client_secrets mints 200) → purely the FE realtime session path.

## ⭐ Pre-orient at Step 1 (confirm — your file from 070)
- `realtimeTurnController.ts:286` `startAutoSession` → `:179` `sessionUpdateInput` → `realtimeWebRtcClient.ts:103`
  `sendClientEvent` fires the `session.update` SYNCHRONOUSLY within the async `startTurn` (`:203`), BEFORE the DC
  reaches `readyState:'open'`. Confirm whether the manual flow sent session.update later/after-open (it worked
  pre-070) — i.e. what 070 reordered.
- Confirm whether `realtimeWebRtcClient` exposes a DC-`open` signal (an `onopen`/a ready promise) the controller can
  await, or whether `sendClientEvent` should queue-until-open internally.

## Design (fix direction — classic WebRTC DC ordering: never send client events until the channel is open)
Two options (Q1):
- **(A) Queue-in-client (my lean — robust, safe-by-construction):** `realtimeWebRtcClient.sendClientEvent` buffers
  client events while `dc.readyState !== 'open'` and flushes the queue on the DC `onopen`. Fixes the WHOLE class (any
  premature client-event send, not just this `session.update`); the controller's call sites stay unchanged.
- **(B) Await-in-controller (minimal):** `startTurn`/`startAutoSession` awaits a DC-`open` promise before the first
  `sendClientEvent`. Smaller, localized — but only fixes this send; a future early-send re-opens the class.

My lean: **(A)** — it makes the transport safe-by-construction and is the durable fix; (B) is the minimal P0 patch if
(A) is riskier than you want under P0 pressure. Your call at Step 2.5.

## Acceptance (RED-first deterministic — the ordering is unit-testable; live timing is the lead re-test)
- [ ] **No `sendClientEvent` reaches `dc.send` before the DC is `open`** — RED via a mock DC (`readyState:'connecting'`
      then fire `onopen`): assert the `session.update` is NOT sent on `connecting`, and IS sent (flushed / awaited)
      after `onopen`. (Option A: queued+flushed; Option B: `startTurn` awaits open then sends.)
- [ ] **`startTurn` no longer rejects when the DC opens asynchronously** — the promise resolves; the turn initializes.
      Pin that a `connecting`-then-`open` sequence yields an initialized turn (no `InvalidStateError` thrown/leaked).
- [ ] **Both modes re-init:** auto (session.update with `turn_detection:server_vad` after open) AND manual (the
      manual flow's session.update) send only after open. Pin both don't send-before-open.
- [ ] **(Flag, not unit-pinned)** Live realtime works again — the lead's re-test confirms auto-VAD + manual produce
      output (un-blocks the realtime-cost capture, still blocked until realtime works).
- [ ] `/preflight` (web) clean.

## Cross-doc invariant impact
- None (transport ordering; no wire model change). Confirm at Step 9.

## Things to flag at Step 2.5
1. **⭐ Queue-in-client (A, my lean) vs await-in-controller (B).** A = robust/safe-by-construction; B = minimal P0
   patch. Your call — pick the one you can land cleanly + fast under P0.
2. **DC-open signal:** does `realtimeWebRtcClient` already expose `onopen`/a ready promise, or add one?
3. **Confirm 070 is the reorder source** (so the fix targets the right send) — and that the fix preserves 070's
   begin-on-`committed`/`response.created` decoupling (don't regress 070's actual fix).

## Dependencies + sequencing
- **Depends on:** 070 (`adc9fa8`, landed — this fixes its regression; does NOT revert it). **⭐ PREEMPTS 071** (the FE
  set 071 aside at its start; resume 071 after this lands). **Blocks:** the realtime-cost live capture (realtime must
  work first).

## Estimated commit count
**1** — the focused DC-open gate. No safety invariant.

## Lessons-logged candidates
- **Convention candidate** — "never send a realtime client event (`session.update`/`response.create`/etc.) before the
  RTCDataChannel is `open` — queue-until-open + flush-on-`onopen` in the WebRTC client (safe-by-construction); a
  transport-timing bug is browser-exempt (ARCH-020) so it's the live smoke, not units, that catches it."
- **Architecture-doc note** — ARCH-010 (the DC-open ordering precondition for client events).

## How to invoke
1. Read this brief + the 070 diff (`adc9fa8`) + ARCH-010.
2. Step 1: confirm the send-site ordering (`:286`/`:179`/`:103`) + the DC-open signal.
3. `/tdd realtime_dc_open_gate` — RED the no-send-before-open + the startTurn-resolves-on-async-open tests.
4. Step 2.5: pick Q1 (queue vs await) + confirm 070's decoupling is preserved. Step 9: categorized summary +
   ship/no-ship + draft commit message. Then resume 071.
