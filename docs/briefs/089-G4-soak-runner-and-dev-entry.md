# /tdd brief — soak_runner_and_dev_entry

## Feature
The **soak runner** that composes the full harness and drives a scripted 5-minute bidirectional EN↔ES conversation through the REAL pipeline for one mode — plus a **dev-only entry** to launch a run and surface the `SoakReport`. The runner's **orchestration is TDD'd** (fake deps + fake timers: drive → sample heap/disconnects → WER-via-script → assemble the report via 087's engine); the **live composition** (synthetic `getUserMedia` → the real `recordingActions`/`realtimeTurnController` → real providers) is the **smoke shell**. This is the §18/§20 shape a third time — and it completes the harness so 090 can run it for real.

## Use case + traceability
- **Task ID:** G.4-FE-drive (the 5-min synthetic soak-harness — the runner + dev entry).
- **Architecture sections it implements:** `ARCHITECTURE.md §15 / ARCH-020` (the 5-min stability run this automates), `§10 / ARCH-013` (the per-turn `LatencyEvent`s it collects), `§12 / ARCH-015` (WER-via-script through `POST /api/evaluation/wer`), `§4 / ARCH-007` (clean-separation — drive via the real store-backed flow, read the store; never bypass it).
- **Related context:** `docs/g4-harness-design.md` (decisions 2A/2C/3A + the dev-only constraint). Consumes everything shipped: 087 `0d64307` (`computeSchedule`, the script, `assembleSoakReport`, `aggregateWer`, drift/leak verdicts), 088 `5b026a2` (`createSyntheticAudioStream`, `createSoakAudioCache`/`fetchDevTtsAudio`, the `audioCaptureController` `getUserMedia` seam), 086 `1f18eaa` (`POST /api/dev/tts`).

## ⚠️ Build vs run boundary (read this)
- **089 BUILDS + TDD's the runner orchestration + wires the live composition + the dev entry.** It does NOT need real keys: the orchestration is TDD'd against fakes; the live wiring is smoke-verified for the non-audio path.
- **The full end-to-end real-audio drive needs REAL TTS** — fake-provider TTS bytes are not decodable by `AudioContext.decodeAudioData`, so the synthetic stream can only be built from REAL synthesized audio. Therefore the **real-key 5-min run × both modes = 090** (lead/user-coordinated live capture), which records the `SoakReport`s into the ARCH-020 checklist + feeds G.5. 089's manual-smoke checklist notes which items defer to 090.

## Acceptance criteria (what "done" means)
- [ ] **`createSoakRunner` / `runSoak(mode, opts)`** — composes a harness capture controller + realtime client with the **synthetic `getUserMedia`** (decision 1A / Q1a — its OWN instances, zero production-singleton mutation), drives the real `recordingActions` (cascade) or `realtimeTurnController` (realtime) in **bidirectional + continuous/auto-VAD** mode, plays the synthetic stream at 1× real-time, and runs for the script's duration.
- [ ] **Heap + disconnect sampling:** samples `performance.memory.usedJSHeapSize` on a fixed interval into a series; counts transport-close/`failed` events (cascade WS close, realtime pc `disconnected`/`failed`). Feeds 087 `leakVerdict` + the disconnect count.
- [ ] **Per-turn collection:** on each completed turn (read from the store — the real flow already accumulates `turns[]`/`latencyEvents[]`), collect the end-to-end latency series + playback-end stamps + the source transcript. **No store bypass** (ARCH-007).
- [ ] **WER-via-script:** pair each completed turn's source transcript (hypothesis) with its script utterance (reference, by order/index) → `POST /api/evaluation/wer` → 087 `aggregateWer`. **Sidesteps the broken manual WER flow** (Finding 3) — the hypothesis is the REAL pipeline STT output, not a click-to-record capture.
- [ ] **Report:** assemble the `SoakReport` (087 `assembleSoakReport`) per mode — the three ARCH-020 booleans + latency-slope/overlaps/skew/leak/WER.
- [ ] **Clean teardown:** at duration end, stop the synthetic stream, end the session, clear the sample interval, dispose the capture controller / realtime pc (no leaked timers/tracks — the harness must not itself leak, or it poisons the measurement).
- [ ] **Dev-only entry:** a dev-gated trigger (e.g. a `?soak=1` dev panel or dev route) to launch a run for a chosen mode + render/log the `SoakReport`. Excluded from the normal demo UI (ARCH-007 clean-separation preserved).
- [ ] Orchestration unit tests pass (fake deps + fake timers); `tsc --noEmit` strict (no `any` on exports); `npm run lint` + `npm run format:check` clean; `/preflight` clean.
- [ ] **Manual-smoke checklist** (session doc; the real-audio items run at 090): synthetic stream → cascade PCM frames + auto-VAD turns; → realtime track + turns; heap series + disconnect count populate; WER pairs resolve; the `SoakReport` renders.

## Wiring / entry point (Step 7.5)
Production entry = the **dev-only soak entry** (`?soak=1` / dev route) → `runSoak(mode)`. This IS the harness's reachable entry (unlike 087/088 which deferred to here). Confirm the dev entry invokes the runner + the runner composes the real drive flow. The end-to-end audio path is smoke/090; the orchestration is suite-pinned.

## Files expected to touch
**New (under `web/src/soak/`):**
- `soakRunner.ts` (+ `.test.ts`) — `createSoakRunner`/`runSoak`: compose, drive, sample, collect, WER, assemble, teardown. Orchestration TDD'd via injected deps.
- `soakHeapSampler.ts` (+ `.test.ts`) — interval heap sampling → series (thin; `performance.memory` read is smoke, the accumulation is tested).
- the dev entry (a small dev-gated component/route + its mount) — UI/smoke.

**Modified (minimal, flag at 2.5 if more):**
- possibly a dev-only mount point (e.g. `main.tsx`/`App` gated on `?soak=1`) — keep it a thin dev-only branch, no production-path change.

## RED test outline (Step 2) — orchestration core (fake deps + fake timers)
1. **`runner_drives_selected_mode`** — `runSoak('cascade')` with fake deps starts a bidirectional+continuous session in cascade mode + starts the synthetic stream; `'realtime'` drives the realtime controller. Why: per-mode drive.
2. **`runner_samples_heap_on_interval`** — fake timers advanced over the duration → heap sampled the expected number of times into the series. Why: leak measurement input.
3. **`runner_collects_turn_series_from_store`** — a fake store with N completed turns → the runner extracts the latency series + playback-end stamps in order. Why: drift/overlap inputs (ARCH-007 store-read, no bypass).
4. **`runner_pairs_script_utterances_for_wer`** — completed turns + the script → reference(script)+hypothesis(turn transcript) pairs in order → fake `/wer` called per pair → `aggregateWer`. Why: WER-via-script (Finding-3 sidestep).
5. **`runner_assembles_soak_report`** — end-to-end with fakes → a `SoakReport` with the three ARCH-020 booleans derived from the collected verdicts. Why: the deliverable artifact.
6. **`runner_tears_down_cleanly`** — at duration end: synthetic stream stopped, session ended, sample interval cleared, capture/pc disposed (assert via fake-dep call spies). Why: the harness must not itself leak (or it poisons the no-leak measurement).
7. **`heap_sampler_accumulates_series`** (`soakHeapSampler.test.ts`) — fake `readHeap` + fake timer → an ordered series; `stop()` clears the interval. Why: thin pure accumulation.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (consumes existing contracts: the store, `LatencyEvent`, `POST /api/evaluation/wer`, the cascade/realtime drive flows; the dev entry adds no persisted field).
- **Orchestrator doc rows to write hot (Step 9 routing):** none expected. This is the slice that COMPLETES the §18/§20-FE-analogue pattern → I'll bank that held lesson at this slice's routing (web LESSONS, next #). The `web/src/soak/` area + ARCH-020 realization note are batched for round-close.

## Things to flag at Step 2.5
1. **How does the runner detect turn completion + when to advance/collect?** In continuous/auto-VAD mode the real flow auto-rearms per utterance and accumulates `turns[]` in the store. Default: the runner **subscribes to the store** (or polls it) and collects each turn as it reaches a terminal status; the synthetic stream's schedule drives the cadence, the auto-VAD detects each utterance boundary. Vote: **store-subscription-driven collection** (no manual per-turn stop — let auto-VAD do it, matching the live demo flow). Confirm the exact store seam (selector/subscription) you'll use.
2. **Run termination — fixed duration vs script-complete?** Default: run until **max(script schedule end, 5 min)**, then teardown — whichever the soak target wants (the script is ~5 min by design). Vote: **schedule-end + a small drain margin** for in-flight final turns; cap at a hard ceiling so a stuck run can't hang.
3. **Dev entry form — query-param panel vs dev route vs console-invokable.** Default: a **`?soak=1` dev-gated panel** with a mode picker + a "run" button rendering the `SoakReport` (JSON + the 3 booleans). Vote: **`?soak=1` panel** (visible, demoable); keep it dev-only (not in the normal UI), ARCH-007 preserved. Confirm the gating mechanism (mirror how the app already gates any dev affordance, or a simple `import.meta.env.DEV` + query check).
4. **Disconnect signal source per mode.** Cascade = WS `close` (the `cascadeStreamClient` close/terminal); realtime = pc `connectionState` `disconnected`/`failed` (the `realtimeWebRtcClient`). Default: subscribe both via the existing client callbacks. Vote: **ride the existing client close/connectionState callbacks** (don't add new transport hooks).

## Dependencies + sequencing
- **Depends on:** 087 (`0d64307`) + 088 (`5b026a2`) + 086 (`1f18eaa`) — all landed.
- **Blocks:** **090** — the real-key 5-min run × both modes (manual, lead/user-coordinated): pre-generate the cached audio (086), run cascade + realtime, capture the two `SoakReport`s, record into the ARCH-020 checklist + feed the real G.5 numbers. Possibly a BE need if the soak surfaces WS/continuous instability or the inter-turn-gap (Option-B).

## Estimated commit count
**1** (preferred) — the runner + heap sampler + dev entry are one cohesive "make the harness runnable" unit; no safety invariant. **If Step-2.5 judges it too large**, split **(a) the runner + heap sampler** (TDD'd orchestration core) from **(b) the dev entry + live composition wiring** (smoke shell). No safety-critical pin → bundling is fine.

## Lessons-logged candidates anticipated
- **Convention candidate (the orchestrator will BANK at this slice)** — the §18/§20-FE-analogue: a FE soak/measurement harness = a **thin runtime smoke-shell over a pure TDD'd core** (087 pure compute + 088/089 TDD'd seams: DI-seam, cache, schedule-consumption, runner orchestration via fake deps/timers; the browser-audio + real-provider live drive is manual-smoke). Sub-notes: WER-via-script feeds the REAL STT as hypothesis (Finding-3 sidestep); the harness must tear down cleanly (it must not itself leak, or it poisons the no-leak measurement); a bytes-not-JSON loader keeps its own boundary.
- **Architecture-doc note candidate** — ARCH-020's 5-min preflight is now an automated synthetic harness (dev-only, capture-boundary injection, store-read collection) producing a structured `SoakReport`; the manual eyeball preflight remains the fallback.

## How to invoke
1. Read this brief + `docs/g4-harness-design.md`; skim the 087 engine + 088 plumbing you compose.
2. Run `/tdd soak_runner_and_dev_entry`.
3. Step 0 (Restate) → confirm against the Feature line + the build-vs-run boundary.
4. Step 2.5 → send the test write-up + answers to the 4 questions (esp. Q1 the store seam + Q3 the dev entry).
5. Step 7.5 → the dev entry IS this slice's reachable entry; the real-audio drive is 090.
6. Step 9 → flag completion of the §18/§20-FE pattern (I bank the lesson here) + any doc-row candidate.
