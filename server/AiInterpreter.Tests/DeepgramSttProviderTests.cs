using System.Net;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Sessions;
using Deepgram.Models.Exceptions.v1;

// Aliases disambiguate the two SDK DTO families that share type names across namespaces:
// the live-WS result (v2.WebSocket) and the pre-recorded REST response (v1.REST).
using WsResult = Deepgram.Models.Listen.v2.WebSocket.ResultResponse;
using WsUtteranceEnd = Deepgram.Models.Listen.v2.WebSocket.UtteranceEndResponse;
using WsChannel = Deepgram.Models.Listen.v2.WebSocket.Channel;
using WsAlt = Deepgram.Models.Listen.v2.WebSocket.Alternative;
using WsWord = Deepgram.Models.Listen.v2.WebSocket.Word;
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

    [Fact]
    public void utterance_end_response_maps_to_stt_utterance_end()
    {
        // I.1 — the SDK's UtteranceEnd (endpointing / detected-silence) message maps to the SttUtteranceEnd
        // terminal marker (no text; the orchestrator honors it only under auto-VAD).
        var ev = DeepgramSttMapping.ToUtteranceEnd(new WsUtteranceEnd(), Now);

        Assert.IsType<SttUtteranceEnd>(ev);
        Assert.Equal(Now, ev.Timestamp);
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

    [Theory] // C.6 — recover the HTTP status from the Deepgram SDK exception's semantic err_code (v6.6.1
             // carries no HttpStatusCode), then map via the vendor-agnostic ProviderErrorMapper. Net-new =
             // the err_code-string -> status extraction; the status -> code table is B.1's (not re-tested).
    [InlineData("TOO_MANY_REQUESTS", "stt.rate_limited", true)]      // 429 — the headline fidelity restore
    [InlineData("INVALID_AUTH", "stt.auth", false)]                  // 401 — bad/missing key
    [InlineData("INSUFFICIENT_PERMISSIONS", "stt.auth", false)]      // 403 (or 401-perms) — shared code, both -> auth
    [InlineData("Bad Request", "stt.invalid_request", false)]        // 400 — note the Title-Case-with-space err_code
    public void deepgram_errcode_recovers_status_and_maps(string errCode, string expectedCode, bool retryable)
    {
        var failed = DeepgramSttMapping.ToFailed(new DeepgramRESTException("body") { ErrCode = errCode }, Now);

        Assert.Equal(expectedCode, failed.Error.Code);
        Assert.Equal(retryable, failed.Error.Retryable);
        Assert.Equal("stt", failed.Error.Stage);
        Assert.Equal("deepgram", failed.Error.Provider);
    }

    [Fact]
    public void deepgram_exception_unmappable_yields_unknown()
    {
        // Unrecognized err_code — INCLUDING the Deepgram 5xx-with-body case (which uses the "error_code"
        // JSON key, so ErrCode stays at the SDK default "Unknown Error Code") — has no recoverable status
        // and falls through to the existing ProviderErrorMapper.Map default -> stt.unknown. This honest degrade is
        // INTENDED (the narrow accepted C.6 residual; mirrors how C.1 test 8 documented its degrade).
        // (Renamed/reframed C.1 test 8 — was "without_status", now the deliberate unmappable case.)
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
        Assert.True(schema.VadEvents == true); // I.1 — vad_events enables the endpointing/UtteranceEnd path
    }

    // === Group 5 — J.1 per-utterance language detection (nova-3 multi → SttFinal.DetectedLanguage) ===

    [Fact]
    public void final_detected_language_from_alternative_languages()
    {
        // Deepgram's own utterance-level pick (Alternative.languages, populated under language=multi) is the
        // dominant source — ["es"] ⇒ Es. Rides the SDK's typed signal (lesson §19 — exposed, not hidden).
        var ev = DeepgramSttMapping.ToSttEvent(WsFinalWith("hola mundo", languages: new[] { "es" }), Now);

        Assert.Equal(LanguageCode.Es, Assert.IsType<SttFinal>(ev).DetectedLanguage);
    }

    [Fact]
    public void final_detected_language_falls_back_to_word_language_mode()
    {
        // No utterance-level languages → fall back to the MODE of the per-word language tags ([es,es,en] ⇒ es).
        var ev = DeepgramSttMapping.ToSttEvent(
            WsFinalWith("hola mundo", languages: null, wordLanguages: new[] { "es", "es", "en" }), Now);

        Assert.Equal(LanguageCode.Es, Assert.IsType<SttFinal>(ev).DetectedLanguage);
    }

    [Fact]
    public void final_detected_language_unknown_or_absent_is_null()
    {
        // Null-tolerant: an out-of-scope language (fr) with no usable word tags ⇒ null (orchestrator falls
        // back to the start-frame direction); a final with no language signal at all ⇒ null too.
        Assert.Null(Assert.IsType<SttFinal>(
            DeepgramSttMapping.ToSttEvent(WsFinalWith("bonjour", languages: new[] { "fr" }), Now)).DetectedLanguage);
        Assert.Null(Assert.IsType<SttFinal>(
            DeepgramSttMapping.ToSttEvent(WsResultOf("hello", isFinal: true), Now)).DetectedLanguage);
    }

    [Fact]
    public void final_detected_language_word_mode_tie_is_null()
    {
        // A genuine 50/50 word-language tie (no utterance-level pick) is ambiguous → null (deterministic, NOT
        // a LINQ-order coin-flip) → the orchestrator keeps the start-frame direction ("ambiguous → fall back").
        var ev = DeepgramSttMapping.ToSttEvent(
            WsFinalWith("hola hello", languages: null, wordLanguages: new[] { "es", "en" }), Now);

        Assert.Null(Assert.IsType<SttFinal>(ev).DetectedLanguage);
    }

    [Fact]
    public void sttfinal_default_detected_language_is_null()
    {
        // Back-compat: the existing 2-arg SttFinal construction leaves DetectedLanguage null (trailing default).
        Assert.Null(new SttFinal("hi", Now).DetectedLanguage);
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

    // A final result carrying the multi-language detection signal (J.1): the alternative's `languages[]`
    // (Deepgram's utterance pick) and/or per-word `language` tags (the fallback signal).
    private static WsResult WsFinalWith(
        string transcript, IReadOnlyList<string>? languages = null, IReadOnlyList<string>? wordLanguages = null) =>
        new()
        {
            IsFinal = true,
            Channel = new WsChannel
            {
                Alternatives = new List<WsAlt>
                {
                    new()
                    {
                        Transcript = transcript,
                        Languages = languages,
                        Words = wordLanguages?.Select(l => new WsWord { Language = l }).ToList(),
                    },
                },
            },
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
