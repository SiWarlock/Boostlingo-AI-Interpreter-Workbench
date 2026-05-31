# /tdd brief — realtime_session_type (P0; `session.update` rejected — missing required `session.type`)

> **Frontend slice (`web/`). P0 — realtime is STILL fully non-functional** (post-070/072): OpenAI REJECTS the
> `session.update` for a missing required `session.type`, so no config (turn_detection / transcription) ever applies →
> stuck at "ready", 0 transcripts, both auto-VAD AND manual dead. **PREEMPTS 071/073-cost-precision.** Definitive
> console root cause (the lead's clean fresh-page capture). FE-owned single-payload fix. No safety invariant.

## Feature
Add the GA-required `session.type:"realtime"` to the realtime `session.update` payload so OpenAI ACCEPTS it — then the
existing `turn_detection:{type:'server_vad'}` (auto-VAD) + `transcription` config finally take effect and realtime
produces transcripts + auto-finalizes. This is the **single bug** behind the whole realtime saga.

## Use case + traceability
- **Task ID:** Phase-I realtime P0 (the `session.type` fix). **Architecture:** ARCH-010 (realtime transport / GA
  session config), ARCH-003 (auto-VAD).
- **Related:** 070 (`adc9fa8` — the begin-decouple; its Step-2.5 FLAGGED this exact `session.type:'realtime'` hedge,
  deferred per §18/§20 "add only if the live capture shows zero VAD events" — now CONFIRMED needed), 072 (`bf877e5` —
  the DC-open queue-gate; **KEEP it — it's correct + load-bearing**: `session.created` arriving proves the channel opens
  before the update is sent). Web lessons §29 (the DC-open queue), §15/§18/§20 (verify-the-live-GA-shape).
- **⭐ DEFINITIVE CONSOLE EVIDENCE (lead, fresh-page capture, `realtimeWebRtcClient.ts:153 [realtime oai-events]`):**
  `session.created` arrives fine (DC opens, session connects; the created session shows `audio.input.turn_detection:null`,
  `transcription.model:gpt-4o-transcribe`). Then OpenAI rejects the `session.update` **TWICE**:
  ```json
  {"type":"error","error":{"type":"invalid_request_error","code":"missing_required_parameter",
   "message":"Missing required parameter: 'session.type'.","param":"session.type"}}
  ```
  (interleaved with an `input_audio_buffer.cleared`). Because the update is rejected, `turn_detection` stays null → the
  session never auto-detects end-of-speech, never responds → "stuck at ready, 0 transcripts." Explains bug C (no
  auto-VAD), the post-070/072 silence, AND manual-mode failing (the session was NEVER configured).

## ⭐ Pre-orient at Step 1 (confirm the exact GA shape — Context7)
- The `session.update` builder in `realtimeWebRtcClient.ts` / `realtimeTurnController.ts` — confirm it currently emits
  `{ type:"session.update", session:{ audio:{ input:{ turn_detection, transcription, … }}}}` WITHOUT a `session.type`.
- **⭐ Confirm the EXACT GA shape via Context7** (the Realtime API moved `session.type` INTO the `session` object): the
  lead's read is `{ type:"session.update", session:{ type:"realtime", …existing audio/turn_detection/transcription }}`
  (the `session.created` echo shows the session object IS `type:"realtime"`). Verify: is `type:"realtime"` a field
  ON the `session` object (lead's read) — and does it belong on BOTH the auto AND manual `session.update` (both were
  failing)? (§18/§20 verify-the-real-shape — but here the live error is already definitive on the WHAT; Context7
  confirms the exact field placement.)

## Design (the single-payload fix; KEEP everything else)
- Add `type:"realtime"` to the `session` object of EVERY `session.update` the controller sends (auto AND manual —
  both were rejected). The rest of the payload (audio/input/turn_detection/transcription) is unchanged.
- **KEEP the 072 DC-open queue-gate** (it works — `session.created` proves it) + 070's begin-decouple (orthogonal).
- Once accepted: `turn_detection:{type:'server_vad'}` applies → auto-VAD detects speech-end → `response.done` → the
  070 finalize path runs; transcription deltas flow → the sink renders + POSTs. (No change to those — they were just
  never reached because the config was rejected.)

## Acceptance (RED-first deterministic — the payload is unit-pinnable; the live behavior is the lead's re-test)
- [ ] **The realtime `session.update` payload includes `session.type:"realtime"`** (the GA-confirmed placement) — pin
      the payload builder in BOTH the auto AND manual paths. RED today (the field is absent → OpenAI rejects).
- [ ] **The existing config still rides** — `turn_detection` (auto→`server_vad`, manual→null) + `transcription` are
      unchanged + still present alongside the new `session.type`. Pin both modes' payloads.
- [ ] **(Flag, not unit-pinned)** Live: the lead's re-test — `session.update` ACCEPTED (no `missing_required_parameter`
      error), transcripts flow, auto-VAD finalizes a turn AND manual works. Un-blocks the realtime-cost capture.
- [ ] `/preflight` (web) clean (incl. the still-held 071 WIP = the EXPECTED RED until 071 resumes — confirm 073 + all
      non-071 green, like 072).

## Cross-doc invariant impact
- None (transport payload shape; no wire model change). Confirm at Step 9.

## Things to flag at Step 2.5
1. **⭐ The exact GA placement of `session.type`** (Context7 + the `session.created` echo) — on the `session` object
   (lead's read) vs elsewhere. Report what Context7 confirms.
2. **Both auto AND manual `session.update` get it** (both were rejected) — confirm the manual path's payload too.
3. **Anything else the GA `session.update` now requires** that the rejection masked (once `session.type` is added, a
   2nd required-field error could surface) — note if Context7 flags other required fields.

## Dependencies + sequencing
- **Depends on:** 070 + 072 (landed; both KEPT — this completes the realtime-revival they started). **⭐ PREEMPTS**
  071 (the drill-in, WIP banked) + the cost-precision slice. **Blocks:** the realtime-cost live capture + the G.5
  realtime numbers (realtime must produce data first).

## Estimated commit count
**1** — the single `session.type` payload addition. No safety invariant.

## Lessons-logged candidates
- **Convention candidate** — "the GA Realtime `session.update` requires `session.type:'realtime'` on the `session`
  object (the `session.created` echo confirms the type); omitting it → OpenAI rejects the update → no config applies →
  silent 'ready'-hang (no crash). The deferred-per-§18/§20 hedge (070) was correct to defer + correct to add once the
  live error named the field — verify-the-real-shape, then act on the live evidence."
- **Architecture-doc note** — ARCH-010 (the GA `session.update` required-field set).

## How to invoke
1. Read this brief + the console evidence above + 072 (KEEP the DC-gate) + web §29.
2. Step 1: confirm the current `session.update` builder + the exact `session.type` placement via Context7.
3. `/tdd realtime_session_type` — RED the payload includes `session.type:"realtime"` (auto + manual) + the existing
   config still rides.
4. Step 2.5: report the Context7-confirmed shape + any other newly-required field. Step 9: categorized summary +
   the live-confirm flag + ship/no-ship + draft commit message. Then resume 071.
