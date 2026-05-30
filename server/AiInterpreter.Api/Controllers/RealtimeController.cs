using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// Realtime ephemeral-credential route (ARCH-009 §6 <c>POST /api/realtime/client-secret</c>). Thin
/// (ARCH-008): all minting/transport logic lives in <see cref="RealtimeClientSecretService"/>. The
/// controller owns the HTTP boundary — capping the client-supplied strings (§16) and mapping the service
/// outcome to <c>Ok(dto)</c> / a sanitized <see cref="UiError"/> exactly like <c>CascadeController</c>
/// (never a raw exception/Result; invariant #1 — no key/stack ever reaches the body).
/// </summary>
[ApiController]
[Route("api/realtime")]
public sealed class RealtimeController : ControllerBase
{
    private const int MaxStringLength = 256;

    private readonly RealtimeClientSecretService _service;
    private readonly ErrorSanitizer _sanitizer;

    public RealtimeController(RealtimeClientSecretService service, ErrorSanitizer sanitizer)
    {
        _service = service;
        _sanitizer = sanitizer;
    }

    [HttpPost("client-secret")]
    public async Task<ActionResult<RealtimeTokenResponse>> Mint(
        [FromBody] RealtimeTokenRequest request, CancellationToken cancellationToken)
    {
        // Cap every client-supplied string before any work (§16 boundary hygiene; sessionId is echoed/keyed,
        // model is allow-listed downstream). Reuse the mapper — no bespoke code. The IsNullOrEmpty guard is
        // defense-in-depth: MVC's implicit-required already 400s a missing sessionId, but it also avoids any
        // null-deref on .Length if binding ever yields null.
        if (string.IsNullOrEmpty(request.SessionId) || request.SessionId.Length > MaxStringLength
            || (request.Model?.Length ?? 0) > MaxStringLength)
        {
            var invalid = ProviderErrorMapper.MapStatus(400, "openai", "realtime");
            return StatusCode(invalid.HttpStatusCode ?? StatusCodes.Status400BadRequest, _sanitizer.ToUiError(invalid));
        }

        var outcome = await _service.MintAsync(request, cancellationToken);
        if (outcome.Error is not null)
        {
            return StatusCode(outcome.Error.HttpStatusCode ?? StatusCodes.Status502BadGateway, _sanitizer.ToUiError(outcome.Error));
        }

        return Ok(outcome.Response);
    }
}
