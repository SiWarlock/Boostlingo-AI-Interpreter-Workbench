using System.Text.RegularExpressions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Realtime;
using Microsoft.Extensions.Configuration;

namespace AiInterpreter.Tests;

// Pins the Options<->config-section contract: ARCH-012 enumerated fields + defaults,
// ARCH-028 env-var list, ARCH-019 (standard keys backend-only — no secret in committed config).
// Binding uses Bind(new T()) — the same semantics services.Configure<T>(section) uses in
// production (wired in A.5) — so type-level defaults apply when a section key is absent.
public class OptionsBindingTests
{
    private static T Bind<T>(IDictionary<string, string?> values, string section)
        where T : new()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var instance = new T();
        config.GetSection(section).Bind(instance);
        return instance;
    }

    [Fact]
    public void deepgram_options_bind_from_section()
    {
        var values = new Dictionary<string, string?>
        {
            ["Deepgram:ApiKey"] = "deepgram-test-key",
            ["Deepgram:BaseUrl"] = "https://example.test",
            ["Deepgram:WebSocketUrl"] = "wss://example.test/listen",
            ["Deepgram:Model"] = "nova-2",
            ["Deepgram:Language"] = "en",
            ["Deepgram:SmartFormat"] = "false",
            ["Deepgram:Encoding"] = "opus",
            ["Deepgram:SampleRate"] = "16000",
            ["Deepgram:Channels"] = "2",
            ["Deepgram:InterimResults"] = "false",
            ["Deepgram:UtteranceEndMs"] = "2000",
            ["Deepgram:TimeoutSeconds"] = "45",
        };

        var opts = Bind<DeepgramOptions>(values, DeepgramOptions.SectionName);

        Assert.Equal("deepgram-test-key", opts.ApiKey);
        Assert.Equal("https://example.test", opts.BaseUrl);
        Assert.Equal("wss://example.test/listen", opts.WebSocketUrl);
        Assert.Equal("nova-2", opts.Model);
        Assert.Equal("en", opts.Language);
        Assert.False(opts.SmartFormat);
        Assert.Equal("opus", opts.Encoding);
        Assert.Equal(16000, opts.SampleRate);
        Assert.Equal(2, opts.Channels);
        Assert.False(opts.InterimResults);
        Assert.Equal(2000, opts.UtteranceEndMs);
        Assert.Equal(45, opts.TimeoutSeconds);
    }

    [Fact]
    public void deepgram_options_defaults_when_absent()
    {
        var opts = Bind<DeepgramOptions>(new Dictionary<string, string?>(), DeepgramOptions.SectionName);

        Assert.Equal("nova-3", opts.Model);
        Assert.Equal("multi", opts.Language);
        Assert.True(opts.SmartFormat);
        Assert.Equal("linear16", opts.Encoding);
        Assert.Equal(48000, opts.SampleRate);
        Assert.Equal(1, opts.Channels);
        Assert.True(opts.InterimResults);
        Assert.Equal(1000, opts.UtteranceEndMs);
        Assert.Equal(30, opts.TimeoutSeconds);
        Assert.Equal("https://api.deepgram.com", opts.BaseUrl);
        Assert.Equal("wss://api.deepgram.com/v1/listen", opts.WebSocketUrl);
        Assert.Null(opts.ApiKey);
    }

    [Fact]
    public void openai_translation_options_defaults()
    {
        var opts = Bind<OpenAiTranslationOptions>(
            new Dictionary<string, string?>(),
            OpenAiTranslationOptions.SectionName);

        Assert.Equal("minimal", opts.ReasoningEffort);
        Assert.Equal("low", opts.Verbosity);
        Assert.True(opts.Stream);
        Assert.Equal("gpt-5-nano", opts.Model);
        Assert.Null(opts.ApiKey);
    }

    [Fact]
    public void openai_tts_options_defaults()
    {
        var values = new Dictionary<string, string?> { ["OpenAiTts:Voice"] = "verse" };

        var opts = Bind<OpenAiTtsOptions>(values, OpenAiTtsOptions.SectionName);

        Assert.Equal("mp3", opts.ResponseFormat);
        Assert.True(opts.Stream);
        Assert.Equal("verse", opts.Voice); // binds from section
        Assert.Equal("gpt-4o-mini-tts", opts.Model); // default
        Assert.Equal(30, opts.TimeoutSeconds);
        Assert.Null(opts.Instructions);
        Assert.Null(opts.VoiceByLanguage);
        Assert.Null(opts.ApiKey);
    }

    [Fact]
    public void realtime_options_defaults()
    {
        var opts = Bind<RealtimeOptions>(new Dictionary<string, string?>(), RealtimeOptions.SectionName);

        Assert.Equal(600, opts.ExpirySeconds);
        Assert.Equal("gpt-4o-transcribe", opts.TranscriptionModel);
        Assert.Equal("gpt-realtime", opts.Model);
        Assert.Equal("alloy", opts.Voice);
        Assert.Equal(10, opts.TokenTimeoutSeconds);
        Assert.Null(opts.ApiKey);
    }

    [Fact]
    public void appsettings_carries_no_secret_values()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        // ARCH-019: standard provider keys are backend-only (env-supplied) and must never appear
        // in committed config. Scan EVERY value in the committed appsettings.json for obvious
        // secret shapes — a structure-independent guard that also defends future edits.
        foreach (var (key, value) in config.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            Assert.False(
                LooksLikeSecret(value),
                $"appsettings.json must not carry a secret-looking value at '{key}'");
        }
    }

    // sk-… (OpenAI standard key), ek_… (ephemeral Realtime credential), dg_… (current Deepgram
    // key format), or a 40-char hex string (legacy Deepgram key format).
    private static bool LooksLikeSecret(string value) =>
        value.StartsWith("sk-", StringComparison.Ordinal)
        || value.StartsWith("ek_", StringComparison.Ordinal)
        || value.StartsWith("dg_", StringComparison.Ordinal)
        || Regex.IsMatch(value, "^[0-9a-fA-F]{40}$");
}
