using System.Runtime.CompilerServices;
using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.4 — CascadeStreamingOrchestrator (ARCH-011 streaming pipeline). CRITICAL tier (ARCH-020): the
// spec's centerpiece, covered thoroughly. Driven end-to-end against the B.2 fakes — no real keys.
//
// The load-bearing pin is STREAMING HONESTY: target transcript + first audio arrive BEFORE Done,
// source partials before the source final, and each first_* latency event is stamped exactly once
// on real first arrival (never a relabeled completion). Empty/partial-failure are proven with
// CALL-COUNT SPIES (the teeth): a stage that must NOT run has Calls == 0.
public class CascadeOrchestratorTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);
    private static readonly LanguageDirection EnToEs = new(LanguageCode.En, LanguageCode.Es);

    private sealed class FakeClock(DateTimeOffset fixedNow) : IClock
    {
        public DateTimeOffset UtcNow => fixedNow;
    }

    // Call-count spy decorators (Step-2.5 Q3). Calls increments when the stage is invoked at all;
    // the decorator delegates streaming to the wrapped fake.
    private sealed class CountingTranslationProvider(ITranslationProvider inner) : ITranslationProvider
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            Calls++;
            return inner.TranslateAsync(request, ct);
        }
    }

    private sealed class CountingTtsProvider(ITtsProvider inner) : ITtsProvider
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, CancellationToken ct)
        {
            Calls++;
            return inner.SynthesizeAsync(request, ct);
        }
    }

    private static async IAsyncEnumerable<AudioFrame> EmptyFrames()
    {
        await Task.CompletedTask;
        yield break;
    }

    // Test-local STT fake emitting TWO finalized segments (the shared B.2 FakeStt emits one). Proves
    // the ARCH-011 nested per-segment loop: segment-1's translation+TTS stream before segment-2's
    // STT final is processed. Kept in the Tests project — no change to the B.2 fakes.
    private sealed class TwoSegmentSttProvider : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new SttStarted(DateTimeOffset.UtcNow);
            yield return new SttPartial("hello", DateTimeOffset.UtcNow);
            yield return new SttFinal("hello world", DateTimeOffset.UtcNow);
            yield return new SttPartial("foo", DateTimeOffset.UtcNow);
            yield return new SttFinal("foo bar", DateTimeOffset.UtcNow);
        }
    }

    private static CascadeStartParams Params(TimeSpan? sttTimeout = null) =>
        new("s1", "t1", EnToEs, "linear16", 16000, "gpt-5.4-nano", "alloy", SttTimeout: sttTimeout);

    private static async Task<List<CascadeOutputEvent>> Run(
        ISttProvider stt, ITranslationProvider translation, ITtsProvider tts, CascadeStartParams p)
    {
        var clock = new FakeClock(Base);
        var orch = new CascadeStreamingOrchestrator(stt, translation, tts, new LatencyEventFactory(clock), clock);
        var outList = new List<CascadeOutputEvent>();
        await foreach (var e in orch.RunAsync(p, EmptyFrames(), CancellationToken.None))
        {
            outList.Add(e);
        }

        return outList;
    }

    [Fact]
    public async Task success_path_streams_segments_and_audio_in_order()
    {
        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params());

        // Terminal Done(Completed), last.
        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        var doneIdx = events.Count - 1;

        // Streaming contract: target transcript + first audio precede Done.
        var firstTargetIdx = events.FindIndex(e => e is Transcript t && t.Segment.Role == "target");
        var firstAudioIdx = events.FindIndex(e => e is Audio);
        Assert.InRange(firstTargetIdx, 0, doneIdx - 1);
        Assert.InRange(firstAudioIdx, 0, doneIdx - 1);

        // Pipeline order: source → target → audio.
        var firstSourceIdx = events.FindIndex(e => e is Transcript t && t.Segment.Role == "source");
        Assert.True(firstSourceIdx < firstTargetIdx && firstTargetIdx < firstAudioIdx);

        // Source partials precede the source final.
        var sourceFinalIdx = events.FindIndex(e => e is Transcript t && t.Segment.Role == "source" && t.Segment.IsFinal);
        Assert.Contains(
            events.Take(sourceFinalIdx),
            e => e is Transcript t && t.Segment.Role == "source" && !t.Segment.IsFinal);

        // Target final content + flag.
        var targetFinal = events.OfType<Transcript>().Single(t => t.Segment.Role == "target" && t.Segment.IsFinal);
        Assert.Equal("hola mundo", targetFinal.Segment.Text);

        // MUST latency events present, names from the ARCH-013 vocabulary.
        var latencyNames = events.OfType<Latency>().Select(l => l.Event.Name).ToHashSet();
        string[] expected =
        [
            LatencyEventNames.CascadeAudioReceived,
            LatencyEventNames.SttStarted,
            LatencyEventNames.SttFinal,
            LatencyEventNames.TranslationStarted,
            LatencyEventNames.TranslationFinal,
            LatencyEventNames.TtsStarted,
            LatencyEventNames.TtsFirstAudio,
            LatencyEventNames.TurnCompleted,
        ];
        foreach (var name in expected)
        {
            Assert.Contains(name, latencyNames);
        }

        // Audio streamed as ordered chunks.
        var audio = events.OfType<Audio>().ToList();
        Assert.Equal(3, audio.Count);
        Assert.Equal([0, 1, 2], audio.Select(a => a.Seq).ToArray());
    }

    [Fact]
    public async Task empty_transcript_short_circuits_no_translation_or_tts()
    {
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider());
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(new FakeSttProvider(FakeSttBehavior.EmptyFinal), translationSpy, ttsSpy, Params());

        Assert.Equal(0, translationSpy.Calls); // the teeth — translation NEVER invoked
        Assert.Equal(0, ttsSpy.Calls);         // the teeth — TTS NEVER invoked
        Assert.Equal("cascade.empty_transcript", events.OfType<Error>().Single().ProviderError.Code);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
    }

    [Fact]
    public async Task translation_failure_keeps_source_skips_tts()
    {
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.Error),
            ttsSpy,
            Params());

        Assert.Equal(0, ttsSpy.Calls); // TTS skipped after translation failure
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "source"); // source kept
        Assert.DoesNotContain(events, e => e is Transcript t && t.Segment.Role == "target"); // no target
        Assert.StartsWith("translation.", events.OfType<Error>().Single().ProviderError.Code);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
    }

    [Fact]
    public async Task tts_failure_keeps_both_transcripts_audio_unavailable()
    {
        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.Error),
            Params());

        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "source" && t.Segment.IsFinal);
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "target" && t.Segment.IsFinal);
        Assert.DoesNotContain(events, e => e is Audio); // no audio
        Assert.StartsWith("tts.", events.OfType<Error>().Single().ProviderError.Code);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
    }

    [Fact]
    public async Task stage_timeout_emits_retryable_timeout_error_and_skips_downstream()
    {
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider());

        // STT delays 500ms per event; STT timeout is 50ms → linked CTS CancelAfter fires first.
        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials, delayPerEvent: TimeSpan.FromMilliseconds(500)),
            translationSpy,
            new FakeTtsProvider(),
            Params(sttTimeout: TimeSpan.FromMilliseconds(50)));

        var err = events.OfType<Error>().Single().ProviderError;
        Assert.Equal("stt.timeout", err.Code);
        Assert.True(err.Retryable);
        Assert.Equal(0, translationSpy.Calls); // downstream skipped on timeout
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
    }

    [Fact]
    public async Task stamps_latency_on_first_arrival_not_synthesized()
    {
        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),          // 3 partials
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal), // 2 deltas
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),          // 3 chunks
            Params());

        var names = events.OfType<Latency>().Select(l => l.Event.Name).ToList();

        // Exactly one first_* per stage — stamped on the first arrival, not per partial/delta/chunk.
        Assert.Equal(1, names.Count(n => n == LatencyEventNames.SttFirstPartial));
        Assert.Equal(1, names.Count(n => n == LatencyEventNames.TranslationFirstToken));
        Assert.Equal(1, names.Count(n => n == LatencyEventNames.TtsFirstAudio));

        // Each first_* precedes its stage's final/complete — proves first-arrival, not a relabeled end.
        int Idx(string name) => events.FindIndex(e => e is Latency l && l.Event.Name == name);
        Assert.True(Idx(LatencyEventNames.SttFirstPartial) < Idx(LatencyEventNames.SttFinal));
        Assert.True(Idx(LatencyEventNames.TranslationFirstToken) < Idx(LatencyEventNames.TranslationFinal));
        Assert.True(Idx(LatencyEventNames.TtsFirstAudio) < Idx(LatencyEventNames.TtsComplete));
    }

    [Fact]
    public async Task two_segment_turn_streams_each_segment_in_arrival_order()
    {
        var events = await Run(
            new TwoSegmentSttProvider(),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params());

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);

        // Two finalized source segments → two finalized target segments.
        var sourceFinals = events.OfType<Transcript>().Where(t => t.Segment.Role == "source" && t.Segment.IsFinal).ToList();
        var targetFinals = events.OfType<Transcript>().Where(t => t.Segment.Role == "target" && t.Segment.IsFinal).ToList();
        Assert.Equal(["hello world", "foo bar"], sourceFinals.Select(t => t.Segment.Text).ToArray());
        Assert.Equal(2, targetFinals.Count);

        // Nested-loop proof: segment-1's target + audio stream BEFORE segment-2's source final is
        // processed (sequential per-segment sub-pipeline, not consume-all-STT-then-translate).
        var seg2SourceFinalIdx = events.FindLastIndex(e => e is Transcript t && t.Segment.Role == "source" && t.Segment.IsFinal);
        var firstTargetIdx = events.FindIndex(e => e is Transcript t && t.Segment.Role == "target");
        var firstAudioIdx = events.FindIndex(e => e is Audio);
        Assert.True(firstTargetIdx < seg2SourceFinalIdx, "segment-1 target must precede segment-2 source final");
        Assert.True(firstAudioIdx < seg2SourceFinalIdx, "segment-1 audio must precede segment-2 source final");

        // TTS invoked per segment: 3 chunks × 2 segments.
        Assert.Equal(6, events.OfType<Audio>().Count());
    }

    [Fact]
    public async Task multi_segment_downstream_does_not_trip_stt_idle_timeout()
    {
        // Each segment's translation+TTS (~150ms) far exceeds the 50ms STT timeout. The STT timer is
        // armed only during each STT MoveNextAsync (instant here) and disarmed during downstream, so
        // segment-2's STT final still arrives → Done(Completed). A whole-stream STT timer would fire
        // mid-downstream and spuriously fail the turn (regression guard for the nested-loop timeout).
        var slow = TimeSpan.FromMilliseconds(15);
        var events = await Run(
            new TwoSegmentSttProvider(),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal, delayPerEvent: slow),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete, delayPerEvent: slow),
            Params(sttTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal(2, events.OfType<Transcript>().Count(t => t.Segment.Role == "source" && t.Segment.IsFinal));
        Assert.DoesNotContain(events, e => e is Error);
    }

    [Fact]
    public async Task caller_cancellation_propagates_not_mapped_to_timeout()
    {
        // An already-cancelled caller token must propagate as OperationCanceledException (client
        // disconnect), NOT be misclassified as a retryable <stage>.timeout error.
        var clock = new FakeClock(Base);
        var orch = new CascadeStreamingOrchestrator(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(),
            new FakeTtsProvider(),
            new LatencyEventFactory(clock),
            clock);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in orch.RunAsync(Params(), EmptyFrames(), cts.Token))
            {
            }
        });
    }
}
