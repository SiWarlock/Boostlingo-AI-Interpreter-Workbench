# /tdd brief — realtime_cost_backend (053-C2a; backend half of the realtime-cost fix)

> **Backend slice (server/).** The backend half of 053-C2 (realtime cost `n/a`). Makes the realtime `/complete` path price from the **exact** token counts the data channel now gives us (`response.done.usage`) instead of the `audio-seconds × 50 tokens/sec` ESTIMATE — which never even ran, because the frontend never called `/complete` for realtime turns. This slice lands the CONTRACT + pricing path; the **frontend half (053-C2b)** — extract `response.done.usage` + call `/complete` — is a separate sibling that lands AFTER this contract settles. No safety invariant.

## Feature
Extend `CompleteTurnRequest` to carry the realtime turn's actual audio-token usage, and make `EstimateRealtime` price from those exact counts when present (falling back to the existing audio-seconds estimate when absent). Realtime cost stops being `n/a`.

## The fixture (the exact live usage to derive against)
`docs/runbooks/053c-realtime-dc-capture.md` (lead-persisted). `response.done.usage`:
```
total 139 · input 68 [text 37 / audio 31 / cached 0] · output 71 [text 17 / audio 54]
```
The source-transcription `…completed` event carries its own usage (`total 40, input 31 [audio 31], output 9`) — **out of scope here** (transcription cost is a separate disclosed line if ever added; do NOT price it in this slice).

## Use case + traceability
- **Task ID:** 053-C2a (realtime cost — backend). **Architecture:** `ARCH-014` (cost estimation, realtime), `ARCH-009`/Appendix A (the `CompleteTurnRequest` DTO contract). **Cross-doc:** YES — `CompleteTurnRequest` gains fields → Appendix A row + an ARCH-014 realization note (orchestrator writes at round-seal; flag categorized at Step 9).
- **Related:** the cascade-cost slice (057, in flight) is the cascade sibling; this is the realtime sibling. Carry-forward "Frontend reports `outputAudioDurationMs` at `/complete`" (origin E.2b) is **superseded** by this approach — exact DC token counts replace the never-implemented played-duration measurement.

## Root cause (grounded; confirm exact lines at Step 1)
- `Cost/CostEstimator.cs` → `EstimateRealtime(model, CostUsage usage, long audioDurationMs)` (~lines 151–202) prices `usd = inputSeconds × 50 ÷ 1M × inputRate + outputSeconds × 50 ÷ 1M × outputRate` — the `RealtimeTokensPerAudioSecond = 50` factor (~line 26) is an explicit ESTIMATE; rates are `RealtimeModelRates.{AudioInput,CachedAudioInput,AudioOutput}UsdPerMillionTokens` (`Cost/PricingOptions.cs` ~lines 52–68).
- `Sessions/SessionService.cs` → `CompleteTurnAsync` (~lines 224–269) already calls `EstimateRealtimeCost(realtimeModel, finalized, request)` (~line 249) and converts duration→seconds (~lines 283–288). **It works — but the frontend realtime path never POSTs `/complete`** (it only `appendTurnEvents`), so it never runs for realtime turns. (053-C2b wires the frontend call.)
- `Sessions/SessionDtos.cs` → `CompleteTurnRequest` (~lines 68–72): `AudioDurationMs?`, `Transcripts?`, `Status?`, `OutputAudioDurationMs?` — **no token-usage fields.**

## Design — exact-count, same basis (the cheap, honest path)
Pricing **basis is unchanged**: realtime is priced by **audio tokens at the audio-token rates** (`PricingBasis="tokens"`). The only change is *counting*: use the DC's exact audio-token counts instead of the `seconds × 50` estimate. **Text tokens stay unpriced** (as today) with an explicit disclosed assumption — consistent with the project's honest-disclosure posture (the blob-STT-cost + cached-input disclosures are the precedent); pricing text tokens would need new config rates and is a separate enhancement, NOT this slice. This keeps the change below any escalation bar (no basis fork, no pricing-config expansion).

## Acceptance (RED-first; deterministic)
- [ ] **`CompleteTurnRequest` gains optional realtime audio-token fields** — `InputAudioTokens?`, `OutputAudioTokens?`, `CachedAudioInputTokens?` (`int?`, `[Range(0, int.MaxValue)]`; all optional → cascade/older callers unaffected). Confirm naming at Step-2.5 (Q1 — flat fields vs a nested `usage` sub-record).
- [ ] **`EstimateRealtime` prices from exact audio-token counts when supplied** — a new path/overload: when token counts are present, `usd = inputAudioTokens ÷ 1M × audioInputRate + cachedAudioInputTokens ÷ 1M × cachedRate + outputAudioTokens ÷ 1M × audioOutputRate` (no `× 50` factor). When absent → the existing seconds×50 estimate (back-compat). RED: feed the fixture counts (in 31 audio / out 54 audio / cached 0) → assert the exact-rate `EstimatedUsd` (compute the expected value from the pricing fixture, not the 50/sec path).
- [ ] **The estimate carries the basis honestly** — `Units` reflects the actual token counts used (not synthesized seconds); `Assumptions` includes a line that **text tokens are not priced** (input text 37 / output text 17 excluded) + which counting path was used (exact-DC vs estimate). Pin the assumption string presence.
- [ ] **`CompleteTurnAsync` threads the new request fields into `EstimateRealtime`** — build the `CostUsage` (or token args) from the request's token fields when present. RED via `SessionsControllerTests` / the service test: a realtime `/complete` carrying the fixture usage → the finalized turn's `CostEstimate` is non-null with the exact-count value.
- [ ] **Degrade honesty preserved** — missing rates/config still → `Unavailable`→null (never a fabricated cost); absent token fields → the estimate path (disclosed), never a silent 0.
- [ ] `/preflight` (backend) clean; full `dotnet test` green; build 0W/0E.

## Files (backend; confirm lines at Step 1)
**Modified:**
- `Sessions/SessionDtos.cs` — `CompleteTurnRequest` + the 3 optional token fields.
- `Cost/CostEstimator.cs` — `EstimateRealtime` exact-count path.
- `Sessions/SessionService.cs` — `CompleteTurnAsync`/`EstimateRealtimeCost` thread the token fields.
- Tests: `CostEstimatorTests.cs`, `SessionsControllerTests.cs` (or the session-service test), + `pricing` test fixture if the realtime rates aren't already populated for a deterministic expected value.

If a `pricing.json` realtime-rates gap blocks a deterministic expected value, **flag at Step 2.5** (we may need to add realtime rates to the test pricing fixture — config, not code).

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes)
- **Model field change:** `CompleteTurnRequest` +3 optional fields → **Appendix A `CompleteTurnRequest` row** + an **ARCH-014 realization note** (realtime priced from exact DC audio-token counts; text tokens disclosed-unpriced; supersedes the `outputAudioDurationMs` played-duration approach). Orchestrator writes both hot at round-seal. The **TS mirror** (`CompleteTurnRequest` in `web/domain.ts`) is added by **053-C2b** (frontend), not here.

## Things to flag at Step 2.5
1. **Flat token fields vs a nested `usage` record on `CompleteTurnRequest`.** My vote: **3 flat optional `int?` fields** (`InputAudioTokens`/`OutputAudioTokens`/`CachedAudioInputTokens`) — minimal, matches the existing flat DTO style, no new nested type to mirror. Confirm vs a `RealtimeUsage` sub-record (cleaner if we later add text-token fields).
2. **Text tokens: disclose-unpriced (this slice) vs price-them (needs config).** My vote: **disclose-unpriced now** (honest assumption line; no pricing-config change; the audio tokens are the dominant realtime cost). Pricing text tokens = a separate enhancement if the write-up needs it. Confirm. **If you find the pricing config ALREADY has text-token rates** (verify in `PricingOptions`/`pricing.json`), flag it — pricing them becomes cheap and we should.
3. **Exact-count overload vs replace the estimate.** My vote: **add the exact-count path, KEEP the seconds×50 estimate as the absent-tokens fallback** (back-compat; some callers may not supply tokens). Confirm vs hard-replacing (simpler but drops the fallback).
4. **Verify the live realtime `usage` field names via Context7** (the Realtime API, NOT Responses) before pinning the frontend contract in 053-C2b — confirm `input_token_details.audio_tokens` / `output_token_details.audio_tokens` / `cached_tokens` are the exact paths (the fixture shows them, but confirm the GA names). Report at Step 9 so I cite them in the 053-C2b brief.

## Dependencies + sequencing
- **Depends on:** nothing blocking (the DC fixture + 057's cost-surface familiarity help). Can run after 057 (same BE impl, warm `CostEstimator`/cost-code context) or standalone.
- **Blocks:** **053-C2b (frontend)** — the frontend extract-usage + `/complete` call needs THIS contract settled first (the exact DTO field names from Q1). I author 053-C2b once this lands.
- Independent of 058 (realtime first-audio, FE).

## Estimated commit count
**1** (possibly 2 if the pricing-fixture addition is separable) — the DTO + estimator path + service threading are one logical unit (realtime exact-count pricing). Cross-doc change present → the doc rows ride my round-seal (not the impl's commit). No safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — "price realtime from the DC's exact audio-token counts, not an audio-seconds × constant estimate; keep the estimate as the absent-tokens fallback; disclose text-tokens-unpriced honestly rather than fabricating or silently dropping." 
- **Architecture-doc note** — ARCH-014 realtime exact-count realization (supersedes the E.2b played-duration approach).

## How to invoke
1. Read this brief + `docs/runbooks/053c-realtime-dc-capture.md` (the usage fixture) + the ARCH-014 realtime cost section.
2. Confirm the file:line refs at Step 1.
3. `/tdd realtime_cost_backend` — RED the `EstimateRealtime` exact-count path first (against the fixture counts), then the DTO + service threading.
4. Step 2.5: answer Q1–Q4 (esp. the text-token disclosure + the Context7 field-name confirm for the C2b contract). Step 9: categorized summary + the cross-doc `CompleteTurnRequest` flag + ship/no-ship + draft commit message.
