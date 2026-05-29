using System.Net;
using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Tests;

// Pins the ARCH-012 exception/HTTP-status -> ProviderError mapping table (the deterministic, TDD'd
// piece of B.1). The mapper sets a SAFE generic message + the normalized code/retryable/status;
// the full sanitizer (server-side log of the original) is B.8.
public class ProviderErrorMappingTests
{
    [Fact]
    public void rate_limit_429_maps_retryable()
    {
        var err = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), "deepgram", "stt");

        Assert.Equal("stt.rate_limited", err.Code);
        Assert.True(err.Retryable);
        Assert.Equal(429, err.HttpStatusCode);
    }

    [Fact]
    public void auth_401_403_not_retryable()
    {
        var unauthorized = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.Unauthorized), "openai", "translation");
        Assert.Equal("translation.auth", unauthorized.Code);
        Assert.False(unauthorized.Retryable);
        Assert.Equal(401, unauthorized.HttpStatusCode);

        var forbidden = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.Forbidden), "openai", "translation");
        Assert.Equal("translation.auth", forbidden.Code);
        Assert.False(forbidden.Retryable);
        Assert.Equal(403, forbidden.HttpStatusCode);
    }

    [Fact]
    public void invalid_request_400_422_not_retryable()
    {
        var unprocessable = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.UnprocessableEntity), "openai", "tts");
        Assert.Equal("tts.invalid_request", unprocessable.Code);
        Assert.False(unprocessable.Retryable);
        Assert.Equal(422, unprocessable.HttpStatusCode);

        var badRequest = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.BadRequest), "openai", "tts");
        Assert.Equal("tts.invalid_request", badRequest.Code);
        Assert.False(badRequest.Retryable);
        Assert.Equal(400, badRequest.HttpStatusCode);
    }

    [Fact]
    public void upstream_5xx_or_network_retryable()
    {
        var server = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), "openai", "translation");
        Assert.Equal("translation.upstream_unavailable", server.Code);
        Assert.True(server.Retryable);
        Assert.Equal(503, server.HttpStatusCode);

        // Lower bound of the 5xx relational pattern.
        var internalError = ProviderErrorMapper.Map(
            new HttpRequestException("x", null, HttpStatusCode.InternalServerError), "openai", "translation");
        Assert.Equal("translation.upstream_unavailable", internalError.Code);
        Assert.Equal(500, internalError.HttpStatusCode);

        // Network failure: HttpRequestException with no status code.
        var network = ProviderErrorMapper.Map(new HttpRequestException("connection refused"), "deepgram", "stt");
        Assert.Equal("stt.upstream_unavailable", network.Code);
        Assert.True(network.Retryable);
        Assert.Null(network.HttpStatusCode);
    }

    [Fact]
    public void timeout_maps_retryable()
    {
        var canceled = ProviderErrorMapper.Map(new OperationCanceledException(), "openai", "tts");
        Assert.Equal("tts.timeout", canceled.Code);
        Assert.True(canceled.Retryable);
        Assert.Null(canceled.HttpStatusCode);

        // TimeoutException maps identically to OperationCanceledException.
        var timedOut = ProviderErrorMapper.Map(new TimeoutException(), "deepgram", "stt");
        Assert.Equal("stt.timeout", timedOut.Code);
        Assert.True(timedOut.Retryable);
    }

    [Fact]
    public void empty_transcript_is_cascade_scoped_retryable()
    {
        var err = ProviderErrorMapper.EmptyTranscript("deepgram");

        Assert.Equal("cascade.empty_transcript", err.Code); // NOT <stage>.* — cascade-level outcome
        Assert.Equal("cascade", err.Stage);
        Assert.True(err.Retryable);
    }

    [Fact]
    public void unknown_fallback_not_retryable()
    {
        var err = ProviderErrorMapper.Map(new InvalidOperationException("weird"), "openai", "translation");

        Assert.Equal("translation.unknown", err.Code);
        Assert.False(err.Retryable);
    }

    [Fact]
    public void unknown_factory_is_nonretryable_fixed_message()
    {
        // C.4b: the orchestrator raises Unknown(...) directly when a STARTED stage stream ends without its
        // terminal event (a fail-closed, non-exception outcome — like Timeout/EmptyTranscript). Fixed safe
        // message per code; non-retryable; no input echoed beyond the closed-set stage literal.
        var err = ProviderErrorMapper.Unknown("openai", "translation");

        Assert.Equal("translation.unknown", err.Code);
        Assert.False(err.Retryable);
        Assert.Equal("openai", err.Provider);
        Assert.Equal("translation", err.Stage);
        Assert.Equal("An unexpected translation error occurred.", err.SafeMessage); // fixed, matches the Map(...) fallback
        Assert.Null(err.HttpStatusCode);
    }

    [Fact]
    public void stage_token_interpolates()
    {
        Assert.Equal("translation.timeout", ProviderErrorMapper.Timeout("openai", "translation").Code);
        Assert.Equal(
            "tts.rate_limited",
            ProviderErrorMapper.Map(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), "openai", "tts").Code);
    }

    [Fact]
    public void safeMessage_carries_no_secret_or_stack()
    {
        var leaky = new HttpRequestException(
            "Authorization: Bearer sk-secret1234567890 thrown at MyProvider.Call() line 42",
            null,
            HttpStatusCode.Unauthorized);

        var err = ProviderErrorMapper.Map(leaky, "openai", "translation");

        Assert.DoesNotContain("sk-secret", err.SafeMessage);
        Assert.DoesNotContain("Bearer", err.SafeMessage);
        Assert.DoesNotContain("MyProvider.Call", err.SafeMessage);
    }
}
