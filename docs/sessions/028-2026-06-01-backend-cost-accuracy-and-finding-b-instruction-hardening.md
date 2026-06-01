# Session 028 — Backend: realtime cost-accuracy fixes + Finding-B instruction hardening

- **Date:** 2026-06-01
- **Phase:** G (pre-G.5 cost-accuracy corrections) + live-test Finding fixes
- **Area:** `server/` (backend implementer)
- **Predecessor:** [027 — orch handoff: wholesale close-out, run pending](027-2026-06-01-orch-handoff-wholesale-closeout-run-pending.md)
- **Successor:** _(next session — TBD)_
- **Round commits (this session, BE):** `2ac8adb` (094), `8b2e555` (097), `8d6b25e` (098). Final backend suite **494 green**.

## Why this session existed

The 2026-06-01 live soak + live realtime re-tests surfaced three Findings the user approved fixing before the G.5 write-up. The backend owned three of them this round:

- **094 / G5-cost** — realtime cost over-count (~1.5× on cached-heavy turns): cached audio was priced at the full rate AND added again at the cached rate.
- **097 / Finding A** — the realtime per-mode Cost/min blanked in the live UI: the §39 `IsEmptySilence` rule excluded every realtime turn (0 transcripts but real cost).
- **098 / Finding B** — the realtime interpreter ANSWERED a spoken question instead of translating it (live repro: "What is your name?" answered in English, with meta-commentary).

## What was built

### 094 — realtime cached-audio repricing (`2ac8adb`)
**Files modified:**
- `AiInterpreter.Api/Cost/CostEstimator.cs` — `EstimateRealtimeFromTokens`: clamp `cachedAudio = min(cached, audioInput)`, REMOVE it from the full-rate base, price it at the cached rate (gpt-realtime $0.40/M; gpt-realtime-mini has no cached rate → cached billed at full input rate, net unchanged). Method comment updated.
- `AiInterpreter.Api/Cost/CostModels.cs` — `CostUsage.CachedAudioInputTokens` doc comment → "cached input-AUDIO (`cached_tokens_details.audio_tokens`), a SUBSET of `AudioInputTokens`".
- `AiInterpreter.Api/Sessions/SessionDtos.cs` — `CompleteTurnRequest` summary updated: the cached field carries `cached_tokens_details.audio_tokens` (audio subset), NOT total `cached_tokens` (which would include text).
- `AiInterpreter.Tests/CostEstimatorTests.cs` — updated `estimate_realtime_exact_tokens_honors_cached_rate` in place (§39 re-assert); added `estimate_realtime_cached_audio_clamped_to_input` + `estimate_realtime_mini_cached_audio_full_rate`; annotated the existing cached=0 test as the no-regression guard.

### 097 — ModeSummary IsEmptySilence cost-bearing refinement (`8b2e555`)
**Files modified:**
- `AiInterpreter.Api/Sessions/SessionSummaryService.cs` — `IsEmptySilence` += `&& t.CostEstimate is null`: a cost-bearing turn is real evidence, not phantom silence. Comment expanded (every realtime turn = 0 persisted transcripts + real cost). Genuine cascade silence (cost-null) still excluded; failed-empty (Failed status) still counted.
- `AiInterpreter.Tests/SessionSummaryServiceTests.cs` — added `summarize_includes_completed_realtime_turn_with_cost_and_no_transcripts` (Finding-A driver) + `summarize_includes_cost_bearing_zero_transcript_turn_regardless_of_mode` (discriminator pin: cost-based, not mode-based); annotated the existing §39 test (comment-only, cost-null note).

### 098 — realtime interpreter instruction hardening, Finding B (`8d6b25e`)
**Files modified:**
- `AiInterpreter.Api/Realtime/RealtimeClientSecretMapping.cs` — both `DefaultInstructionsTemplate` (one-direction) + `DefaultBidirectionalInstructionsTemplate` RESTRUCTURED with four elements: (1) emphatic conduit framing ("ONLY a translation conduit … NEVER a conversational party"), (2) target-language lock ("ALWAYS output ONLY the {target} translation" / "…the translation in the OTHER language" — never any other language, never your own words), (3) explicit question-handling ("translate the QUESTION itself … NEVER answer, respond to, explain, or add anything"), (4) an example (one-direction: a direction-safe principle; bidirectional: the literal `"What is your name?" → "¿Cómo te llamas?"`). One-direction keeps "from {source} to {target}" so the direction still fills.
- `AiInterpreter.Tests/RealtimeClientSecretMappingTests.cs` — added `bidirectional_instructions_forbid_answering` + `one_direction_instructions_forbid_answering`; renamed `renders_one_direction_byte_identical_regression` → `renders_one_direction_exact_render_and_overload_default` with the `OneDirectionEnToEs` constant updated to the restructured render (§39 re-assert-in-place); kept the overload-default equivalence assertion.

## Decisions made

- **094 — base-exclusion, not added-on-top.** Cached audio is a subset of total input audio; price `(input − cached)` at full + `cached` at cached rate. Defensive clamp `min(cached, input)` guards the FE-lands-after gap (a too-large cached value can't drive a negative base). Mini (no cached rate) bills cached at full → net unchanged from a no-cache world. (Step-2.5 Q1/Q2/Q3 all endorsed.)
- **097 — cost-based discriminator, not mode-based.** `&& CostEstimate == null` is semantically precise and mode-agnostic; validated across 100% of the persisted corpus (cascade silence = costNULL; realtime = hasCost). A discriminator-pin test (cascade 0-transcript + cost → INCLUDED) locks this against the `Mode != Realtime` alternative. A $0-cost realtime turn (present CostEstimate) is INCLUDED (a `response.done` fired → real turn).
- **098 — RESTRUCTURE, not gentle append.** A new live repro showed the prior "speak only the translation, no commentary" was violated outright (answered, in the wrong language, with meta-commentary), so a one-line "never answer" append wouldn't hold. Restructured both templates with the four elements; kept "from {source} to {target}" so the direction still fills. (Orch TWEAK after a fresh live repro; re-approved.)

## Decisions explicitly NOT made (deferred)

- **098 — cascade translation-prompt hardening** (`OpenAiTranslationMapping.cs:174`): same theoretical assistant-vs-interpreter risk, but Finding B was user-scoped to REALTIME (no cascade repro). Flagged as an optional parallel Carry-forward follow-up, NOT bundled (separate prompt).
- **097 — realtime transcript persistence**: whether realtime turns SHOULD persist their transcripts (so the history drill-in shows them) is a separate, larger question (a new `/complete` wire path). This slice is summary-inclusion only (Q3). Flagged to the lead as a separate follow-up.
- **094 — text-token pricing**: realtime text input stays disclosed-unpriced (no text rates configured); out of scope.

## TDD compliance

**Clean — all three slices test-first.** Each: RED confirmed before GREEN (094: 3 tests failed with expected/actual cost mismatch; 097: 2 tests failed `Assert.NotNull` on the excluded mode summary; 098: 3 tests failed against the un-hardened templates), then minimal GREEN. The instruction string (098) is a deterministic, test-pinned deliverable for PRESENCE/WIRING; the actual model behavior (does it stop answering) is **eval-observed**, not unit-asserted (per root TDD posture / ARCH-020).

## Reachability (Step 7.5 — all wired, no gaps)

- **094** — `POST …/turns/{turnId}/complete` → `SessionService.FinalizeTurn` (296) → `EstimateRealtimeCost` (320) → maps `CachedAudioInputTokens`→`CostUsage` (343) → `EstimateRealtime` (356) → `EstimateRealtimeFromTokens`.
- **097** — `GET /api/sessions/{id}/summary` (`SessionsController.cs:88`) → `SessionService.Summary` (185) → `_summaryService.Compute` (191) → `SummarizeMode` → `IsEmptySilence`.
- **098** — `POST /api/realtime/client-secret` (`RealtimeController.cs:30`) → `RealtimeClientSecretService` (`.cs:147` `BuildRequestBody`) → `RenderInstructions` → both restructured templates → minted `session.instructions`.

All three are predicate/constant/math changes inside existing reachable paths; no new callers.

## Open follow-ups

### Step-9 categorized items (orchestrator routes hot; verify at `/orchestrate-end`)
- **094 — Cross-doc invariant change (documented SEMANTICS, no structural field change):** `CompleteTurnRequest.cachedAudioInputTokens` + `CostUsage.CachedAudioInputTokens` semantics "total cached_tokens" → "cached input-AUDIO (`cached_tokens_details.audio_tokens`), subset of input audio". Shape unchanged (`int?`). → orch writes the `web/CLAUDE.md` `CompleteTurnRequest` row note + ARCH Appendix A note + an ARCH-014 realization note.
- **094 — Convention candidate** (server LESSONS): a discounted-subset rate must be REMOVED from the full-rate base, never added on top; a live capture can falsify a `≈0, immaterial` simplification — re-price when the assumption breaks.
- **097 — Convention candidate / §39 refinement** (server LESSONS): `IsEmptySilence` must ALSO require `CostEstimate==null` — a cost-bearing turn (every realtime turn) is real evidence, not phantom silence; the filter that drops cascade auto-VAD silence must not drop realtime. Discriminator validated cost-based across 100% of the persisted corpus.
- **097 — Architecture-doc note** (ARCH-009/013): realtime turns persist with 0 transcripts (FE-store-side) but contribute cost to `ModeSummary`.
- **098 — Convention candidate** (server LESSONS): an interpreter/translation instruction must be an emphatic conduit framing + target-language lock + explicit translate-the-question-never-answer (with an example) — "speak only the translation / no commentary" alone gets violated outright. String is deterministic + test-pinned; effectiveness is eval-observed.
- **098 — Architecture-doc note** (ARCH-010): the minted realtime `instructions` (both templates) explicitly forbid answering + lock the output language.
- **098 — Carry-forward follow-up (Q4):** cascade translation-prompt hardening (`OpenAiTranslationMapping.cs:174`) — optional parallel follow-up, realtime-only this round.

### Eval-observed (NOT a unit test)
- **098 — Finding B live confirm:** ✅ **USER-LIVE-CONFIRMED** per the orchestrator's close-out — source "What is your name?" → output "¿Cómo te llamas?" (translated question, in Spanish — not answered, not English, no meta-commentary).

### Cross-doc audit
- **No structural cross-doc drift this session.** No model in the `server/CLAUDE.md` cross-doc table had a field add/remove/rename. The 094 documented-semantics note (above) is orch-owned and flagged; no ARCHITECTURE.md structural change owed.

## How to use what was built

Realtime cost now prices cached audio at the cached rate (no over-count); the realtime per-mode Cost/min populates in `GET /summary` for completed realtime turns; the minted realtime interpreter instruction translates questions instead of answering them. The corrected G.5 realtime cost number needs a fresh post-094+095 realtime run (the captured soak persisted only total `cached_tokens`).
