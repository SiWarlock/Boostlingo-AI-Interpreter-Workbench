# /tdd brief — cascade_continuous_listening

## Feature
Make cascade **auto-VAD mode continuous** (hands-free back-and-forth): after an auto-finalized turn (Deepgram utterance-end → translate → TTS → `done`), **auto-re-arm and begin the next turn automatically** — looping until the user explicitly ends the session — instead of stopping the capture and waiting for a manual Start. Each re-armed turn is its own turn (its own per-utterance direction-detect via J.1/078, its own metrics). Matches realtime's continuous server-VAD loop. **FE-ONLY** — no backend change (see scope note).

## Use case + traceability
- **Task ID:** J.5 (Phase J completion — the cascade half of "a back-and-forth conversation can happen, hands-free")
- **Architecture sections it implements:** ARCH-011 (cascade streaming), ARCH-007 (capture lifecycle / turn-control), ARCH-003 (auto-VAD, REVISED — auto-VAD is now *continuous*, not single-turn)
- **Related context:** Phase-J smoke Finding (user, 2026-05-31, via lead): realtime bidirectional works hands-free; cascade required a manual Start per utterance. Realtime template: `web/src/realtime/realtimeTurnController.ts` re-arm loop (on `responseDone`: clear segment, `setTurnStatus('recording')`, connection persists). Phase-I I.3 (`bb145be`) made cascade auto-VAD finalize-one-turn-then-stop — this completes it to continuous.

## ⭐ Scope note — why FE-only (orchestrator-confirmed)
The cascade WS is one-turn-per-connection (`CascadeWebSocketEndpoint` reads one `start`, runs the turn, the server closes on `done`). **Manual mode already does fresh-WS-per-turn** (every Start→Stop = a new WS + one turn + close) — multiple turns per session via multiple connections is the proven existing pattern. Continuous-listening = **auto-trigger the next turn on auto-VAD `done`** (a new WS + re-arm), exactly as manual mode does on a Start click. **No backend change.** The inter-turn reconnect gap (WS upgrade + Deepgram connect) is **masked by TTS playback** (the next speaker is listening to the translation during re-arm). *(If a live smoke shows a demo-noticeable gap — e.g. rapid same-speaker multi-utterance — the seamless fallback is a BE continuous-orchestrator change, Option B, which the orchestrator will scope then. Ship FE-only first.)*

## Acceptance criteria (what "done" means)
- [ ] In **auto-VAD mode**, on an auto-finalized turn's `done` (status completed), the FE **automatically begins the next turn** (new turnId + new WS + re-armed capture) when the session is live and the user hasn't ended it — no manual Start.
- [ ] The loop continues turn-after-turn until the user **explicitly ends** (a "Stop/End conversation" action) — the user-end flag prevents the next re-arm + does the final cleanup (mic stop, no new WS).
- [ ] **Manual mode is UNCHANGED** (single-turn-per-Start-click; no auto-re-arm) — gated on `turnControlMode === 'auto'`.
- [ ] Each re-armed turn is independent: its own turnId, its own per-utterance direction (J.1 cascade detect / J.3 realtime — for cascade, the backend re-detects per turn), its own metrics; turns accumulate in the session.
- [ ] No double-arm / orphaned-capture races (a `done` re-arm + a concurrent user-end resolve to exactly one outcome).
- [ ] All Vitest units pass; `tsc --noEmit` clean; `/preflight` clean.

## Files expected to touch (confirm at Step 1/2.5)
**Modified (likely):**
- `web/src/state/recordingActions.ts` — the re-arm logic: on cascade auto-VAD `done` (auto mode + session live + not user-ended), begin the next turn instead of just `onCascadeTerminal` stopping; a user-end flag/action that breaks the loop.
- `web/src/cascade/cascadeStreamClient.ts` — surface a `done`-with-status signal sufficient to drive re-arm (it already invokes `onTerminal`); possibly a distinct "auto-finalized, re-armable" vs "ended" signal.
- `web/src/state/sessionStore.ts` — a `continuous`/`userEndedConversation` state bit if needed for the loop gate.
- The capture controller — ideally **keep the getUserMedia mic stream alive across turns** (re-open only the WS + re-route PCM) to avoid re-prompt/latency (see Step-2.5 Q1).
- The session-end / Stop control component — the explicit "end conversation" action.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`auto_vad_done_rearms_next_turn`** — in auto mode, a cascade `done` (completed) on a live session ⇒ a new turn begins (a new `start`/turn is initiated). Why: the continuous loop.
2. **`manual_mode_done_does_not_rearm`** — manual mode `done` ⇒ NO re-arm (single-turn preserved). Why: gating, no regression.
3. **`user_end_stops_the_loop`** — after the user ends the conversation, a subsequent `done` does NOT re-arm + the mic is stopped. Why: explicit termination.
4. **`rearm_skipped_when_session_not_live`** — `done` when the session is ended/not-live ⇒ no re-arm. Why: don't re-arm a dead session.
5. **`error_terminal_does_not_rearm`** — a cascade `error` terminal ⇒ no auto-re-arm (surface the error, don't loop on failure). Why: fail-safe (don't spin on a broken stream).
6. **`rearm_begins_a_fresh_independent_turn`** — the re-armed turn has a new turnId (not reusing the prior) + accumulates. Why: independent per-utterance turns.

## Cross-doc invariant impact
- **Model field changes:** none expected (FE state + lifecycle only). A new `continuous`/`userEnded` UI state bit is FE-runtime, not a wire mirror. If a new wire field is needed (it shouldn't be — reuses `start`/`done`), flag at Step 9.
- **Architecture note (orchestrator writes):** ARCH-003/011 — cascade auto-VAD is now *continuous* (re-arm loop), completing the Phase-I single-turn auto-VAD; pairs with the Phase-J ARCH revision.

## Things to flag at Step 2.5
1. **Keep the mic stream alive across turns** (one getUserMedia for the session, re-open only the WS + re-route PCM) vs a full `startRecording()` re-acquire per turn? **Default vote: keep the mic stream alive** — avoids a per-turn permission re-prompt + device re-init latency + shrinks the inter-turn gap. If the current `captureHandle` re-acquires getUserMedia per start, separate mic-acquire (session-scoped) from WS-open (per-turn).
2. **Re-arm trigger point** — in `onCascadeTerminal` (on `done`) vs a higher-level orchestration hook? **Default vote: a re-arm hook keyed on the `done`-completed terminal** (auto + live + not-ended); keep `onCascadeTerminal`'s mic-stop for the end path. Distinguish a re-armable `done` from an `error`/ended terminal.
3. **How does the user END the continuous conversation?** **Default vote: the existing End/Stop control sets a `userEndedConversation` flag → the next `done` doesn't re-arm + cleans up** (mic stop, final session end). Confirm the exact control with the FE's session-end UX.
4. **Inter-turn gap acceptance** — the WS+Deepgram reconnect gap is masked by TTS playback in the normal back-and-forth. **Default vote: ship FE-only, document the rapid-same-speaker edge case**; the seamless Option B (BE continuous orchestrator) is the smoke-gated fallback. Flag at Step 9 if the gap looks problematic in dev.

## Dependencies + sequencing
- **Depends on:** Phase-I cascade auto-VAD (062/063, landed) + J.1 cascade bidirectional detect (078, landed — each re-armed turn re-detects direction). 
- **Blocks:** the bidirectional cascade live-smoke being a true continuous conversation; the G.4 5-min soak-harness (consumes the continuous loop).

## Estimated commit count
**1–2.** The re-arm loop + the user-end gate (one logical unit; the mic-stream-lifecycle refactor may be a sibling commit if sizable). FE-only; no safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — cascade continuous-listening = auto-trigger the manual-mode fresh-WS-per-turn pattern on auto-VAD `done` (no BE change; reconnect gap masked by TTS); the realtime persistent-loop is the UX template but cascade reconnects per turn.
- **Architecture-doc note candidate** — ARCH-003/011: cascade auto-VAD is continuous (completes the I.3 single-turn version).
