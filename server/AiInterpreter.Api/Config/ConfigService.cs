using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Common;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Config;

/// <summary>
/// Builds the <see cref="ConfigResponse"/> from provider-key PRESENCE only (safety invariant #1 /
/// ARCH-019): <c>configured = !IsNullOrWhiteSpace(ApiKey)</c> — a key VALUE never enters the response.
/// The OpenAI key gates realtime/translation/tts; the Deepgram key gates stt (mirrors the Program.cs
/// flat-env fan-out). Selectable model catalogs are hardcoded (fixed by ARCH-010/ARCH-014).
/// </summary>
public interface IConfigService
{
    ConfigResponse GetConfig();
}

/// <inheritdoc cref="IConfigService"/>
public sealed class ConfigService : IConfigService
{
    private static readonly string[] RealtimeModels = ["gpt-realtime", "gpt-realtime-mini"];
    private static readonly string[] TranslationModels = ["gpt-5-nano", "gpt-5-mini"];
    private static readonly string[] Languages = ["en", "es"];

    private readonly RealtimeOptions _realtime;
    private readonly OpenAiTranslationOptions _translation;
    private readonly OpenAiTtsOptions _tts;
    private readonly DeepgramOptions _deepgram;
    private readonly Result<PricingOptions> _pricing;

    public ConfigService(
        IOptions<RealtimeOptions> realtime,
        IOptions<OpenAiTranslationOptions> translation,
        IOptions<OpenAiTtsOptions> tts,
        IOptions<DeepgramOptions> deepgram,
        Result<PricingOptions> pricing)
    {
        _realtime = realtime.Value;
        _translation = translation.Value;
        _tts = tts.Value;
        _deepgram = deepgram.Value;
        _pricing = pricing;
    }

    public ConfigResponse GetConfig() => new(
        // Defensive copies of the static catalogs (collection-expression spread) so a caller can't
        // mutate the shared backing arrays through the returned response.
        Realtime: new RealtimeCapability(Configured: HasKey(_realtime.ApiKey), Models: [.. RealtimeModels]),
        Cascade: new CascadeCapability(
            Stt: new SttCapability(HasKey(_deepgram.ApiKey), "deepgram", _deepgram.Model),
            Translation: new TranslationCapability(HasKey(_translation.ApiKey), "openai", [.. TranslationModels]),
            Tts: new TtsCapability(HasKey(_tts.ApiKey), "openai", _tts.Model)),
        Languages: [.. Languages],
        // Degrade-safe: the pricing loader already returns Result.Failure on a missing/invalid file
        // (ARCH-018); surface a safe fallback string rather than throwing.
        PricingConfigVersion: _pricing.IsSuccess ? _pricing.Value.Version ?? "unavailable" : "unavailable");

    private static bool HasKey(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);
}
