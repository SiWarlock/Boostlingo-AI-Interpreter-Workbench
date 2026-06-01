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

    // Normalized rejection codes (ARCH-018). invalid_audio = the SECURITY encoding allowlist (audio
    // content unacceptable); invalid_request = a malformed/missing-field request frame (robustness).
    private const string InvalidAudioCode = "cascade.invalid_audio";
    private const string InvalidRequestCode = "cascade.invalid_request";

    // Upper bound on every client-supplied start-frame string (ARCH-019 / lesson §16). sessionId/turnId are
    // echoed (turnId in every `done`) + used as store keys; model/voice cross to the provider. Server ids are
    // ~16 chars; 256 is generous headroom while bounding reflection/allocation at the WS boundary.
    private const int MaxFieldLength = 256;

    /// <summary>Result of parsing the WS <c>start</c> frame: either valid params or a rejection error (never both).</summary>
    public readonly record struct StartParse(CascadeStartParams? Params, ProviderError? Error);

    /// <summary>
    /// Parses + validates the <c>start</c> frame BEFORE building <see cref="CascadeStartParams"/>. Rejections,
    /// in order: malformed JSON → <c>cascade.invalid_request</c>; an encoding outside the SECURITY allowlist →
    /// <c>cascade.invalid_audio</c> (header-injection guard — the orchestrator interpolates the encoding into a
    /// provider content-type); a missing/blank <c>sessionId</c>/<c>turnId</c>, missing direction, or
    /// <c>sampleRate &lt;= 0</c> → <c>cascade.invalid_request</c>.
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
            return Invalid(InvalidRequestCode, "The start frame was malformed.");
        }

        if (dto is null)
        {
            return Invalid(InvalidRequestCode, "The start frame was malformed.");
        }

        // SECURITY allowlist (C.4a, ARCH-019) — kept first + distinct from the field checks below.
        if (string.IsNullOrWhiteSpace(dto.Encoding) || !AllowedEncodings.Contains(dto.Encoding))
        {
            return Invalid(InvalidAudioCode, "The audio encoding is not supported.");
        }

        // Field validation (C.4b, ARCH-019) — reject missing/blank ids + non-positive sample rate.
        if (dto.Direction is null
            || string.IsNullOrWhiteSpace(dto.SessionId)
            || string.IsNullOrWhiteSpace(dto.TurnId)
            || dto.SampleRate <= 0)
        {
            return Invalid(InvalidRequestCode, "The start frame is missing or has invalid required fields.");
        }

        // Length caps (ARCH-019 / lesson §16) — bound every client-supplied string at the WS boundary.
        if (dto.SessionId.Length > MaxFieldLength
            || dto.TurnId.Length > MaxFieldLength
            || (dto.TranslationModel?.Length ?? 0) > MaxFieldLength
            || (dto.TtsVoice?.Length ?? 0) > MaxFieldLength)
        {
            return Invalid(InvalidRequestCode, "A start frame field exceeds the maximum length.");
        }

        var p = new CascadeStartParams(
            dto.SessionId,
            dto.TurnId,
            new LanguageDirection(dto.Direction.Source, dto.Direction.Target),
            dto.Encoding,
            dto.SampleRate,
            dto.TranslationModel ?? string.Empty,
            dto.TtsVoice ?? string.Empty,
            AutoVad: dto.AutoVad,
            Bidirectional: dto.Bidirectional);

        return new StartParse(p, null);
    }

    private static StartParse Invalid(string code, string message) => new(
        null,
        new ProviderError("cascade", "cascade", code, message, Retryable: false));

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
        string? TtsVoice,
        // I.1 — auto-VAD per-turn flag (bool; missing ⇒ false). No validation needed (closed value domain).
        bool AutoVad = false,
        // J.1 — bidirectional per-turn flag (bool; missing ⇒ false). No validation needed (closed value domain).
        bool Bidirectional = false);

    private sealed record DirectionDto(LanguageCode Source, LanguageCode Target);
}
