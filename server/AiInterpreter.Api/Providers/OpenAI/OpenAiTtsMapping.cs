using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.OpenAI;

/// <summary>
/// The pure, deterministic surface of <see cref="OpenAiTtsProvider"/> (C.3) — the `/v1/audio/speech`
/// request-body builder, the pre-call 4096-char input-cap projection, the exception -> <see cref="TtsFailed"/>
/// projection, and the response Content-Type resolution. The transport shell drives the network + the binary
/// chunk-read loop; these helpers hold the logic (lesson §20, extended for a binary chunk stream).
/// </summary>
internal static class OpenAiTtsMapping
{
    private const string Provider = "openai";
    private const string Stage = "tts";

    /// <summary>OpenAI `/v1/audio/speech` input cap (ARCH-011) — characters, not bytes.</summary>
    public const int MaxInputChars = 4096;

    /// <summary>
    /// Builds the `/v1/audio/speech` request body. <c>stream_format="audio"</c> is LOAD-BEARING: gpt-4o-mini-tts
    /// defaults it to <c>sse</c> (base64 audio wrapped in SSE events) — <c>audio</c> forces the raw chunked-binary
    /// body this provider reads. Voice/format from the request (per-turn), falling back to options; optional
    /// <c>instructions</c> only when supplied (gpt-4o-mini-tts honors it; legacy tts-1* ignore it).
    /// </summary>
    public static object BuildRequestBody(TtsRequest request, OpenAiTtsOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model,
            ["input"] = request.Text,
            ["voice"] = ResolveVoice(request.Voice, request.TargetLanguage, options),
            ["response_format"] = string.IsNullOrWhiteSpace(request.ResponseFormat) ? options.ResponseFormat : request.ResponseFormat,
            ["stream_format"] = "audio",
        };

        var instructions = !string.IsNullOrWhiteSpace(request.Instructions) ? request.Instructions
            : !string.IsNullOrWhiteSpace(options.Instructions) ? options.Instructions
            : null;
        if (instructions is not null)
        {
            body["instructions"] = instructions;
        }

        return body;
    }

    /// <summary>
    /// The pre-call 4096-char cap violation (ARCH-011): a client-side over-length input is genuinely an
    /// <c>invalid_request</c> (non-retryable) — the same code a server 400 gives — so it reuses the vendor-agnostic
    /// <see cref="ProviderErrorMapper.MapStatus"/>(400) (no new mapper code). The provider emits this WITHOUT an HTTP call.
    /// </summary>
    public static TtsFailed CapExceeded(DateTimeOffset timestamp) =>
        new(ProviderErrorMapper.MapStatus(400, Provider, Stage), timestamp);

    /// <summary>
    /// Projects an HTTP/exception failure to <see cref="TtsFailed"/> via the single ARCH-012 mapper owner —
    /// SafeMessage never echoes the exception (ARCH-018/019). Raw HttpClient surfaces status-bearing errors, so
    /// 429/401/403 map correctly; `/v1/audio/speech` (audio mode) has no in-band error frame, so a truncated
    /// stream simply throws on read and lands here.
    /// </summary>
    public static TtsFailed ToFailed(Exception exception, DateTimeOffset timestamp) =>
        new(ProviderErrorMapper.Map(exception, Provider, Stage), timestamp);

    /// <summary>
    /// Resolves the effective TTS voice (C.4b — <c>VoiceByLanguage</c> resolution). Precedence: an explicit
    /// non-blank request voice wins (per-turn override); else the per-target-language map
    /// <c>VoiceByLanguage[targetLanguage]</c> (keyed by the lowercase code "en"/"es"); else the
    /// <c>options.Voice</c> default. Keeping it here (not in the orchestrator) keeps the cascade provider-agnostic.
    /// </summary>
    public static string ResolveVoice(string? requestVoice, LanguageCode targetLanguage, OpenAiTtsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(requestVoice))
        {
            return requestVoice;
        }

        var key = targetLanguage.ToString().ToLowerInvariant(); // LanguageCode.Es -> "es"
        if (options.VoiceByLanguage is not null
            && options.VoiceByLanguage.TryGetValue(key, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return options.Voice;
    }

    /// <summary>The response `Content-Type` header first (real value); else derive from `response_format`.</summary>
    public static string ResolveContentType(string? header, string responseFormat)
    {
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header;
        }

        return responseFormat?.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream",
        };
    }
}
