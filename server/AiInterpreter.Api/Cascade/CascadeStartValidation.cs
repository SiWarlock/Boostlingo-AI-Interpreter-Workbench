using System.Text.Json;
using System.Text.Json.Serialization;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// SECURITY boundary (C.4a, ARCH-019): parses + validates the ARCH-009 WS <c>start</c> frame before any
/// orchestration. The orchestrator interpolates <c>Encoding</c> into a content-type (<c>audio/{encoding}</c>),
/// so an unvalidated value is a header-injection surface at the real provider — the WS boundary (NOT the
/// orchestrator) owns the closed <c>encoding</c> allowlist. Pure + unit-TDD'd; kept in its own file so the
/// safety validation commits in isolation.
/// </summary>
internal static class CascadeStartValidation
{
    // Closed allowlist — raw PCM only. Anything else (recorded containers, injection strings) is rejected.
    private static readonly HashSet<string> AllowedEncodings = new(StringComparer.Ordinal) { "linear16", "pcm" };

    /// <summary>Result of parsing the WS <c>start</c> frame: either valid params or a rejection error (never both).</summary>
    public readonly record struct StartParse(CascadeStartParams? Params, ProviderError? Error);

    /// <summary>
    /// Parses + validates the <c>start</c> frame. Rejects an encoding outside the closed allowlist (or malformed
    /// JSON) with <c>cascade.invalid_audio</c> BEFORE building <see cref="CascadeStartParams"/>.
    /// </summary>
    public static StartParse ParseStart(string json)
    {
        StartFrameDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StartFrameDto>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return Invalid();
        }

        if (dto?.Direction is null || string.IsNullOrWhiteSpace(dto.Encoding) || !AllowedEncodings.Contains(dto.Encoding))
        {
            return Invalid();
        }

        var p = new CascadeStartParams(
            dto.SessionId ?? string.Empty,
            dto.TurnId ?? string.Empty,
            new LanguageDirection(dto.Direction.Source, dto.Direction.Target),
            dto.Encoding,
            dto.SampleRate,
            dto.TranslationModel ?? string.Empty,
            dto.TtsVoice ?? string.Empty);

        return new StartParse(p, null);
    }

    private static StartParse Invalid() => new(
        null,
        new ProviderError("cascade", "cascade", "cascade.invalid_audio", "The audio encoding is not supported.", Retryable: false));

    // The ARCH-009 `start` frame (server-side view); deserialized via JsonDefaults (camelCase + enum-as-string,
    // so "en"/"es" -> LanguageCode). Direction is a nested {source,target}.
    private sealed record StartFrameDto(
        [property: JsonPropertyName("type")] string? Type,
        string? SessionId,
        string? TurnId,
        DirectionDto? Direction,
        string? Encoding,
        int SampleRate,
        string? TranslationModel,
        string? TtsVoice);

    private sealed record DirectionDto(LanguageCode Source, LanguageCode Target);
}
