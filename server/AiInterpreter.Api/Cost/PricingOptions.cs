using System.Text.Json.Serialization;

namespace AiInterpreter.Api.Cost;

/// <summary>
/// Pricing configuration — the full ARCH-014 shape (A.4). FILE-loaded from PRICING_CONFIG_PATH via
/// <see cref="PricingLoader"/> + the shared <c>JsonDefaults.Options</c> (camelCase) — NOT an
/// appsettings section, so there is no SectionName. All rates are nullable decimals (estimates;
/// some are build-time-confirm placeholders). Consumed by B.5 (CostEstimator branches on basis).
/// </summary>
public sealed class PricingOptions
{
    public string? Version { get; set; }
    public string? Currency { get; set; }
    public string? Disclaimer { get; set; }
    public PricingProviders? Providers { get; set; }
}

public sealed class PricingProviders
{
    public DeepgramPricing? Deepgram { get; set; }
    public OpenAiPricing? Openai { get; set; }
}

public sealed class DeepgramPricing
{
    public DeepgramSttRates? Stt { get; set; }
}

public sealed class DeepgramSttRates
{
    public string? Model { get; set; }
    public string? Language { get; set; }
    public decimal? StreamingUsdPerAudioMinute { get; set; }
    public decimal? PreRecordedUsdPerAudioMinute { get; set; }
    public decimal? EffectiveUsdPerAudioMinute { get; set; }
    public string? Note { get; set; }
}

public sealed class OpenAiPricing
{
    public RealtimePricing? Realtime { get; set; }
    public Dictionary<string, TranslationModelRates>? Translation { get; set; }
    public Dictionary<string, TtsModelRates>? Tts { get; set; }
}

/// <summary>
/// Explicit (not a dictionary): in ARCH-014 the realtime block carries <c>estimatorNote</c> (a
/// string) as a sibling of the per-model entries, so it cannot bind as a
/// <c>Dictionary&lt;string, RealtimeModelRates&gt;</c> without restructuring the (verbatim) JSON.
/// </summary>
public sealed class RealtimePricing
{
    [JsonPropertyName("gpt-realtime")]
    public RealtimeModelRates? GptRealtime { get; set; }

    [JsonPropertyName("gpt-realtime-mini")]
    public RealtimeModelRates? GptRealtimeMini { get; set; }

    public string? EstimatorNote { get; set; }
}

public sealed class RealtimeModelRates
{
    public decimal? AudioInputUsdPerMillionTokens { get; set; }
    public decimal? CachedAudioInputUsdPerMillionTokens { get; set; }
    public decimal? AudioOutputUsdPerMillionTokens { get; set; }
}

public sealed class TranslationModelRates
{
    public decimal? InputUsdPerMillionTokens { get; set; }
    public decimal? OutputUsdPerMillionTokens { get; set; }
    public string? Note { get; set; }
}

public sealed class TtsModelRates
{
    // basis differs per model (audio_output_tokens vs characters), so all rate fields are nullable.
    public string? PricingBasis { get; set; }
    public decimal? TextInputUsdPerMillionTokens { get; set; }
    public decimal? AudioOutputUsdPerMillionTokens { get; set; }
    public decimal? ApproxUsdPerAudioMinute { get; set; }
    public decimal? UsdPerMillionCharacters { get; set; }
}
