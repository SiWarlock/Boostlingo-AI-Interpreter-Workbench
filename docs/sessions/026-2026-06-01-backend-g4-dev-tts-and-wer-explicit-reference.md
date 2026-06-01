# Session 026 — Backend G.4 support: dev TTS synthesis endpoint + /wer explicit-reference path

- **Date:** 2026-06-01
- **Phase:** G.4 (5-minute synthetic soak-harness) — backend support half
- **Role:** `boostlingo-main-backend-implementer`
- **Predecessor:** [025 — frontend G.4 soak-harness](025-2026-06-01-frontend-g4-soak-harness.md)
- **Successor:** _(TBD — round close-out / next run-surfaced BE need)_

## Why this session existed

G.4's 5-minute synthetic soak-harness is FE-heavy (browser-side synthetic-audio injection, the run, the measurement). Two backend support surfaces were needed and dispatched as standalone slices:

1. **086 — dev TTS synthesis endpoint.** The OpenAI key is server-side only (invariant #1), so the browser harness cannot call OpenAI TTS directly to generate its scripted EN/ES conversation audio. It needs a backend endpoint to synthesize + return audio bytes (which it caches + reuses across both modes). No standalone TTS route existed (TTS was reachable only inside the cascade orchestrator).
2. **090 — `/wer` explicit-reference path.** The soak scores its committed FE script against WER. To use the ONE canonical `WerCalculator` (no FE reimplementation — user Option iii), `POST /api/evaluation/wer` needed to accept a caller-supplied `{reference, hypothesis}` in addition to the existing `phraseId` store-lookup path.

## What was built

### 086 — `feat(server): dev-only TTS synthesis endpoint for the G.4 soak-harness` (`1f18eaa`)

**Files created:**
- `server/AiInterpreter.Api/Dev/DevTtsSynthesisService.cs` — `IDevTtsSynthesisService` + `DevTtsSynthesisService` (collects the `ITtsProvider` stream into one Seq-ordered payload + resolves content-type from `TtsFirstAudio`, else `TtsComplete` for the no-chunk variant), `DevTtsSynthesisOutcome`/`DevTtsSynthesisStatus`, and the `DevTtsRequest` DTO. Stateless (no `SessionStore`/writer dependency); cap pre-check (reuses `OpenAiTtsMapping.CapExceeded`) + provider `TtsFailed` → safe `ProviderError` outcome.
- `server/AiInterpreter.Tests/DevTtsSynthesisServiceTests.cs` — 7 unit tests.
- `server/AiInterpreter.Tests/DevTtsEndpointTests.cs` — 5 WAF wire tests.

**Files modified:**
- `server/AiInterpreter.Api/Program.cs` — Transient DI registration + a **Development-gated** `MapPost("/api/dev/tts")` (thin handler → service → `Results.File` / sanitized `Results.Json`). Route is NEVER mapped in Production (mirrors the Swagger gating idiom).

### 090 — `feat(server): additive /wer explicit-reference path for the G.4 soak` (`6b83ac6`)

**Files modified:**
- `server/AiInterpreter.Api/Evaluation/EvaluationService.cs` — `ComputeWerAsync`: a neither-identifier guard (`Reference is null && IsNullOrEmpty(PhraseId)` → `Invalid`/400, preserves the pre-relaxation no-identifier→400 contract); the explicit-reference branch (`Reference is not null` → reference-wins, reference capped at `MaxHypothesisChars` + empty/whitespace-rejected **before** `_compute`); a `try/catch(ArgumentException)` around `_compute` converting a normalizes-to-empty reference (e.g. punctuation-only `"..."`) → `Invalid`/400 (never a 500 — the security-pass fix). The hypothesis cap (m) + the optional TurnId-attach tail are unchanged.
- `server/AiInterpreter.Api/Evaluation/EvaluationModels.cs` — `WerRequest`: gained trailing-defaulted `string? Reference = null`; `PhraseId` relaxed `[Required] string` → `[MaxLength(256)] string?` (the soak sends no phraseId); doc comment updated.
- `server/AiInterpreter.Tests/EvaluationServiceTests.cs` — +10 service tests (incl. #9 neither-identifier, #10 punctuation-only from the security pass).
- `server/AiInterpreter.Tests/EvaluationEndpointTests.cs` — +2 WAF wire tests.

## Decisions made

- **086 dev-gating → Development-gated minimal-API `MapPost`** (true gate; route absent in Production), not a 404-returning controller. Matches the existing Swagger Development-gating idiom. Response = raw bytes (`Results.File`); ResponseFormat = `wav` override (deterministic lossless decode); Model from options; voice-by-language via empty `Voice` → `ResolveVoice`/`VoiceByLanguage` (§38); placeholder `"soak"`/`"synth"` ids. DI lifetime Transient (avoids a captive typed-HttpClient `ITtsProvider`).
- **086 cap-owns-chokepoint** — the service pre-checks `text.Length > MaxInputChars` before any provider call (the fake doesn't enforce it; mirrors §27), so the rejection is deterministic + identical to the real provider.
- **090 precedence → reference-wins** (trigger `Reference is not null`); reference cap reuses `MaxHypothesisChars` (DP symmetric in n,m); empty/whitespace → boundary `Invalid`/400; optional `Reference` field + in-service branch (one route, §27 chokepoint single).
- **090 ⭐ soak path posts NO TurnId** → no turn-attach, no `IsEvaluation` marking → the soak's real interpretation turns stay IN the per-mode comparison (§29/§39). Pinned by `explicit_reference_no_turn_id_no_attach_no_eval_marking`.
- **090 security fix (in-slice, pre-authorized)** — the security-reviewer flagged a Medium (punctuation-only reference → sanitized 500); folded a `try/catch(ArgumentException)` → 400 + a test into this slice.

## Decisions explicitly NOT made

- Did **not** add a dedicated `MaxReferenceChars` const — reused `MaxHypothesisChars` (the DP is symmetric; soak refs are short).
- Did **not** pre-build a normalize-in-service empty-check (used the calculator's own throw + catch — robust to any Unicode composition).
- The `TtsVoiceUsed`-per-direction observability + `VoiceByLanguage` both-languages config-confirm remain open Carry-forward items (not in scope for these slices).

## TDD compliance

**Clean — both slices test-first.** RED confirmed before GREEN each time: 086 RED = `CS0234`/`CS0246` on the missing `Dev` types; 090 RED = `CS1729` on the missing `WerRequest.Reference` param. No TDD violations. The 090 security-fix test (#10) was added under the security-reviewer's fix-in-slice verdict (orchestrator pre-authorized).

## Reachability

- **086** — reachable from `POST /api/dev/tts` (Development-gated `app.MapPost` in `Program.cs`) → `IDevTtsSynthesisService.SynthesizeAsync` (Transient DI). `endpoint_synthesizes_audio_in_development` exercises the full HTTP→route→service→provider→bytes path; `endpoint_not_mapped_in_production` pins the 404 gate. Production caller: the G.4-FE harness audio-injection slice.
- **090** — reachable from `POST /api/evaluation/wer` → `EvaluationController.ComputeWer` (UNCHANGED — `Invalid→400`/`Computed→200` already mapped) → `EvaluationService.ComputeWerAsync` explicit branch. Two wire tests exercise the full path (200 + 400). Existing phraseId path byte-identical. Production caller: FE soak runner (089b); WER panel = phraseId caller.

No tested-but-unwired gaps.

## Open follow-ups (Step-9 categorized list — orchestrator routes/verifies)

**086:**
- _Architecture doc note / API route row (orch-owned):_ new **dev-only** API route `POST /api/dev/tts` → ARCH-009 §6 + the routing manifest (noted dev-only); no Appendix-A model row (internal types). + an ARCH-020 realization note (the 5-min preflight is now also driven by a synthetic harness).
- _Convention candidate (orch's call):_ dev-only harness/synthesis surface = Development-gated minimal-API unmap + stateless (extends §28 stateless-transcribe + §27 cap-chokepoint). _(Orch declined a standalone lesson — folded into the ARCH route-row rationale.)_

**090:**
- _⚠️ Cross-doc invariant CHANGE (orch-owned, batched for round-close ARCH-doc-sync):_ `WerRequest` gained `Reference? (string?)` + `PhraseId` relaxed to `[MaxLength(256)] string?`. → `server/CLAUDE.md` cross-doc row + `ARCHITECTURE.md` ARCH-009/Appendix-A + the **TS `WerRequest` mirror** note (FE writes in 089b) + the **§27 additive-exception amendment**. Orch confirmed captured.
- _Architecture doc note:_ ARCH-009 `/wer` now accepts `{phraseId | reference}` + hypothesis; the soak uses the no-TurnId explicit path so its measured turns stay in the comparison.

No Carry-forward additions, no deferments from either slice. Security-reviewer ran on 090 (invariants 1–5 PASS; 1 Medium fixed in-slice; no Critical/High).

## How to use what was built

- **086:** `POST /api/dev/tts` with `{ "text": "...", "language": "en"|"es" }` in Development → raw `audio/*` bytes (wav). Browser harness: `res.arrayBuffer()` → `AudioContext.decodeAudioData`. 404 outside Development.
- **090:** `POST /api/evaluation/wer` with `{ sessionId, reference, hypothesis }` (no phraseId, no turnId) → `WerResponse` scored against the supplied reference by the canonical `WerCalculator`; empty/over-cap reference → 400 `evaluation.invalid_phrase`. The existing `{ sessionId, phraseId, hypothesis }` flow is unchanged.
