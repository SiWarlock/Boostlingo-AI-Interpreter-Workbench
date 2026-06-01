# /tdd brief — cascade_tts_first_audio_timing

## Feature
Make the cascade `tts.first_audio` latency metric meaningful. Today `TtsFirstAudioMs = Between(tts.started, tts.first_audio)` reads ≈0 ms on every live turn because `tts.started` is stamped on the **first provider event received** — which already absorbs the request→provider round-trip, so first-audio-after-that is sub-millisecond. Re-anchor `tts.started` to the **TTS request-initiation moment** so the metric reflects the genuine time-to-first-audio latency.

## Use case + traceability
- **Task ID:** metrics/cost-accuracy round (feeds G.5 write-up) — BE half.
- **Architecture sections it implements:** `ARCHITECTURE.md` ARCH-013 (per-stage latency; honest instrumentation, never synthesized). ARCH-011 (streaming cascade).
- **Related context:** Carry-forward "cascade metrics correctness bug (4) TTS first-audio 0 ms" (user live-smoke, 3 screenshots) + the lead's metrics/cost item (d). web LESSON §25 (per-stage values are DURATIONS keyed to the panel; the FE half shipped at 056-c1). **⭐ This SUPERSEDES the provisional 057c decision** (`CascadeStreamingOrchestrator.cs:431-433` — "the live 0 ms … is a provider-synchronous-yield artifact … No fix"): 057c logged the delta "so the live smoke quantifies it," the smoke has now confirmed ≈0 ms, and the lead has listed fixing it as in-scope. The honest read: the stamps ARE on distinct provider events (057c is right about that), but `tts.started`'s placement makes the metric measure ~nothing. Moving the START anchor to initiation is the fix.

## ⚠️ Safety / honesty invariant note
This slice touches the **streaming-honesty zone** (root CLAUDE.md item 5; `LatencyEventFactory` doc: "producers stamp on the real first arrival of a provider event; they never synthesize or back-date"). The fix is **honest instrumentation, not synthesis** — `tts.started` is stamped at a REAL pipeline moment (when we initiate the TTS request), not fabricated or back-dated. **Mandatory `security-reviewer` pass at Step 7→8** to confirm the change preserves the no-synthesis / no-back-date invariant. This slice gets its OWN commit (honesty-touching).

## Acceptance criteria (what "done" means)
- [ ] `tts.started`'s absolute `Timestamp` is stamped at TTS **request-initiation** (immediately before / at the start of the TTS synthesis enumeration), NOT on the provider's first `TtsStarted` event arrival.
- [ ] `TtsFirstAudioMs = Between(tts.started, tts.first_audio)` therefore reflects the real to-first-audio latency (a positive, provider-round-trip-inclusive value) — no longer ≈0 on a turn where the provider yields its start + first-audio events synchronously.
- [ ] `tts.complete` / the `TtsCompleteMs` stage duration measures from the SAME initiation anchor (consistent — the TTS stage now begins at initiation, closing the previously-uncounted translation.final→provider-ack gap).
- [ ] No stamp is fabricated or back-dated; `tts.first_audio` still stamps on the real provider first-audio event; all other stage events unchanged.
- [ ] The `security-reviewer` pass confirms the honesty invariant holds.
- [ ] All existing cascade-orchestrator tests still pass (any that asserted the old `tts.started`-on-provider-event timing get updated to the initiation anchor — flag at Step 2.5 if any existing assertion locks the old placement).
- [ ] `/preflight` clean (backend suite green).

## Files expected to touch
**Modified:**
- `Cascade/CascadeStreamingOrchestrator.cs` — stamp `tts.started` at TTS request-initiation (before the `SynthesizeAsync` enumeration loop); drop the re-stamp on the `case TtsStarted` provider event (or keep the provider event consumed without re-stamping). Update / remove the 057c "no fix" diagnostic comment (the delta is now the metric itself).
- `CascadeOrchestratorTests.cs` (`server/AiInterpreter.Tests/`) — RED test pinning the initiation anchor (see outline).

If implementation needs files beyond this list (e.g. the TTS stage signature, or a test-harness clock extension), **flag at Step 2.5** before going GREEN.

## RED test outline (Step 2)
Tests in `CascadeOrchestratorTests.cs` (against the existing fake TTS provider + injected `IClock`):

1. **`tts_started_stamped_at_request_initiation`** — drive the orchestrator's TTS stage with a fake clock that ADVANCES (e.g. +300 ms) between TTS request-initiation and the first provider event (modeling the provider round-trip), and a fake provider that yields `TtsStarted` + `TtsFirstAudio` synchronously.
   - Asserts: the emitted `tts.started` event's `Timestamp` equals the initiation clock value (T0), and `Between(tts.started, tts.first_audio)` ≈ the advanced delta (300 ms) — NOT 0.
   - Why: ARCH-013 — the metric must capture real to-first-audio latency; pins the artifact fix.
2. **`tts_first_audio_still_on_provider_event`** — the `tts.first_audio` event's `Timestamp` is the clock value at the provider's real first-audio event (unchanged; only the START anchor moved).
   - Why: honesty — first-audio is still stamped on the real provider arrival, never synthesized.
3. **`tts_complete_measures_from_initiation`** — `Between(tts.started, tts.complete)` includes the initiation→complete span (consistent anchor).
   - Why: stage-duration consistency; the stage now begins at initiation.
4. **(regression)** any existing test that asserted the prior `tts.started` placement is updated to the initiation anchor (no silent behavior lock). Flag the list at Step 2.5.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** NONE. No DTO / wire / persisted-model field changes; `LatencyEventNames` unchanged; metric formula in `MetricsAggregator` unchanged (it's correct — only the producer's stamp placement moves).
- **Orchestrator doc rows to write hot:** none (no cross-doc table row, no Appendix A change). **ARCH-013 realization-note** candidate (tts.started anchored at stage initiation, not provider-ack, so TtsFirstAudioMs is a real latency) — orchestrator folds at round-seal. The 057c supersession gets recorded in the session doc + this brief.

## Things to flag at Step 2.5
1. **Where exactly to anchor `tts.started`?** Options: **(a)** at TTS request-initiation — the clock value captured immediately before the synthesis enumeration begins [**my default vote** — captures the full to-first-audio latency incl. the provider round-trip; closes the previously-uncounted translation.final→ack gap]; **(b)** keep on the provider `TtsStarted` event but re-anchor `tts.first_audio` to the first `TtsAudioChunk` instead of the `TtsFirstAudio` marker (smaller change, but stays ≈0 if the provider yields marker+chunk synchronously — likely does NOT fix the symptom). My strong lean is **(a)**.
2. **Does any existing assertion lock the old `tts.started`-on-provider-event timing?** If yes, list them; they get updated to the initiation anchor (this is the intended behavior change, not a regression). Surface the list so I confirm none is load-bearing elsewhere.
3. **Honesty-invariant framing.** Confirm in the Step-2.5 write-up that stamping at initiation is a real-moment stamp (not back-dating a provider event). This is the crux the `security-reviewer` will check — get it explicit.

## Dependencies + sequencing
- **Depends on:** nothing — self-contained in the cascade streaming orchestrator.
- **Blocks:** nothing hard. Sibling to the FE cascade `playback.started` instrumentation (a separate slice — enables `speechEndToPlaybackMs`). Both feed the G.5 write-up's latency axis. The session-avg `stt.final` re-anchor is ALREADY done (057a — do NOT re-implement).

## Estimated commit count
**1.** Single focused honesty-touching change → its OWN commit (never bundled, per the safety-slice rule). Small but invariant-adjacent.

## Lessons-logged candidates anticipated
- **Convention candidate** — "a stage-START latency marker is stamped at stage INITIATION (our request), not on the provider's first response event — else the stage's to-first-output metric collapses to ~0 because the start absorbs the round-trip." (Generalizes the 057c supersession.)
- **Architecture-doc note candidate** — ARCH-013: `tts.started` anchored at TTS stage initiation; `TtsFirstAudioMs` is a real to-first-audio latency.

## How to invoke
1. Read this brief end-to-end (note the honesty-invariant section + the 057c supersession).
2. Run `/tdd cascade_tts_first_audio_timing`.
3. Step 0 (Restate) — confirm against the Feature line.
4. Step 2.5 — send the per-test write-up + your read on the design questions (esp. Q1 anchor choice + Q2 existing-assertion list); I reply `APPROVED.`/`TWEAK:`/`ADD:`.
5. Step 7→8 — **`security-reviewer` pass mandatory** (honesty zone).
6. Step 9 — categorized summary (note the 057c supersession + any updated existing tests) + ship/no-ship + draft commit message.
