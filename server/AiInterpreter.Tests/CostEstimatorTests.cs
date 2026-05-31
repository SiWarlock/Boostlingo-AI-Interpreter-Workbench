using AiInterpreter.Api.Common;
using AiInterpreter.Api.Config;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Realtime;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// B.5 — CostEstimator (ARCH-014). IMPORTANT tier (ARCH-020): deterministic per-basis estimates +
// degrade-don't-crash (lesson §3). Computed in decimal with no rounding, so every expected value is
// derived from the same rates the estimator reads (config/pricing.json, copied to test output).
//
// Pins the "0.0 configured rate != absent config" distinction (lesson §9): a PRESENT 0.0 rate estimates
// to 0.0, whereas genuinely-missing pricing degrades to unavailable — pinned via a synthetic 0.0-rate
// config (the real pricing.json no longer carries a 0.0 placeholder, removed in the 051 model fix).
public class CostEstimatorTests
{
    private const decimal Million = 1_000_000m;

    private static CostEstimator Estimator()
    {
        var pricing = PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        Assert.True(pricing.IsSuccess, pricing.Error);
        return new CostEstimator(pricing);
    }

    // GUARD (051): every translation model the ConfigService OFFERS must resolve to a pricing entry with a
    // non-zero rate — so a not-on-key / unpriced / 0.0-placeholder model can't ship silently again (the 051
    // bug: a configured model name not available on the user's key + a mini-tier 0.0 placeholder reading as
    // "free"). Couples the catalog <-> pricing <-> non-zero; robust to future catalog additions.
    [Fact]
    public void every_configured_translation_model_prices_to_a_nonzero_estimate()
    {
        var config = new ConfigService(
            Options.Create(new RealtimeOptions()),
            Options.Create(new OpenAiTranslationOptions()),
            Options.Create(new OpenAiTtsOptions()),
            Options.Create(new DeepgramOptions()),
            PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "pricing.json")));
        var models = config.GetConfig().Cascade.Translation.Models;
        var estimator = Estimator();

        Assert.NotEmpty(models);
        Assert.Contains(new OpenAiTranslationOptions().Model, models); // the configured default is one the catalog offers
        foreach (var model in models)
        {
            var r = estimator.EstimateTranslation(model, new CostUsage { InputTokens = 1000, OutputTokens = 1000 });
            Assert.True(r.IsSuccess, $"configured translation model '{model}' has no pricing entry");
            Assert.True(r.Value.EstimatedUsd > 0m, $"configured translation model '{model}' prices to 0 (placeholder?)");
        }
    }

    [Fact]
    public void deepgram_stt_audio_minute_basis_estimate()
    {
        var r = Estimator().EstimateStt(new CostUsage { AudioMinutes = 0.5m });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(0.0029m, r.Value.EstimatedUsd); // 0.5 min x 0.0058
        Assert.Equal("usd_per_audio_minute", r.Value.PricingBasis);
        Assert.Equal("nova-3", r.Value.Model);
        Assert.Equal("deepgram", r.Value.Provider);
        Assert.Equal("2026-05-31-payg-estimates", r.Value.PricingConfigVersion);
        Assert.Contains(r.Value.Assumptions, a => a.Contains("not provider invoice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void openai_translation_tokens_basis_estimate()
    {
        var r = Estimator().EstimateTranslation("gpt-5-nano", new CostUsage { InputTokens = 14, OutputTokens = 18 });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(14m / Million * 0.05m + 18m / Million * 0.40m, r.Value.EstimatedUsd);
        Assert.Equal("tokens", r.Value.PricingBasis);
        Assert.Equal("gpt-5-nano", r.Value.Model);
        Assert.Equal("openai", r.Value.Provider);
    }

    [Fact]
    public void openai_tts_audio_output_tokens_basis_estimate()
    {
        var r = Estimator().EstimateTts("gpt-4o-mini-tts", new CostUsage { AudioOutputTokens = 500, TextInputTokens = 20 });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(500m / Million * 12.0m + 20m / Million * 0.60m, r.Value.EstimatedUsd);
        Assert.Equal("audio_output_tokens", r.Value.PricingBasis);
        Assert.Equal("gpt-4o-mini-tts", r.Value.Model);
    }

    [Fact]
    public void openai_tts_audio_output_tokens_falls_back_to_approx_minute()
    {
        // No token counts available → fall back to approxUsdPerAudioMinute x minutes (Q4).
        var r = Estimator().EstimateTts("gpt-4o-mini-tts", new CostUsage { AudioMinutes = 0.25m });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(0.25m * 0.015m, r.Value.EstimatedUsd);
        Assert.Contains(r.Value.Assumptions, a => a.Contains("approx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void openai_tts_characters_basis_estimate()
    {
        var r = Estimator().EstimateTts("tts-1", new CostUsage { Characters = 200 });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(200m / Million * 15.0m, r.Value.EstimatedUsd);
        Assert.Equal("characters", r.Value.PricingBasis);
        Assert.Equal("tts-1", r.Value.Model);
    }

    [Fact]
    public void realtime_audio_seconds_converted_to_tokens()
    {
        var f = CostEstimator.RealtimeTokensPerAudioSecond;

        var r = Estimator().EstimateRealtime(
            "gpt-realtime", new CostUsage { AudioInputSeconds = 10m, AudioOutputSeconds = 5m });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(10m * f / Million * 32.0m + 5m * f / Million * 64.0m, r.Value.EstimatedUsd);
        Assert.Equal("gpt-realtime", r.Value.Model);

        // Cached-input rate honored when cached seconds are supplied (gpt-realtime has the cached rate).
        var cached = Estimator().EstimateRealtime(
            "gpt-realtime", new CostUsage { AudioInputSeconds = 10m, CachedAudioInputSeconds = 4m, AudioOutputSeconds = 5m });

        Assert.Equal(
            10m * f / Million * 32.0m + 4m * f / Million * 0.40m + 5m * f / Million * 64.0m,
            cached.Value.EstimatedUsd);
    }

    [Fact]
    public void cascade_turn_aggregates_three_stages()
    {
        var sttUsd = 0.5m * 0.0058m;
        var translationUsd = 14m / Million * 0.05m + 18m / Million * 0.40m;
        var ttsUsd = 200m / Million * 15.0m;

        var r = Estimator().EstimateCascadeTurn(
            translationModel: "gpt-5-nano",
            ttsModel: "tts-1",
            sttUsage: new CostUsage { AudioMinutes = 0.5m },
            translationUsage: new CostUsage { InputTokens = 14, OutputTokens = 18 },
            ttsUsage: new CostUsage { Characters = 200 },
            audioDurationMs: 30000);

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(sttUsd + translationUsd + ttsUsd, r.Value.EstimatedUsd);
        Assert.Equal("composite", r.Value.PricingBasis);
        Assert.Equal("cascade", r.Value.Provider);
        Assert.Equal("gpt-5-nano", r.Value.Model); // translation model = the cascade comparison axis
        Assert.Equal(sttUsd, r.Value.Units["sttUsd"]);
        Assert.Equal(translationUsd, r.Value.Units["translationUsd"]);
        Assert.Equal(ttsUsd, r.Value.Units["ttsUsd"]);
    }

    [Fact]
    public void cost_per_minute_divides_by_audio_duration()
    {
        var usage = new CostUsage { InputTokens = 1000, OutputTokens = 1000 };
        var usd = 1000m / Million * 0.05m + 1000m / Million * 0.40m;

        var r = Estimator().EstimateTranslation("gpt-5-nano", usage, audioDurationMs: 30000);
        Assert.Equal(usd, r.Value.EstimatedUsd);
        Assert.Equal(usd / (30000m / 60000m), r.Value.EstimatedUsdPerMinute); // 30s = 0.5 min

        // Zero duration → null (no divide-by-zero).
        var zero = Estimator().EstimateTranslation("gpt-5-nano", usage, audioDurationMs: 0);
        Assert.Null(zero.Value.EstimatedUsdPerMinute);
    }

    [Fact]
    public void missing_pricing_degrades_to_unavailable()
    {
        // Loader failure → all estimates degrade, no throw.
        var degraded = new CostEstimator(Result<PricingOptions>.Failure("no config"));
        var r = degraded.EstimateStt(new CostUsage { AudioMinutes = 0.5m });
        Assert.False(r.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));

        // Present pricing but an absent model also degrades (model genuinely missing from config).
        var absentModel = Estimator().EstimateTranslation("nonexistent-model", new CostUsage { InputTokens = 1, OutputTokens = 1 });
        Assert.False(absentModel.IsSuccess);

        // A composite degrades wholesale if ANY stage cannot be priced (no partial-garbage estimate).
        var oneStageMissing = Estimator().EstimateCascadeTurn(
            translationModel: "gpt-5-nano",
            ttsModel: "nonexistent-tts",
            sttUsage: new CostUsage { AudioMinutes = 0.5m },
            translationUsage: new CostUsage { InputTokens = 14, OutputTokens = 18 },
            ttsUsage: new CostUsage { Characters = 200 },
            audioDurationMs: 30000);
        Assert.False(oneStageMissing.IsSuccess);
    }

    [Fact]
    public void realtime_mini_cached_seconds_fall_back_to_full_input_rate()
    {
        // gpt-realtime-mini has NO cached-input rate. Cached seconds must still be billed (at the
        // full input rate), disclosed in Assumptions — never silently dropped or a crash.
        var f = CostEstimator.RealtimeTokensPerAudioSecond;
        var r = Estimator().EstimateRealtime(
            "gpt-realtime-mini",
            new CostUsage { AudioInputSeconds = 10m, CachedAudioInputSeconds = 4m, AudioOutputSeconds = 5m });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(
            10m * f / Million * 10.0m + 5m * f / Million * 20.0m + 4m * f / Million * 10.0m, // cached at full input rate
            r.Value.EstimatedUsd);
        Assert.Contains(r.Value.Assumptions, a => a.Contains("cached", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void usage_and_model_gaps_degrade_not_throw()
    {
        var est = Estimator();

        // Required usage absent → degrade (not a crash, not a $0 estimate).
        Assert.False(est.EstimateStt(new CostUsage()).IsSuccess);                 // no AudioMinutes
        Assert.False(est.EstimateTts("tts-1", new CostUsage()).IsSuccess);        // characters basis, no Characters
        Assert.False(est.EstimateRealtime("unknown-realtime", new CostUsage { AudioInputSeconds = 1m }).IsSuccess); // unknown model
    }

    [Fact]
    public void present_zero_configured_rate_estimates_to_zero_not_unavailable()
    {
        // Lesson §9: a PRESENT 0.0 rate is configured data → estimates to 0.0 (distinguished from an ABSENT
        // rate, which degrades to unavailable). Pinned via a synthetic 0.0-rate config — the real pricing.json
        // no longer carries a 0.0 placeholder (removed in the 051 model fix), so the behavior is pinned here
        // decoupled from the live config rather than re-introducing a placeholder.
        var pricing = Result<PricingOptions>.Success(new PricingOptions
        {
            Version = "test",
            Providers = new PricingProviders
            {
                Openai = new OpenAiPricing
                {
                    Translation = new Dictionary<string, TranslationModelRates>
                    {
                        ["zero-rate-model"] = new() { InputUsdPerMillionTokens = 0.0m, OutputUsdPerMillionTokens = 0.0m },
                    },
                },
            },
        });

        var r = new CostEstimator(pricing).EstimateTranslation(
            "zero-rate-model", new CostUsage { InputTokens = 1000, OutputTokens = 1000 });

        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(0.0m, r.Value.EstimatedUsd); // present 0.0 rate → estimates to 0, NOT unavailable
        Assert.Equal("zero-rate-model", r.Value.Model);
    }
}
