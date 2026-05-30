using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// Evaluation routes (ARCH-009): <c>GET /api/evaluation/phrases</c>, <c>POST /api/evaluation/wer</c>,
/// <c>POST /api/evaluation/transcribe</c> (STT-only). Thin (ARCH-008): orchestration lives in
/// <see cref="EvaluationService"/>; the controller binds, validates the audio upload (size/type → 413/415,
/// invariant #5 — reusing <see cref="CascadeUploadValidation"/>) + caps the client-supplied id strings,
/// maps the service outcome to a status code + a sanitized error/persistence warning, and never serializes
/// a <c>Result</c>/LoadError.
/// </summary>
[ApiController]
[Route("api/evaluation")]
public sealed class EvaluationController : ControllerBase
{
    private const int MaxIdLength = 256;

    private readonly EvaluationService _service;
    private readonly ErrorSanitizer _sanitizer;
    private readonly long _maxUploadBytes;

    public EvaluationController(EvaluationService service, ErrorSanitizer sanitizer, IConfiguration configuration)
    {
        _service = service;
        _sanitizer = sanitizer;
        // EVAL_MAX_UPLOAD_BYTES tunes the eval route independently; falls back to the cascade key, then the
        // shared default (~10MB). Same validator → one upload-validation surface across both routes.
        _maxUploadBytes = configuration.GetValue<long?>("EVAL_MAX_UPLOAD_BYTES")
            ?? configuration.GetValue<long?>("CASCADE_MAX_UPLOAD_BYTES")
            ?? CascadeUploadValidation.DefaultMaxBytes;
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

    /// <summary>STT-only transcription of an uploaded phrase recording (ARCH-009). SAFETY (invariant #5):
    /// the audio upload is validated (size + content-type → 413/415, reusing <see cref="CascadeUploadValidation"/>)
    /// BEFORE any provider call; the id strings are length-capped (§16). The derived container encoding routes
    /// the pre-recorded STT path. An STT failure → a sanitized <see cref="UiError"/> (no payload leak).</summary>
    [HttpPost("transcribe")]
    public async Task<ActionResult<TranscribeResponse>> Transcribe(
        [FromForm] TranscribeForm form, CancellationToken cancellationToken)
    {
        // SAFETY (invariant #5): validate the upload BEFORE any provider call. The content-type drives both
        // the size/type gate and the derived container encoding (routing to pre-recorded STT).
        var check = CascadeUploadValidation.Validate(form.Audio?.ContentType, form.Audio?.Length ?? 0, _maxUploadBytes);
        if (!check.Ok)
        {
            return StatusCode(check.StatusCode, _sanitizer.ToUiError(check.Error!));
        }

        // Cap the client-supplied id strings (§16 boundary hygiene; ids are echoed/passed to the provider).
        if ((form.SessionId?.Length ?? 0) > MaxIdLength || (form.PhraseId?.Length ?? 0) > MaxIdLength)
        {
            var invalid = new Providers.Abstractions.ProviderError(
                "evaluation", "evaluation", "evaluation.invalid_request", "The request fields are invalid.",
                Retryable: false, StatusCodes.Status400BadRequest);
            return StatusCode(StatusCodes.Status400BadRequest, _sanitizer.ToUiError(invalid));
        }

        byte[] audioBytes;
        await using (var stream = form.Audio!.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            audioBytes = buffer.ToArray();
        }

        var outcome = await _service.TranscribeAsync(
            audioBytes, check.Encoding, form.Language, form.SessionId ?? string.Empty, cancellationToken);

        return outcome.Status == EvaluationTranscribeStatus.Ok
            ? Ok(outcome.Response)
            : StatusCode(outcome.Error!.HttpStatusCode ?? StatusCodes.Status502BadGateway, _sanitizer.ToUiError(outcome.Error));
    }
}
