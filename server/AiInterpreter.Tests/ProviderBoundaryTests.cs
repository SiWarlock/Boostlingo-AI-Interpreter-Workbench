using System.Net;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.10 — provider-boundary contract suite (ARCH-012 / ARCH-020 CRITICAL). The interface-level
// conformance contract every provider must honor — driven via the INTERFACE type (ISttProvider/
// ITranslationProvider/ITtsProvider) so C.5 swaps the construction (FakeX -> real provider) and reuses
// the same assertions + this file's helpers.
//
// Scoped tight vs the existing coverage (deliberately NOT duplicated):
//  - B.1 ProviderErrorMappingTests owns the exception->ProviderError mapper truth table (in isolation).
//  - B.2 FakeProvidersTests owns per-fake variant ordering + cancellation (mid-stream + pre-cancelled).
// The NET-NEW core here is error-code PRESERVATION through the boundary: a *Failed event carries the
// real ARCH-012 ProviderError (Code + Retryable + Stage), tying B.1's mapper output to a streamed
// *Failed. (B.2 only asserts the failed code is non-empty.) The success-ordering anchors assert the
// FULL ordered contract — single terminal, nothing after — at the interface level (B.2 spot-checks the
// concrete fakes). Cancellation is intentionally NOT re-tested here — B.2 covers per-stage mid-stream +
// pre-cancelled OCE thoroughly, and C.5's real-provider (HTTP-stream) cancellation warrants its own case.
public class ProviderBoundaryTests
{
    // === Net-new (MUST): a *Failed event preserves the real ARCH-012 ProviderError across the boundary ===

    [Fact]
    public async Task stt_failed_event_carries_real_provider_error()
    {
        var scripted = ProviderErrorMapper.Map(
            new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests), "deepgram", "stt");
        ISttProvider provider = new FakeSttProvider(FakeSttBehavior.PartialsThenError, error: scripted);

        var events = await Collect(provider.TranscribeAsync(SttReq(), default));

        var failed = Assert.IsType<SttFailed>(events[^1]);
        Assert.Equal("stt.rate_limited", failed.Error.Code);
        Assert.True(failed.Error.Retryable);
        Assert.Equal("stt", failed.Error.Stage);
        Assert.Equal("deepgram", failed.Error.Provider);
        Assert.DoesNotContain(events, e => e is SttFinal); // terminal failure — no final after
    }

    [Fact]
    public async Task translation_failed_event_carries_real_provider_error()
    {
        // A non-retryable code (auth) — proves Retryable=false is preserved unchanged too.
        var scripted = ProviderErrorMapper.Map(
            new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden), "openai", "translation");
        ITranslationProvider provider = new FakeTranslationProvider(FakeTranslationBehavior.Error, error: scripted);

        var events = await Collect(provider.TranslateAsync(TransReq(), default));

        var failed = Assert.IsType<TranslationFailed>(events[^1]);
        Assert.Equal("translation.auth", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
        Assert.Equal("translation", failed.Error.Stage);
        Assert.Equal("openai", failed.Error.Provider);
        Assert.DoesNotContain(events, e => e is TranslationFinal);
    }

    [Fact]
    public async Task tts_failed_event_carries_real_provider_error()
    {
        var scripted = ProviderErrorMapper.Map(
            new HttpRequestException("server error", null, HttpStatusCode.InternalServerError), "openai", "tts");
        ITtsProvider provider = new FakeTtsProvider(FakeTtsBehavior.Error, error: scripted);

        var events = await Collect(provider.SynthesizeAsync(TtsReq(), default));

        var failed = Assert.IsType<TtsFailed>(events[^1]);
        Assert.Equal("tts.upstream_unavailable", failed.Error.Code);
        Assert.True(failed.Error.Retryable);
        Assert.Equal("tts", failed.Error.Stage);
        Assert.Equal("openai", failed.Error.Provider);
        Assert.DoesNotContain(events, e => e is TtsComplete);
    }

    // === Contract anchors: the success-path ordered-event contract at the interface level (C.5 reuse) ===

    [Fact]
    public async Task stt_success_contract_ordered_events()
    {
        ISttProvider provider = new FakeSttProvider(FakeSttBehavior.SuccessWithPartials);

        var events = await Collect(provider.TranscribeAsync(SttReq(), default));

        Assert.IsType<SttStarted>(events[0]);
        Assert.NotEmpty(events.OfType<SttPartial>());
        Assert.IsType<SttFinal>(events[^1]);
        Assert.Single(events.OfType<SttFinal>());            // exactly one terminal
        Assert.DoesNotContain(events, e => e is SttFailed);  // success path has no failure terminal
    }

    [Fact]
    public async Task translation_success_contract_ordered_events()
    {
        ITranslationProvider provider = new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal);

        var events = await Collect(provider.TranslateAsync(TransReq(), default));

        Assert.IsType<TranslationStarted>(events[0]);
        Assert.NotEmpty(events.OfType<TranslationPartial>());
        Assert.IsType<TranslationFinal>(events[^1]);
        Assert.Single(events.OfType<TranslationFinal>());
        Assert.DoesNotContain(events, e => e is TranslationFailed);
    }

    [Fact]
    public async Task tts_success_contract_ordered_events()
    {
        ITtsProvider provider = new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete);

        var events = await Collect(provider.SynthesizeAsync(TtsReq(), default));

        Assert.IsType<TtsStarted>(events[0]);
        // FirstAudio is always present; IF chunks follow, it precedes the first one. A conformant
        // no-chunk success (FirstAudio→Complete) must NOT be rejected — that's a real C.5 provider shape.
        var firstAudioIdx = events.FindIndex(e => e is TtsFirstAudio);
        Assert.True(firstAudioIdx >= 0);
        var chunks = events.OfType<TtsAudioChunk>().ToList();
        if (chunks.Count > 0)
        {
            Assert.True(firstAudioIdx < events.FindIndex(e => e is TtsAudioChunk));
        }
        // Chunk seq is monotonic from 0 (the streaming contract).
        Assert.Equal(Enumerable.Range(0, chunks.Count).ToList(), chunks.Select(c => c.Seq).ToList());
        Assert.IsType<TtsComplete>(events[^1]);
        Assert.Single(events.OfType<TtsComplete>());
        Assert.DoesNotContain(events, e => e is TtsFailed);
    }

    // === C.5 — SafeMessage sentinel sweep across the REAL-provider mappings (invariant #4, mirrors B.7a) ===

    [Fact]
    public void safe_message_never_echoes_provider_text()
    {
        // Inject an exception whose message carries secret-/provider-shaped tokens; every real-provider
        // mapping's SafeMessage must echo NONE of them (the mapper sets a fixed string per code).
        const string secret = "sk-live-SECRET123";
        var leaky = new HttpRequestException(
            $"{secret} Bearer xyz err_msg=boom err_code=RATE_LIMIT at Provider.Call()", null, HttpStatusCode.TooManyRequests);
        var ts = DateTimeOffset.UtcNow;

        var errors = new[]
        {
            DeepgramSttMapping.ToFailed(leaky, ts).Error,
            OpenAiTranslationMapping.ToFailed(leaky, ts).Error,
            OpenAiTtsMapping.ToFailed(leaky, ts).Error,
            ProviderErrorMapper.Map(leaky, "openai", "translation"),
            ProviderErrorMapper.Unknown("deepgram", "stt"),
        };

        foreach (var err in errors)
        {
            Assert.DoesNotContain(secret, err.SafeMessage);
            Assert.DoesNotContain("Bearer", err.SafeMessage);
            Assert.DoesNotContain("err_msg", err.SafeMessage);
            Assert.DoesNotContain("err_code", err.SafeMessage);
            Assert.DoesNotContain("boom", err.SafeMessage);
            Assert.DoesNotContain("Provider.Call", err.SafeMessage);
        }
    }

    // === helpers (shared with C.5, which extends this file with real-provider/HTTP-mock cases) ===

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var events = new List<T>();
        await foreach (var e in source)
        {
            events.Add(e);
        }

        return events;
    }

    private static async IAsyncEnumerable<AudioFrame> NoFrames()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SttRequest SttReq() =>
        new(NoFrames(), "audio/pcm", "linear16", 48000, LanguageCode.En, "multi", "session_1", "turn_1");

    private static TranslationRequest TransReq() =>
        new("hello world", LanguageCode.En, LanguageCode.Es, "gpt-5.4-nano", "session_1", "turn_1");

    private static TtsRequest TtsReq() =>
        new("hola mundo", LanguageCode.Es, "alloy", "gpt-4o-mini-tts", "mp3", null, "session_1", "turn_1");
}
