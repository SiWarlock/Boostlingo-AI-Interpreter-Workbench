# /tdd brief — soak_live_composition_and_dev_entry (089b)

## Feature
The **smoke-shell half** of the soak harness (the 089 split's second slice): wire the 089a runner to the REAL app — `composeSoakDrive` (synthetic audio → capture/realtime clients with the synthetic `getUserMedia` → the real `recordingActions`/`realtimeTurnController` in bidirectional+continuous mode), the store→`SoakTurnObservation` adapter, the WER production wiring to `POST /api/evaluation/wer {reference, hypothesis}` (090), the disconnect wiring, and the dev-only `?soak=1` entry. Completes the §18/§20-FE pattern (the held lesson banks at this slice).

## Use case + traceability
- **Task ID:** G.4-FE-drive-b (the 5-min synthetic soak-harness — live composition + dev entry).
- **Architecture sections it implements:** `ARCHITECTURE.md §15 / ARCH-020` (the 5-min run), `§4 / ARCH-007` (clean-separation — compose the real store-backed drive, read the store, never bypass), `§10 / ARCH-013` (the per-turn metrics the adapter reads), `§12 / ARCH-015` (WER via `/wer`).
- **Related context:** `docs/g4-harness-design.md`; consumes 087 (`0d64307`), 088 (`5b026a2`), 089a (`624cecc` — the `SoakDrive`/`SoakStoreView`/`computeWer` seam contract), and **090 (`<hash on dispatch>` — the `/wer` explicit-reference path)**. Confirmed request shape: `POST /api/evaluation/wer` body `{ sessionId, reference, hypothesis }` (NO `phraseId`, NO `turnId` → no attach, no `IsEvaluation`; the soak's turns stay in the comparison).

## ⚠️ Build vs run boundary (carried from 089)
089b BUILDS + wires the composition + dev entry + TDD's the deterministic seams (store adapter, WER request-shaping). The **full end-to-end real-audio drive needs real TTS** (fake-provider bytes aren't decodable), so the actual 5-min × both-modes RUN = the **manual real-key run** (lead/user-coordinated; records the `SoakReport`s into the ARCH-020 checklist + feeds G.5). The manual-smoke checklist marks what that run exercises.

## Acceptance criteria
- [ ] **`composeSoakDrive(mode)`** implements the 089a `SoakDrive` seam (`start(mode)`/`stop()`/`disconnectCount()`): builds the synthetic stream (088 `createSyntheticAudioStream` over `soakAudioCache`-loaded `AudioBuffer`s + 087 `computeSchedule(durations, gapMs)` index-aligned), constructs its OWN capture controller + realtime client with the **synthetic `getUserMedia`** (Q1a — zero production-singleton mutation), and drives the real `recordingActions` (cascade) / `realtimeTurnController` (realtime) in **bidirectional + continuous/auto-VAD**.
- [ ] **Store→`SoakTurnObservation` adapter** (`SoakStoreView`): reads `store.getState().turns[]` (no bypass, ARCH-007) → per turn `{ index, endToEndLatencyMs = deriveTurnMetrics(turn).speechEndToFirstAudioMs, playbackEndMs, sourceTranscript }`. **`playbackEndMs` = the playback END run-relative** (NOT `playback.started`): derive as `playback.started`(run-relative) + the output-audio duration, or a playback-complete stamp if one exists. `sourceTranscript` = the final source segments joined. **TDD'd** against a fake store state.
- [ ] **WER production wiring** — `computeWer(reference, hypothesis)` POSTs `/api/evaluation/wer { sessionId, reference, hypothesis }` → returns `result.wer`. Request-shaping **TDD'd** (fetch-mock, like 088 `fetchDevTtsAudio`): asserts the URL + body shape + reads `result.wer`; non-ok → sanitized error. **NO client-side WER calc** (090 owns the canonical algorithm).
- [ ] **TS `WerRequest` mirror** — add `reference?: string` + make `phraseId?` optional (mirror 090's backend change). The soak request type-checks.
- [ ] **Disconnect wiring** — cascade WS `close` + realtime pc `disconnected`/`failed` (via the existing client callbacks) → `disconnectCount()`.
- [ ] **Dev-only `?soak=1` entry** — a dev-gated panel (mode picker + "run" → renders the `SoakReport`: the 3 ARCH-020 booleans + latency-slope/overlaps/skew/leak/WER). Excluded from the normal demo UI (ARCH-007 clean-separation; gate via `import.meta.env.DEV` + the query, or mirror an existing dev affordance).
- [ ] Deterministic-seam unit tests pass; `tsc --noEmit` strict; `npm run lint` + `npm run format:check` clean; `/preflight` clean.
- [ ] **Manual-smoke checklist** (session doc; the real-audio items run at the manual run): synthetic stream → cascade PCM frames + auto-VAD turns; → realtime track + turns; heap series + disconnect count populate; WER pairs resolve via `/wer`; the `SoakReport` renders in the `?soak=1` panel.

## Wiring / entry point (Step 7.5)
`?soak=1` dev entry → `runSoak(mode, composeSoakDrive(...) + the real deps)`. This is the harness's reachable production entry (closes the 087/088/089a deferrals). Confirm the dev entry composes the real drive + the runner; the end-to-end real-audio path is the manual run.

## Files expected to touch
**New (under `web/src/soak/`):**
- `composeSoakDrive.ts` (+ minimal `.test.ts` for any deterministic wiring) — the `SoakDrive` impl. Mostly smoke.
- `soakStoreView.ts` (+ `.test.ts`) — the store→`SoakTurnObservation` adapter. TDD'd.
- `soakWerClient.ts` (+ `.test.ts`) — `computeWer` → `/wer` request-shaping. TDD'd (fetch-mock).
- the `?soak=1` dev panel component (+ its mount). Smoke.

**Modified:**
- the TS `WerRequest` type (`web/src/types/…`) — `reference?` + `phraseId?` optional.
- a dev-only mount point (e.g. `App`/`main.tsx` gated on `?soak=1` + `import.meta.env.DEV`) — thin dev branch, no production-path change.

## RED test outline (Step 2) — deterministic seams
1. **`store_view_maps_turns_to_observations`** (`soakStoreView.test.ts`) — fake store with N turns → ordered `SoakTurnObservation[]`: `endToEndLatencyMs` from `deriveTurnMetrics`, `sourceTranscript` joined, index in order. Why: drift/overlap/WER inputs (ARCH-007 store-read).
2. **`store_view_playback_end_is_end_not_start`** — a turn with `playback.started` + an output duration → `playbackEndMs == started + duration` (run-relative), NOT `started`. Why: the overlap detector needs playback END (089a flag).
3. **`wer_client_posts_reference_hypothesis`** (`soakWerClient.test.ts`) — `computeWer('ref','hyp')` (fetch-mock) → POST `/api/evaluation/wer` body `{sessionId, reference:'ref', hypothesis:'hyp'}` (no phraseId/turnId), reads `result.wer`. Why: the 090 contract.
4. **`wer_client_non_ok_surfaces_error`** — non-2xx → sanitized error (no client WER fallback). Why: degrade honestly.
5. **(if any deterministic `composeSoakDrive` wiring)** — e.g. index-alignment of buffers↔schedule, or the disconnect-counter increment on a faked close. Keep it thin; the AudioContext/track/real-client wiring is smoke.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** the **TS `WerRequest` mirror** gains `reference?` + `phraseId?` optional (mirrors 090's backend `WerRequest`). Flag at Step 9 — the orchestrator writes the `web/CLAUDE.md` cross-doc row + the ARCH Appendix-A note (paired with 090's server-side change, same round). The TS TYPE itself is your code (this slice).
- **Lesson:** the §18/§20-FE-analogue lesson banks at THIS slice's routing (the pattern is now complete: 087 pure core + 088/089a TDD'd seams + 089b smoke shell). Flag completion at Step 9.

## Things to flag at Step 2.5
1. **`playbackEndMs` derivation.** Default: `playback.started`(run-relative) + the per-turn output-audio duration (the realtime path measures played output; cascade has the TTS audio duration). If no reliable output-duration exists per mode, fall back to a documented approximation + note it. Vote: **started + output-duration**; flag if a mode lacks the duration so we degrade explicitly (an honest `playbackEndMs: null` → the overlap detector skips that pair, per 087).
2. **`composeSoakDrive` — how the synthetic `getUserMedia` reaches `recordingActions`/`realtimeTurnController`.** Q1a (confirmed feasible): construct a capture controller + realtime client with synthetic `getUserMedia`, pass them as the `RecordingDeps`/`RealtimeTurnDeps` the real drive logic takes. Confirm the exact deps you thread + that the continuous/auto-VAD + bidirectional flags are set on the drive.
2b. **Session for the drive.** The runner needs a real session (`POST /api/sessions`, bidirectional) for the turns to land in the store + a `sessionId` for the WER POST. Confirm the dev entry creates one per run (+ ends it on teardown).
3. **Dev-entry gating + form.** Default: `?soak=1` + `import.meta.env.DEV` gates a panel (mode picker + run button + `SoakReport` JSON/booleans render). Vote: **query + DEV-gate**, mirror any existing dev affordance; keep it out of the normal UI.
4. **Run config surfaced in the panel?** Default: the panel can expose `durationMs`/thresholds (or use sensible constants from 087/089a). Vote: **constants with optional overrides** — the manual run wants a quick launch.

## Dependencies + sequencing
- **Depends on:** 087 (`0d64307`) + 088 (`5b026a2`) + 089a (`624cecc`) + **090 (the `/wer` explicit-reference path — landing now; I'll confirm the hash on dispatch)**.
- **Blocks:** the **manual real-key 5-min run × both modes** (lead/user-coordinated) — the harness's first real exercise; records the `SoakReport`s → the ARCH-020 checklist + G.5. Possibly a BE need if the run surfaces WS/continuous instability or the inter-turn-gap (Option-B).

## Estimated commit count
**1** (preferred) — the live composition + adapter + WER client + dev entry are one cohesive "wire the harness to the real app" unit; no safety invariant. **If Step-2.5 judges it too large**, split **(a) the deterministic seams** (`soakStoreView` + `soakWerClient` + the TS mirror, TDD'd) from **(b) `composeSoakDrive` + the dev entry** (smoke).

## Lessons-logged candidates anticipated
- **Convention candidate (orchestrator BANKS here)** — the §18/§20-FE-analogue, now complete: a FE soak/measurement harness = a **thin runtime smoke-shell over a pure TDD'd core** (087 pure compute + 088/089a TDD'd seams + 089b's deterministic adapter/WER-client, with the browser-audio + real-provider live drive as manual-smoke). Sub-notes: WER-via-script feeds the REAL STT as hypothesis through the canonical backend `/wer` (Finding-3 sidestep); the harness tears down cleanly (must not itself leak); `playbackEndMs` is the playback END (overlap needs it).
- **Architecture-doc note candidate** — ARCH-020's 5-min preflight is now an automated dev-only synthetic harness (capture-boundary injection, store-read collection, canonical-WER scoring) producing a structured `SoakReport`.

## How to invoke
1. Read this brief + `docs/g4-harness-design.md`; you know 087/088/089a + the 090 request shape.
2. Run `/tdd soak_live_composition_and_dev_entry`.
3. Step 0 (Restate) → confirm against the Feature line + the build-vs-run boundary.
4. Step 2.5 → answers to the 4 questions (esp. Q1 `playbackEndMs` + Q2 the compose wiring + Q2b the session).
5. Step 7.5 → the `?soak=1` dev entry is the reachable entry; the real-audio run is manual.
6. Step 9 → flag the TS `WerRequest` mirror cross-doc + the §18/§20-FE lesson completion (I bank it).
