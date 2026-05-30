using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiInterpreter.Tests;

// E.1 — RealtimeController (POST /api/realtime/client-secret) HTTP boundary, via WebApplicationFactory
// (the CascadeControllerTests precedent). Pins the WIRE contract a real MVC boot is needed for: model
// binding + the §16 string caps → 400, the service outcome → 200 token DTO / sanitized UiError + status.
// The broker's typed HttpClient is overridden with a canned upstream handler (no live OpenAI call); the
// standard key is supplied via OPENAI_API_KEY so the key-presence path is exercised end-to-end.
[Collection("HostEnv")]
public class RealtimeControllerTests : IDisposable
{
    public RealtimeControllerTests()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-controller");
        Environment.SetEnvironmentVariable("USE_FAKE_PROVIDERS", "true"); // cascade stays fake; realtime broker is unconditional
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("USE_FAKE_PROVIDERS", null);
    }

    private static WebApplicationFactory<Program> Factory(HttpStatusCode upstreamStatus, string upstreamBody) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient<RealtimeClientSecretService>(c => c.BaseAddress = new Uri("https://api.openai.com/"))
                .ConfigurePrimaryHttpMessageHandler(() => new CannedHandler(upstreamStatus, upstreamBody))));

    private static StringContent Body(string sessionId, string? model)
    {
        var req = new RealtimeTokenRequest(sessionId, new LanguageDirection(LanguageCode.En, LanguageCode.Es), model);
        return new StringContent(JsonSerializer.Serialize(req, JsonDefaults.Options), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task mint_endpoint_returns_200_token_shape()
    {
        const long epoch = 1756310386;
        var ga = $"{{\"value\":\"ek_wired\",\"expires_at\":{epoch},\"session\":{{\"id\":\"sess_1\"}}}}";
        using var factory = Factory(HttpStatusCode.OK, ga);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/realtime/client-secret", Body("session_1", null));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ek_wired", root.GetProperty("clientSecret").GetString()); // camelCase via JsonDefaults
        Assert.Equal("gpt-realtime", root.GetProperty("model").GetString());     // resolved default
        Assert.Equal(JsonValueKind.String, root.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task mint_failure_returns_sanitized_uierror_with_status()
    {
        // The upstream 401 body even echoes a fake key — the wire response must be a sanitized UiError with
        // the surfaced status, never a raw exception/payload (ARCH-018 + invariant #1).
        using var factory = Factory(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"key sk-LEAK invalid\"}}");
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/realtime/client-secret", Body("session_1", null));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("realtime.auth", doc.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("sk-", raw, StringComparison.Ordinal); // no key/payload leak on the wire
    }

    [Fact]
    public async Task oversized_session_id_rejected_400()
    {
        using var factory = Factory(HttpStatusCode.OK, "{}");
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/realtime/client-secret", Body(new string('x', 257), null));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // §16 cap — before any upstream call
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("realtime.invalid_request", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task offcatalog_model_rejected_400()
    {
        using var factory = Factory(HttpStatusCode.OK, "{}");
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/realtime/client-secret", Body("session_1", "gpt-bogus"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // allow-list fail-closed (Q4)
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("realtime.invalid_request", doc.RootElement.GetProperty("code").GetString());
    }

    // Canned upstream: returns a fixed status + body regardless of request (no live OpenAI call).
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CannedHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
