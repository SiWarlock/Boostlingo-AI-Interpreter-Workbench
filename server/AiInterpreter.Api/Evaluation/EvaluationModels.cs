using System.ComponentModel.DataAnnotations;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;

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
/// <para>Boundary caps (ARCH-019 / lesson §16): the lookup/store-key strings carry <c>[MaxLength]</c>
/// so MVC auto-rejects an oversized id before the service. <see cref="Hypothesis"/> is DELIBERATELY
/// uncapped here — its length cap lives in <see cref="EvaluationService"/> (the single WER chokepoint),
/// which returns the domain <c>evaluation.invalid_phrase</c> 400 (a <c>[MaxLength]</c> would instead emit
/// a generic ProblemDetails AND bypass the service-side DoS guard). It is <c>string?</c> (not
/// <c>[Required]</c>): a missing/empty hypothesis is a VALID evaluation case (STT produced nothing →
/// WER ≈ 1.0), and System.Text.Json may bind an absent field to null on a non-nullable ref type.
/// DataAnnotations target the record PARAMETER, not the property (lesson §16).</para>
/// </summary>
public sealed record WerRequest(
    [Required, MaxLength(256)] string SessionId,
    [MaxLength(256)] string? TurnId,
    [Required, MaxLength(256)] string PhraseId,
    string? Hypothesis);

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
