using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiInterpreter.Api.Providers.Deepgram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// A.5 convergence/wiring slice — HTTP integration tests (WebApplicationFactory) proving the host
// wires the A.2 Options (+ flat-env->section bridge), A.3 JsonDefaults on the HTTP pipeline, the
// A.4 pricing loader (degrade-safe), CORS restricted to the frontend origin (ARCH-019), and the
// /api/health route (ARCH-029).
[Collection("HostEnv")]
public class HostIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task health_returns_ok()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void http_json_pipeline_uses_jsondefaults_contract()
    {
        // Proves JsonDefaults.Apply is on the HTTP (minimal-API) JSON pipeline — the same contract
        // (camelCase + enum-as-string) that persistence uses. Pre-B.9 there is no domain endpoint,
        // so assert the configured options the pipeline actually serializes with.
        var options = _factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
        var serializer = options.Value.SerializerOptions;

        Assert.Equal(JsonNamingPolicy.CamelCase, serializer.PropertyNamingPolicy);
        Assert.Contains(serializer.Converters, c => c is JsonStringEnumConverter);
    }

    [Fact]
    public void options_resolve_from_env_bridge()
    {
        // The flat ARCH-028 env var must populate the A.2 section-bound Options. Set a real env var
        // BEFORE the host builds (WebApplication.CreateBuilder reads env at creation, before the
        // bridge runs) and resolve IOptions from a dedicated host.
        Environment.SetEnvironmentVariable("DEEPGRAM_API_KEY", "dg-bridge-test");
        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var deepgram = factory.Services.GetRequiredService<IOptions<DeepgramOptions>>();

            Assert.Equal("dg-bridge-test", deepgram.Value.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPGRAM_API_KEY", null);
        }
    }

    [Fact]
    public async Task cors_allows_configured_origin_only()
    {
        var client = _factory.CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        allowed.Headers.Add("Origin", "http://localhost:5173");
        var allowedResp = await client.SendAsync(allowed);
        Assert.True(allowedResp.Headers.Contains("Access-Control-Allow-Origin"));

        var denied = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        denied.Headers.Add("Origin", "http://evil.example");
        var deniedResp = await client.SendAsync(denied);
        Assert.False(deniedResp.Headers.Contains("Access-Control-Allow-Origin"));

        // A preflight (OPTIONS) from the allowed origin is also approved by the policy.
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        var preflightResp = await client.SendAsync(preflight);
        Assert.True(preflightResp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task swagger_not_exposed_in_production()
    {
        // Swagger registration + middleware are both Development-gated (ARCH-019 surface-minimization).
        using var prodFactory = _factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var resp = await prodFactory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task host_starts_when_pricing_missing()
    {
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", "/no/such/pricing-config.json");
        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var resp = await factory.CreateClient().GetAsync("/api/health");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", null);
        }
    }
}

// Env-var-touching integration tests share a non-parallel collection: process env vars are
// process-wide, and B.9's ConfigEndpointTests will also toggle them — a shared collection
// serializes these classes so they can't race across parallel test runs.
[CollectionDefinition("HostEnv", DisableParallelization = true)]
public class HostEnvCollection { }
