# /tdd brief — realtime_first_audio_anchor (053-C1; frontend)

> **Small, clean, deterministic frontend slice.** Fixes the realtime metrics headline (`speech_end_to_first_audio_ms` + everything chained off it) being a permanent `n/a`. Root cause is locked from the user's live DC capture. The cost half of 053-C is a SEPARATE cross-area sibling (053-C2) — NOT in this brief.

## Feature
Anchor realtime first-audio on the data-channel event that actually fires (`output_audio_buffer.started`) instead of `response.output_audio.delta` (which never arrives over the DC — the audio bytes ride the WebRTC media track). This makes the realtime first-audio latency marker stamp on a real event → the realtime metrics headline populates.

## The fixture (derive RED tests against this)
`docs/runbooks/053c-realtime-dc-capture.md` — the verbatim ordered realtime DC event sequence from a live turn (the lead persisted it). Key facts:
- **`response.output_audio.delta` does NOT appear on the DC** — audio is on the media track (`pc.ontrack`). The current sink stamps `realtime.first_audio_delta` on `audioDelta` (normalized from `response.output_audio.delta`) → it never fires → the marker is never stamped → `deriveTurnMetrics`' chain (`tts.first_audio ?? realtime.first_audio_delta ?? playback.started`) finds nothing → permanent `n/a`.
- **`output_audio_buffer.started` DOES fire** (fixture event #13, mid-output-transcript-deltas, with a `response_id`). It's the correct first-audio anchor. (`output_audio_buffer.stopped` also fires at the end — #22.)
- Confirmed GA `type` strings are in the fixture (resolves ARCH-010 §7 smoke-confirm items A/B/D for the audio markers).

## Use case + traceability
- **Task ID:** 053-C1 (realtime first-audio anchor). **Architecture:** `ARCH-010` (realtime event mapping, §7 — the GA `type` strings + the audio-marker smoke-confirm) + `ARCH-013` (the `speech_end_to_first_audio_ms` selection chain). No safety invariant. No cross-doc model change (the `LatencyEvent`/marker names are not contract types; `NormalizedRealtimeEvent` is area-local).
- **Related:** 056-c1 (`7e5440b`) implemented the selector chain's realtime fallback (`web §25`/`§23`) — this slice makes the marker it reads actually get stamped. The realtime DC delivers events (053-A confirmed); this is a derivation gap, not a dead channel.

## Bug (symptom solid; root cause locked by the fixture)
Realtime metrics ALL `n/a` despite a completed turn with audio + transcripts. **Root cause:** the sink stamps `realtime.first_audio_delta` only on `audioDelta` (= `response.output_audio.delta`), which never arrives on the DC. Fix: stamp it on `output_audio_buffer.started` (the event that fires), keeping the `audioDelta` path as a fallback for any environment where the delta does arrive.

## Acceptance (RED-first; deterministic — the §16 sink + §9 normalizer patterns)
- [ ] **Normalizer** (`realtimeEvents.ts`): `output_audio_buffer.started` → a new `NormalizedRealtimeEvent` (e.g. `{ kind: 'outputAudioStarted' }`, no payload — the event carries only `response_id`/`event_id`, nothing the sink needs). Unknown/missing still → `null` (guard-the-body, §9). RED in `realtimeEvents.test.ts`.
- [ ] **Sink** (`realtimeEventSink.ts`): on `outputAudioStarted`, stamp **the same markers currently stamped on `audioDelta`** — `realtime.first_audio_delta` (stage `realtime`) + `playback.started` (stage `playback`) — gated by the **same per-turn `firstAudioStamped` latch** so whichever of `outputAudioStarted` / `audioDelta` fires first wins and the other does not double-stamp. Keep the `audioDelta` branch as the fallback. RED in `realtimeEventSink.test.ts`: an `outputAudioStarted` event stamps both markers once; a subsequent `audioDelta` does NOT re-stamp (latch holds); and the reverse order also stamps-once.
- [ ] **Selector UNCHANGED** (`selectors.ts`): `deriveTurnMetrics` already reads `realtime.first_audio_delta` in the chain — do NOT touch it. Add/confirm a `selectors.test.ts` case: a realtime turn whose first-audio marker is present (stamped via the buffer-started path) → `speechEndToFirstAudioMs` is non-`n/a` (anchored on `stt.final ?? recording.stopped`; realtime has no `stt.final` so it uses `recording.stopped` — the realtime speech-end, unchanged from 056-c1).
- [ ] Existing realtime tests stay green (adjust queries, not assertions). `npm run format:check && lint && typecheck && test` green.

## Files (frontend; confirm at Step 1)
**Modified:**
- `web/src/realtime/realtimeEvents.ts` — add the `output_audio_buffer.started` case + the `outputAudioStarted` union member (~line 67, near the other lifecycle cases).
- `web/src/realtime/realtimeEventSink.ts` — handle `outputAudioStarted` (stamp via the shared `firstAudioStamped` latch, ~line 97–108 alongside the `audioDelta` case).
- Tests: `web/src/realtime/realtimeEvents.test.ts`, `web/src/realtime/realtimeEventSink.test.ts`, `web/src/state/selectors.test.ts`.

If the fix needs more than this (e.g. the selector DOES need a new marker name), **flag at Step 2.5** before GREEN.

## Wiring / entry point (Step 7.5)
`realtimeWebRtcClient.onServerEvent` (raw DC frame) → `parseRealtimeEvent` → `normalizeRealtimeEvent` → `sink.handle` (`realtimeTurnController.ts:150`). The new `outputAudioStarted` flows the existing path; the stamped marker is read by `deriveTurnMetrics` → MetricsPanel. Reachable from a real realtime turn.

## Things to flag at Step 2.5
1. **New event kind vs reuse `audioDelta`.** My vote: a **new `outputAudioStarted` kind** (no payload) — `audioDelta` carries `base64` (timing+discard), `output_audio_buffer.started` has no audio; conflating them muddies the type. Stamp the same markers from both via the shared latch. Confirm.
2. **Marker name** — keep `realtime.first_audio_delta` (now also stamped on buffer-started) to leave the selector chain untouched, or rename to a neutral `realtime.first_audio`? My vote: **keep the name** (zero selector churn; the name is internal, not a contract). A rename touches the selector + its tests for cosmetics only. Confirm — if you rename, the selector chain change is in-scope here and must stay green.
3. **Keep the `audioDelta` first-audio stamp as a fallback?** My vote: **yes, keep it** (latch-guarded) — harmless, and protects against an environment/SDK where the delta does arrive on the DC. Confirm.

## Dependencies + sequencing
- **Depends on:** the 056-c1 selector chain (landed `7e5440b`); the DC fixture (lead-persisted).
- **Sibling (NOT this slice):** **053-C2 realtime cost** — read `response.done.usage` from the DC → price via the backend (the realtime path doesn't call `/complete` today + the frontend has no pricing config → cross-area: extend `CompleteTurnRequest` + `EstimateRealtime`). Authored separately; pairs with the cascade-cost-backend slice (057). Do NOT pull cost into this slice.
- **Blocks:** the realtime half of the G.5 comparison (realtime latency numbers).

## Estimated commit count
**1** — a focused normalizer + sink fix (one logical unit; ~15–25 lines + tests). No safety invariant; no cross-doc change.

## Lessons-logged candidates anticipated
- **Convention candidate** — "anchor realtime first-audio on `output_audio_buffer.started` (the DC event that fires), NOT `response.output_audio.delta` (audio is on the media track, never the DC); the selector chain was right, the stamped event was wrong — the §18/§20 verify-the-real-wire-shape discipline, now smoke-confirmed." Refines §23. Orchestrator's call at round-seal.
- **Architecture-doc note candidate** — ARCH-010 §7: confirm the realtime audio markers (`output_audio_buffer.started`/`.stopped`) + that `response.output_audio.delta` does not appear on the DC under WebRTC.

## How to invoke
1. Read this brief + `docs/runbooks/053c-realtime-dc-capture.md`.
2. `/tdd realtime_first_audio_anchor` — RED the normalizer case first, then the sink stamp (shared latch), then the selector non-n/a confirmation.
3. Step 2.5: answer Q1–Q3. Step 9: categorized summary + ship/no-ship + draft commit message (+ flag the ARCH-010 §7 audio-marker confirm note).
