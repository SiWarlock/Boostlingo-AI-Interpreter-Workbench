using System.Net;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// E.1 — RealtimeClientSecretService transport tests (ARCH-010 GA mint + ARCH-018 sanitized errors).
//
// Mock HttpMessageHandler (the OpenAiTranslationProviderTests precedent) drives the broker end-to-end with
// NO live key: GA endpoint/auth/Safety-Identifier, success mapping, model resolution + allow-list short-
// circuit, missing-key short-circuit, the sanitized error table, the single bounded 429 retry (delay
// injected → no wall-clock, lesson §6), and the SAFETY #1 sentinel (the standard key never leaks).
public class RealtimeClientSecretServiceTests
{
    private static readonly LanguageDirection EnToEs = new(LanguageCode.En, LanguageCode.Es);

    // === Group 1 — the GA upstream call: endpoint + auth + safety-identifier ===

    [Fact]
    public async Task posts_to_client_secrets_ga_endpoint_with_bearer()
    {
        var handler = new QueueHandler((HttpStatusCode.OK, GaBody("ek_ok", 1756310386), null));

        await Service(handler, Opts(apiKey: "sk-test-123")).MintAsync(Req(), default);

        Assert.NotNull(handler.LastRequest);
        // GA endpoint — NEVER the legacy /v1/realtime/sessions (brief acceptance pin).
        Assert.Equal("/v1/realtime/client_secrets", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("sessions", handler.LastRequest.RequestUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-123", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.True(handler.LastRequest.Headers.Contains("OpenAI-Safety-Identifier"));
    }

    // === Group 2 — success: token DTO + model resolution ===

    [Fact]
    public async Task success_returns_token_dto()
    {
        const long epoch = 1756310386;
        var handler = new QueueHandler((HttpStatusCode.OK, GaBody("ek_minted", epoch), null));

        var outcome = await Service(handler, Opts(apiKey: "sk-test", model: "gpt-realtime")).MintAsync(Req(), default);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Response);
        Assert.Equal("ek_minted", outcome.Response!.ClientSecret);
        Assert.Equal("gpt-realtime", outcome.Response.Model); // resolved from options (request model null)
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(epoch), DateTimeOffset.Parse(outcome.Response.ExpiresAt));
    }

    [Fact]
    public async Task resolves_request_model_over_default()
    {
        var handler = new QueueHandler((HttpStatusCode.OK, GaBody("ek_ok", 1756310386), null));

        var outcome = await Service(handler, Opts(apiKey: "sk-test", model: "gpt-realtime"))
            .MintAsync(Req(model: "gpt-realtime-mini"), default);

        Assert.Null(outcome.Error);
        Assert.Equal("gpt-realtime-mini", outcome.Response!.Model);
        // The resolved model is what we sent upstream.
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("gpt-realtime-mini", doc.RootElement.GetProperty("session").GetProperty("model").GetString());
    }

    // === Group 3 — fail-closed short-circuits (NO upstream call) ===

    [Fact]
    public async Task offcatalog_model_short_circuits_without_call()
    {
        var handler = new QueueHandler((HttpStatusCode.OK, GaBody("ek_ok", 1), null));

        var outcome = await Service(handler, Opts(apiKey: "sk-test")).MintAsync(Req(model: "gpt-bogus"), default);

        Assert.Null(outcome.Response);
        Assert.Equal("realtime.invalid_request", outcome.Error!.Code);
        Assert.Equal(0, handler.CallCount); // never reached OpenAI
    }

    [Fact]
    public async Task missing_api_key_short_circuits_without_call()
    {
        var handler = new QueueHandler((HttpStatusCode.OK, GaBody("ek_ok", 1), null));

        var outcome = await Service(handler, Opts(apiKey: "")).MintAsync(Req(), default);

        Assert.Null(outcome.Response);
        Assert.Equal("realtime.auth", outcome.Error!.Code);
        Assert.False(outcome.Error.Retryable);
        Assert.Equal(0, handler.CallCount); // no doomed upstream call (capability-from-key-presence, §15)
    }

    // === Group 4 — the sanitized error table (ARCH-018) ===

    [Fact]
    public async Task auth_failure_maps_realtime_auth_nonretryable()
    {
        var handler = new QueueHandler((HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}", null));

        var outcome = await Service(handler, Opts(apiKey: "sk-test")).MintAsync(Req(), default);

        Assert.Null(outcome.Response);
        Assert.Equal("realtime.auth", outcome.Error!.Code);
        Assert.Equal("realtime", outcome.Error.Stage);
        Assert.Equal("openai", outcome.Error.Provider);
        Assert.False(outcome.Error.Retryable);
    }

    [Fact]
    public async Task network_failure_maps_upstream_unavailable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused")); // no HTTP status

        var outcome = await Service(handler, Opts(apiKey: "sk-test")).MintAsync(Req(), default);

        Assert.Equal("realtime.upstream_unavailable", outcome.Error!.Code);
        Assert.True(outcome.Error.Retryable);
    }

    [Fact]
    public async Task timeout_maps_realtime_timeout()
    {
        // A linked-CTS timeout surfaces as an OCE while the CALLER ct is NOT cancelled (lesson §20 polarity):
        // it must map to realtime.timeout, not propagate as a cancellation. Simulated by the handler throwing.
        var handler = new ThrowingHandler(new TaskCanceledException("timed out"));

        var outcome = await Service(handler, Opts(apiKey: "sk-test")).MintAsync(Req(), default);

        Assert.Equal("realtime.timeout", outcome.Error!.Code);
        Assert.True(outcome.Error.Retryable);
    }

    // === Group 5 — the single bounded 429 retry (delay injected; no wall-clock) ===

    [Fact]
    public async Task rate_limit_retries_once_then_succeeds()
    {
        var handler = new QueueHandler(
            (HttpStatusCode.TooManyRequests, "{\"error\":{}}", "0"), // Retry-After: 0
            (HttpStatusCode.OK, GaBody("ek_after_retry", 1756310386), null));
        var delays = new List<TimeSpan>();

        var outcome = await Service(handler, Opts(apiKey: "sk-test"), CaptureDelay(delays)).MintAsync(Req(), default);

        Assert.Null(outcome.Error);
        Assert.Equal("ek_after_retry", outcome.Response!.ClientSecret);
        Assert.Equal(2, handler.CallCount);                 // exactly one retry
        Assert.Equal(TimeSpan.Zero, Assert.Single(delays)); // honored Retry-After: 0
    }

    [Fact]
    public async Task rate_limit_retries_once_then_fails()
    {
        // Two 429s (no Retry-After header → the fixed fallback backoff) → realtime.rate_limited after
        // EXACTLY one retry (no infinite loop).
        var handler = new QueueHandler(
            (HttpStatusCode.TooManyRequests, "{\"error\":{}}", null),
            (HttpStatusCode.TooManyRequests, "{\"error\":{}}", null));
        var delays = new List<TimeSpan>();

        var outcome = await Service(handler, Opts(apiKey: "sk-test"), CaptureDelay(delays)).MintAsync(Req(), default);

        Assert.Equal("realtime.rate_limited", outcome.Error!.Code);
        Assert.True(outcome.Error.Retryable);
        Assert.Equal(2, handler.CallCount);              // one retry, then stop
        Assert.True(Assert.Single(delays) > TimeSpan.Zero); // fallback backoff when Retry-After absent
    }

    // === Group 6 — SAFETY #1: the standard key never crosses the boundary ===

    [Fact]
    public async Task standard_key_never_in_failure_output()
    {
        // ApiKey is a sentinel; the upstream 401 body even echoes a fake sk- key. The produced UiError —
        // serialized as it would go on the wire — must contain neither the real key nor the echoed one nor
        // the raw upstream payload (invariant #1; mirrors the persistence sentinel posture).
        const string sentinelKey = "sk-SENTINEL-LEAK-ME";
        var handler = new QueueHandler(
            (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"key sk-ECHOED-FROM-UPSTREAM is invalid\"}}", null));

        var outcome = await Service(handler, Opts(apiKey: sentinelKey)).MintAsync(Req(), default);

        Assert.NotNull(outcome.Error);
        var sanitizer = new ErrorSanitizer(NullLogger<ErrorSanitizer>.Instance);
        var wire = JsonSerializer.Serialize(sanitizer.ToUiError(outcome.Error!), JsonDefaults.Options);

        Assert.DoesNotContain("sk-", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", outcome.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream", wire, StringComparison.OrdinalIgnoreCase); // no raw provider payload
    }

    // === fixtures + helpers ===

    private static RealtimeOptions Opts(string apiKey = "sk-test", string model = "gpt-realtime") => new()
    {
        ApiKey = apiKey,
        Model = model,
        Voice = "marin",
        ExpirySeconds = 600,
        TokenTimeoutSeconds = 10,
        TranscriptionModel = "gpt-4o-transcribe",
    };

    private static RealtimeTokenRequest Req(string? model = null) => new("session_1", EnToEs, model);

    private static Func<TimeSpan, CancellationToken, Task> CaptureDelay(List<TimeSpan> sink) =>
        (d, _) => { sink.Add(d); return Task.CompletedTask; };

    private static RealtimeClientSecretService Service(
        HttpMessageHandler handler, RealtimeOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new RealtimeClientSecretService(http, Options.Create(options), delay ?? ((_, _) => Task.CompletedTask));
    }

    private static string GaBody(string value, long expiresAt) =>
        $"{{\"value\":\"{value}\",\"expires_at\":{expiresAt},\"session\":{{\"id\":\"sess_1\",\"model\":\"gpt-realtime\"}}}}";

    // Returns a queued sequence of (status, body, Retry-After?) responses; captures the last request + body
    // and counts calls. A test supplying N responses asserts the broker made exactly N upstream calls.
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? RetryAfter)> _responses;

        public QueueHandler(params (HttpStatusCode Status, string Body, string? RetryAfter)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string, string?)>(responses);

        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_responses.Count == 0)
            {
                // Fail loud rather than replaying a stale response — a broker that over-calls the upstream
                // (e.g. an unbounded retry loop) must surface, not pass green on a recycled response.
                throw new InvalidOperationException("QueueHandler: no responses remaining (broker over-called the upstream)");
            }

            var (status, body, retryAfter) = _responses.Dequeue();
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        }
    }

    // Throws on send — simulates a network failure or a linked-CTS timeout (no HTTP response at all).
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
    }
}
