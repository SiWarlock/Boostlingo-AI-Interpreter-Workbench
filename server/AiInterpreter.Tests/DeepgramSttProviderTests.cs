using System.Net;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Sessions;
using Deepgram.Models.Exceptions.v1;

// Aliases disambiguate the two SDK DTO families that share type names across namespaces:
// the live-WS result (v2.WebSocket) and the pre-recorded REST response (v1.REST).
using WsResult = Deepgram.Models.Listen.v2.WebSocket.ResultResponse;
using WsChannel = Deepgram.Models.Listen.v2.WebSocket.Channel;
using WsAlt = Deepgram.Models.Listen.v2.WebSocket.Alternative;
using RestSync = Deepgram.Models.Listen.v1.REST.SyncResponse;
using RestResults = Deepgram.Models.Listen.v1.REST.Results;
using RestChannel = Deepgram.Models.Listen.v1.REST.Channel;
using RestAlt = Deepgram.Models.Listen.v1.REST.Alternative;

namespace AiInterpreter.Tests;

// C.1 — DeepgramSttProvider deterministic-surface tests (ARCH-011 / ARCH-012 / ARCH-020).
//
// Per the brief's settled TDD posture: the live-WS network transport shell (connect/subscribe/send/
// finish) is MANUAL-SMOKE, exempt from unit TDD. What is deterministic — and therefore TDD'd here —
// is the pure SDK-result -> SttEvent mapping, the pre-recorded REST response parsing, and the
// exception -> ProviderError catch-path wiring. These three pure helpers (DeepgramSttMapping) are what
// the (untested) transport shell calls; the shell does no logic of its own.
//
// Scope guard (lesson §17): we do NOT re-test the ProviderErrorMapper truth table (B.1 owns it). The
// error tests pin only that the provider's failure projection routes through the mapper with the
// correct provider/stage constants ("deepgram"/"stt") and that the verdict flows through unchanged —
// a representative retryable, non-retryable, and the honest no-status-SDK-exception degrade.
public class DeepgramSttProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 29, 0, 0, 0, TimeSpan.Zero);

    // === Group 1 — live-WS result -> SttEvent mapping (pure, synthetic ResultResponse) ===

    [Fact]
    public void interim_result_maps_to_partial()
    {
        var ev = DeepgramSttMapping.ToSttEvent(WsResultOf("hel", isFinal: false), Now);

        var partial = Assert.IsType<SttPartial>(ev);
        Assert.Equal("hel", partial.Text);
    }

    [Fact]
    public void final_result_maps_to_final()
    {
        var ev = DeepgramSttMapping.ToSttEvent(WsResultOf("hello world", isFinal: true), Now);

        var final = Assert.IsType<SttFinal>(ev);
        Assert.Equal("hello world", final.Text);
    }

    [Fact]
    public void empty_final_maps_to_final_empty()
    {
        // A final whose transcript is whitespace/empty is SttFinal("") — NOT SttFailed, NOT
        // cascade.empty_transcript (the orchestrator owns that short-circuit). Matches FakeStt.EmptyFinal.
        var ev = DeepgramSttMapping.ToSttEvent(WsResultOf("   ", isFinal: true), Now);

        var final = Assert.IsType<SttFinal>(ev);
        Assert.Equal(string.Empty, final.Text);
    }

    [Fact]
    public void result_with_no_channel_maps_to_empty_partial()
    {
        // Defensive null-nav: a deserialized result with no Channel/Alternatives must not throw —
        // an interim with no transcript degrades to SttPartial("").
        var result = new WsResult { IsFinal = false, Channel = null };

        var ev = DeepgramSttMapping.ToSttEvent(result, Now);

        var partial = Assert.IsType<SttPartial>(ev);
        Assert.Equal(string.Empty, partial.Text);
    }

    // === Group 2 — pre-recorded REST fallback parsing (pure, synthetic SyncResponse) ===

    [Fact]
    public void prerecorded_yields_started_then_single_final()
    {
        var events = DeepgramSttMapping.ParsePrerecorded(RestSyncOf("hola mundo"), Now);

        Assert.Collection(
            events,
            e => Assert.IsType<SttStarted>(e),
            e => Assert.Equal("hola mundo", Assert.IsType<SttFinal>(e).Text));
        Assert.DoesNotContain(events, e => e is SttPartial); // fallback is single-final, no interim
    }

    [Fact]
    public void prerecorded_empty_yields_final_empty()
    {
        var events = DeepgramSttMapping.ParsePrerecorded(RestSyncOf(null), Now);

        var final = Assert.IsType<SttFinal>(events[^1]);
        Assert.Equal(string.Empty, final.Text);
    }

    // === Group 3 — exception -> ProviderError catch-path wiring (DeepgramSttMapping.ToFailed) ===

    [Fact]
    public void rate_limited_yields_failed_retryable()
    {
        // The empty-body Deepgram error path surfaces a status-bearing HttpRequestException; proves the
        // catch->mapper wiring + correct "deepgram"/"stt" constants. The 429->rate_limited mapping
        // itself is B.1's ProviderErrorMappingTests; we assert it flows through unchanged.
        var failed = DeepgramSttMapping.ToFailed(
            new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests), Now);

        Assert.Equal("stt.rate_limited", failed.Error.Code);
        Assert.True(failed.Error.Retryable);
        Assert.Equal("stt", failed.Error.Stage);
        Assert.Equal("deepgram", failed.Error.Provider);
    }

    [Fact]
    public void auth_error_yields_failed_nonretryable()
    {
        var failed = DeepgramSttMapping.ToFailed(
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden), Now);

        Assert.Equal("stt.auth", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("deepgram", failed.Error.Provider);
    }

    [Fact]
    public void deepgram_exception_without_status_yields_unknown()
    {
        // The COMMON Deepgram error path (non-empty body) throws DeepgramRESTException, which carries NO
        // HTTP status — so it lands on the mapper's {stage}.unknown (non-retryable) fallback. This pins
        // that honest degrade as intended behavior (the fidelity gap flagged at Step 2.5/Step 9).
        var failed = DeepgramSttMapping.ToFailed(new DeepgramRESTException("upstream error body"), Now);

        Assert.Equal("stt.unknown", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("deepgram", failed.Error.Provider);
    }

    // === Group 4 — live request schema built from request + options (ARCH-030 no-resample/no-transcode) ===

    [Fact]
    public void live_schema_built_from_request_and_options()
    {
        var options = new DeepgramOptions(); // inline defaults: nova-3 / multi / smart_format / interim / ch=1 / utt=1000
        var request = LiveReq(sampleRate: 16000); // deliberately != the DeepgramOptions default (48000)

        var schema = DeepgramSttMapping.BuildLiveSchema(request, options);

        // ARCH-030: sample_rate + encoding come from the REQUEST, never hardcoded/options-defaulted — a
        // hardcoded 48000 would silently corrupt non-48000 capture.
        Assert.Equal((int?)16000, schema.SampleRate);
        Assert.Equal("linear16", schema.Encoding);
        Assert.Equal("multi", schema.Language);
        // Provider config (ARCH-012 DeepgramOptions): model + the streaming flags.
        Assert.Equal("nova-3", schema.Model);
        Assert.True(schema.InterimResults == true);
        Assert.True(schema.SmartFormat == true);
        Assert.Equal((int?)1, schema.Channels);
        Assert.Equal("1000", schema.UtteranceEnd);
    }

    // === synthetic SDK-DTO builders ===

    private static async IAsyncEnumerable<AudioFrame> NoFrames()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SttRequest LiveReq(int sampleRate) =>
        new(NoFrames(), "audio/pcm", "linear16", sampleRate, LanguageCode.En, "multi", "session_1", "turn_1");

    private static WsResult WsResultOf(string? transcript, bool isFinal) =>
        new()
        {
            IsFinal = isFinal,
            Channel = new WsChannel { Alternatives = new List<WsAlt> { new() { Transcript = transcript } } },
        };

    private static RestSync RestSyncOf(string? transcript) =>
        new()
        {
            Results = new RestResults
            {
                Channels = new List<RestChannel>
                {
                    new() { Alternatives = new List<RestAlt> { new() { Transcript = transcript } } },
                },
            },
        };
}
