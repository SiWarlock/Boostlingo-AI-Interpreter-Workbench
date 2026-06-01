using System.ComponentModel.DataAnnotations;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Http;

namespace AiInterpreter.Api.Evaluation;

// Request/response DTOs + service outcomes for the evaluation HTTP surface (ARCH-009). Wire DTOs
// serialize camelCase via the shared JsonDefaults (the MVC AddJsonOptions path, lesson §15). The
// outcome types are area-local (not serialized) — the service's discriminated result that the thin
// controller maps to a status code. F.1a adds the WER shapes; F.1b adds the transcribe shapes.

/// <summary>
/// <c>POST /api/evaluation/wer</c> request (ARCH-009). <c>TurnId</c> is optional: when present the
/// computed <see cref="WerResult"/> is attached to that turn + best-effort persisted (ARCH-016); when
/// absent it is compute-and-return only.
///
/// <para>Reference source (090, G.4): <see cref="PhraseId"/> resolves the reference from the scripted
/// store (the DEFAULT phrase flow), OR an explicit <see cref="Reference"/> is supplied directly (the
/// measurement-workbench/soak path — scored by the SAME canonical <see cref="WerCalculator"/>, no client
/// reimplementation). When both are present, <see cref="Reference"/> wins; <see cref="PhraseId"/> is
/// therefore optional. The soak posts <c>{reference, hypothesis}</c> with NO <c>TurnId</c> so its REAL
/// interpretation turns stay un-attached + unmarked (and thus IN the per-mode comparison).</para>
///
/// <para>Boundary caps (ARCH-019 / lesson §16/§27): the lookup/store-key strings carry <c>[MaxLength]</c>
/// so MVC auto-rejects an oversized id before the service. <see cref="Hypothesis"/> AND <see cref="Reference"/>
/// are DELIBERATELY uncapped here — their length caps live in <see cref="EvaluationService"/> (the single WER
/// chokepoint, bounding BOTH n×m DP dimensions), which returns the domain <c>evaluation.invalid_phrase</c> 400
/// (a <c>[MaxLength]</c> would instead emit a generic ProblemDetails AND bypass the service-side DoS guard).
/// <see cref="Hypothesis"/> is <c>string?</c> (not <c>[Required]</c>): a missing/empty hypothesis is a VALID
/// evaluation case (STT produced nothing → WER ≈ 1.0), and System.Text.Json may bind an absent field to null
/// on a non-nullable ref type. DataAnnotations target the record PARAMETER, not the property (lesson §16).</para>
/// </summary>
public sealed record WerRequest(
    [Required, MaxLength(256)] string SessionId,
    [MaxLength(256)] string? TurnId,
    [MaxLength(256)] string? PhraseId,
    string? Hypothesis,
    string? Reference = null);

/// <summary>
/// <c>POST /api/evaluation/wer</c> response: the full <see cref="WerResult"/> + an optional
/// persistence warning (the best-effort turn-attach write degraded — still 200, never 500).
/// </summary>
public sealed record WerResponse(WerResult Result, UiError? PersistenceWarning);

/// <summary>Outcome status of <see cref="EvaluationService.ComputeWerAsync"/> (area-local).</summary>
public enum EvaluationWerStatus
{
    Computed,
    Invalid,         // hypothesis over the length cap (ARCH-019 DoS guard) -> 400 evaluation.invalid_phrase
    PhraseNotFound,  // unknown phraseId -> 404 evaluation.phrase_not_found
    TurnNotFound,    // turnId supplied but session/turn unknown -> 404 turn.not_found
}

/// <summary>
/// The discriminated result of <see cref="EvaluationService.ComputeWerAsync"/> (area-local; not
/// serialized). <see cref="Persist"/> is the best-effort turn-attach write result (null when no
/// <c>turnId</c> was supplied), which the controller maps to an optional persistence warning.
/// </summary>
public sealed record EvaluationWerOutcome(EvaluationWerStatus Status, WerResult? Result, Result<string>? Persist)
{
    public static EvaluationWerOutcome Computed(WerResult result, Result<string>? persist) =>
        new(EvaluationWerStatus.Computed, result, persist);

    public static EvaluationWerOutcome Invalid() => new(EvaluationWerStatus.Invalid, null, null);

    public static EvaluationWerOutcome PhraseNotFound() => new(EvaluationWerStatus.PhraseNotFound, null, null);

    public static EvaluationWerOutcome TurnNotFound() => new(EvaluationWerStatus.TurnNotFound, null, null);
}

// --- Transcribe (F.1b — STT-only) ---

/// <summary>
/// Multipart form binding for <c>POST /api/evaluation/transcribe</c> (a bindable class — settable
/// properties — to avoid the record-positional-param binding gotcha, lesson §16). The <c>Audio</c>
/// file's content-type drives BOTH the upload validation (size/type) and the derived STT container
/// encoding (routing the pre-recorded REST path). <c>SessionId</c>/<c>PhraseId</c> are carried per the
/// ARCH-009 contract but transcribe is stateless (no turn write); the controller caps the id lengths.
/// </summary>
public sealed class TranscribeForm
{
    public string? SessionId { get; set; }
    public string? PhraseId { get; set; }
    public LanguageCode Language { get; set; }
    public IFormFile? Audio { get; set; }
}

/// <summary>
/// <c>POST /api/evaluation/transcribe</c> response (ARCH-009): the STT hypothesis + the provider/model
/// identity that produced it + the latency events stamped on real arrival (no synthesis). STT-only —
/// no translation/TTS (the WER comparison is a separate <c>/wer</c> call).
/// </summary>
public sealed record TranscribeResponse(
    string Hypothesis, string SttProvider, string SttModel, List<LatencyEvent> LatencyEvents);

/// <summary>Outcome status of <see cref="EvaluationService.TranscribeAsync"/> (area-local).</summary>
public enum EvaluationTranscribeStatus
{
    Ok,
    SttFailed, // the STT provider emitted SttFailed → surface the preserved ProviderError, sanitized
}

/// <summary>
/// The discriminated result of <see cref="EvaluationService.TranscribeAsync"/> (area-local; not
/// serialized). On <see cref="EvaluationTranscribeStatus.SttFailed"/>, <see cref="Error"/> carries the
/// already-safe <see cref="ProviderError"/> the controller projects to a <see cref="UiError"/>.
/// </summary>
public sealed record EvaluationTranscribeOutcome(
    EvaluationTranscribeStatus Status, TranscribeResponse? Response, ProviderError? Error)
{
    public static EvaluationTranscribeOutcome Ok(TranscribeResponse response) =>
        new(EvaluationTranscribeStatus.Ok, response, null);

    public static EvaluationTranscribeOutcome Failed(ProviderError error) =>
        new(EvaluationTranscribeStatus.SttFailed, null, error);
}
