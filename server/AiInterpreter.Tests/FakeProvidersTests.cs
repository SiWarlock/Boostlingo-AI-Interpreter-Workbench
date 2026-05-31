using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.2 fake-provider contract tests: each fake + variant emits the ARCH-012 ordered event sequence,
// and cancellation mid-stream surfaces OperationCanceledException. Delay timing is NOT wall-clock-
// asserted (just that variants don't reorder + cancellation is honored). Comprehensive cross-cutting
// boundary tests are B.10's ProviderBoundaryTests.
public class FakeProvidersTests
{
    // --- STT ---

    [Fact]
    public async Task fake_stt_success_emits_started_partials_final()
    {
        var events = await Collect(new FakeSttProvider(FakeSttBehavior.SuccessWithPartials).TranscribeAsync(SttReq(), default));

        Assert.IsType<SttStarted>(events[0]);
        Assert.NotEmpty(events.OfType<SttPartial>());
        var final = Assert.IsType<SttFinal>(events[^1]);
        Assert.NotEmpty(final.Text);
    }

    [Fact]
    public async Task fake_stt_empty_final()
    {
        var events = await Collect(new FakeSttProvider(FakeSttBehavior.EmptyFinal).TranscribeAsync(SttReq(), default));

        Assert.IsType<SttStarted>(events[0]);
        Assert.Empty(events.OfType<SttPartial>());
        var final = Assert.IsType<SttFinal>(events[^1]);
        Assert.Equal(string.Empty, final.Text);
    }

    [Fact]
    public async Task fake_stt_partials_then_error()
    {
        var events = await Collect(new FakeSttProvider(FakeSttBehavior.PartialsThenError).TranscribeAsync(SttReq(), default));

        Assert.IsType<SttStarted>(events[0]);
        Assert.NotEmpty(events.OfType<SttPartial>());
        var failed = Assert.IsType<SttFailed>(events[^1]);
        Assert.NotEmpty(failed.Error.Code);
        Assert.DoesNotContain(events, e => e is SttFinal); // error short-circuits — no final
    }

    // --- Translation ---

    [Fact]
    public async Task fake_translation_token_stream_then_final()
    {
        var events = await Collect(
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal).TranslateAsync(TransReq(), default));

        Assert.IsType<TranslationStarted>(events[0]);
        Assert.NotEmpty(events.OfType<TranslationPartial>());
        var final = Assert.IsType<TranslationFinal>(events[^1]);
        Assert.NotEmpty(final.Text);
    }

    [Fact]
    public async Task fake_translation_immediate_final_only()
    {
        var events = await Collect(
            new FakeTranslationProvider(FakeTranslationBehavior.ImmediateFinalOnly).TranslateAsync(TransReq(), default));

        Assert.IsType<TranslationStarted>(events[0]);
        Assert.Empty(events.OfType<TranslationPartial>());
        Assert.IsType<TranslationFinal>(events[^1]);
    }

    [Fact]
    public async Task fake_translation_error()
    {
        var events = await Collect(
            new FakeTranslationProvider(FakeTranslationBehavior.Error).TranslateAsync(TransReq(), default));

        Assert.IsType<TranslationStarted>(events[0]);
        var failed = Assert.IsType<TranslationFailed>(events[^1]);
        Assert.NotEmpty(failed.Error.Code);
        Assert.DoesNotContain(events, e => e is TranslationFinal); // error short-circuits — no final
    }

    // --- TTS ---

    [Fact]
    public async Task fake_tts_chunked_then_complete()
    {
        var events = await Collect(new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete).SynthesizeAsync(TtsReq(), default));

        Assert.IsType<TtsStarted>(events[0]);
        Assert.IsType<TtsFirstAudio>(events[1]);
        var chunks = events.OfType<TtsAudioChunk>().ToList();
        Assert.NotEmpty(chunks);
        Assert.Equal(Enumerable.Range(0, chunks.Count).ToList(), chunks.Select(c => c.Seq).ToList()); // seq ordered
        Assert.IsType<TtsComplete>(events[^1]);
    }

    [Fact]
    public async Task fake_tts_complete_only()
    {
        var events = await Collect(new FakeTtsProvider(FakeTtsBehavior.CompleteOnly).SynthesizeAsync(TtsReq(), default));

        Assert.IsType<TtsStarted>(events[0]);
        Assert.IsType<TtsFirstAudio>(events[1]);
        Assert.Empty(events.OfType<TtsAudioChunk>());
        Assert.IsType<TtsComplete>(events[^1]);
    }

    [Fact]
    public async Task fake_tts_error()
    {
        var events = await Collect(new FakeTtsProvider(FakeTtsBehavior.Error).SynthesizeAsync(TtsReq(), default));

        Assert.IsType<TtsStarted>(events[0]);
        var failed = Assert.IsType<TtsFailed>(events[^1]);
        Assert.NotEmpty(failed.Error.Code);
        Assert.DoesNotContain(events, e => e is TtsComplete); // error short-circuits — no complete
    }

    // --- Cancellation (per stage; B.4 wraps EACH stage with CancelAfter and relies on this) ---

    [Fact]
    public Task stt_cancellation_mid_stream_throws_oce() =>
        AssertCancelsMidStream(ct => new FakeSttProvider().TranscribeAsync(SttReq(), ct));

    [Fact]
    public Task translation_cancellation_mid_stream_throws_oce() =>
        AssertCancelsMidStream(ct => new FakeTranslationProvider().TranslateAsync(TransReq(), ct));

    [Fact]
    public Task tts_cancellation_mid_stream_throws_oce() =>
        AssertCancelsMidStream(ct => new FakeTtsProvider().SynthesizeAsync(TtsReq(), ct));

    [Fact]
    public async Task pre_cancelled_token_emits_nothing()
    {
        // B.4 may start a downstream stage with an already-cancelled linked token (upstream timed
        // out) — PaceAsync's ThrowIfCancellationRequested fires before the first yield.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collected = new List<SttEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in new FakeSttProvider().TranscribeAsync(SttReq(), cts.Token))
            {
                collected.Add(e);
            }
        });

        Assert.Empty(collected);
    }

    // --- helpers ---

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var events = new List<T>();
        await foreach (var e in source)
        {
            events.Add(e);
        }

        return events;
    }

    private static async Task AssertCancelsMidStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> stream)
    {
        using var cts = new CancellationTokenSource();
        var collected = new List<T>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in stream(cts.Token))
            {
                collected.Add(e);
                await cts.CancelAsync(); // cancel after the first event → the next PaceAsync throws
            }
        });

        Assert.NotEmpty(collected); // >=1 event arrived before cancellation took effect
    }

    // Empty frame stream — the fakes emit scripted events regardless of the request, so the
    // request's AudioFrames is never enumerated. (await keeps it a valid async iterator.)
    private static async IAsyncEnumerable<AudioFrame> NoFrames()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SttRequest SttReq() =>
        new(NoFrames(), "audio/pcm", "linear16", 48000, LanguageCode.En, "multi", "session_1", "turn_1");

    private static TranslationRequest TransReq() =>
        new("hello world", LanguageCode.En, LanguageCode.Es, "gpt-5-nano", "session_1", "turn_1");

    private static TtsRequest TtsReq() =>
        new("hola mundo", LanguageCode.Es, "alloy", "gpt-4o-mini-tts", "mp3", null, "session_1", "turn_1");
}
