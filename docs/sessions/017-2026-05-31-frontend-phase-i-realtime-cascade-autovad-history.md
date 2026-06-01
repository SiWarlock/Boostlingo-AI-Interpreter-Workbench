# Session 017 — Frontend: Phase-I auto-VAD (realtime s2 + cascade), H.3 history, + the realtime P0

- **Date:** 2026-05-31
- **Phase:** Phase I (auto-VAD, both transports) + H.3-frontend (session history) + a realtime P0 regression
- **Area:** frontend (`web/`)
- **Predecessor:** [015 — frontend metrics / realtime cost / Phase-I slice 1](015-2026-05-31-frontend-metrics-realtime-cost-phase-i-slice1.md)
- **Successor:** [018 — FE realtime revival (session.type P0) + H.3 drill-in + cost polish + realtime $/min](018-2026-05-31-frontend-realtime-revival-h3-drillin-cost-polish.md)

> Implementer session doc (technical close-out only). `MVP_TASKS.md` / `web/LESSONS.md` / `web/CLAUDE.md` / `ARCHITECTURE.md` are orchestrator-owned + untouched here; the Step-9 items below were hot-routed by the orchestrator during the session.

## Why this session existed

Fresh FE implementer (predecessor cycled at 015). Drove Phase-I auto-VAD to completion on both transports, the H.3 session-history frontend, and — at the end — a **P0 regression** (realtime 100% dead) surfaced by the live both-modes smoke.

## What was built (per slice — all `/tdd`, RED-first)

**064 — realtime auto-VAD multi-segment lifecycle (I.2 s2)** (`06a604a`)
- `realtimeTurnController.ts`: replaced slice-1's single-utterance `settled` latch with a per-segment lifecycle — begin a turn per server `speech_started`, finalize per `response.done` (+ §26 `/complete`+`/events` per auto-turn); 3A speech-end anchor on the server `speech_stopped` (into `turn.recording.stopped` → reconciles with the backend session-avg); close-listening Stop. `realtimeEvents.ts`: `speechStarted`/`speechStopped`/`committed` normalized events. (web §27.)

**066 — cascade auto-VAD frontend wiring (I.3)** (`bb145be`)
- `cascadeStreamClient.ts`: `autoVad` in `CascadeStartParams`/`buildStartFrame` (omit-when-falsy); an `onTerminal` delegate fired on every turn-end (done/error/abnormal-close, guarded). `recordingActions.ts`: `autoVad:true` in auto; `onCascadeTerminal()` stops the mic on the backend auto-finalize (idempotent). `main.tsx`: composition-root `setOnTerminal` wiring.

**067 — session-history panel (H.3-frontend)** (`5452cd5`)
- New `SessionHistory.tsx` (fetch-on-mount + Refresh, transient state, mode chips, bounded list) + `historyActions.loadHistory` + `sessionsApi.listSessions` + the `SessionListItem` TS mirror + `errorCopy('sessions.read_failed')` + the `App` mount. List-only (drill-in = 071).

**070 — realtime auto-VAD finalize fix (Phase-I Bug C)** (`adc9fa8`)
- `realtimeTurnController.ts` `handleAutoServerEvent`: **decoupled the turn lifecycle from the UNCONFIRMED `speech_started`** — also begin the segment on the 053-C-CONFIRMED `committed`/`response.created` (guarded → one begin), so the turn finalizes regardless of the VAD speech-event shape. Root cause: the 064 lifecycle gated the whole turn on `speech_started`, which the manual-mode 053-C capture never live-confirmed.

**072 — realtime DC-open gate (P0; realtime dead)** (`bf877e5`)
- New `realtimeClientEventQueue.ts` (pure `createClientEventQueue`: buffer-while-not-open + flush-in-order on `onopen` + clear-on-teardown). `realtimeWebRtcClient.ts`: wired it into `sendClientEvent` + `connect`'s DC `onopen` + `teardown`. **The P0:** `sendClientEvent` called `dc.send()` without a `readyState` check → `InvalidStateError` on a `connecting` DC → `startTurn` rejected → realtime dead (both modes). A no-op would DROP the `session.update`; the queue sends it after open. Orthogonal to 070.

## Decisions made
- **070 root cause = GA-string fragility, not "events unhandled":** 064 DID handle the events but gated on the unconfirmed `speech_started`. Fix = begin on the confirmed `committed`/`response.created` (defer-to-the-evidence — Context7 + the 053-C capture).
- **072 = forward-fix, not revert:** Option A (queue-in-client) over a no-op (which drops the config) or await-in-controller. Preserves 070's decoupling. The connect→send race is the 064 I.2-s2 structure, not 070 (code-traced; the orchestrator accepted the correction).
- **066 `onTerminal` fired on ALL turn-end paths** (done/error/abnormal-close), not just `done` — no mic-leak on a failed/dropped auto turn.
- **072 fake-DC test** pins the load-bearing `onopen→flush` wiring deterministically (the queue logic + the wiring both unit-pinned; the rest of the WebRTC client stays manual-smoke).

## Decisions explicitly NOT made (deferred)
- **⭐ 071 — H.3 history drill-in (bounded scroll + click-to-expand accordion).** Step-2.5 written + pre-approved by the orchestrator on-sight, then PREEMPTED by the 072 P0. **WIP banked uncommitted in the tree** (see Held state). The fresh-me resumes it.
- **070 reviewer MEDs (→ live-capture hardening):** the stale `pendingRecordingStoppedTs` between-segments anchor; the `responseCreated`-only fast-`response.done` race. Both bounded fallback-path edges.
- **072 hardening LOWs:** queue `MAX_PENDING` cap; flush-rawSend-throw guard; null the DC `onopen`/`onmessage` in teardown; fake-DC handler type fidelity.

## TDD compliance
**Clean.** Every slice RED-first via `/tdd` (the per-segment lifecycle, the cascade autoVad + onTerminal, the history list, the begin-trigger decoupling, the DC-open queue). Manual-smoke-exempt: the WebRTC/DC plumbing + the cascade capture internals (ARCH-020). No violations.

## Reachability (Step 7.5 — each verified on a production entry path)
- **064:** SessionSetup Auto toggle → `startAutoSession` wires `handleAutoServerEvent` to the production `realtimeWebRtcClient.onServerEvent` → per-segment turns.
- **066:** SessionSetup toggle → `recordingActions.startRecording` (`autoVad`) → backend `done` → `onTerminal` → `recordingController.onCascadeTerminal` (via `main.tsx`).
- **067:** `App` mounts `SessionHistory` → mount fetch → `loadHistory` → `listSessions`.
- **070:** broadens the existing `handleAutoServerEvent` begin-triggers (no new wiring).
- **072:** the production `realtimeWebRtcClient` singleton's `sendClientEvent` now queues; reachable from `startTurn` → `sendClientEvent`. **Live confirm = the lead's re-test** (transport-timing, ARCH-020).
- **No tested-but-unwired gaps** in the shipped slices.

## Open follow-ups (Step-9 categorized — orchestrator hot-routed during the session)
- **Architecture notes:** ARCH-003/010 — realtime auto-VAD begin-on-confirmed-events + finalize (070); ARCH-010 DC-open ordering precondition (072); ARCH-009 cascade `autoVad` (066); ARCH-013 cascade `speechEndToPlaybackMs` n/a in auto (066).
- **Lessons (web):** the auto-VAD multi-segment lifecycle (§27, 064); cascade `onTerminal` capture-stop (066); don't gate a realtime lifecycle on an unconfirmed event-string — begin on confirmed events guarded (070); never send a realtime client event before the DC is `open` — queue-until-open + flush-on-`onopen` (072).
- **Cross-doc (mirror-registration, no ARCHITECTURE.md change):** `TurnControlMode` (063); `CascadeStartParams.autoVad` (066); `SessionListItem` (067).
- **Carry-forward (→ live-capture hardening):** 070's 2 reviewer MEDs; 072's hardening LOWs; the cascade `speechEndToPlaybackMs`-in-auto follow-up.
- **Carry-forward (→ 071, banked):** the FE history drill-in (resume from the in-tree WIP).
- **⭐ Verify-at-smoke (the lead's re-test):** 072 — realtime works again (auto+manual produce output), un-blocking the realtime-cost capture (073); 070 — re-pin any divergent server-VAD GA `type`.

## ⚠️ Held state (carry across the cycle)
**071 WIP is uncommitted + intact in the working tree** — 4 RED test files: `state/historyActions.test.ts` (M, +`loadSessionDetail`), `components/SessionHistory.test.tsx` (M, +accordion/scroll), `state/historyDetail.test.ts` (new, `toTurnDetailView`), `components/SessionDetail.test.tsx` (new). **The 7 RED 071-WIP failures are EXPECTED** (impl unwritten — Step-2.5 paused) — NOT a regression: the shipped 070/072 are clean, all 282 non-071 tests green, zero non-071 type errors. The fresh-me resumes 071 from this tree (Step-2.5 pre-approved on-sight: single-open · fetch-once-cache · new compact `SessionDetail` reusing the pure helpers · 420px scroll cap · `TurnDetailView` projection + `deriveTurnMetrics` → `Pick<…,'latencyEvents'>` · reuse `sessions.read_failed`).
