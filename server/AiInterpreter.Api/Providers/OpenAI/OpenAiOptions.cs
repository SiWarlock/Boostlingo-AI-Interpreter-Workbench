namespace AiInterpreter.Api.Providers.OpenAI;

/// <summary>
/// OpenAI translation (Responses API) provider configuration (ARCH-012 enumerated). Bound from
/// the "OpenAiTranslation" config section via <c>IOptions</c> (wired in A.5). <see cref="ApiKey"/>
/// is the standard OpenAI key — backend-only (ARCH-019), supplied via <c>OPENAI_API_KEY</c>.
/// </summary>
public sealed class OpenAiTranslationOptions
{
    public const string SectionName = "OpenAiTranslation";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-5-nano";
    public string ReasoningEffort { get; set; } = "minimal";
    public string Verbosity { get; set; } = "low";
    public bool Stream { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// OpenAI TTS provider configuration (ARCH-012 enumerated). Bound from the "OpenAiTts" config
/// section via <c>IOptions</c> (wired in A.5). <see cref="ApiKey"/> is the standard OpenAI key —
/// backend-only (ARCH-019), supplied via <c>OPENAI_API_KEY</c>.
/// </summary>
public sealed class OpenAiTtsOptions
{
    public const string SectionName = "OpenAiTts";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4o-mini-tts";
    public string Voice { get; set; } = "alloy";

    /// <summary>
    /// Optional per-language voice override map (e.g. "es" → a Spanish-leaning voice), keyed by the
    /// lowercase language code. Part of the ARCH-012 enumeration; resolved to the effective voice by
    /// <see cref="OpenAiTtsMapping.ResolveVoice"/> (C.4b) — an explicit request voice still wins over it.
    /// </summary>
    public Dictionary<string, string>? VoiceByLanguage { get; set; }

    public string ResponseFormat { get; set; } = "mp3";
    public bool Stream { get; set; } = true;
    public string? Instructions { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
