using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Providers.OpenAI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiInterpreter.Tests;

// G.4-BE — POST /api/dev/tts wire tests (WebApplicationFactory). The dev-only synthesis route the soak-harness
// fetches to cache the scripted EN/ES audio (the OpenAI key never reaches the browser — invariant #1). Pins:
// Development-only mapping (404 outside), the raw-bytes 200 body, a sanitized UiError envelope on failure
// (invariant #4), the 400 over-cap path, and that synthesis writes no session file (invariant #3). Fake providers.
public sealed class DevTtsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DevTtsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // Development + fake providers (no real key in the test host → FakeTtsProvider).
    private HttpClient CreateClient(Action<IWebHostBuilder>? extra = null) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("USE_FAKE_PROVIDERS", "true");
            extra?.Invoke(b);
        }).CreateClient();

    [Fact]
    public async Task endpoint_synthesizes_audio_in_development()
    {
        using var client = CreateClient();

        var resp = await client.PostAsJsonAsync("/api/dev/tts", new { text = "hello", language = "en" });

        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes); // fake → 3 chunks of [1,2,3]
    }

    // Dev-only surface: the route is NEVER mapped outside Development, so the synth surface stays out of the
    // demo/production build (mirrors the Swagger Development-gating; ARCH-019 surface-minimization).
    [Fact]
    public async Task endpoint_not_mapped_in_production()
    {
        using var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("USE_FAKE_PROVIDERS", "true");
        }).CreateClient();

        var resp = await client.PostAsJsonAsync("/api/dev/tts", new { text = "hello", language = "en" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // A provider failure → a sanitized UiError envelope (the internal Provider identity is dropped; no raw
    // payload/stack — invariant #4). The erroring fake's ProviderError has no HTTP status → defaults to 502.
    [Fact]
    public async Task endpoint_provider_failure_returns_sanitized_uierror()
    {
        using var client = CreateClient(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITtsProvider>();
            s.AddSingleton<ITtsProvider>(_ => new FakeTtsProvider(FakeTtsBehavior.Error));
        }));

        var resp = await client.PostAsJsonAsync("/api/dev/tts", new { text = "hello", language = "en" });

        Assert.False(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tts.upstream_unavailable", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("provider", out _)); // UiError drops the internal provider identity
    }

    // An over-cap input is rejected 400 (the CapExceeded path) BEFORE any provider call — clean invalid_request.
    [Fact]
    public async Task endpoint_over_cap_returns_400()
    {
        using var client = CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/dev/tts", new { text = new string('a', OpenAiTtsMapping.MaxInputChars + 1), language = "en" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tts.invalid_request", body.GetProperty("code").GetString());
    }

    // Stateless (invariant #3, §28 precedent): a synth call writes NO file under SESSION_DATA_DIR — synthesis
    // persists nothing (no SessionStore/writer touched).
    [Fact]
    public async Task endpoint_writes_no_session_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiw-dev-tts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var client = CreateClient(b => b.UseSetting("SESSION_DATA_DIR", dir));

            var resp = await client.PostAsJsonAsync("/api/dev/tts", new { text = "hello", language = "en" });
            resp.EnsureSuccessStatusCode();

            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
