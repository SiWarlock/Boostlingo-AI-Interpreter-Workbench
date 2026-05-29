using System.Globalization;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;
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
    /// LIMITATION (Deepgram SDK v6.6.1): API errors surface as <c>DeepgramException</c>/<c>DeepgramRESTException</c>
    /// which carry NO HTTP status, so a common-path 429/401/403 degrades to <c>stt.unknown</c> (non-retryable) via the
    /// mapper's default branch; only the rare empty-body path throws a status-bearing <c>HttpRequestException</c> that
    /// maps to <c>stt.rate_limited</c>/<c>stt.auth</c>. Accepted for C.1 (no speculative mapper expansion); a Phase-C
    /// hardening task tracks recovering the status from the SDK exception.
    /// </summary>
    public static SttFailed ToFailed(Exception exception, DateTimeOffset timestamp) =>
        new(ProviderErrorMapper.Map(exception, Provider, Stage), timestamp);

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
