using System.Net;

namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>
/// The single owner of the ARCH-012 exception/HTTP-status -> <see cref="ProviderError"/> mapping
/// table. Real providers (C) catch their SDK/HTTP exceptions and call <see cref="Map"/>; the B.4
/// orchestrator calls <see cref="EmptyTranscript"/> / <see cref="Timeout"/> for the non-exception
/// outcomes it raises directly.
///
/// SafeMessage is a fixed generic string per code — it NEVER echoes the exception message, so no
/// secret or stack trace can leak through the boundary (ARCH-018/019). B.8's ErrorSanitizer owns
/// the full sanitize + server-side log of the original exception.
///
/// CALLER CONTRACT: <c>stage</c> is a closed set — "stt" | "translation" | "tts" | "cascade" —
/// passed as a compile-time literal by the C providers / B.4 orchestrator. It is interpolated into
/// <c>Code</c> + <c>SafeMessage</c> (both persisted + surfaced to the UI), so callers must never
/// derive it from external/untrusted input.
/// </summary>
public static class ProviderErrorMapper
{
    public static ProviderError Map(Exception exception, string provider, string stage) => exception switch
    {
        OperationCanceledException => Timeout(provider, stage),
        TimeoutException => Timeout(provider, stage),
        HttpRequestException http => MapHttpStatus(http.StatusCode, provider, stage),
        _ => Unknown(provider, stage),
    };

    /// <summary>STT returned an empty final transcript — a cascade-level short-circuit, not an STT failure.</summary>
    public static ProviderError EmptyTranscript(string provider) => new(
        provider, "cascade", "cascade.empty_transcript", "No speech was detected in the audio.", Retryable: true);

    /// <summary>
    /// A non-exception, fail-closed outcome: a <b>started</b> stage stream ended WITHOUT its terminal event
    /// (no <c>TranslationFinal</c> / <c>TtsComplete</c> / <c>SttFinal</c>). The B.4 orchestrator raises this
    /// directly (like <see cref="Timeout"/> / <see cref="EmptyTranscript"/>) so a terminal-less real-provider
    /// stream fails closed instead of silently skipping/completing (ARCH-011/018). Non-retryable; same fixed
    /// SafeMessage as the <see cref="Map"/> generic fallback (never echoes provider text — lesson §5/§13).
    /// </summary>
    public static ProviderError Unknown(string provider, string stage) => new(
        provider, stage, $"{stage}.unknown", $"An unexpected {stage} error occurred.", Retryable: false);

    /// <summary>A stage exceeded its timeout (the B.4 CancelAfter path calls this directly).</summary>
    public static ProviderError Timeout(string provider, string stage) => new(
        provider, stage, $"{stage}.timeout", $"The {stage} stage timed out.", Retryable: true);

    /// <summary>
    /// Maps an explicit HTTP status code to a <see cref="ProviderError"/> using the SAME ARCH-012 table
    /// as the <see cref="HttpRequestException"/> path. For SDKs that surface the status OUTSIDE an
    /// <see cref="HttpRequestException"/> — e.g. the Deepgram provider recovers it from the SDK exception's
    /// <c>err_code</c> string, since v6.6.1 discards the numeric <c>StatusCode</c> on the error-body path —
    /// call this directly with the recovered status. Keeps the status->code table single-owned and the
    /// mapper vendor-agnostic (no provider-SDK dependency in Abstractions). Caller passes a valid HTTP
    /// status; an out-of-range value degrades to the <c>{stage}.unknown</c> default (same as any unrecognized status).
    /// </summary>
    public static ProviderError MapStatus(int statusCode, string provider, string stage) =>
        MapHttpStatus((HttpStatusCode)statusCode, provider, stage);

    private static ProviderError MapHttpStatus(HttpStatusCode? status, string provider, string stage)
    {
        var code = (int?)status;

        return status switch
        {
            HttpStatusCode.TooManyRequests => new ProviderError(provider, stage, $"{stage}.rate_limited",
                $"The {stage} provider is rate-limited; please retry shortly.", Retryable: true, code),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderError(provider, stage,
                $"{stage}.auth", $"The {stage} provider rejected the configured credentials.", Retryable: false, code),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new ProviderError(provider, stage,
                $"{stage}.invalid_request", $"The {stage} request was invalid.", Retryable: false, code),
            // Network failure: HttpRequestException with no HTTP status.
            null => new ProviderError(provider, stage, $"{stage}.upstream_unavailable",
                $"The {stage} provider is temporarily unavailable.", Retryable: true),
            >= HttpStatusCode.InternalServerError => new ProviderError(provider, stage, $"{stage}.upstream_unavailable",
                $"The {stage} provider is temporarily unavailable.", Retryable: true, code),
            _ => new ProviderError(provider, stage, $"{stage}.unknown",
                $"An unexpected {stage} error occurred.", Retryable: false, code),
        };
    }
}
