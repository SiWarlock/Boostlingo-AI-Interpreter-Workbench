using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// Evaluation routes (ARCH-009): <c>GET /api/evaluation/phrases</c> + <c>POST /api/evaluation/wer</c>
/// (<c>POST .../transcribe</c> arrives in F.1b). Thin (ARCH-008): orchestration lives in
/// <see cref="EvaluationService"/>; the controller binds, maps the service outcome to a status code +
/// (on the attach path) a sanitized persistence warning, and never serializes a <c>Result</c>/LoadError.
/// </summary>
[ApiController]
[Route("api/evaluation")]
public sealed class EvaluationController : ControllerBase
{
    private readonly EvaluationService _service;
    private readonly ErrorSanitizer _sanitizer;

    public EvaluationController(EvaluationService service, ErrorSanitizer sanitizer)
    {
        _service = service;
        _sanitizer = sanitizer;
    }

    /// <summary>The scripted evaluation phrases (ARCH-015). Degrades to an empty list when the store
    /// failed to load — the <c>LoadError</c> is NEVER surfaced (ARCH-019, lesson §10).</summary>
    [HttpGet("phrases")]
    public ActionResult<IReadOnlyList<EvaluationPhrase>> GetPhrases() => Ok(_service.GetPhrases());

    /// <summary>Computes WER for a hypothesis against a scripted phrase (ARCH-015). The hypothesis-length
    /// cap (ARCH-019 DoS guard) + phrase lookup + the optional turn-attach/persist live in the service;
    /// the controller maps the outcome to 200/400/404 with sanitized errors.</summary>
    [HttpPost("wer")]
    public async Task<ActionResult<WerResponse>> ComputeWer(
        [FromBody] WerRequest request, CancellationToken cancellationToken)
    {
        var outcome = await _service.ComputeWerAsync(request, cancellationToken);
        switch (outcome.Status)
        {
            case EvaluationWerStatus.Invalid:
                return StatusCode(
                    StatusCodes.Status400BadRequest,
                    _sanitizer.ForCode("evaluation.invalid_phrase", StatusCodes.Status400BadRequest));
            case EvaluationWerStatus.PhraseNotFound:
                return StatusCode(
                    StatusCodes.Status404NotFound,
                    _sanitizer.ForCode("evaluation.phrase_not_found", StatusCodes.Status404NotFound));
            case EvaluationWerStatus.TurnNotFound:
                return StatusCode(
                    StatusCodes.Status404NotFound,
                    _sanitizer.ForCode("turn.not_found", StatusCodes.Status404NotFound));
            case EvaluationWerStatus.Computed:
                // Best-effort persist (Q1=a turn-attach): a degraded write surfaces a safe
                // persistence.failed warning in the 200 body — never a 500 (mirrors CompleteTurn/End).
                var warning = outcome.Persist is { } persist
                    ? persist.ToPersistenceOutcome(_sanitizer).Warning
                    : null;
                return Ok(new WerResponse(outcome.Result!, warning));
            default:
                // Exhaustive: a future EvaluationWerStatus must add its own branch, not fall silently
                // into the 200 path (where Result! would NRE).
                throw new InvalidOperationException($"Unhandled evaluation WER status: {outcome.Status}");
        }
    }
}
