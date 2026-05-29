using System.Globalization;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;
using Deepgram.Models.Exceptions.v1;
using Deepgram.Models.Listen.v2.WebSocket;
using WsResultResponse = Deepgram.Models.Listen.v2.WebSocket.ResultResponse;
using RestSyncResponse = Deepgram.Models.Listen.v1.REST.SyncResponse;

namespace AiInterpreter.Api.Providers.Deepgram;

/// <summary>
/// The pure, deterministic surface of <see cref="DeepgramSttProvider"/> (C.1) — the SDK-result -> normalized
/// <see cref="SttEvent"/> mapping, the pre-recorded REST response parsing, the exception -> <see cref="SttFailed"/>
/// projection, and the live request-schema build. The transport shell calls these; the shell itself does no logic
/// (so it stays manual-smoke per the ARCH-020 posture). <c>internal</c> + InternalsVisibleTo lets these be unit-TDD'd
/// without a live socket.
/// </summary>
internal static class DeepgramSttMapping
{
    private const string Provider = "deepgram";
    private const string Stage = "stt";

    /// <summary>
    /// A live-WS result -> one <see cref="SttEvent"/>: an interim (<c>IsFinal != true</c>) -> <see cref="SttPartial"/>;
    /// a final (<c>IsFinal == true</c>) -> <see cref="SttFinal"/>, with a whitespace/empty transcript normalized to
    /// <see cref="string.Empty"/> (NOT <see cref="SttFailed"/> / <c>cascade.empty_transcript</c> — the orchestrator owns
    /// that short-circuit, ARCH-011).
    /// </summary>
    public static SttEvent ToSttEvent(WsResultResponse result, DateTimeOffset timestamp)
    {
        var transcript = result.Channel?.Alternatives?.FirstOrDefault()?.Transcript;

        return result.IsFinal == true
            ? new SttFinal(NormalizeFinal(transcript), timestamp)
            : new SttPartial(transcript ?? string.Empty, timestamp);
    }

    /// <summary>
    /// The pre-recorded REST response -> the ordered fallback contract: <see cref="SttStarted"/> then a single
    /// <see cref="SttFinal"/> (no interim), empty transcript normalized to <see cref="string.Empty"/> (ARCH-011 fallback).
    /// </summary>
    public static IReadOnlyList<SttEvent> ParsePrerecorded(RestSyncResponse response, DateTimeOffset timestamp)
    {
        var transcript = response.Results?.Channels?.FirstOrDefault()?.Alternatives?.FirstOrDefault()?.Transcript;

        return new SttEvent[]
        {
            new SttStarted(timestamp),
            new SttFinal(NormalizeFinal(transcript), timestamp),
        };
    }

    /// <summary>
    /// Projects an SDK/HTTP exception to <see cref="SttFailed"/> via the single ARCH-012 mapper owner
    /// (<see cref="ProviderErrorMapper"/>) with the provider/stage constants — SafeMessage never echoes the exception,
    /// so no key/stack can leak (ARCH-018/019).
    ///
    /// STATUS RECOVERY (C.6): Deepgram SDK v6.6.1 discards the numeric HTTP status on the error-body path —
    /// <c>DeepgramException</c>/<c>DeepgramRESTException</c> carry only the semantic <c>err_code</c> string. We recover
    /// the status by matching that string (<see cref="TryHttpStatusFromErrCode"/>) and route it through the vendor-
    /// agnostic <see cref="ProviderErrorMapper.MapStatus"/>, so a common-path 429/401/403/400 maps correctly
    /// (<c>stt.rate_limited</c>/<c>stt.auth</c>/<c>stt.invalid_request</c>). An unrecognized/absent <c>err_code</c>
    /// (incl. Deepgram 5xx-with-body, which uses the <c>error_code</c> key) falls through to the existing mapper's
    /// <c>stt.unknown</c> degrade; an empty-body error still arrives as a status-bearing <see cref="HttpRequestException"/>
    /// handled by <see cref="ProviderErrorMapper.Map"/>.
    /// </summary>
    public static SttFailed ToFailed(Exception exception, DateTimeOffset timestamp) =>
        new(MapException(exception), timestamp);

    private static ProviderError MapException(Exception exception)
    {
        // err_code DECIDES the status only; it never enters the ProviderError (SafeMessage stays fixed-per-code,
        // so no err_code/err_msg leak — ARCH-018/019).
        if (exception is DeepgramException dg && TryHttpStatusFromErrCode(dg.ErrCode, out var status))
        {
            return ProviderErrorMapper.MapStatus(status, Provider, Stage);
        }

        // Status-bearing HttpRequestException (empty-body path) / OCE / Timeout / unrecognized err_code:
        // the existing mapper handles the status, the timeout, and the stt.unknown fallback.
        return ProviderErrorMapper.Map(exception, Provider, Stage);
    }

    // Deepgram's documented err_code strings (v6.6.1, matched EXACT + case-sensitive) -> the HTTP status the SDK
    // discarded (ARCH-012). "Bad Request" is Title-Case-with-space (a Deepgram artifact) unlike the UPPER_SNAKE
    // others; an SDK casing change would degrade that case to stt.unknown. 5xx-with-body is intentionally absent
    // (Deepgram uses the "error_code" key there, so ErrCode stays at the SDK default "Unknown Error Code" -> the
    // unmappable fallback). 401-insufficient-permissions shares "INSUFFICIENT_PERMISSIONS" with 403 — harmless,
    // both -> stt.auth.
    private static bool TryHttpStatusFromErrCode(string? errCode, out int status)
    {
        status = errCode switch
        {
            "TOO_MANY_REQUESTS" => 429,
            "INVALID_AUTH" => 401,
            "INSUFFICIENT_PERMISSIONS" => 403,
            "Bad Request" => 400,
            _ => 0,
        };

        return status != 0;
    }

    /// <summary>
    /// Builds the Deepgram live-listen schema from the request + options. ARCH-030 no-resample/no-transcode:
    /// <c>encoding</c> + <c>sample_rate</c> + <c>language</c> come from the REQUEST (never hardcoded); model + the
    /// streaming flags come from <see cref="DeepgramOptions"/>.
    /// </summary>
    public static LiveSchema BuildLiveSchema(SttRequest request, DeepgramOptions options) => new()
    {
        Model = options.Model,
        Language = request.SttLanguage,
        SmartFormat = options.SmartFormat,
        InterimResults = options.InterimResults,
        Encoding = request.Encoding,
        SampleRate = request.SampleRate,
        Channels = options.Channels,
        UtteranceEnd = options.UtteranceEndMs.ToString(CultureInfo.InvariantCulture),
    };

    private static string NormalizeFinal(string? transcript) =>
        string.IsNullOrWhiteSpace(transcript) ? string.Empty : transcript;
}
