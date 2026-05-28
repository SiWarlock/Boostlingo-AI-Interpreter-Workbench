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
        HttpRequestException http => MapHttp(http, provider, stage),
        _ => new ProviderError(provider, stage, $"{stage}.unknown",
            $"An unexpected {stage} error occurred.", Retryable: false),
    };

    /// <summary>STT returned an empty final transcript — a cascade-level short-circuit, not an STT failure.</summary>
    public static ProviderError EmptyTranscript(string provider) => new(
        provider, "cascade", "cascade.empty_transcript", "No speech was detected in the audio.", Retryable: true);

    /// <summary>A stage exceeded its timeout (the B.4 CancelAfter path calls this directly).</summary>
    public static ProviderError Timeout(string provider, string stage) => new(
        provider, stage, $"{stage}.timeout", $"The {stage} stage timed out.", Retryable: true);

    private static ProviderError MapHttp(HttpRequestException http, string provider, string stage)
    {
        var status = http.StatusCode;
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
