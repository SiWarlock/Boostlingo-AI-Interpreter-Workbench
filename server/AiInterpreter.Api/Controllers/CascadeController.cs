using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// Cascade blob-fallback route (ARCH-009 <c>POST /api/cascade/turn</c>). Thin (ARCH-008): all
/// provider/orchestration logic lives in <see cref="CascadeOrchestrator"/>. The controller owns the HTTP
/// boundary — the SAFETY upload validation (size + content-type → 413/415, invariant #5), capping the
/// client-supplied turn ids (§16), reading the multipart body, and mapping the service result / errors to
/// sanitized <see cref="UiError"/>s (never a raw exception/Result). The streaming WS path is the primary
/// transport; this is the documented non-streaming fallback.
/// </summary>
[ApiController]
[Route("api/cascade")]
public sealed class CascadeController : ControllerBase
{
    private const int MaxIdLength = 256;

    private readonly CascadeOrchestrator _orchestrator;
    private readonly ErrorSanitizer _sanitizer;
    private readonly long _maxUploadBytes;

    public CascadeController(CascadeOrchestrator orchestrator, ErrorSanitizer sanitizer, IConfiguration configuration)
    {
        _orchestrator = orchestrator;
        _sanitizer = sanitizer;
        _maxUploadBytes = configuration.GetValue<long?>("CASCADE_MAX_UPLOAD_BYTES") ?? CascadeUploadValidation.DefaultMaxBytes;
    }

    [HttpPost("turn")]
    public async Task<ActionResult<CascadeTurnResponse>> PostTurn([FromForm] CascadeTurnForm form, CancellationToken cancellationToken)
    {
        // SAFETY (invariant #5): validate the upload BEFORE any provider call. The content-type drives both
        // the size/type gate and the derived container encoding (routing to pre-recorded STT).
        var check = CascadeUploadValidation.Validate(form.Audio?.ContentType, form.Audio?.Length ?? 0, _maxUploadBytes);
        if (!check.Ok)
        {
            return StatusCode(check.StatusCode, _sanitizer.ToUiError(check.Error!));
        }

        // Cap every client-supplied string (ids are store keys + echoed; model/voice persist to the turn;
        // §16 WS-boundary precedent — bound all boundary strings).
        if ((form.SessionId?.Length ?? 0) > MaxIdLength || (form.TurnId?.Length ?? 0) > MaxIdLength
            || (form.TranslationModel?.Length ?? 0) > MaxIdLength || (form.TtsVoice?.Length ?? 0) > MaxIdLength)
        {
            var invalid = new ProviderError("cascade", "cascade", "cascade.invalid_request", "The request fields are invalid.", Retryable: false, 400);
            return StatusCode(StatusCodes.Status400BadRequest, _sanitizer.ToUiError(invalid));
        }

        byte[] audioBytes;
        await using (var stream = form.Audio!.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            audioBytes = buffer.ToArray();
        }

        var p = new CascadeBlobParams(
            form.SessionId ?? string.Empty,
            form.TurnId ?? string.Empty,
            new LanguageDirection(form.Source, form.Target),
            check.Encoding,
            form.TranslationModel ?? string.Empty,
            form.TtsVoice ?? string.Empty);

        var result = await _orchestrator.RunBlobTurnAsync(p, audioBytes, cancellationToken);
        if (result is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, _sanitizer.ForCode("turn.not_found", StatusCodes.Status404NotFound));
        }

        // Audio in the RESPONSE only (never persisted, invariant #3); omit when empty.
        var (_, warning) = result.Persist.ToPersistenceOutcome(_sanitizer);
        var audioBase64 = result.AudioBytes.Length > 0 ? Convert.ToBase64String(result.AudioBytes) : null;
        return Ok(new CascadeTurnResponse(result.Turn, audioBase64, audioBase64 is null ? null : result.AudioContentType, warning));
    }
}
