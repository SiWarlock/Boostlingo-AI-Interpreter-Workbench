using AiInterpreter.Api.Common;

namespace AiInterpreter.Api.Security;

/// <summary>
/// Controller-boundary mapping for a best-effort persistence <see cref="Result{T}"/> (shared by the
/// session <c>/end</c> MUST-write and the turn <c>/complete</c> best-effort write). Lives in
/// <c>Security</c> (not <c>Common</c>) because it produces a <see cref="UiError"/> via
/// <see cref="ErrorSanitizer"/> — keeping the layer direction <c>Security → Common</c>, never the reverse.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Maps a persistence write result to its wire-safe reporting pair: on success, the FILENAME only
    /// (never the absolute server path — ARCH-019) and no warning; on failure, no path and a sanitized
    /// <c>persistence.failed</c> <see cref="UiError"/> (the raw <c>Result.Error</c> is logged
    /// server-side only). Exactly one of the two is non-null.
    /// </summary>
    public static (string? PersistedPath, UiError? Warning) ToPersistenceOutcome(
        this Result<string> persist, ErrorSanitizer sanitizer)
        => persist.IsSuccess
            ? (Path.GetFileName(persist.Value), null)
            : (null, sanitizer.SanitizeResult("persistence.failed", persist));
}
