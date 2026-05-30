# Session 010 — Phase F: F.1 evaluation endpoints (F.1a WER + F.1b transcribe)

- **Date:** 2026-05-30
- **Phase:** F (Evaluation, Summary & Comparison) — the backend opening slice
- **Predecessor:** [009-2026-05-30-phase-e-frontend.md](009-2026-05-30-phase-e-frontend.md)
- **Successor:** _(next session)_
- **Area:** `server/` (backend implementer)
- **Slice commits:** F.1a `7e29f35` · F.1b `f9cf6a9`
- **Backend suite:** 316 → **339 green** (+23: F.1a +16, F.1b +7); build 0W/0E + `dotnet format --verify-no-changes` clean at close.
- **Preflight at close:** GREEN — `dotnet format --verify-no-changes` exit 0 · `dotnet build` 0W/0E · `dotnet test` 339/339.

## Why this session existed

Phases A–E shipped both interpretation modes. Phase F opens with the committed WER **Evaluation panel**'s backend (ARCH-015) — the three evaluation HTTP endpoints the F.2 panel + F.3 comparison summary consume. F.1 was split into two security-isolated commits (one safety pin each, per the "every safety-critical slice gets its own commit" rule):
- **F.1a** — `GET /api/evaluation/phrases` + `POST /api/evaluation/wer`; safety pin = the ARCH-019 hypothesis-length DoS cap before `WerCalculator.Compute`'s n×m DP matrix.
- **F.1b** — `POST /api/evaluation/transcribe` (STT-only); safety pin = the invariant-#5 audio-upload validation before any provider call.

Both reuse landed seams: B.6 `WerCalculator` + `EvaluationPhraseStore`, C.1 `ISttProvider` REST pre-recorded path, C.5 `CascadeUploadValidation`, B.8 `ErrorSanitizer`.

## What was built

### Files created (F.1a)
- `server/AiInterpreter.Api/Evaluation/EvaluationService.cs` — evaluation orchestration (ARCH-008 thin-controller collaborator). F.1a: `GetPhrases()` (degrades to empty list, never surfaces `LoadError`) + `ComputeWerAsync` (hypothesis cap as the FIRST action → phrase lookup → compute → optional turn-attach+persist). The WER compute routes through an injected `Func<…,WerResult>` delegate so the cap-before-compute property is unit-pinnable. F.1b extended it with `TranscribeAsync` (see below).
- `server/AiInterpreter.Api/Evaluation/EvaluationModels.cs` — wire DTOs + area-local outcomes. F.1a: `WerRequest`, `WerResponse`, `EvaluationWerStatus`, `EvaluationWerOutcome`. F.1b added the transcribe shapes.
- `server/AiInterpreter.Api/Controllers/EvaluationController.cs` — thin HTTP boundary; outcome→status mapping (200/400/404) + sanitized `persistenceWarning`; exhaustive switch (throws on an unhandled future status). F.1b added the `Transcribe` action.
- `server/AiInterpreter.Tests/EvaluationServiceTests.cs` — service-level unit tests (F.1a: 10; F.1b: +4 = 14).
- `server/AiInterpreter.Tests/EvaluationEndpointTests.cs` — `WebApplicationFactory` wire tests (F.1a: 6; F.1b: +3 = 9).

### Files modified
- `server/AiInterpreter.Api/Evaluation/EvaluationService.cs` (F.1b) — `+ TranscribeAsync` (wrap the blob as a 1-frame `AudioFrame` through the SAME `ISttProvider` REST path; collect `SttFinal` text JOINED across multiple finals with a single space; stamp `stt.first_partial`/`stt.final` on real arrival only — never synthesized; `SttFailed` → preserved `ProviderError`; stateless — no store/writer touch) + 4 new ctor deps (`ISttProvider`, `LatencyEventFactory`, `IClock`, `IOptions<DeepgramOptions>`) on both the public + internal-test ctors + `OneFrameAsync` helper.
- `server/AiInterpreter.Api/Evaluation/EvaluationModels.cs` (F.1b) — `+ TranscribeForm` (bindable `[FromForm]` class, §16), `TranscribeResponse`, `EvaluationTranscribeStatus`, `EvaluationTranscribeOutcome`.
- `server/AiInterpreter.Api/Controllers/EvaluationController.cs` (F.1b) — `+ Transcribe` action: SAFETY upload-validation (`CascadeUploadValidation.Validate` → 413/415) BEFORE any provider call + id-string cap (400) + read→service→200/sanitized-error; `_maxUploadBytes` from `EVAL_MAX_UPLOAD_BYTES` → `CASCADE_MAX_UPLOAD_BYTES` → default.
- `server/AiInterpreter.Api/Program.cs` — F.1a: `AddSingleton<EvaluationService>`. F.1b: the framework multipart/Kestrel body-limit backstop is now `Math.Max(cascade, eval)` upload caps (was cascade-only) so a higher per-route cap isn't preempted by a framework 500.
- `server/AiInterpreter.Api/Security/ErrorSanitizer.cs` (F.1a) — 2 fixed-safe messages: `evaluation.invalid_phrase`, `evaluation.phrase_not_found`.

## Decisions made

- **Q1 = (a) persist server-side** when `turnId` present (compute-only when absent) — the only path that gets WER into the session JSON for F.3's comparison summary (ARCH-016). **Refinement vs the brief:** reuse the EXISTING `SessionStore.UpdateTurn(sessionId, turnId, transform)` seam (not a new `SetTurnWerResult`) → **`SessionStore` is unchanged**. Used `UpdateTurn` (unconditional), NOT `FinalizeTurn` (which refuses an already-terminal turn — WER is attached after the turn completed).
- **R1 — the hypothesis cap lives in the SERVICE, not the controller** (the single WER chokepoint), routed through an injected compute delegate via an `internal` test ctor (`InternalsVisibleTo`, lesson §18) so test #5 can prove `Compute` is never invoked on an over-cap hypothesis. More bypass-proof than cap-in-controller; keeps the controller a pure thin mapper. Order: cap → lookup → compute → attach+persist.
- **Hypothesis cap is a DOMAIN 400, not a `[MaxLength]` DataAnnotation** — a `[MaxLength]` would emit a generic ProblemDetails AND bypass the service's `evaluation.invalid_phrase` security chokepoint. `Hypothesis` is `string?` (not `[Required]`): an empty/absent hypothesis is a VALID WER case (STT produced nothing → WER ≈ 1.0). The id strings (`SessionId`/`PhraseId`/`TurnId`) DO carry `[MaxLength(256)]` — they're not the DoS-guarded field.
- **Transcribe is STT-only and stateless** — wrap the blob as a 1-frame `AudioFrame` through the same `ISttProvider` (the derived container content-type routes Deepgram's pre-recorded REST path, C.1); no translation/TTS dependency exists on the service (structural). The audio is transcribed then dropped — never persisted, no turn created (invariant #3).
- **Join multiple `SttFinal`s** (single space), never last-only — Deepgram REST can segment a phrase into >1 final; last-only would silently truncate the hypothesis → wrong WER (ADD-2, lesson §16 family).
- **Reuse `CascadeUploadValidation` for the transcribe upload boundary** — one validator across both audio routes (validate-before-provider, strip MIME params); `EVAL_MAX_UPLOAD_BYTES` tunes the eval cap independently with a cascade-cap fallback.

## Decisions explicitly NOT made (deferred)

- **F.2 / F.3 not started** — HALTED after F.1b per the lead's user-directed pause (no auto-advance to F.2).
- **`evaluation.invalid_request` not folded into `SafeMessageForCode`** — the inline `ProviderError` + `ToUiError` path is byte-for-byte the `CascadeController` precedent (safe-by-construction, fixed literal). Tightening would touch both controllers → a G cleanup, not F.1b.
- **`OneFrameAsync` not extracted to a shared util** — duplicated from `CascadeOrchestrator`; brief §99 pre-approved this as acceptable until a 3rd caller.
- **`SttLanguage = language.ToString().ToLowerInvariant()`** kept as-is — correct for the En/Es enum (matches the approved Q6); the BCP-47 fragility is a future-enum concern, surfaced as a Phase-G/lang-expansion follow-up.

## TDD compliance

**Clean.** Both slices followed RED → Step-2.5 review → GREEN: tests written first, RED confirmed (CS0246 missing-type for both features), orchestrator-approved Step-2.5 before GREEN, then minimum impl to pass. No production code shipped before its test.

Two mid-GREEN process issues, both caught BEFORE the commit (not TDD violations — the tests existed first):
- **F.1b missing-types build break** — an Edit to `EvaluationModels.cs` silently failed to apply (`old_string` mismatch), so the service/controller referenced 4 types that weren't written; `dotnet build` failed (CS0246). Caught by both reviewers' [HIGH]; fixed (types added) before commit.
- **F.1b MIME-param test-helper bug** — the multipart test helper used `new MediaTypeHeaderValue("audio/webm; codecs=opus")` (single-arg ctor rejects parameters → `FormatException`); fixed to `MediaTypeHeaderValue.Parse(...)`. Production `CascadeUploadValidation` MIME-stripping was always correct; the bug was test-only.

## Reachability (Step 7.5)

- **`GET /api/evaluation/phrases`** → `EvaluationController.GetPhrases` → `EvaluationService.GetPhrases`. Reachable via `[ApiController]`/`[Route("api/evaluation")]` → `app.MapControllers()` (Program.cs:215); service in DI (Program.cs:54). Exercised by `EvaluationEndpointTests` over the real HTTP pipeline.
- **`POST /api/evaluation/wer`** → `ComputeWer` → `EvaluationService.ComputeWerAsync` → `WerCalculator.Compute` (+ `SessionStore.UpdateTurn` + `SessionPersistenceWriter.WriteAsync` when `turnId` present). Wire-tested (200/400/404).
- **`POST /api/evaluation/transcribe`** → `Transcribe` → `CascadeUploadValidation.Validate` then `EvaluationService.TranscribeAsync` → `ISttProvider.TranscribeAsync`. Wire-tested (200/413/415).
- **No tested-but-unwired gaps.** All three routes are reachable from the production MVC pipeline; the wire tests prove it end-to-end (not unit-only).

## Open follow-ups

### Step-9 categorized items (orchestrator routes hot; surfaced here for `/orchestrate-end` verification)
- **Cross-doc invariant change** → `ARCHITECTURE.md` ARCH-009 Evaluation APIs + Appendix-A "API DTOs" row: make the `Wer` (F.1a) + `Transcribe` (F.1b) placeholders concrete (`WerRequest`/`WerResponse`, `TranscribeForm`/`TranscribeResponse`). _(Orch edits present in the tree: `ARCHITECTURE.md`, `server/CLAUDE.md` modified.)_ **No ARCH-005 record field changed** (`WerResult`/`EvaluationPhrase` reused verbatim; the turn's `WerResult?` already existed).
- **Architecture doc note** → ARCH-018 code list: `+ evaluation.invalid_phrase`, `+ evaluation.phrase_not_found`, `+ evaluation.invalid_request`. ARCH-016 realization: turn-level WER persist via the existing `UpdateTurn` + writer (no new write path). ARCH-019 realizations: the hypothesis-cap chokepoint + the reused upload-validation surface + raw-audio-never-persisted (stateless transcribe).
- **Cross-doc invariant change** → ARCH-028 + `.env.example`: `+ EVAL_MAX_UPLOAD_BYTES` (sibling of `CASCADE_MAX_UPLOAD_BYTES`, falls back to it). _(`.env.example` modified in the tree.)_
- **Convention candidate** → `server/LESSONS.md` §27/§28 (orch's call on numbering): (a) "Evaluation WER endpoint caps hypothesis length in the SERVICE before the DP allocation, via an injected compute delegate so cap-before-compute is unit-pinnable; the cap returns a DOMAIN 400, NOT a `[MaxLength]` (which would ProblemDetails-bypass the chokepoint); source the WER reference from the store by phraseId, never the request — pin arg-order with an asymmetric test." (b) "Reuse `SessionStore.UpdateTurn` (not a new method) for a post-completion attach — unconditional, where `FinalizeTurn` refuses an already-terminal turn." (c) "Evaluation transcribe is STT-only — wrap the blob as a 1-frame `AudioFrame` through the same `ISttProvider` REST path + JOIN `SttFinal`s (never last-only → truncated hypothesis); reuse `CascadeUploadValidation` (validate-before-provider, strip MIME params); the framework body-limit backstop must be the MAX of all audio routes' caps." _(Orch's `server/LESSONS.md` edit present in the tree.)_
- **Future TODO — belongs to a phase** → the `SttLanguage = enum.ToString()` BCP-47 fragility (a future enum member like `ZhCn` → `"zhcn"`, invalid): a lang-pair-expansion / G-hardening task. Not a current bug (En/Es are valid).
- **Future TODO — out of scope (declined reviewer LOWs, flagged-not-dropped):** a named compute-delegate type (ADD-1 pins arg order; no existing convention); a redundant `persistenceWarning` wire test (the controller mapping is identical to CompleteTurn/End, already wire-tested in `SessionsControllerTests`); `evaluation.invalid_request` → `ForCode`/`SafeMessageForCode` (a both-controllers G cleanup).

### Process follow-ups (banked to memory `rtk-git-commit-swallow`)
- A new failure mode surfaced + banked: composing a teammate `SendMessage` that quotes a count/hash in the SAME turn as the producing command → the figure is guesswork. This session produced multiple mis-reports (two phantom hashes — `4be4c43` then `6256d77`, real values `7e29f35`/`f9cf6a9`; and miscounts 339→"338/1"→"341", true 339). **Rule:** run the command, read the literal output on the next turn, then send quoting only that literal output. Memory + MEMORY.md index updated.

## How to use what was built

- `GET /api/evaluation/phrases` → the scripted phrase list (F.2 phrase selector).
- `POST /api/evaluation/transcribe` (multipart: `SessionId`, `PhraseId`, `Language`, `Audio`) → `{hypothesis, sttProvider, sttModel, latencyEvents}` (STT-only).
- `POST /api/evaluation/wer` (`{sessionId, turnId?, phraseId, hypothesis}`) → `{result: WerResult, persistenceWarning?}`. With `turnId`, the `WerResult` is attached to the turn + persisted (feeds F.3).
