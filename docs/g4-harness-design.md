# G.4 — 5-Minute Synthetic Soak-Harness — Phase Design + 3 Load-Bearing Sub-Decisions

> Authored by `boostlingo-main-orchestrator` for the lead → user (AskUserQuestion). Mapped against the real `web/` capture code + ARCH-020. **No brief dispatch until the 3 sub-decisions settle.** Mirrors `docs/bidirectional-phase-design.md`.

## Read-back (cycle Step 4)
- `/orchestrate-start` run (not `/session-start`).
- Registry written: `boostlingo-main-orchestrator`, team `boostlingo-main`, role orchestrator, cwd = repo root.
- cwd = repo root (`ai-interpreter-workbench/`).
- Predecessor handoff `023` read + `MVP_TASKS.md` "Currently in progress" + Carry-forward. Phase J SEALED at `938d7fc`; **server 464 / web 342 green.** Both `boostlingo-main-` impls verified standing by.
- Owed (non-blocking, NOT this design): the Phase-J ARCH-doc-sync pass (Carry-forward; one focused pass clears 067–077 + 074–077 + Phase-J residual).

## The capability (user-settled framing)
A dev-only harness that drives a **scripted bidirectional EN↔ES help-desk conversation through the REAL pipeline for 5 min × EACH mode**, reusing the now-complete Phase-J bidirectional feature, to validate ARCH-020 stability (§15: *5-minute run → (1) no disconnect, (2) no audio drift/overlap, (3) no memory leak*) plus latency, cost, and **WER-via-script**.

**User decisions already locked (via lead, do NOT re-litigate):**
- (a) Scenario = neutral **travel / help-desk** exchange (traveler ↔ local), ~5 min alternating EN + ES, scripted → known ground-truth text.
- Audio = synthesize each utterance once with the **already-integrated OpenAI TTS** (EN voice / ES voice), **cache** it, reuse the **identical** audio for BOTH modes (fair + repeatable).
- Injection = **PROGRAMMATIC into the capture pipeline** — the cascade audio worklet + the realtime WebRTC track — **NO virtual mic device**.
- (b) WER = **WER-via-script** — auto-score STT output against the known script text, both modes (sidesteps the broken click-to-record WER flow, which stays queued/not-urgent).
- (c) Start now. Timebox OFF — full scope, both modes.

---

## Key finding — there is ONE clean injection primitive that serves BOTH modes

Both capture paths begin at a `getUserMedia({ audio: true })` call. So the harness does **not** need to splice into the worklet or the WebRTC track internals — it splices **one layer up**, at the `getUserMedia` boundary, returning a **synthetic `MediaStream`** in place of the mic. Everything downstream (worklet PCM encode, real WS send; WebRTC encode, real track send) then runs **unchanged on the real pipeline**.

**The primitive:** a Web Audio graph the harness owns —
`AudioBufferSourceNode` (a decoded, cached TTS utterance) → `MediaStreamAudioDestinationNode` → `.stream` (a real `MediaStream` carrying a real `MediaStreamTrack`).
Scheduling each utterance's `AudioBufferSourceNode.start(when)` at its scripted offset plays the conversation **at 1× real-time** into that one stream. That same synthetic stream feeds both modes:
- **Cascade:** `audioCaptureController` does `context.createMediaStreamSource(stream)` → worklet → **real linear16 PCM frames** → real WS.
- **Realtime:** `realtimeWebRtcClient` does `pc.addTrack(track, micStream)` → **real WebRTC encode/send**.

**Mapped seams (real file:line, verified this session):**
- Cascade mic: `web/src/audio/audioCaptureController.ts:53` calls `navigator.mediaDevices.getUserMedia` **directly** (no DI seam today) → `:54` `new AudioContext()` (browser-default sample rate, no resample) → `:55` loads `pcmWorklet.ts` → `:56` `createMediaStreamSource(stream)` → `:57–62` `AudioWorkletNode('pcm-frame-processor')` → `port.onmessage` → `onFrame`.
- Cascade PCM format: `web/src/audio/pcm.ts` Float32→Int16 linear16; `web/src/audio/pcmWorklet.ts` 20 ms frames at context sample rate; sent raw-binary at `web/src/cascade/cascadeStreamClient.ts:244` (`sendFrame` → `ws.send`); `buildStartFrame` declares `encoding:'linear16'` + `sampleRate` (`:71–86`).
- Realtime mic: `web/src/realtime/realtimeWebRtcClient.ts:121` `getUserMedia` is **already DI-injectable** via `RealtimeWebRtcDeps.getUserMedia` (`:79–87`, default `navigator.mediaDevices.getUserMedia`) → `:159–161` `pc.addTrack(track, micStream)`.
- Lifecycle hooks the harness drives: cascade `web/src/state/recordingActions.ts` (`startRecording`/`stopRecording`/the J.5 `onCascadeTerminal` auto-VAD re-arm loop); realtime `web/src/realtime/realtimeTurnController.ts` (auto-VAD continuous listening). Output/playback: `web/src/audio/playbackController.ts` (cascade MSE + `playback.started` stamp) + `realtimeWebRtcClient.ts:212` `attachRemoteAudio` (realtime track).

**Implication:** the realtime seam exists already; only the cascade `audioCaptureController` needs a small parallel DI seam (mirroring the realtime `getUserMedia` dep + the existing `wsFactory`/`createPeerConnection` test injectables) for the cleanest splice. That choice is sub-decision (1).

---

## (1) The programmatic-injection SEAM — where/how to splice the synthetic stream

- **Option A — Inject at the `getUserMedia` boundary via a DI seam (RECOMMENDED).** The harness builds the synthetic `MediaStream` (above) and supplies it where each controller calls `getUserMedia`. Realtime already accepts this (`RealtimeWebRtcDeps.getUserMedia`); add a **parallel DI seam to `audioCaptureController`** (a `getUserMedia?` dep, defaulting to `navigator.mediaDevices.getUserMedia` — identical to the realtime pattern + the existing `wsFactory`/`createPeerConnection` injectables). *Tradeoff:* **most faithful** — the worklet does real PCM encoding, WebRTC does real encode, both transports run for real, so drift/leak/disconnect are measured on the genuine path; the only production change is one additive, test-shaped DI seam (no behavior change when the dep is absent). Requires the synthetic graph to play at 1× real-time (it does — `AudioBufferSourceNode` scheduling).
- **Option B — Inject below the worklet (feed PCM frames directly to `onFrame`/`sendFrame`).** The harness encodes linear16 itself and pushes frames on a 20 ms timer. *Tradeoff:* skips the **real** worklet/encode path (less faithful), **and has no realtime analogue** — WebRTC needs a real track, so realtime would still require the getUserMedia injection. So B can't unify the two modes and under-tests cascade. **Rejected.**
- **Option C — Globally monkeypatch `navigator.mediaDevices.getUserMedia`.** Replace the global before the run; both controllers pick up the synthetic stream with **zero production change**. *Tradeoff:* global mutation that also captures the blob-recording path, must be restored carefully, and introduces global state that muddies leak attribution (the very thing the soak measures). The "no-code-change" escape hatch, not the clean seam.
- **Recommendation: A.** **Impact:** A exercises the full real pipeline (worklet PCM + WebRTC encode) so the soak numbers are genuine, at the cost of one additive DI seam consistent with the codebase's existing test seams. B can't cover realtime; C trades the production change for global state that weakens leak measurement. None of the three changes the **comparison** — they change only injection fidelity.

## (2) The audio-drift MEASUREMENT method — what "no audio drift/overlap" means operationally

ARCH-020's "no audio drift/overlap" needs a concrete, deterministic pass/fail. "Drift" = does latency/buffering **accumulate** over 5 min so output progressively lags or successive turns collide?

- **Option A — Per-turn injected-vs-observed delta trend + overlap detection (RECOMMENDED).** The harness owns the injection clock, so per utterance it knows the scheduled vs actual injection start, and the pipeline already emits per-turn `LatencyEvent`s (speech-end→first-audio, etc.). **Drift = the slope of per-turn end-to-end latency over the run** (flat ≈ no drift; rising = accumulating lag — the ARCH-020 failure). **Overlap = turn N+1's injection/capture beginning before turn N's output playback completes** when the script says it shouldn't (read from the `playback.started`/playback-complete stamps + the harness schedule). *Tradeoff:* rides the existing LatencyEvent trail + the harness's deterministic clock; no DSP; deterministic thresholds (latency slope ≈ 0, zero unplanned overlaps).
- **Option B — Acoustic waveform cross-correlation of output vs a reference.** Capture output audio, cross-correlate against an expected waveform for sample-level offset growth. *Tradeoff:* most rigorous in principle, but **defeated by non-determinism** — the translated TTS output text varies run-to-run, so there's no fixed reference waveform to correlate against; plus it needs output capture + DSP. Overkill + fragile for a workbench.
- **Option C — Playback-clock vs wall-clock skew sampling.** Periodically sample the playback `currentTime` against `performance.now()`; drift = the slope of (audioClock − wallClock). *Tradeoff:* simple + catches playback buffer underrun/overrun accumulation, but only the **playback leg**, not end-to-end pipeline drift; cascade-MSE vs realtime-track playback differ. A useful **secondary** signal, not the primary.
- **Recommendation: A, with C folded in as a cheap secondary.** **Impact:** A gives a deterministic, instrumentation-native drift verdict that reuses the metrics trail the workbench already produces; C adds a low-cost playback-skew cross-check; B's acoustic approach is unreliable against non-deterministic translation output. (Disconnect = transport close/`failed` events; memory-leak = `performance.memory.usedJSHeapSize` sampled over the run — Chrome-only, which matches the documented Chrome demo path — looking for a monotonic, non-plateauing climb after warm-up.)

## (3) The script content + length — the canonical scripted conversation

User-fixed: travel/help-desk, traveler (EN) ↔ local (ES), ~5 min, alternating EN+ES, known text. Open: the exact length/cadence, which interacts with the J.5 continuous loop + the smoke-gated inter-turn-gap (Carry-forward item, origin 082-J5).

- **Option A — ~24 utterances (12 EN + 12 ES), alternating, ~8–12 words each, ~12 s/turn cadence (RECOMMENDED).** A realistic help-desk back-and-forth (traveler asks EN → rendered ES; local answers ES → rendered EN), ~5 min, with **natural inter-utterance gaps**. Alternating source language exercises bidirectional detect+flip **in both directions**; ~24 known-text utterances/mode = a solid WER-via-script sample. *Tradeoff:* the realistic cadence is also the honest demo cadence — its natural gaps won't artificially trip the inter-turn reconnect, so the soak reflects real use.
- **Option B — Denser: ~40 short utterances, ~7 s/turn.** More turn-churn (more reconnect-loop stress) + more WER samples, but **risks tripping the smoke-gated inter-turn-gap** and reads less like real use. *Tradeoff:* better as a later **stress variant** than the canonical run.
- **Option C — Sparser: ~12 longer utterances, ~20–25 s/turn.** Fewer reconnects, longer continuous segments — stresses single-stream stability over turn-churn; fewer WER samples. *Tradeoff:* under-exercises the per-turn reconnect path the demo actually uses.
- **Recommendation: A** (canonical), with **B retained as an optional stress variant** if the soak looks clean and time allows. **Impact:** A balances turn-churn stress vs single-segment stability and yields ~24 WER samples/mode; A's realistic gaps are exactly what would (honestly) expose the inter-turn-gap issue if it's real, without manufacturing it.

---

## Design constraints (NOT decisions — baked in regardless of the picks above)
- **1× real-time pacing is mandatory.** The synthetic graph plays at wall-clock speed so the 5-min soak genuinely takes 5 min and actually stresses stability/leak. No batch/faster-than-real-time feed.
- **Dev-only entry, never in the demo/production surface.** The harness is gated behind a dev-only trigger (e.g. `?soak=1` dev panel or a dev route), excluded from the demo UI. **ARCH-007 clean-separation holds** — components still render only from the store; the harness injects at the capture boundary + reads metrics, it does not bypass the store.
- **WER-via-script consumes the REAL STT output.** Per turn, `POST /wer` (existing `EvaluationService`, ARCH-015) with `reference = scripted utterance text`, `hypothesis = the pipeline's captured source transcript` (cascade Deepgram final; realtime `input_audio_transcription`). The harness aggregates per-mode WER. This sidesteps the broken record→/transcribe→/wer flow (Finding 3) by feeding the real pipeline's STT as the hypothesis — no dependency on that fix.
- **TTS cache** — synthesize each utterance once (EN voice for EN lines, ES voice for ES lines) via the integrated OpenAI TTS; cache as WAV keyed by `(text, voice)`. Open sub-question (minor; defaulting unless the user/lead objects): **cache on first run + gitignore** (regenerate if missing) vs **commit the WAVs** for fully offline/deterministic replay. Default lean: generate-on-first-run + gitignore (no binaries in the repo; the script text is the committed source of truth).

## Proposed slice decomposition → briefs (DRAFT — dispatch only after the 3 picks land)
- **G.4a (FE / seam):** add the `getUserMedia?` DI seam to `audioCaptureController` (mirror the realtime dep) + the synthetic-stream generator (`AudioBufferSource → MediaStreamAudioDestinationNode`) from cached WAV. *(TDD-able: the DI seam + the PCM-frames-flow-from-a-synthetic-stream assertion.)*
- **G.4b (harness driver):** the scripted-conversation runner (load/cache TTS, schedule utterances at 1× real-time, drive start/stop/turn lifecycle for a mode, 5-min loop) — dev-only entry.
- **G.4c (measurement):** the soak instrumentation — latency-slope + overlap detection (decision 2A), heap sampling, disconnect watch — emitting a structured soak report; WER-via-script aggregation.
- **G.4d (run + record):** execute 5 min × both modes; log results into the ARCH-020 checklist + feed real numbers to G.5. Plus the existing G.4 manual-preflight items (mic-denied path; backend code-hygiene LOWs) folded in.

*(Decomposition is provisional — the seam pick (1) most affects G.4a; the script pick (3) sets G.4b's data; the drift pick (2) sets G.4c. Final brief split happens after the picks.)*

## Cross-doc impact (orchestrator writes hot during the round)
- New dev-only harness modules under `web/src/` (likely a `web/src/soak/` area) — **no contract-model changes** expected (the harness consumes existing WS/track/`/wer` contracts). The one production touch is the additive `audioCaptureController` `getUserMedia` DI seam (a test/harness seam, not a wire-contract field) → a `web/CLAUDE.md` note, not an Appendix-A row, unless Step-2.5 surfaces a contract change.
- ARCH-020 §15 gets a realization note (the manual 5-min preflight is now **also** driven by a synthetic harness); ARCH-007 note that the harness is a dev-only capture-boundary injector preserving clean-separation. Folds into the owed Phase-J ARCH-doc-sync pass.

---

## WER-wiring sub-decision (089b — escalated 2026-06-01, NEEDS USER CALL)

**Discovered at 089 Step-2.5 (FE implementer, orchestrator-verified):** `POST /api/evaluation/wer` sources the reference from the phrase store **by `phraseId`** (server `EvaluationService.cs:93`; lesson §27 — deliberate design), NOT from the request body. The soak's references are the committed FE script (087 `soakScript.ts`), which are not backend phrases. So the harness **cannot score its script via `/wer` as-is** — my 089 brief's `{reference, hypothesis}` assumption was wrong. The runner's `computeWer` is an **abstract seam**, so **089a (orchestration) is unblocked**; only the 089b PRODUCTION wiring needs this call.

**Options (for AskUserQuestion):**
- **(i) Compute WER client-side** — a small TDD'd `soakWerCalc.ts` mirroring the ARCH-015 algorithm (normalize → DP edit-ops → (S+I+D)/N, unbounded). *Tradeoff:* self-contained, FE-only, no BE dep, unblocks 089b immediately; spec-faithful if TDD'd against the same ARCH-015 cases. **BUT** a SECOND WER implementation (the backend `WerCalculator` is canonical) — a DRY/divergence smell for a measurement workbench. *(FE implementer's lean.)*
- **(ii) Seed the 24 soak utterances as backend `EvaluationPhrase`s** → the runner POSTs `/wer {phraseId, hypothesis}`. *Tradeoff:* uses the CANONICAL backend WER; **no contract change**. **BUT** couples the FE script ↔ BE phrases (the script text lives in two places; needs a consistency check) + a BE slice.
- **(iii) Extend `/wer` with an additive explicit-reference path** (capped, parallel to the phraseId path) → the runner POSTs `{reference: script-text, hypothesis}`. *Tradeoff:* CANONICAL WER, **no script↔phrase coupling**, gives the holding BE a real slice; **BUT** a contract-surface change that reopens §27's reference-from-store design (additive, doesn't break the existing phraseId path).

**Orchestrator lean:** soft — **(i)** for simplicity / self-containment / immediate unblock IF the user accepts a spec-faithful FE reimplementation; **(ii) or (iii)** if comparison-WER integrity (one canonical algorithm) outweighs the coupling/contract cost. Genuinely the user's call — it trades harness-simplicity vs WER-canonical-purity, and the WER axis is eval-judged in G.5.

**Impact:** purely HOW the soak WER is computed — same number range either way; (i) FE-only, (ii)/(iii) reuse the backend algorithm. Does NOT affect the latency / cost / stability measurement. 089a proceeds regardless; only 089b's WER wiring waits on this.

---

## Output-audio-duration work plan (post-091, pre-run — user-directed 2026-06-01)

User decision: BUILD the output-audio signal work AFTER 091, BEFORE the live run (the run is gated on it → overlap + correct realtime cost captured on the first pass).

**⭐ Orchestrator verification (codegraph + reads) — the realtime cost fix is FE-ONLY:**
- `EstimateRealtime` already has an **exact-token path** (`EstimateRealtimeFromTokens`, 053-C2a): prices `inputTokens×inputRate + outputTokens×outputRate` from `response.done.usage`.
- `SessionService.EstimateRealtimeCost` already **maps `CompleteTurnRequest.{Input,Output,Cached}AudioTokens → CostUsage`** (the `hasTokens` branch).
- `CompleteTurnRequest` (both the TS type and the backend DTO) already **carry `inputAudioTokens`/`outputAudioTokens`/`cachedAudioInputTokens`**.
- ⇒ The realtime output undercount is purely that the **FE doesn't populate those token fields from `response.done.usage` at `/complete`**. The BE needs NO change. The fix prices the **reported** tokens (honest — the assumption string already reads "priced from exact audio-token counts (response.done.usage)"), not a duration estimate.

**Decomposition (both FE; BE has NO slice here):**
- **092 (FE) — realtime cost: report `response.done.usage` audio-tokens at `/complete`** (Consumer B). Parse the GA `response.done.usage` token details (input/output/cached audio tokens) in the realtime sink → populate `CompleteTurnRequest.{input,output,cached}AudioTokens` → the BE's existing exact-token path prices output correctly. Surface the output-token count into the store/turn so 093 can reuse it. (Verify the exact GA `usage` field names at Step-2.5 / the live run — carry-forward 76.)
- **093 (FE) — soak overlap completion** (Consumer A). Feed per-turn output-duration into the soak's `resolveOutputDurationMs`: **realtime** = output-audio-tokens ÷ `RealtimeTokensPerAudioSecond` (derived from the reported tokens — disclosed, not a fabricated metric); **cascade** = the played TTS audio duration. → `playbackEndMs = playback.started + duration` → 087 `detectOverlaps` runs → `overlapMeasured` flips true → completes drift-decision 2A.

**Honest-instrumentation note:** realtime cost = reported tokens (exact); realtime overlap-duration = derived from reported tokens (disclosed derivation, same tokens-per-second constant the cost uses); cascade output-duration = measured played audio. No synthetic-precision claims.

**Sequence:** 091 (089b) → 092 (cost-token-report) → 093 (overlap) → the live 5-min × both-modes run.
