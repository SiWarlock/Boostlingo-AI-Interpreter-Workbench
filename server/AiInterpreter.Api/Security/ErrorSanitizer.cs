using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace AiInterpreter.Api.Security;

/// <summary>
/// The global error boundary (ARCH-018 / ARCH-019, safety invariant #4). Turns any internal/provider
/// error into a safe, normalized <see cref="UiError"/> — NEVER leaking a stack trace, a secret, or a
/// raw provider payload to a client response or an unfiltered log.
///
/// <b>Safe-by-construction, not scrub</b> (lesson §5, extended to the global boundary): the
/// <see cref="UiError.SafeMessage"/> is ALWAYS a fixed string per code; untrusted input
/// (<c>ex.Message</c> / <c>Result.Error</c> / a stack trace) is NEVER interpolated into the output.
/// The original (full) detail is logged server-side only via the injected <see cref="ILogger"/>, so
/// diagnosis stays possible while the client sees only the safe <see cref="UiError"/>.
///
/// <b>Division of labor with <see cref="ProviderErrorMapper"/></b>: provider-stream exceptions go
/// through the mapper first (it owns the ARCH-012 HTTP-status table + the already-safe
/// <see cref="ProviderError"/>); this sanitizer <see cref="ToUiError"/>-projects that into a
/// <see cref="UiError"/> and owns the generic <see cref="Exception"/> boundary +
/// <see cref="SanitizeResult"/>. It does not duplicate the mapper's table.
/// </summary>
public sealed class ErrorSanitizer
{
    private const string GenericCode = "internal.error";
    private const string GenericMessage = "An unexpected error occurred.";

    private readonly ILogger<ErrorSanitizer> _logger;

    public ErrorSanitizer(ILogger<ErrorSanitizer> logger) => _logger = logger;

    /// <summary>
    /// Generic boundary: any unmapped exception → a fixed-safe <c>internal.error</c> (status 500). The
    /// original exception (message + stack) is logged server-side; it appears NOWHERE in the result.
    /// </summary>
    public UiError Sanitize(Exception exception, string? turnId = null)
    {
        _logger.LogError(exception, "Unhandled error sanitized to {Code}", GenericCode);
        return new UiError(GenericCode, GenericMessage, Stage: null, Retryable: false, TurnId: turnId)
        {
            HttpStatusCode = 500,
        };
    }

    /// <summary>
    /// Projects an already-safe <see cref="ProviderError"/> → <see cref="UiError"/>. The internal
    /// <c>Provider</c> identity is dropped (not on the wire shape); Code/SafeMessage/Stage/Retryable +
    /// HTTP status carry through.
    /// </summary>
    public UiError ToUiError(ProviderError error, string? turnId = null) =>
        new(error.Code, error.SafeMessage, error.Stage, error.Retryable, turnId)
        {
            HttpStatusCode = error.HttpStatusCode,
        };

    /// <summary>
    /// Maps a <b>failed</b> <see cref="Result"/> → <see cref="UiError"/> WITHOUT echoing
    /// <c>Result.Error</c>: a fixed-safe message per <paramref name="code"/> (see
    /// <see cref="SafeMessageForCode"/>). The raw error string is logged server-side only (single-lined
    /// so it can't forge log lines). (<c>Result&lt;T&gt;</c> gets a sibling overload in B.9, where its
    /// first consumer + test live.)
    /// </summary>
    /// <param name="code">
    /// A normalized <c>&lt;stage&gt;.&lt;reason&gt;</c> code — MUST be a compile-time literal from the
    /// error-code table, NEVER derived from external/request input (it becomes <c>UiError.Code</c> on
    /// the wire). Mirrors <see cref="ProviderErrorMapper"/>'s CALLER CONTRACT.
    /// </param>
    public UiError SanitizeResult(
        string code, Result result, string? stage = null, bool retryable = false,
        int? httpStatusCode = null, string? turnId = null)
    {
        if (!result.IsSuccess && result.Error is not null)
        {
            // Single-line the internal error before logging: a multi-line Result.Error must not forge
            // extra log lines in a plain text sink (defense-in-depth; the client never sees it).
            _logger.LogError("Result failure sanitized to {Code}: {RawError}",
                code, result.Error.ReplaceLineEndings(" "));
        }

        return new UiError(code, SafeMessageForCode(code), stage, retryable, turnId)
        {
            HttpStatusCode = httpStatusCode,
        };
    }

    /// <summary>
    /// <see cref="Result{T}"/> sibling of <see cref="SanitizeResult(string, Result, string?, bool, int?, string?)"/>
    /// (B.9c-i; first consumer = the <c>/end</c> <c>Result&lt;string&gt;</c> persistence path). Same
    /// safe-by-construction contract: a fixed message per code, the raw <c>Result.Error</c> logged
    /// server-side (single-lined) but NEVER echoed into the <see cref="UiError"/>. Parameter order
    /// matches the non-generic sibling so a positional caller can't get the wrong semantics.
    /// </summary>
    public UiError SanitizeResult<T>(
        string code, Result<T> result, string? stage = null, bool retryable = false,
        int? httpStatusCode = null, string? turnId = null)
    {
        if (!result.IsSuccess && result.Error is not null)
        {
            _logger.LogError("Result failure sanitized to {Code}: {RawError}",
                code, result.Error.ReplaceLineEndings(" "));
        }

        return new UiError(code, SafeMessageForCode(code), stage, retryable, turnId)
        {
            HttpStatusCode = httpStatusCode,
        };
    }

    /// <summary>
    /// Builds a safe <see cref="UiError"/> for an EXPECTED condition (not an exception or failed Result)
    /// — e.g. a 404 <c>session.not_found</c>. No server-side log (the condition is normal), the message
    /// comes from the owned <see cref="SafeMessageForCode"/> map so the sanitizer stays the single owner
    /// of safe messages. The <paramref name="code"/> must be a compile-time normalized literal.
    /// </summary>
    public UiError ForCode(
        string code, int? httpStatusCode = null, bool retryable = false, string? stage = null, string? turnId = null)
        => new(code, SafeMessageForCode(code), stage, retryable, turnId) { HttpStatusCode = httpStatusCode };

    // Fixed-safe UI strings for the non-provider codes (NOT the ARCH-012 HTTP table — provider codes
    // carry their own SafeMessage via ToUiError). Grows as Result-path/expected-condition consumers add
    // codes; the generic fallback guarantees an unmapped code still yields a safe message, never an echo.
    private static string SafeMessageForCode(string code) => code switch
    {
        "persistence.failed" => "Saving the session failed; the session continues.",
        "session.not_found" => "Session not found.",
        "turn.not_found" => "Turn not found.",
        "evaluation.invalid_phrase" => "The submitted text is too long or invalid.",
        "evaluation.phrase_not_found" => "Evaluation phrase not found.",
        _ => GenericMessage,
    };
}
