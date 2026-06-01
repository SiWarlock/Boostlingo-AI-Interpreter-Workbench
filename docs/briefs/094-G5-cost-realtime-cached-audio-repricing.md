# /tdd brief — realtime_cached_audio_repricing

## Feature
Correct `CostEstimator.EstimateRealtimeFromTokens` so cached realtime input-audio tokens are priced at the **cached** rate (gpt-realtime: $0.40/M) instead of the full audio-input rate ($32/M) — by **removing the cached-audio portion from the full-rate base** rather than adding cached on top. Fixes a ~1.5× realtime cost over-count on cached-heavy turns (surfaced by the 2026-06-01 live soak; user-approved fix, option A).

## Use case + traceability
- **Task ID:** G5-cost (pre-G.5 cost-accuracy correction; feeds the G.5 comparison write-up)
- **Architecture sections it implements:** `ARCH-014` (cost estimation + pricing.json), `ARCH-013` (metrics/latency model only insofar as cost rides the turn)
- **Related context:** the live soak Finding (orch analysis 2026-06-01) — realtime per-turn input re-bills the accumulating conversation context; cached tokens are ~75% of input on mid/late realtime turns, so the prior "cached≈0, immaterial" assumption (parser + estimator comments) is falsified. server LESSONS §25 (realtime per-turn cost), §9 (branch-on-basis + degrade). **Paired FE slice 095** forwards `cached_tokens_details.audio_tokens` so this estimator receives the actual cached *audio* count (today the FE sends total `cached_tokens` incl. text).

## Current behavior (the bug)
`EstimateRealtimeFromTokens` (CostEstimator.cs:215-249) computes:
```
usd = audio_tokens * inputRate + output_audio * outputRate        // (1) prices ALL input audio at full rate
    + cached_tokens * cachedRate                                   // (2) then ADDS cached on top
```
Because the cached-audio portion is already inside `audio_tokens` (priced at full $32/M in (1)) **and** added again in (2), and because `cached_tokens` historically carried text+audio, the estimate over-counts. With the cached rate being an 80× discount ($32 → $0.40), the correct treatment must move cached audio OUT of the full-rate base.

## Acceptance criteria (what "done" means)
- [ ] `CachedAudioInputTokens` is interpreted as the **cached input-AUDIO** count (a subset of `AudioInputTokens`), priced at the cached rate; the doc comments on `CostUsage.CachedAudioInputTokens` + `EstimateRealtimeFromTokens` updated to say "cached audio" (not "total cached").
- [ ] Corrected formula: `usd = (audioInput − cachedAudioEffective) * inputRate + cachedAudioEffective * cachedRate + outputAudio * outputRate`, where `cachedAudioEffective = min(cachedAudio, audioInput)` (defensive clamp — cached audio can't exceed total input audio; guards a too-large value during the FE-lands-after gap).
- [ ] gpt-realtime (cached rate configured): cached audio priced at $0.40/M, removed from the $32/M base.
- [ ] gpt-realtime-mini (NO cached rate): cached audio billed at the full input rate (so the whole input audio is at full rate — mini has no cache discount); the existing "no cached-input rate configured" assumption still appended. Net for mini: `(audioInput − cachedEff)*inputRate + cachedEff*inputRate = audioInput*inputRate` — i.e. unchanged from a no-cache world (correct).
- [ ] `cachedAudio == 0` path is byte-identical to today (no regression for the common early-turn case).
- [ ] Honest-degrade unchanged: absent/zero usage still → `Unavailable`/null, never a synthetic $0 (§9/§25); the seconds-estimate fallback path (`EstimateRealtime`, lines 178-208) is **untouched**.
- [ ] All unit tests in `server/AiInterpreter.Tests/CostEstimatorTests.cs` pass (incl. the updated `estimate_realtime_exact_tokens_honors_cached_rate`).
- [ ] `/preflight` clean (dotnet format + build + test).

## Wiring / entry point (Step 7.5)
Already wired — `SessionService.cs:356` calls `EstimateRealtime` at `POST …/turns/{turnId}/complete` (the realtime finalize, inside `FinalizeTurn`); `SessionService.cs:343` maps `request.CachedAudioInputTokens → CostUsage.CachedAudioInputTokens`. No new caller; this slice changes the math inside the existing reachable path. Confirm the call path is unchanged.

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Cost/CostEstimator.cs` — `EstimateRealtimeFromTokens` re-pricing + comment.
- `server/AiInterpreter.Api/Cost/CostModels.cs` — `CostUsage.CachedAudioInputTokens` doc comment (semantics now "cached audio").
- `server/AiInterpreter.Tests/CostEstimatorTests.cs` — update `estimate_realtime_exact_tokens_honors_cached_rate` expected value (contract change — re-assert in place with a clear comment, don't silently delete; §39 precedent) + add the clamp + mini cases.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2) — `CostEstimatorTests.cs`
1. **`estimate_realtime_exact_tokens_honors_cached_rate`** (UPDATE existing) — in 31 audio / out 54 / cachedAudio 10 → asserts `(31−10)*32/M + 10*0.40/M + 54*64/M` (not the old `31*32 + 10*0.40 + 54*64`). Why: the corrected base-exclusion.
2. **`estimate_realtime_exact_tokens_cached_zero_unchanged`** — cachedAudio 0 → asserts `31*32/M + 54*64/M` (no cached term). Why: no regression for early turns.
3. **`estimate_realtime_cached_audio_clamped_to_input`** — audio 20 / cachedAudio 50 (pathological / too-large) → `cachedEff = 20`, asserts `0*32/M + 20*0.40/M + out*64/M` (never negative base). Why: defensive clamp.
4. **`estimate_realtime_mini_cached_audio_full_rate`** — gpt-realtime-mini, in 31 / cachedAudio 10 / out 54 → asserts `(31−10)*10/M + 10*10/M + 54*20/M == 31*10/M + 54*20/M` (whole input at full rate) + the "no cached-input rate configured" assumption present. Why: mini has no cache discount; the rename must not change mini's number.
5. (Keep) the existing seconds-estimate realtime tests UNCHANGED (this slice touches only the exact-token path).

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none structural — `CompleteTurnRequest.CachedAudioInputTokens` + `CostUsage.CachedAudioInputTokens` keep their shape (`int?`), but their **documented semantics change** from "total cached_tokens" → "cached input-audio tokens (`cached_tokens_details.audio_tokens`)". The implementer updates the in-code doc comments (SessionDtos.cs:67-71 + CostModels.cs:43); the **orchestrator** updates the `web/CLAUDE.md` `CompleteTurnRequest` cross-doc row + the `ARCHITECTURE.md` Appendix A note hot at Step 9.
- **Orchestrator doc rows to write hot:** the `CompleteTurnRequest.cachedAudioInputTokens` semantic note (cross-doc) + an ARCH-014 realization note (cached audio priced at the cached rate, removed from the full-rate base).

## Things to flag at Step 2.5
1. **Clamp vs. trust the input.** Should the estimator clamp `cachedAudio ≤ audioInput`, or trust the FE to send a valid subset? My default vote: **clamp** (`min(cachedAudio, audioInput)`) — cheap defense-in-depth, makes the BE correct even during the FE-lands-after gap, and a too-large cached value can't drive a negative full-rate base. One-liner, pinned by test 3.
2. **Mini-model framing.** gpt-realtime-mini has no cached rate → cached billed at full. My default vote: **bill cached audio at the full input rate** (so mini's total is unchanged vs a no-cache world) + keep the existing "no cached-input rate configured" assumption string. Rationale: mini genuinely has no cache discount; over-disclosing is honest.
3. **Should text-cached be priced at all?** No — text input is deliberately disclosed-unpriced today (no text rates configured). This slice stays audio-only; do NOT add text pricing. My default vote: **keep text unpriced** (out of scope; separate disclosed simplification).

## Dependencies + sequencing
- **Depends on:** nothing to start (tests provide cached-audio directly). For end-to-end correctness in production, pairs with **FE slice 095** (forwards `cached_tokens_details.audio_tokens`).
- **Blocks:** the G.5 corrected realtime cost number — which also needs a fresh realtime run post-fix (the captured soak persisted only total `cached_tokens`, not the audio breakdown), so a re-measure is required after both 094 + 095 land.

## Estimated commit count
**1.** Focused estimator correction (formula + comment + tests). Not a safety-invariant slice (cost accuracy, not a Key-safety-rule); reviewer fan-out stays disabled per standing directive — an on-demand security-reviewer is unnecessary here (no input-validation / secret / persistence surface touched).

## Lessons-logged candidates anticipated
- **Convention candidate** — "a discounted-subset rate must be REMOVED from the full-rate base, never added on top; a live capture can falsify a `≈0, immaterial` simplification — re-price when the assumption breaks."
- **Architecture-doc note candidate** — ARCH-014: realtime cached audio priced at the cached rate; the field carries `cached_tokens_details.audio_tokens` (a subset of input audio).

## How to invoke
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd realtime_cached_audio_repricing` in the backend (`server/`) implementer session.
3. Step 0 restate → Step 1 file list → Step 2 RED → **Step 2.5 ping the orchestrator** with the test designs + answers to the 3 questions → GREEN after approval.
4. Step 9: surface the cross-doc semantic note (orchestrator writes the doc rows).
