using System.Globalization;
using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Realtime;

/// <summary>
/// The pure, deterministic surface of <see cref="RealtimeClientSecretService"/> (E.1) — the GA
/// <c>client_secrets</c> request-body builder, the interpreter-instructions render, the response parse
/// (<c>value</c> + <c>expires_at</c>), the epoch→ISO-8601 format, and the exception→<see cref="ProviderError"/>
/// projection. The service drives the network; these helpers hold the logic (lesson §18/§20 applied to a
/// non-streaming upstream call). <c>internal</c> + InternalsVisibleTo keeps them unit-reachable.
/// </summary>
internal static class RealtimeClientSecretMapping
{
    private const string Provider = "openai";
    private const string Stage = "realtime";

    // ARCH-010 default faithful-interpreter prompt; {source}/{target} fill from the turn direction.
    private const string DefaultInstructionsTemplate =
        "You are a faithful realtime interpreter. Render the speaker's words from {source} to {target}. " +
        "Speak only the translation — no commentary, no preamble.";

    // J.2 (Phase J) — the bidirectional interpreter prompt (detect EN/ES → render the OTHER). No {source}/
    // {target} placeholders: gpt-realtime detects the spoken language itself. Const-only (no RealtimeOptions
    // override yet — YAGNI; the one-direction InstructionsTemplate override exists but nothing sets it).
    private const string DefaultBidirectionalInstructionsTemplate =
        "You are a faithful realtime interpreter. The speaker may talk in English or Spanish. Detect which " +
        "language they are speaking and render their words in the OTHER language. Speak only the translation — " +
        "no commentary, no preamble.";

    /// <summary>The minted ephemeral secret + its expiry, parsed from the GA response.</summary>
    internal readonly record struct RealtimeSecret(string Value, long ExpiresAtEpoch);

    /// <summary>
    /// Builds the ARCH-010 GA <c>client_secrets</c> request body. Property names are snake_case identifiers
    /// (<c>expires_after</c>/<c>output_modalities</c>/<c>turn_detection</c>) so the shared camelCase policy —
    /// which only lowercases the first char — leaves them intact. <c>turn_detection</c> is an EXPLICIT null
    /// (VAD off, manual turns) — <see cref="JsonDefaults"/> writes nulls explicitly (no WhenWritingNull).
    /// </summary>
    public static object BuildRequestBody(LanguageDirection direction, string resolvedModel, RealtimeOptions options, bool bidirectional = false) => new
    {
        expires_after = new { anchor = "created_at", seconds = options.ExpirySeconds },
        session = new
        {
            type = "realtime",
            model = resolvedModel,
            instructions = RenderInstructions(options.InstructionsTemplate, direction, bidirectional),
            output_modalities = new[] { "audio" },
            audio = new
            {
                input = new
                {
                    turn_detection = (object?)null,
                    transcription = new { model = options.TranscriptionModel },
                },
                output = new { voice = options.Voice },
            },
        },
    };

    /// <summary>
    /// Renders the interpreter instructions. J.2: <paramref name="bidirectional"/> true ⇒ the detect-EN/ES →
    /// render-the-OTHER prompt (const; <paramref name="template"/>/<paramref name="direction"/> unused — the
    /// model self-detects). False ⇒ the one-direction render ({source}/{target} filled from the direction;
    /// default prompt when the template is null/blank), byte-identical to before this slice.
    /// </summary>
    public static string RenderInstructions(string? template, LanguageDirection direction, bool bidirectional = false)
    {
        if (bidirectional)
        {
            return DefaultBidirectionalInstructionsTemplate;
        }

        return (string.IsNullOrWhiteSpace(template) ? DefaultInstructionsTemplate : template)
            .Replace("{source}", Name(direction.Source), StringComparison.Ordinal)
            .Replace("{target}", Name(direction.Target), StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses the GA <c>client_secrets</c> 200 body. Tolerant of BOTH the GA top-level shape
    /// (<c>{ value, expires_at }</c> — primary) AND the legacy <c>/v1/realtime/sessions</c> nested shape
    /// (<c>{ client_secret: { value, expires_at } }</c> — fallback insurance, lesson §18/§20); returns null
    /// when neither yields a value (malformed → the service maps to <c>realtime.unknown</c>, never fabricates).
    /// </summary>
    public static RealtimeSecret? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (TryReadSecret(root, out var topLevel))
            {
                return topLevel;
            }

            if (root.TryGetProperty("client_secret", out var nested) &&
                nested.ValueKind == JsonValueKind.Object &&
                TryReadSecret(nested, out var nestedSecret))
            {
                return nestedSecret;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Formats a Unix-epoch-seconds expiry as a round-trip ISO-8601 UTC string.</summary>
    public static string ToIso8601(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Projects a transport/HTTP exception → <see cref="ProviderError"/> via the single ARCH-012 mapper.</summary>
    public static ProviderError ToFailed(Exception exception) =>
        ProviderErrorMapper.Map(exception, Provider, Stage);

    private static bool TryReadSecret(JsonElement element, out RealtimeSecret secret)
    {
        secret = default;
        if (element.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.String &&
            element.TryGetProperty("expires_at", out var expiresEl) && expiresEl.TryGetInt64(out var epoch))
        {
            var value = valueEl.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                secret = new RealtimeSecret(value, epoch);
                return true;
            }
        }

        return false;
    }

    // Fallback yields the enum member name (e.g. "En"), not a display name — update the switch when a
    // LanguageCode is added (mirrors OpenAiTranslationMapping.Name; EN/ES is the ARCH-002 MVP pair).
    private static string Name(LanguageCode code) => code switch
    {
        LanguageCode.En => "English",
        LanguageCode.Es => "Spanish",
        _ => code.ToString(),
    };
}
