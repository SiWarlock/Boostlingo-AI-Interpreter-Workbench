# /tdd brief — fe_cached_audio_forwarding

## Feature
Make the realtime usage normalizer forward the actual cached **audio** count — `response.done.usage.input_token_details.cached_tokens_details.audio_tokens` — into `RealtimeUsageTokens.cachedAudioInputTokens`, instead of the total `cached_tokens` (text+audio) it sends today. This gives the backend (paired slice 094) the cached-audio subset it needs to price cached audio at the cached rate. Makes the field's value finally match its name.

## Use case + traceability
- **Task ID:** G5-cost (pre-G.5 cost-accuracy correction; FE half)
- **Architecture sections it implements:** `ARCH-010` (GA event mapping, §7), `ARCH-014` (cost — the forwarded usage feeds the realtime estimate)
- **Related context:** the live soak Finding (orch 2026-06-01) — cached tokens are ~75% of input on mid/late realtime turns, falsifying the "cached≈0, immaterial" assumption baked into the current `extractRealtimeUsage` comment (realtimeEvents.ts:86-88). CF76 live evidence: GA `input_token_details` carries `cached_tokens` (total, text+audio) AND `cached_tokens_details:{text_tokens, audio_tokens}` (the modality breakdown). The cached AUDIO = `cached_tokens_details.audio_tokens` (e.g. total 512 = text 192 + audio 320). web LESSONS §26 (realtime cost extraction — dual-read, real-0≠absent, never synthetic), §23 (honest-omit). **Paired BE slice 094** re-prices using this value; the field's documented semantics change is coordinated across both.

## Current behavior
`extractRealtimeUsage` (realtimeEvents.ts:89) reads `inputDetails?.cached_tokens` (the TOTAL cached, incl. text) into `cachedAudioInputTokens`. The field is named for cached *audio* but carries total cached — a latent mismatch that was immaterial while cached≈0. The live run makes it material.

## Acceptance criteria (what "done" means)
- [ ] `extractRealtimeUsage` reads `input_token_details.cached_tokens_details.audio_tokens` into `cachedAudioInputTokens` (the cached input-AUDIO subset), NOT `input_token_details.cached_tokens`.
- [ ] A real `cached_tokens_details.audio_tokens: 0` (present) is kept as `0` (real-0 ≠ absent — the existing discipline, web §26); a fully-absent `cached_tokens_details` object → `cachedAudioInputTokens` **omitted** (never fabricate 0).
- [ ] Each path stays independently guarded + never throws (lesson §9); `inputAudioTokens` (`input_token_details.audio_tokens`) + `outputAudioTokens` (`output_token_details.audio_tokens`) reads are UNCHANGED.
- [ ] The doc comments (realtimeEvents.ts:10-11 + the Q5 comment at 86-88) updated: `cachedAudioInputTokens` now carries `cached_tokens_details.audio_tokens`; the "immaterial while cached=0 / BE-confirmed path is cached_tokens" note is corrected (the live run made it material; FE+BE agree on the audio-subset path).
- [ ] All unit tests in `web/src/realtime/realtimeEvents.test.ts` pass (updated) + the forwarding tests in `realtimeTurnController.test.ts` pass (fixture updates — see Step 2.5 Q2).
- [ ] `/preflight` clean (`npm run format:check && lint && typecheck && test`).

## Wiring / entry point (Step 7.5)
Already wired — `realtimeEvents.ts` `normalizeRealtimeEvent` (`response.done` case) → `extractRealtimeUsage` → `NormalizedRealtimeEvent.responseDone.usage` → `realtimeTurnController.ts:166-167` builds `body.cachedAudioInputTokens` → `POST …/turns/{turnId}/complete`. No new caller; this slice changes which GA field the existing reachable extractor reads. Confirm the controller still forwards `usage.cachedAudioInputTokens` unchanged.

## Files expected to touch
**Modified:**
- `web/src/realtime/realtimeEvents.ts` — `extractRealtimeUsage` cached read + doc comments.
- `web/src/realtime/realtimeEvents.test.ts` — update cached assertions; add a non-zero `cached_tokens_details.audio_tokens` fixture + an absent-details omit case.
- `web/src/realtime/realtimeTurnController.test.ts` — fixture updates so cached-0 cases include `cached_tokens_details:{audio_tokens:0}` (preserve the "real 0 forwarded" assertions) per Step-2.5 Q2.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2) — `realtimeEvents.test.ts`
1. **`extractRealtimeUsage reads cached audio from cached_tokens_details`** — GA frame `input_token_details:{audio_tokens:498, cached_tokens:512, cached_tokens_details:{text_tokens:192, audio_tokens:320}}` → asserts `cachedAudioInputTokens === 320` (NOT 512). Why: the corrected source (CF76 live shape).
2. **`cached_tokens_details audio 0 present → kept as 0`** — `cached_tokens_details:{audio_tokens:0}` → asserts `cachedAudioInputTokens === 0` (real 0, present in the object). Why: real-0 ≠ absent (web §26).
3. **`cached_tokens_details absent → cachedAudioInputTokens omitted`** — `input_token_details:{audio_tokens:31}` (no `cached_tokens_details`) → asserts the key is absent from the result. Why: honest-omit, never fabricate 0 (§23/§26).
4. (UPDATE existing) the A1 dual-read / nesting test + the comment at line 70 → cached now from `cached_tokens_details.audio_tokens`.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none structural — `RealtimeUsageTokens` is frontend-internal (not a wire mirror). The wire field `CompleteTurnRequest.cachedAudioInputTokens` keeps its `number | null` shape; its **documented semantics** change (total cached → cached audio), coordinated with BE slice 094. The implementer updates the FE code comments; the **orchestrator** writes the `web/CLAUDE.md` `CompleteTurnRequest` cross-doc row note hot at Step 9 (paired with 094's).
- **Orchestrator doc rows to write hot:** the `cachedAudioInputTokens` semantic note (shared with 094).

## Things to flag at Step 2.5
1. **Source path.** Read `cached_tokens_details.audio_tokens` (cached AUDIO only) — confirmed correct vs. the BE pairing. My default vote: **`cached_tokens_details.audio_tokens`** — the BE (094) subtracts this from the audio base; sending total `cached_tokens` (incl. text) would over-subtract. Pinned by tests 1-3.
2. **Absent `cached_tokens_details` → omit vs 0.** When the object is entirely absent, omit `cachedAudioInputTokens` (don't fabricate 0). My default vote: **omit** (absent ≠ 0; web §23/§26). Consequence: the `realtimeTurnController.test.ts` cached-0 fixtures (which today carry only `cached_tokens:0`, no details) must add `cached_tokens_details:{audio_tokens:0}` to keep asserting a forwarded real 0 — update those fixtures.
3. **Keep the `cached_tokens` read at all?** No other consumer needs total cached today. My default vote: **drop the `cached_tokens` read** entirely (replace, not add a second field) — the field is `cachedAudioInputTokens` and should carry cached audio; total cached has no consumer.

## Dependencies + sequencing
- **Depends on:** nothing to start. Pairs with **BE slice 094** for end-to-end correctness; can land in parallel (different area). The intermediate state (one slice landed, not the other) is bounded + harmless — no live realtime traffic between commits.
- **Blocks:** the G.5 corrected realtime cost re-measure (needs both 094 + 095 landed + a fresh realtime run — the captured soak persisted only total `cached_tokens`).

## Estimated commit count
**1.** Focused normalizer-source change + tests. Not a safety-invariant slice; reviewer fan-out stays disabled per standing directive.

## Lessons-logged candidates anticipated
- **Convention candidate** — "a live capture can falsify a `cached≈0, immaterial` simplification; read the modality-specific cached breakdown (`cached_tokens_details.audio_tokens`), not the aggregate `cached_tokens`, so the field's value matches its name + the BE's discount basis."

## How to invoke
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd fe_cached_audio_forwarding` in the frontend (`web/`) implementer session.
3. Step 0 restate → Step 1 file list → Step 2 RED → **Step 2.5 ping the orchestrator** with the test designs + answers to the 3 questions → GREEN after approval.
4. Step 9: surface the cross-doc semantic note (orchestrator writes the doc row, shared with 094).
