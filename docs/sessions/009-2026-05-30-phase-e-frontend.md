# Session 009 — Phase E (Realtime) FRONTEND: E.3 + E.4a + E.4b + E.5a + E.5b

- **Date:** 2026-05-30
- **Phase:** E (Realtime Mode) — the frontend round (the backend round was session 008)
- **Predecessor:** [008-2026-05-30-phase-e-backend.md](008-2026-05-30-phase-e-backend.md)
- **Successor:** _(next session)_
- **Area:** `web/` (frontend implementer)
- **Slice commits:** E.3 `87d77d0` · E.4a `7b63fc4` · E.4b `f2b5647` · E.5a `a09c15e` · E.5b `f95bea6`
- **Web suite:** 97 → **157 green** (+60 across the round); typecheck + ESLint clean every slice.
- **Preflight at close:** GREEN — ESLint clean · `tsc --noEmit` clean · Vitest **157/157** · Prettier `format:check` clean (after a `/session-end` audit-fix — see the process finding in Open follow-ups).

## Why this session existed

The Phase-E **backend** shipped in session 008 (the ephemeral-credential broker E.1, realtime cost E.2b, stale-flush E.5-backend). This session built the entire Phase-E **frontend** — the browser Realtime path end-to-end, consuming the shipped backend (E.1's `POST /api/realtime/client-secret` mint + E.2's `POST …/turns/{turnId}/events` ingest). Five slices took realtime from nothing to a reachable, audible, disconnect-resilient, mode-switch-clean mode in the real UI.

## What was built

The realtime path, layered bottom-up:

### Files created
- `web/src/api/realtimeApi.ts` (E.3) — `mintClientSecret` over the shared `http` boundary (`ek_…` mint from our backend).
- `web/src/realtime/realtimeEvents.ts` (E.3) — pure, stateless GA-event normalizer: `parseRealtimeEvent` + `normalizeRealtimeEvent` → the `NormalizedRealtimeEvent` union.
- `web/src/realtime/realtimeWebRtcClient.ts` (E.3) — `realtimeCallsUrl` + `exchangeSdpOffer` (tested seams) + the `createRealtimeWebRtcClient` manual-smoke shell (RTCPeerConnection + `oai-events` DC + getUserMedia/addTrack + SDP handshake).
- `web/src/realtime/realtimeEventSink.ts` (E.4a) — per-turn stateful sink: `NormalizedRealtimeEvent` → store (transcripts by role w/ incremental-token accumulation; first-of-type browser-clock latency stamps; `audioDelta` timing-only; `responseDone` finalize + complete; `error` → failTurn).
- `web/src/realtime/realtimeTurnController.ts` (E.4b) — DI'd turn controller (createTurn → beginTurn → connect → per-turn sink wiring → buffer-delimited manual VAD-off turns → recording stamps → report on `responseDone`); the §11 cascade-recording analogue.
- `web/src/realtime/realtimeConnectionManager.ts` (E.5a) — the connection lifecycle: one persistent pc held across turns (idempotent `ensureConnected`, latch-reset-on-failure), connection-timing stamps, disconnect-surfacing, `teardown()`, and (E.5b) `onModeSwitch`.
- Test files for each of the above + `web/src/components/TranscriptPanel.test.tsx`, `RecordingControls.test.tsx`, `SessionSetup.test.tsx` (new component tests).

### Files modified
- `web/src/types/domain.ts` (E.3) — added `RealtimeTokenRequest` / `RealtimeTokenResponse` TS mirrors of the E.1 backend DTOs.
- `web/src/api/sessionsApi.ts` (E.4a) — added `appendTurnEvents` (`POST …/turns/{turnId}/events`, body `{events}`).
- `web/src/components/RecordingControls.tsx` (E.4b) — dispatch Start/Stop by `currentMode` (realtime → controller; cascade unchanged) — the realtime path's real entry point.
- `web/src/components/ModeToggle.tsx` (E.5b) — onClick → `realtimeConnectionManager.onModeSwitch(state.mode, value)` (Flow-G teardown) + a same-mode no-op guard.
- `web/src/components/SessionSetup.tsx` (E.5a) — End onClick → `realtimeConnectionManager.teardown()`.
- `web/src/components/errorCopy.ts` (E.5a) — added the `realtime.session.disconnected` advise-switch copy.
- `web/src/realtime/realtimeWebRtcClient.ts` (E.4b/E.5a) — gained `sendClientEvent` + settable raw `onServerEvent` (E.4b, normalize moved into the tested controller; removed `deps.onEvent`); the interim `<audio>` playback + once-stamped `playback.started` (E.4b); `onConnectionState` + `close()`→`teardown()` + `<audio>` detach (E.5a).

## Decisions made

- **E.4 split a/b, E.5 split a/b** (the D.4a/D.4b precedent) — E.4a = the data path (sink + reporting), E.4b = turn control + UI wiring; E.5a = lifecycle + disconnect + teardown, E.5b = mode-switch. Each ~one cohesive commit.
- **Normalize moved INTO the tested controller** (E.4b) — the E.3 client delivered normalized events via a construction-time `deps.onEvent`; E.4b replaced it with a settable raw `onServerEvent` so parse→normalize→sink is unit-tested in the controller, shrinking the manual-smoke shell.
- **`audioDelta` is TIMING-ONLY** (E.4a, invariant #3) — it stamps `realtime.first_audio_delta` and writes NO audio to the store; realtime audio plays via the WebRTC media track (E.3 `ontrack` → a detached `<audio>`, E.4b interim). The sink's `Pick<SessionStore>` excludes any audio sink by construction.
- **Incremental-token accumulation** (E.4a) — realtime deltas are incremental tokens, but the store's §10 `appendSegment` replaces the trailing partial (cascade-cumulative); the sink accumulates per-role running text so the panel renders the growing transcript, not just the last token. (Caught a correctness gap in the E.4a brief.)
- **Persistent pc + idempotent connect with latch-reset-on-failure** (E.5a) — one pc held across turns; the `connectionStarted` latch resets on a failed `connect()` so a later turn retries (a transient failure would otherwise have permanently bricked realtime mode), and the controller fails+aborts that turn.
- **`realtime.session.connecting` stamped at connect INITIATION** (E.5a) — so `realtime_connect_ms` computes; `connected`/`disconnected` from the pc connectionstate via a settable `onConnectionState` shell delegate → the tested mapper. Disconnect is surfaced (never swallowed): a sanitized `realtime.session.disconnected` UiError → failTurn/addError + errorCopy advise-switch.
- **Mode-switch double-mic fix** (E.5b) — `onModeSwitch(from,to)` reuses `teardown()` iff `from==='realtime' && to!=='realtime'`; the rule lives on the manager (the store reducer stays pure).
- **Manager owns the connection lifecycle** (E.5a Q1) — isolates connect/teardown/connection-state from the turn controller; E.5b's mode-switch extended it cleanly.

## Decisions explicitly NOT made (deferred)

- **`ModeTransitionEvent` emit + persistence (Flow-G timeline)** — RE-SEQUENCED. The backend `SessionStore.RecordModeTransition` is built but UNWIRED (no endpoint), and the team was frontend-only this round → needs a backend `POST /api/sessions/{id}/mode-transition` slice + the frontend POST + the TS `ModeTransitionEvent` mirror + the store `modeTransitions` graduation. NOT deliverable-blocking (the comparison derives each turn's mode from the per-turn `mode` field). Backend dependency surfaced to the lead.
- **Re-mint on `expiresAt` + auto-reconnect (≤2)** — ARCH-010 nice-to-haves; the 60-min session cap > the 5-min demo, so no mid-demo re-mint → documented fallbacks (G).
- **The realtime smoke-confirm bundle** — the first real-key smoke (demo-checklist, ARCH-020 exempt) must confirm: the GA event `type` strings (`response.output_audio.delta` vs legacy `response.audio.delta`; the transcript deltas; `input_audio_transcription.completed`'s `transcript` field), the error envelope, whether WebRTC emits `output_audio.delta` on the DC (else `playback.started` is the only realtime first-audio timing), the connectionstate event names, and the manual-turn-control frame envelopes (`session.update`{turn_detection:null}/`input_audio_buffer.clear`/`commit`/`response.create`). Pin ARCH-010 §7 then.
- **LOW hardenings (→ G)** — the `responseDone`+`error` ordering race; `onServerEvent` write-only immutability; the SessionSetup `teardown`-fires-even-if-`endSession`-fails interaction.

## TDD compliance

**CLEAN.** Every slice was RED-first (confirmed RED for the right reason before GREEN — module-not-found / "is not a function" / assertion-fail, captured each Step 3). The manual-smoke shells (`createRealtimeWebRtcClient`, the interim `<audio>` playback, the pc/connectionstate/teardown ops) are documented-exempt per the root TDD posture (browser WebRTC). One transparency note: **E.4b's `TranscriptPanel.test.tsx` tests are CHARACTERIZATION** — the "source unavailable" behavior shipped in D.6/D.7, so those 2 tests were green-on-arrival (you can't fail-first on already-shipped behavior); orchestrator-approved as characterization, flagged at Step-2.5. Review-surfaced HIGH findings (E.4a stale `sourceText`; E.5a connect-failure latch; E.4b playback double-stamp) were each fixed in-slice with a pinning test.

## Reachability

**All frontend Phase-E features are reachable from real production entry points** — no tested-but-unwired gaps:
- **E.3** (transport library) + **E.4a** (data path) were foundation/data-path slices, consumer-pending → wired by E.4b.
- **E.4b** CLOSED the realtime reachability gap: `App.tsx:97 <RecordingControls/>` → (mode==='realtime') → `realtimeTurnController.startTurn/stopTurn` → E.3 client + E.4a sink.
- **E.5a**: `ensureConnected` via `realtimeTurnController` (startTurn); `teardown` via `SessionSetup.tsx` End onClick.
- **E.5b**: `onModeSwitch` via `ModeToggle.tsx` onClick (App→ModeToggle).

## Open follow-ups

### Step-9 categorized items (surfaced for `/orchestrate-end` verification — orchestrator hot-routed these during the round)
- **Convention lessons (→ `web/LESSONS.md` §15–§18 + `web/CLAUDE.md` index):** realtime WebRTC transport (§15); realtime data-path sink (§16); realtime turn controller (§17); realtime connection lifecycle + mode-switch (§18). _(Orchestrator-written; in the working tree.)_
- **Cross-doc invariant (→ `web/CLAUDE.md` row):** `RealtimeTokenRequest`/`RealtimeTokenResponse` TS mirror-registration (E.3). No `ARCHITECTURE.md` contract change (Appendix A already had the `ClientSecret` row). _(Orchestrator-written.)_
- **Architecture-doc notes (→ `ARCHITECTURE.md` ARCH-010 §7):** the realtime metrics split + `connecting`@initiation + the smoke-confirm bundle (event type strings, error envelope, `output_audio.delta`-on-DC, connectionstate names, frame envelopes). _(Orchestrator-written.)_
- **Future TODO — RE-SEQUENCED (Carry-forward + backend dependency):** the `ModeTransitionEvent` persistence (backend `POST …/mode-transition` + frontend POST + TS mirror + `modeTransitions` graduation).
- **Future TODO — deferred (G / fallbacks):** re-mint on `expiresAt`; auto-reconnect (≤2); the LOW hardenings (`responseDone`+`error` race; `onServerEvent` immutability; SessionSetup teardown-on-failed-End).
- **Discharged this round:** the B.9c-ii failed-realtime-turn `Errors` population; the double-`connect()` orphan-leak guard; the B.3-origin `realtime.session.connecting` stamp; the `<audio>` lifecycle teardown (all at E.5a); the D.3 capture-reuse decision (E.3); the realtime cost output-side `outputAudioDurationMs` (still a carry-forward — E.4 priced input-only; the field exists, the frontend report is a later accuracy pass).
- **Wiring follow-ups:** NONE — all features production-reachable.

### Process finding (surfaced to the orchestrator)
- **The per-slice preflight omits `format:check`.** `web/CLAUDE.md`'s preflight line is `lint && typecheck && test` (no Prettier), so 9 realtime files (8 test files + `realtimeTurnController.ts`) committed across E.3–E.5b drifted from Prettier — caught only at this `/session-end` full gate. **Fixed here** via `prettier --write` (whitespace/line-wrap only; no logic change; 157 tests still green), committed separately as `style(web)`. **Recommendation (orchestrator-owned `web/CLAUDE.md` / tooling):** add `format:check` to the per-slice preflight line, or a Prettier pre-commit hook, so the drift can't recur (mirrors web lesson §1's formatter-discipline concern).

### Operational
- The realtime path is fake-tested + manual-smoke-exempt for the WebRTC/audio internals; the **first real-key realtime smoke (demo-checklist)** remains outstanding alongside the still-pending C/D cascade real-key smoke.

## How to use what was built

The realtime mode now works end-to-end in the browser: select Realtime in `ModeToggle` (config-gated) → Start session (SessionSetup) → Start recording (RecordingControls) streams the mic over WebRTC, Stop commits + requests a response → translated voice plays via the remote `<audio>` + source/target transcripts render (or "source unavailable" if input transcription is off) → per-stage realtime latency + cost panels populate. A mid-session disconnect surfaces an advise-switch-to-Cascade error; switching to Cascade tears the realtime connection down (no double-mic); End Session tears it down. Real-provider behavior pending the deferred real-key smoke.
