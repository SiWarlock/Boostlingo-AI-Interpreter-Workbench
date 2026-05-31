using System.Net;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Config;
using AiInterpreter.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiInterpreter.Tests;

// B.9b — ConfigController + ConfigService (ARCH-009 GET /api/config / ARCH-019 invariant #1). SAFETY
// slice: test 2 is the centerpiece — capability flags derive from key PRESENCE only; a key VALUE must
// never appear in the response (the invariant-#1 drift defense, same sentinel-scan pattern as B.7a/B.8).
//
// In the HostEnv collection: these tests set process-wide env vars + boot WebApplicationFactory, so
// they must serialize against the other host/env tests (B.9a, HostIntegrationTests).
[Collection("HostEnv")]
public class ConfigEndpointTests
{
    // Sets the provider keys (+ a deterministic pricing path) the host reads at build, fetches
    // /api/config from a fresh host, and restores the env. (Env is read at builder creation.)
    private static async Task<(HttpStatusCode Status, string Body)> GetConfig(string? openAiKey, string? deepgramKey)
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", openAiKey);
        Environment.SetEnvironmentVariable("DEEPGRAM_API_KEY", deepgramKey);
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var resp = await factory.CreateClient().GetAsync("/api/config");
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPGRAM_API_KEY", null);
            Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", null);
        }
    }

    // 1 — keys present -> every stage configured=true; ARCH-009 shape present.
    [Fact]
    public async Task config_returns_configured_true_when_keys_present()
    {
        var (status, body) = await GetConfig("sk-real-openai", "dg-real-key");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("realtime").GetProperty("configured").GetBoolean());
        var cascade = root.GetProperty("cascade");
        Assert.True(cascade.GetProperty("stt").GetProperty("configured").GetBoolean());
        Assert.True(cascade.GetProperty("translation").GetProperty("configured").GetBoolean());
        Assert.True(cascade.GetProperty("tts").GetProperty("configured").GetBoolean());
        Assert.True(root.TryGetProperty("languages", out _));
        // Value assertion (not existence-only): proves the nominal load, not the degrade fallback.
        Assert.NotEqual("unavailable", root.GetProperty("pricingConfigVersion").GetString());
    }

    // 2 (SAFETY #1) — a sentinel key VALUE never appears in the response; only flags + model names do.
    [Fact]
    public async Task config_response_excludes_secret_values()
    {
        var (_, body) = await GetConfig("sk-SENTINEL-CONFIG", "dg-SENTINEL");

        Assert.DoesNotContain("sk-SENTINEL-CONFIG", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dg-SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dg-", body, StringComparison.Ordinal);  // parity with the sk- prefix scan
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
        // Not trivially empty — the legitimate capability data is present.
        Assert.Contains("gpt-realtime", body, StringComparison.Ordinal);
        Assert.Contains("\"configured\"", body, StringComparison.Ordinal);
    }

    // 3 — a stage whose key is absent reports configured=false (ARCH-020 named case).
    [Fact]
    public async Task config_returns_configured_false_when_key_absent()
    {
        var (_, body) = await GetConfig(openAiKey: null, deepgramKey: null);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("realtime").GetProperty("configured").GetBoolean());
        var cascade = root.GetProperty("cascade");
        Assert.False(cascade.GetProperty("stt").GetProperty("configured").GetBoolean());
        Assert.False(cascade.GetProperty("translation").GetProperty("configured").GetBoolean());
        Assert.False(cascade.GetProperty("tts").GetProperty("configured").GetBoolean());
    }

    // 4 — hardcoded selectable catalogs + single configured models + languages (ARCH-010/014/002).
    [Fact]
    public async Task config_model_catalogs_and_languages()
    {
        var (_, body) = await GetConfig("sk-real-openai", "dg-real-key");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "gpt-realtime", "gpt-realtime-mini" },
            root.GetProperty("realtime").GetProperty("models").EnumerateArray().Select(e => e.GetString()).ToArray());

        var cascade = root.GetProperty("cascade");
        Assert.Equal(
            new[] { "gpt-5-nano", "gpt-5-mini" },
            cascade.GetProperty("translation").GetProperty("models").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("deepgram", cascade.GetProperty("stt").GetProperty("provider").GetString());
        Assert.Equal("nova-3", cascade.GetProperty("stt").GetProperty("model").GetString());
        Assert.Equal("openai", cascade.GetProperty("tts").GetProperty("provider").GetString());
        Assert.Equal("gpt-4o-mini-tts", cascade.GetProperty("tts").GetProperty("model").GetString());

        Assert.Equal(
            new[] { "en", "es" },
            root.GetProperty("languages").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // 5 — pricingConfigVersion reflects the loaded pricing version.
    [Fact]
    public async Task config_pricing_version_present()
    {
        var (_, body) = await GetConfig("sk-real-openai", "dg-real-key");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("2026-05-31-payg-estimates", doc.RootElement.GetProperty("pricingConfigVersion").GetString());
    }

    // 6 (relocated B.9a boundary proof) — a real endpoint whose service throws (with a secret) is caught
    // by the global handler and returned as a sanitized UiError (500, no stack/secret). Proves
    // app.UseExceptionHandler() catches on the production path.
    [Fact]
    public async Task unhandled_endpoint_exception_returns_sanitized_uierror()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IConfigService>();
                s.AddSingleton<IConfigService, ThrowingConfigService>();
            }));

        var resp = await factory.CreateClient().GetAsync("/api/config");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.DoesNotContain("sk-LEAK-123", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Evil.Method", body, StringComparison.Ordinal);
        Assert.DoesNotContain("boom with secret", body, StringComparison.Ordinal);
        var ui = JsonSerializer.Deserialize<UiError>(body, JsonDefaults.Options);
        Assert.NotNull(ui);
        Assert.Equal("internal.error", ui!.Code);
    }

    private sealed class ThrowingConfigService : IConfigService
    {
        public ConfigResponse GetConfig() =>
            throw new InvalidOperationException("boom with secret sk-LEAK-123\n  at Evil.Method()");
    }
}
