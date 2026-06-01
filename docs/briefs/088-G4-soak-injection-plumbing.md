# /tdd brief — soak_injection_plumbing

## Feature
The **audio-injection plumbing** for the G.4 soak-harness: (1) a `getUserMedia` **DI seam** on `audioCaptureController` (cascade-side parity with the realtime client's existing seam), (2) a **synthetic-stream generator** that plays cached TTS utterances at 1× real-time into a `MediaStream` (the single primitive that feeds BOTH modes), and (3) a **cached-audio loader** that fetches synthesized utterances from the dev TTS endpoint (086) and caches+decodes them. This is the **thin smoke-shell** half of the §18/§20 pattern over 087's pure core — the browser-audio wiring is manual-smoke-verified; the deterministic seams (the DI default-preservation, the cache-key/dedup orchestration) are unit-TDD'd.

## Use case + traceability
- **Task ID:** G.4-FE-inject (the 5-min synthetic soak-harness — frontend injection plumbing).
- **Architecture sections it implements:** `ARCHITECTURE.md §4 / ARCH-030` (audio capture format — the synthetic stream feeds the SAME linear16 worklet path), `§4 / ARCH-007` (clean-separation — inject at the capture boundary, never bypass the store), `§7 / ARCH-010` (realtime track), `§15 / ARCH-020` (the soak this enables).
- **Related context:** `docs/g4-harness-design.md` (decision **1A** — inject at the `getUserMedia` boundary via a DI seam) + the 087 commit `0d64307` (the pure core this plumbing feeds) + the 086 commit `1f18eaa` (`POST /api/dev/tts`, the cached-audio source). The injection primitive: `AudioBufferSourceNode` (decoded TTS) → `MediaStreamAudioDestinationNode` → `.stream` — one synthetic `MediaStream` injected at each path's `getUserMedia`; downstream (cascade worklet→PCM→WS; realtime WebRTC track) runs UNCHANGED.

## The seam (verified file:line)
- Cascade: `web/src/audio/audioCaptureController.ts:53` — `createAudioCaptureController()` takes no args; `getUserMedia` is hard-coded (`navigator.mediaDevices.getUserMedia({ audio: true })`). Add an **optional `getUserMedia` dep** (default = `navigator.mediaDevices.getUserMedia`), used at the streaming path (line 53). The production singleton (line 120, `createAudioCaptureController()` no-args) keeps the default → **byte-identical** when not injected. (The blob path, line 81, is eval-recording — out of soak scope; leave it on the default unless trivial to share.)
- Realtime: `web/src/realtime/realtimeWebRtcClient.ts` — the `RealtimeWebRtcDeps.getUserMedia` seam **already exists** (default `navigator.mediaDevices.getUserMedia`, used at the `addTrack` path). No new realtime production change — the harness just supplies it.

## Acceptance criteria (what "done" means)
- [ ] **DI seam (cascade):** `createAudioCaptureController(deps?: { getUserMedia?: ... })` — when `deps.getUserMedia` is provided, `startStreaming` uses it; when absent, it uses `navigator.mediaDevices.getUserMedia` (**byte-identical to today**; the production singleton is unchanged in behavior). Pinned by tests.
- [ ] **Synthetic-stream generator** (`web/src/soak/`): given an ordered list of decoded `AudioBuffer`s + the schedule (087 `computeSchedule`) + a target `AudioContext`, build `AudioBufferSourceNode`s → one `MediaStreamAudioDestinationNode`, expose `{ stream: MediaStream, start(): void, stop(): void }` that schedules each buffer at its 1×-real-time offset. **Browser-audio = manual-smoke** (per the ARCH-020/ARCH-030 capture-exempt posture); the **schedule it consumes is 087-tested**.
- [ ] **Cached-audio loader** (`web/src/soak/`): for each script utterance, `POST /api/dev/tts { text, language }` → `arrayBuffer()` → `AudioContext.decodeAudioData` → cache by key `(text, language)`; **fetch-once** (a cache hit skips the network). The cache-key + dedup + per-utterance orchestration is **unit-TDD'd** (fake fetch + fake decode); the real fetch/decode is smoke.
- [ ] **Clean-separation (ARCH-007) preserved:** the plumbing injects at the capture boundary + reads nothing from / writes nothing to the store directly; the production singleton's default path is untouched. No new persisted field.
- [ ] All unit tests pass; `tsc --noEmit` strict (no `any` on exports); `npm run lint` + `npm run format:check` clean; `/preflight` clean.
- [ ] **Manual-smoke checklist** (documented in the session doc, NOT unit-asserted): a synthetic stream injected via the cascade DI seam produces PCM frames on `onFrame`; injected via the realtime `getUserMedia` dep adds a track to the pc. (Full end-to-end live drive is 089.)

## Wiring / entry point (Step 7.5)
The DI seam's production entry stays the singleton (`audioCaptureController`, unchanged default). The synthetic-stream generator + loader are consumed by **089** (the live drive assembles the stack with the synthetic `getUserMedia` injected + drives a run). So like 087, full production reachability lands in 089 — note it at Step 7.5; pin within-slice via the suite + the smoke checklist. Not a gap.

## Files expected to touch
**New (under `web/src/soak/`):**
- `syntheticAudioStream.ts` (+ `.test.ts` for the schedule-consumption seam) — the `AudioBufferSource → MediaStreamAudioDestinationNode` generator.
- `soakAudioCache.ts` (+ `.test.ts`) — the fetch→decode→cache loader (TDD the cache-key/dedup with injected fetch+decode).

**Modified:**
- `web/src/audio/audioCaptureController.ts` — add the optional `getUserMedia` dep (default preserved). (+ its existing test file gains the DI-seam cases.)

If implementation needs more (e.g. a shared dev-injection accessor so the PRODUCTION singletons pick up the synthetic stream), **flag at Step 2.5** — see Q1.

## RED test outline (Step 2)
1. **`capture_uses_injected_getusermedia`** (`audioCaptureController.test.ts`) — a controller built with a fake `getUserMedia` → `startStreaming` calls the fake, not `navigator`. Why: decision 1A seam.
2. **`capture_defaults_to_navigator_when_absent`** — no dep → uses `navigator.mediaDevices.getUserMedia` (byte-identical). Why: the production singleton must be unchanged.
3. **`cache_fetches_once_per_key`** (`soakAudioCache.test.ts`) — two requests for the same `(text, language)` → one fetch call, second served from cache. Why: fetch-once dedup.
4. **`cache_keys_by_text_and_language`** — same text, different language → two distinct fetches/entries. Why: EN-voice vs ES-voice are distinct audio.
5. **`cache_decode_failure_surfaces`** — a decode/fetch rejection → a clean error outcome (no half-cached entry). Why: robustness (degrade, don't poison the cache).
6. **`synthetic_stream_schedules_buffers_at_offsets`** (`syntheticAudioStream.test.ts`) — given fake buffers + a known schedule, the generator schedules each source at the computed offset (assert the scheduling calls/order against a fake `AudioContext`/source — NOT real audio). Why: 1×-real-time pacing; reuses 087 `computeSchedule`.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (the DI seam is a constructor dep, not a wire contract; new harness modules add no persisted field).
- **Orchestrator doc rows to write hot (Step 9 routing):** likely none. The `audioCaptureController` DI seam is a test/harness seam (note-worthy in `web/CLAUDE.md` alongside the soak-area row I'm holding until the area completes). Flag any lesson candidate at Step 9.

## Things to flag at Step 2.5
1. **How does the PRODUCTION cascade/realtime path use the synthetic `getUserMedia`?** (a) **The harness constructs its OWN capture controller + realtime client with the synthetic `getUserMedia` injected, and 089 drives the real `recordingActions`/`realtimeTurnController` logic with those instances** (the `capture` / webrtc-deps are already injectable) — clean isolation, no production global mutation, no restore-after. (b) a dev-only runtime setter on each production singleton (mutable `getUserMedia` ref set before a run, restored after) — reuses the exact singletons but adds global state. (c) a shared dev-injection module both singletons consult. My default vote: **(a)** — self-contained harness composition, zero production-singleton mutation, exercises all the real per-mode logic with synthetic audio at the bottom. This slice just needs the DI seam to EXIST; 089 does the composition. Confirm so 089's shape is set.
2. **Inject the blob-path `getUserMedia` (line 81) too?** Default: **no** — the soak drives the streaming path (cascade WS / realtime track); the blob path is eval-only. Vote: **streaming-only**; leave blob on the default. (Share the dep trivially only if it's free.)
3. **Cache persistence — in-memory only, or also gitignored disk?** Decision 4 = generate-on-first-run + gitignore (script text is the committed source of truth). Default: **in-memory for a run**; an optional gitignored on-disk cache (e.g. under a dev cache dir) is a nice-to-have to avoid re-synthesizing across runs. Vote: **in-memory this slice; on-disk gitignored cache only if cheap** (else 089/opportunistic).
4. **Decode target — the capture `AudioContext` or a harness-owned one?** Default: a **harness-owned `AudioContext`** hosts the `AudioBufferSource → MediaStreamAudioDestinationNode`; the resulting `MediaStream` crosses into the capture path via `getUserMedia` injection (MediaStream is the interchange; the two contexts stay independent). Vote: **harness-owned context**.

## Dependencies + sequencing
- **Depends on:** 087 (`0d64307` — `computeSchedule` + the script) + 086 (`1f18eaa` — `POST /api/dev/tts`). Both landed.
- **Blocks:** **089** (the live drive: assemble the stack with synthetic `getUserMedia`, run 5 min × each mode, sample heap/disconnects, assemble the `SoakReport` via 087, render via the dev entry). **090** = the actual real-key run + record into the ARCH-020 checklist + G.5 (live, lead/user-coordinated).

## Estimated commit count
**1** (preferred) — the DI seam + the two plumbing modules are one cohesive "feed synthetic audio in" unit, no safety invariant. The DI seam is additive + default-preserving (no behavior change). **If Step-2.5 judges it too large**, split **(a) the `audioCaptureController` DI seam** (its own small commit — it's the one production touch) from **(b) the two `web/src/soak/` harness modules**. No safety-critical pin, so bundling is fine.

## Lessons-logged candidates anticipated
- **Convention candidate** (the §18/§20-FE-analogue lesson the orchestrator is HOLDING from 087) — this slice completes the "thin smoke-shell over a pure TDD'd core" pattern: the browser-audio injection is the smoke shell; the DI-seam + cache-orchestration are the TDD'd seams. Bank once 089 closes the loop.
- **Architecture-doc note candidate** — the `audioCaptureController` `getUserMedia` DI seam (cascade parity with the realtime seam) is the single programmatic-injection boundary for synthetic audio (ARCH-030/ARCH-007 note: dev-only injection preserves clean-separation).

## How to invoke
1. Read this brief + `docs/g4-harness-design.md` (decision 1A) + skim the 087 `web/src/soak/` modules you'll consume.
2. Run `/tdd soak_injection_plumbing`.
3. Step 0 (Restate) → confirm against the Feature line.
4. Step 2.5 → send the test write-up + answers to the 4 questions (esp. Q1, which sets 089's shape).
5. Step 7.5 → note production wiring lands in 089 (this is plumbing, not a gap); include the manual-smoke checklist in the eventual session doc.
6. Step 9 → surface any lesson/doc-row candidate.
