# /tdd brief — realtime_output_cost_token_report

> **⚠️ RESCOPED 2026-06-01 (premise correction).** The original brief (below) assumed the FE didn't report `response.done.usage` audio-tokens — **WRONG.** The FE verified (file:line + green tests) that `finalizeTurn` ALREADY parses + reports `{input,output,cached}AudioTokens` to `/complete` (053-C2b / web §26); the BE's exact-token path already prices realtime output. ACs 1–3 are DONE (the orchestrator conflated the stale carry-forward-74 *duration*-undercount with the *token* path). **The actual 092 = AC #4 ONLY:** surface the per-turn output-audio-token count onto the store `TurnViewModel` (+ a store action + the controller wiring on `responseDone`) so **093** derives the realtime overlap-duration (÷ tokens-per-second). Cross-doc: the `TurnViewModel` field (ARCH-007 §4 / Appendix-A). The live GA-wire-shape verification is carry-forward-76 (a manual-run check, not this slice). The original ACs/tests below are SUPERSEDED — kept for the audit trail.

## Feature (RESCOPED → AC #4 only)
Surface the per-turn realtime **output-audio-token count** onto the store `TurnViewModel` so the soak (093) can derive the realtime overlap-duration. The parse + `/complete` reporting + BE pricing already exist (053-C2b); this slice only adds the store-side per-turn record. FE-only.

## Feature (ORIGINAL — SUPERSEDED, premise was wrong)
Fix the realtime **output-cost undercount**: parse `response.done.usage` audio-token counts in the realtime path and populate `CompleteTurnRequest.{inputAudioTokens, outputAudioTokens, cachedAudioInputTokens}` at `POST …/complete`. The backend then prices output correctly via its **existing** exact-token path. FE-only — no backend change.

## Use case + traceability
- **Task ID:** G.4-FE-cost (Consumer B of the output-audio-signal work).
- **Architecture sections:** `ARCHITECTURE.md §11 / ARCH-014` (cost), `§7 / ARCH-010` (realtime DC events). Lessons §25/§31 (realtime cost at `/complete`; reported-value, never synthetic).
- **Related context:** `docs/g4-harness-design.md` → "Output-audio-duration work plan." **⭐ Orchestrator-verified the BE is already wired:** `EstimateRealtimeFromTokens` (053-C2a) prices `inputTokens×inputRate + outputTokens×outputRate`; `SessionService.EstimateRealtimeCost` maps `CompleteTurnRequest.{Input,Output,Cached}AudioTokens → CostUsage` (the `hasTokens` branch); `CompleteTurnRequest` (TS + backend DTO) already carries the token fields. **The ONLY gap is the FE not populating them from `response.done.usage`.** Today realtime prices input-only (output ≈ 0) → it reads cheaper than reality on the G.5 cost axis.

## Acceptance criteria
- [ ] The realtime path parses `response.done.usage` → the **audio-token** counts: output-audio tokens, input-audio tokens, cached-input tokens (the GA wire shape — the BE comment notes `InputAudioTokens = input_token_details.audio_tokens`; verify the exact GA field path at Step-2.5 / the live run — carry-forward 76).
- [ ] `finalizeTurn` / the realtime `/complete` call populates `CompleteTurnRequest.{inputAudioTokens, outputAudioTokens, cachedAudioInputTokens}` from the parsed usage. (It already sends `audioDurationMs` per 076 — additive.)
- [ ] **Honest-degrade:** when `response.done.usage` is absent/partial, OMIT the token fields (don't send 0) → the BE falls back to its legacy seconds path / null, never a synthetic $0 (§25/§9/§12).
- [ ] **Surface the per-turn output-audio-token count onto the store turn** so **093** can derive the realtime overlap-duration from it (÷ tokens-per-second). (Shape TBD at Step-2.5 — minimal.)
- [ ] Deterministic parsing/population unit-TDD'd against a `response.done.usage` fixture; the live DC delivery is smoke (the manual run confirms the GA shape). `tsc`/lint/format/`/preflight` clean.

## Wiring / entry point (Step 7.5)
The realtime turn controller's `response.done` handler → the usage parse → the existing `POST …/turns/{turnId}/complete` (`CompleteTurnRequest`). Confirm the BE's `EstimateRealtimeCost` `hasTokens` branch now fires (it's pinned BE-side already; this slice supplies the input). No new route.

## Files expected to touch
**Modified:** the realtime sink/turn controller (parse `response.done.usage`); the realtime `/complete` body construction (`finalizeTurn` or equivalent). A pure usage-parse helper (NEW small file) is encouraged for TDD. Tests alongside.

If a backend change turns out to be needed (it shouldn't), **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`parses_response_done_usage_audio_tokens`** — a GA `response.done.usage` fixture (e.g. output audio 73, input audio 85, cached 0) → the parser returns `{outputAudioTokens:73, inputAudioTokens:85, cachedAudioInputTokens:0}`. Why: the reported-token basis.
2. **`complete_request_populates_token_fields`** — given parsed usage → `CompleteTurnRequest.{inputAudioTokens, outputAudioTokens, cachedAudioInputTokens}` set; `audioDurationMs` still sent. Why: the BE exact-token path needs them.
3. **`absent_usage_omits_tokens`** — no/partial `response.done.usage` → token fields omitted (not 0) → BE legacy/null path. Why: honest-degrade (§25).
4. **`output_token_count_surfaced_for_093`** — the parsed output-audio-token count reaches the store turn (the seam 093 reads). Why: the realtime overlap-duration derivation input.

## Cross-doc invariant impact
- **Model field changes:** none new — `CompleteTurnRequest` already has the token fields (TS + backend); this slice just POPULATES them. No cross-doc row. (Flag at Step 9 only if Step-2.5 surfaces a new field.)

## Things to flag at Step 2.5
1. **Exact GA `response.done.usage` field path.** Default: `output_token_details.audio_tokens` / `input_token_details.audio_tokens` / `input_token_details.cached_tokens` (the BE's documented mapping). Vote: **use that shape, verify against the lead's live capture / the manual run** (carry-forward 76 — the realtime GA wire-shape smoke). Pin the parser to the documented shape; the run confirms.
2. **Where the realtime usage is already parsed (if anywhere).** Confirm what the realtime path currently extracts from `response.done` (it may already read `usage` for something). Reuse, don't duplicate.
3. **The store seam for the output-token count (for 093).** Default: a minimal per-turn field/marker. Vote: smallest thing 093 can read — confirm the shape so 093's resolver is clean.
4. **Cached-input tokens.** Default: report them too (the BE prices cached at the cached rate, else full). Vote: include cached.

## Dependencies + sequencing
- **Depends on:** the BE exact-token path (present) + `CompleteTurnRequest` token fields (present). Nothing new.
- **Blocks:** **093** (reads the surfaced output-token count to derive the realtime overlap-duration). Feeds **G.5** (correct realtime cost on the comparison axis — accuracy-critical, the user has emphasized this).

## Estimated commit count
**1.** Focused parse-and-report; no safety invariant; FE-only.

## Lessons-logged candidates anticipated
- **Architecture-doc note** — realtime cost now prices the REPORTED `response.done.usage` audio-tokens (the BE's exact-token path was always ready; the FE wasn't reporting them); the legacy seconds estimate is the fallback. Closes the carry-forward realtime-output-undercount (item 74).

## How to invoke
1. Read this brief + the `docs/g4-harness-design.md` plan; skim §25.
2. `/tdd realtime_output_cost_token_report` → Step 0 restate → Step 2.5 (the 4 Qs, esp. Q1 the GA field path) → Step 9.
