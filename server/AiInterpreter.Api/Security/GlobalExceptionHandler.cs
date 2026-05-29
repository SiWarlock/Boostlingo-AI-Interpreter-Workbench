using AiInterpreter.Api.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace AiInterpreter.Api.Security;

/// <summary>
/// The global HTTP error boundary (ARCH-018 / ARCH-019, safety invariant #4): catches any
/// otherwise-unhandled exception and writes a safe <see cref="UiError"/> JSON response — so a
/// framework error page (which leaks a stack trace in Development) NEVER reaches the client.
///
/// Thin by design: it routes the exception through the B.8 <see cref="ErrorSanitizer"/> (the single
/// sanitizer owner, which logs the original server-side, single-lined — lesson §13) and serializes the
/// resulting <see cref="UiError"/> via the shared <see cref="JsonDefaults"/> contract. It does NOT
/// re-sanitize or re-log. The body is the exact ARCH-007 TS <c>UiError</c> mirror — never
/// <c>ProblemDetails</c> (the frontend <c>ErrorBanner</c> consumes <c>UiError</c>). C.4's WebSocket
/// path emits <c>error</c> frames directly; this handler covers the HTTP endpoints.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ErrorSanitizer _sanitizer;

    public GlobalExceptionHandler(ErrorSanitizer sanitizer) => _sanitizer = sanitizer;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var uiError = _sanitizer.Sanitize(exception);

        httpContext.Response.StatusCode = uiError.HttpStatusCode ?? StatusCodes.Status500InternalServerError;
        // Serialize with the shared JsonDefaults EXPLICITLY (not the ambient HTTP-pipeline options): the
        // exception path runs outside MVC's JSON formatting, so passing JsonDefaults keeps the body the
        // exact camelCase TS UiError mirror. WriteAsJsonAsync also sets Content-Type:
        // application/json; charset=utf-8. Keep this explicit — don't "simplify" to ambient options.
        await httpContext.Response.WriteAsJsonAsync(uiError, JsonDefaults.Options, cancellationToken);

        return true;
    }
}
