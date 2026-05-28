using AiInterpreter.Api.Cost;

namespace AiInterpreter.Tests;

// Pins the A.4 pricing data + load/degrade contract (ARCH-014 shape + ARCH-018 degrade-don't-crash).
// pricing.json is FILE-loaded via PricingLoader (not an appsettings section) and deserialized with
// the shared JsonDefaults.Options. The committed config/pricing.json is copied to the test output.
public class PricingConfigTests
{
    private static string CommittedPricingPath => Path.Combine(AppContext.BaseDirectory, "pricing.json");

    [Fact]
    public void loads_arch014_pricing_from_valid_file()
    {
        var result = PricingLoader.Load(CommittedPricingPath);

        Assert.True(result.IsSuccess, result.Error);
        var p = result.Value;
        Assert.Equal("2026-05-28-payg-estimates", p.Version);
        Assert.Equal("USD", p.Currency);
        Assert.False(string.IsNullOrWhiteSpace(p.Disclaimer));

        var stt = p.Providers!.Deepgram!.Stt!;
        Assert.Equal(0.0058m, stt.StreamingUsdPerAudioMinute);
        Assert.Equal(0.0092m, stt.PreRecordedUsdPerAudioMinute);
        Assert.Equal(0.0058m, stt.EffectiveUsdPerAudioMinute);

        var realtime = p.Providers.Openai!.Realtime!;
        Assert.Equal(32.0m, realtime.GptRealtime!.AudioInputUsdPerMillionTokens);
        Assert.Equal(0.40m, realtime.GptRealtime.CachedAudioInputUsdPerMillionTokens);
        Assert.Equal(64.0m, realtime.GptRealtime.AudioOutputUsdPerMillionTokens);
        Assert.Equal(10.0m, realtime.GptRealtimeMini!.AudioInputUsdPerMillionTokens);
        Assert.Equal(20.0m, realtime.GptRealtimeMini.AudioOutputUsdPerMillionTokens);
        Assert.Null(realtime.GptRealtimeMini.CachedAudioInputUsdPerMillionTokens); // mini lacks the cached rate
        Assert.False(string.IsNullOrWhiteSpace(realtime.EstimatorNote));

        var nano = p.Providers.Openai.Translation!["gpt-5.4-nano"];
        Assert.Equal(0.20m, nano.InputUsdPerMillionTokens);
        Assert.Equal(1.25m, nano.OutputUsdPerMillionTokens);

        Assert.Equal("audio_output_tokens", p.Providers.Openai.Tts!["gpt-4o-mini-tts"].PricingBasis);
        var tts1 = p.Providers.Openai.Tts["tts-1"];
        Assert.Equal("characters", tts1.PricingBasis);
        Assert.Equal(15.0m, tts1.UsdPerMillionCharacters);
    }

    [Fact]
    public void missing_file_degrades_not_throws()
    {
        var result = PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "no-such-pricing.json"));

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void invalid_json_degrades_not_throws()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"pricing-invalid-{Guid.NewGuid():N}.json");
        File.WriteAllText(temp, "{ this is not valid json ");
        try
        {
            var result = PricingLoader.Load(temp);

            Assert.False(result.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void gpt_5_4_mini_placeholder_present()
    {
        var result = PricingLoader.Load(CommittedPricingPath);

        Assert.True(result.IsSuccess, result.Error);
        var mini = result.Value.Providers!.Openai!.Translation!["gpt-5.4-mini"];
        Assert.Equal(0.0m, mini.InputUsdPerMillionTokens);
        Assert.Equal(0.0m, mini.OutputUsdPerMillionTokens);
        Assert.NotNull(mini.Note);
        Assert.Contains("CONFIRM", mini.Note!);
    }
}
