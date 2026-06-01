# Session 025 — Frontend: G.4 Synthetic Soak-Harness (087–093)

- **Date:** 2026-06-01
- **Phase:** G.4 (5-minute synthetic soak-harness, frontend)
- **Predecessor:** [022 — frontend Phase-J bidirectional/continuous + smoke fixes](022-2026-06-01-frontend-phase-j-bidirectional-continuous-smoke-fixes.md) _(G.4 framing set up by the orch handoff [023](023-2026-06-01-orch-handoff-phase-j-seal-g4-next.md))_
- **Successor:** [029 — frontend live-Finding fixes (cached-audio cost, push-to-talk WER, display-turn)](029-2026-06-01-frontend-live-finding-fixes-cost-wer-displayturn.md)

## Why this session existed

ARCH-020 requires a 5-minute back-and-forth run with **no disconnect / no audio drift-overlap / no memory leak**, plus latency/cost/WER. The team built an automated, dev-only **synthetic soak-harness** that drives a scripted bidirectional EN↔ES conversation through the REAL pipeline (synthesized TTS audio injected at the capture boundary, reused across both modes), measuring stability + collecting metrics into a structured `SoakReport`. This session is the **frontend build of that harness (briefs 087–093)** plus a verify-before-building correction on brief 092.

The harness follows the **§18/§20-FE-analogue pattern**: a thin runtime smoke-shell over a pure TDD'd core (the deterministic compute/seams are unit-tested; the browser-audio + real-provider live drive is manual-smoke).

## What was built (by slice)

### 087 — soak engine deterministic core (`0d64307`)
New `web/src/soak/` area, all pure + fixture-tested:
- `soakScript.ts` — the canonical 24-utterance EN↔ES help-desk script (committed WER ground-truth) + `validateSoakScript` shape invariants.
- `soakSchedule.ts` — `computeSchedule` (1×-real-time cumulative offsets + expected playback-end times).
- `soakDrift.ts` — `latencySlope` + one-sided `driftVerdict`, `detectOverlaps`, `playbackSkewSlope` (decisions 2A/2C).
- `soakLeak.ts` — `leakVerdict` (warm-up-then-trend on heap samples, one-sided).
- `soakWer.ts` — `aggregateWer` (mean/median/count, unbounded, degrade-to-null never synthetic-0).
- `soakReport.ts` — `assembleSoakReport` deriving the three ARCH-020 booleans.

### 088 — audio-injection plumbing (`5b026a2`)
- **Modified** `audioCaptureController.ts` — optional `getUserMedia` DI seam (cascade parity with the realtime client; default-preserving → the production singleton is byte-identical).
- `soakAudioCache.ts` — `createSoakAudioCache` (fetch-once + evict-on-failure) + `fetchDevTtsAudio` (`POST /api/dev/tts`, raw WAV bytes, bypasses the JSON `http` helper on its own no-leak boundary).
- `syntheticAudioStream.ts` — `createSyntheticAudioStream` (`AudioBufferSource → MediaStreamAudioDestinationNode → MediaStream`, scheduled at the 087 offsets).
- New `audioCaptureController.test.ts` (the DI seam).

### 089a — runner orchestration core (`624cecc`)
- `soakRunner.ts` — `createSoakRunner(deps).run(mode)` / `runSoak`: sample heap → drive the mode → play the synthetic stream → wait the duration → tear down cleanly → collect per-turn series **from the store** (no bypass, ARCH-007) → pair script↔transcript for WER → assemble the `SoakReport`. Orchestration over abstract injected seams (`SoakDrive`/`SoakStoreView`/`computeWer`), fully fake-dep/fake-timer testable.
- `soakHeapSampler.ts` — interval `usedJSHeapSize` accumulation + clean stop.

### 091 / 089b — live composition + dev entry (`430181b`)
- `soakStoreView.ts` — the store→`SoakTurnObservation` adapter (end-to-end latency via `deriveTurnMetrics`, joined source transcript, `playbackEndMs` = run-relative playback END).
- `soakWerClient.ts` — `createSoakWer` → `POST /api/evaluation/wer {sessionId, reference, hypothesis}` (090's explicit-reference path), reads `result.wer`.
- `composeSoakDrive.ts` — the `SoakDrive` live composition (synthetic `getUserMedia` → own capture/realtime clients → the real `recordingActions`/`realtimeTurnController`, bidirectional + auto-VAD continuous) + the TDD'd `buildSoakScheduleFromBuffers` + `runSoakHarness` (the composition root). Mostly smoke.
- `SoakPanel.tsx` + `main.tsx` — the dev-only `?soak=1` entry (`import.meta.env.DEV`-gated), out of the normal UI.
- **Modified** `domain.ts` — `WerRequest` gained `reference?` + made `phraseId?` optional (mirror 090).
- **Modified** `soakReport.ts`/`soakRunner.ts` — `overlapMeasured` disclosure (honest-degrade: with no per-turn output-duration every `playbackEndMs` is null → disclose unmeasured rather than a silent clean pass).

### 092 — realtime output-audio-token store surface (`7eb38ce`)
**Rescoped after a verify-before-building catch:** the brief's ACs 1–3 (parse `response.done.usage` + populate the `/complete` token fields + honest-omit) were **already shipped at 053-C2b** (verified via `extractRealtimeUsage` + `finalizeTurn`, called on `responseDone` in both manual + auto-VAD paths). Caught before writing no-op tests; rescoped (with the orch) to AC #4 only:
- **Modified** `domain.ts` — `TurnViewModel.outputAudioTokens?: number`.
- **Modified** `sessionStore.ts` — `setTurnOutputAudioTokens` action (mirrors `setTurnCost`; set on `currentTurn` so it rides into `turns[]`).
- **Modified** `realtimeEventSink.ts` — the `responseDone` handler surfaces `event.usage.outputAudioTokens` before `completeTurn` (one parse, two consumers — the controller still forwards usage to `/complete` for cost).
- **Modified** `realtimeTurnController.ts` — widened its store Pick (it builds the sink).

### 093 — soak overlap (decision-2A) + precise disconnect (`0767ac1`) — last code slice
- **Modified** `soakStoreView.ts` — `resolveSoakOutputDurationMs` (realtime: output tokens ÷ 50 tokens/s; cascade: target-transcript chars ÷ 900 chars/min → ms; null when absent) + the 2 BE-mirror constants.
- **Modified** `composeSoakDrive.ts` — `isTransportDisconnect` + `countTransportDisconnects` (precise codes); `disconnectCount()` uses it (replaces the 089b failed-turn proxy); `runSoakHarness` wires the real resolver.
- **Modified** `soakReport.ts`/`soakRunner.ts` — `OverlapBasis` (`'token-derived' | 'char-estimate' | 'none'`) disclosure (so a cascade `overlapMeasured:true` reads as estimate-based, not exact).
- **Modified** `SoakPanel.tsx` — surfaces `overlapBasis`.

## Decisions made

- **§18/§20-FE-analogue pattern** — pure TDD'd core (087 + the deterministic seams) under a manual-smoke runtime shell (browser audio + real-provider drive). Reviewers off per the standing directive; no safety invariant touched.
- **Injection at the `getUserMedia` boundary (decision 1A)** — one synthetic `MediaStream` feeds both modes; the only production change is the additive, default-preserving `audioCaptureController` DI seam.
- **WER via the canonical `/wer` explicit-reference path** (user Option iii / BE 090) — no client-side WER calc; the soak's real STT is the hypothesis (Finding-3 sidestep).
- **Honest-degrade disclosures** — `overlapMeasured` (was overlap checked at all) + `overlapBasis` (token-derived precise vs char-estimate rougher) — so the `SoakReport` never reads a non-measurement as a clean pass, consistent with the project's no-synthetic-metric posture.
- **Realtime overlap-duration from REPORTED tokens (÷50); cascade from the disclosed §36 char→minutes estimate (÷900) over the target transcript.** Both disclosed-not-fabricated; null when absent.
- **Precise transport-disconnect count** — only `cascade.connection_lost` + `realtime.session.disconnected` (the codes the WS-close/pc-failed paths emit), not every failed turn — a deviation from the brief's "subscribe to callbacks" because the cascade client exposes no raw onClose and the realtime `onConnectionState` is owned by the connection manager (orch-confirmed).

## Decisions explicitly NOT made (deferred)

- **No output-audio-duration MEASUREMENT** — 093 uses derived estimates (realtime tokens, cascade chars), not a measured played-audio duration. A real `outputAudioDurationMs` measurement (the lead is surfacing it) would un-estimate cascade overlap + close the realtime cost-unit gap; deferred.
- **No client-callback disconnect subscription** — store-code-counting instead (the raw callbacks are infeasible to subscribe without client surgery / manager conflict).
- **No BE phrase-seeding for WER** — the 090 explicit-reference path made it unnecessary.
- **Live GA-wire-shape verification** (does `output_token_details.audio_tokens` match live; does the synthetic stream produce real cascade PCM / realtime track) — carry-forward-76, a manual-run check.

## TDD compliance

**Clean.** Every deterministic slice was RED→GREEN (script/schedule/drift/leak/WER/report; the DI seam; cache dedup; schedule-consumption; runner orchestration via fake deps/timers; store adapter; WER request-shaping; the `setTurnOutputAudioTokens` action + sink wiring; the overlap derivations + disconnect classify/count + `overlapBasis`). The browser-audio + real-provider live drive (`composeSoakDrive` real-client wiring, `SoakPanel`, `runSoakHarness`, the AudioContext/worklet/track bits) is **manual-smoke-exempt** per the root TDD posture. **No TDD violations.** 092's verify-before-building catch avoided writing no-op tests against already-shipped behavior.

## Reachability

Production entry = the dev-only **`?soak=1`** route (`main.tsx`, `import.meta.env.DEV`-gated) → `SoakPanel` → `runSoakHarness(mode)` → `composeSoakDrive` + `runSoak`. The full chain is reachable end-to-end; **every production soak export is consumed via `runSoakHarness`** (the 087 engine, 088 injection, 089a runner, 089b/091 composition, 092 token surface, 093 overlap/disconnect). The realtime `outputAudioTokens` surface (092) is reachable on the real realtime path (`onServerEvent → sink → setTurnOutputAudioTokens → completeTurn → turns[]`).
- **`validateSoakScript`** is the one non-runtime export — a committed-script integrity guard, exercised by its suite (CI catches a malformed-script edit), intentionally not on the runtime path. Not a wiring gap.
- The full real-audio end-to-end drive is the **manual real-key run** (decodable TTS needs real keys).

## Open follow-ups

**Cross-doc invariant changes (orchestrator-owned — flagged at Step 9, ride the orch's round commit):**
- `TurnViewModel.outputAudioTokens?: number` (092) → `web/CLAUDE.md` cross-doc row + ARCH-007 §4 / Appendix-A note.
- `WerRequest.reference?` + `phraseId?` optional (091) → `web/CLAUDE.md` row + ARCH note (paired with BE 090).

**Architecture-doc note (orchestrator):** ARCH-020 realization — the 5-min preflight is now an automated dev-only synthetic harness producing a structured `SoakReport`; decision-2A overlap is real (realtime token-derived precise + cascade disclosed char-estimate via `overlapBasis`); `noDisconnect` reflects precise transport disconnects; per-mode measurability.

**Lesson (orchestrator — banking):** §18/§20-FE-analogue (thin smoke-shell over a pure TDD'd core; WER-via-canonical-`/wer`; clean teardown; `playbackEndMs` = END; overlap disclosed-unmeasured; bytes-not-JSON loader). Plus a process note: verify own brief premises before building (the 092 catch).

**Carry-forward / G-hardening (LOW):**
- `REALTIME_TOKENS_PER_AUDIO_SECOND=50` (`CostEstimator.cs:26`) + `TTS_APPROX_CHARS_PER_MINUTE=900` (`CascadeWsMapping.cs:99`) are hardcoded BE mirrors — config-sync if the BE rates drift.
- `disconnectCount` is a precise store-code count, not a raw transport-callback subscription (documented; refine only if a manual-run need appears).
- `composeSoakDrive` re-points `setOnTerminal` to the soak's recording controller for the run; the production wiring is stale post-run (dev-only; a reload resets).
- Output-audio-duration MEASUREMENT (the lead's surfacing) would replace the cascade char-estimate + close the realtime cost-unit gap.

**Manual-smoke checklist for the real-key run (G.4-run):** synthetic stream → cascade PCM frames + auto-VAD turns; → realtime track + turns; heap series + disconnect count populate; WER pairs resolve via `/wer`; overlap detection runs (`overlapBasis` per mode); the `SoakReport` renders in the `?soak=1` panel; verify the live `output_token_details.audio_tokens` GA shape (CF76).

## How to use what was built

Run the dev server, open `http://localhost:5173/?soak=1` (DEV only), pick a mode, click **Run soak**. With real provider keys, it drives a scripted 5-min bidirectional conversation through the real pipeline and renders the `SoakReport` (the three ARCH-020 booleans + latency-slope/overlaps/`overlapBasis`/skew/leak/WER). Record the two reports (cascade + realtime) into the ARCH-020 checklist + G.5.
