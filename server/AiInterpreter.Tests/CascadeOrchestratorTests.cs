using System.Runtime.CompilerServices;
using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

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

    // J.1 — recording decorators capturing the LAST request the orchestrator built, so a bidir test can assert
    // the resolved Source/Target (direction flip) + the empty TTS voice (VoiceByLanguage delegation).
    private sealed class RecordingTranslationProvider(ITranslationProvider inner) : ITranslationProvider
    {
        public TranslationRequest? LastRequest { get; private set; }

        public IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return inner.TranslateAsync(request, ct);
        }
    }

    private sealed class RecordingTtsProvider(ITtsProvider inner) : ITtsProvider
    {
        public TtsRequest? LastRequest { get; private set; }

        public IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, CancellationToken ct)
        {
            LastRequest = request;
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

    // I.1 — a fake that yields SttStarted then a scripted event list verbatim (so a test can interleave an
    // SttUtteranceEnd endpointing marker among partials/finals).
    private sealed class ScriptedSttProvider(params SttEvent[] events) : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new SttStarted(DateTimeOffset.UtcNow);
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }
        }
    }

    private static CascadeStartParams Params(TimeSpan? sttTimeout = null, bool autoVad = false, bool bidirectional = false) =>
        new("s1", "t1", EnToEs, "linear16", 16000, "gpt-5-nano", "alloy", SttTimeout: sttTimeout, AutoVad: autoVad, Bidirectional: bidirectional);

    private static async Task<List<CascadeOutputEvent>> Run(
        ISttProvider stt, ITranslationProvider translation, ITtsProvider tts, CascadeStartParams p)
    {
        var clock = new FakeClock(Base);
        var orch = new CascadeStreamingOrchestrator(
            stt, translation, tts, new LatencyEventFactory(clock), clock,
            NullLogger<CascadeStreamingOrchestrator>.Instance);
        var outList = new List<CascadeOutputEvent>();
        await foreach (var e in orch.RunAsync(p, EmptyFrames(), CancellationToken.None))
        {
            outList.Add(e);
        }

        return outList;
    }

    // === J.1 — bidirectional per-utterance direction flip ===

    [Fact]
    public async Task bidirectional_flips_direction_from_detected_language()
    {
        // bidir on; the STT final detects Spanish → the orchestrator translates Es→En (the OTHER language) and
        // targets the TTS at En, delegating the voice to VoiceByLanguage by passing an EMPTY request voice.
        var translation = new RecordingTranslationProvider(new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal));
        var tts = new RecordingTtsProvider(new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete));
        var stt = new ScriptedSttProvider(new SttFinal("hola mundo", DateTimeOffset.UtcNow, LanguageCode.Es));

        await Run(stt, translation, tts, Params(bidirectional: true));

        Assert.Equal(LanguageCode.Es, translation.LastRequest!.SourceLanguage); // flipped: detected source
        Assert.Equal(LanguageCode.En, translation.LastRequest!.TargetLanguage); // the OTHER language
        Assert.Equal(LanguageCode.En, tts.LastRequest!.TargetLanguage);          // TTS voice resolves to En
        Assert.Equal(string.Empty, tts.LastRequest!.Voice);                      // empty → VoiceByLanguage[en] drives it
    }

    [Fact]
    public async Task bidirectional_emits_direction_event_before_target_transcript()
    {
        // A Direction{Es→En} event is emitted per resolved segment, BEFORE the target transcript (so the FE can
        // stamp the live turn's direction before rendering the translation).
        var stt = new ScriptedSttProvider(new SttFinal("hola mundo", DateTimeOffset.UtcNow, LanguageCode.Es));

        var events = await Run(
            stt,
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params(bidirectional: true));

        var directionIdx = events.FindIndex(e => e is Direction);
        var dir = Assert.IsType<Direction>(events[directionIdx]);
        Assert.Equal(LanguageCode.Es, dir.Resolved.Source);
        Assert.Equal(LanguageCode.En, dir.Resolved.Target);

        var firstTargetIdx = events.FindIndex(e => e is Transcript t && t.Segment.Role == "target");
        Assert.InRange(firstTargetIdx, 0, events.Count - 1);
        Assert.True(directionIdx < firstTargetIdx); // direction precedes the target transcript
    }

    [Fact]
    public async Task bidirectional_null_detection_falls_back_to_start_direction()
    {
        // bidir on but the final carries NO detected language (null) → fall back to the start-frame direction
        // (En→Es); no wrong-direction translation.
        var translation = new RecordingTranslationProvider(new FakeTranslationProvider());
        var stt = new ScriptedSttProvider(new SttFinal("hello world", DateTimeOffset.UtcNow, DetectedLanguage: null));

        await Run(stt, translation, new FakeTtsProvider(), Params(bidirectional: true));

        Assert.Equal(LanguageCode.En, translation.LastRequest!.SourceLanguage); // start-frame, NOT flipped
        Assert.Equal(LanguageCode.Es, translation.LastRequest!.TargetLanguage);
    }

    [Fact]
    public async Task one_direction_mode_does_not_flip_or_emit_direction()
    {
        // bidir OFF (default): even when the final carries a detected language, the orchestrator does NOT flip,
        // does NOT emit a Direction event, and KEEPS the frame's TTS voice — byte-identical to today.
        var translation = new RecordingTranslationProvider(new FakeTranslationProvider());
        var tts = new RecordingTtsProvider(new FakeTtsProvider());
        var stt = new ScriptedSttProvider(new SttFinal("hola mundo", DateTimeOffset.UtcNow, LanguageCode.Es));

        var events = await Run(stt, translation, tts, Params()); // bidirectional: false

        Assert.DoesNotContain(events, e => e is Direction);
        Assert.Equal(LanguageCode.En, translation.LastRequest!.SourceLanguage); // p.Direction, not flipped to Es
        Assert.Equal(LanguageCode.Es, translation.LastRequest!.TargetLanguage);
        Assert.Equal("alloy", tts.LastRequest!.Voice); // frame voice preserved (not emptied)
    }

    // === J.6 — auto-VAD empty turn = silence (Completed), not failed (Phase-J smoke Finding 1) ===

    [Theory]
    [InlineData(true)]   // exercise the auto-VAD utterance-end terminal path
    [InlineData(false)]  // exercise the stream-end terminal path (the shared TerminalFailure() covers both)
    public async Task autovad_empty_only_finals_completes_not_failed(bool useUtteranceEnd)
    {
        // auto-VAD: an empty-only final (a silent gap / ended-while-waiting in the VAD-delimited continuous
        // loop) → Completed-silence, NO cascade.empty_transcript error. Both terminal paths honor the scoping.
        var ts = DateTimeOffset.UtcNow;
        var script = useUtteranceEnd
            ? new SttEvent[] { new SttFinal(string.Empty, ts), new SttUtteranceEnd(ts) }
            : new SttEvent[] { new SttFinal(string.Empty, ts) };

        var events = await Run(
            new ScriptedSttProvider(script), new FakeTranslationProvider(), new FakeTtsProvider(), Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
    }

    [Fact]
    public async Task manual_empty_only_finals_still_fails()
    {
        // Manual mode: an empty-only final is deliberately-recorded silence → STILL cascade.empty_transcript +
        // Failed (regression preserved — the user's silence is a real signal).
        var events = await Run(
            new ScriptedSttProvider(new SttFinal(string.Empty, DateTimeOffset.UtcNow)),
            new FakeTranslationProvider(), new FakeTtsProvider(), Params(autoVad: false));

        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal("cascade.empty_transcript", Assert.IsType<Error>(events.First(e => e is Error)).ProviderError.Code);
    }

    [Fact]
    public async Task autovad_pure_silence_completes()
    {
        // auto-VAD, NO finals at all (pure silence) → Completed (unchanged §31 Q4; sawEmptyFinal never set).
        var events = await Run(
            new ScriptedSttProvider(), new FakeTranslationProvider(), new FakeTtsProvider(), Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
    }

    [Fact]
    public async Task autovad_has_content_completes()
    {
        // auto-VAD, a non-empty final → the loop's normal turn: Completed with content (translation + TTS ran).
        var events = await Run(
            new ScriptedSttProvider(new SttFinal("hola mundo", DateTimeOffset.UtcNow)),
            new FakeTranslationProvider(), new FakeTtsProvider(), Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "target"); // content flowed downstream
    }

    [Fact]
    public async Task autovad_dangling_partial_still_unknown()
    {
        // auto-VAD, a non-empty PARTIAL then no final (a LOST final) → still stt.unknown + Failed. A lost final
        // is a real failure, NOT silenced by the auto-VAD empty scoping (don't over-broaden — pendingPartial arm
        // is untouched).
        var events = await Run(
            new ScriptedSttProvider(new SttPartial("hel", DateTimeOffset.UtcNow)),
            new FakeTranslationProvider(), new FakeTtsProvider(), Params(autoVad: true));

        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal("stt.unknown", Assert.IsType<Error>(events.First(e => e is Error)).ProviderError.Code);
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

    // 052 note: a single empty final (no non-empty ever) now fails cascade.empty_transcript at STREAM END
    // (skipped per-final, then failed-closed at end) rather than on the first empty final — the OUTCOME is
    // unchanged (no translation/TTS; one empty_transcript Error; Done(Failed)), so the assertions still hold.
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

    // === C.4b — stream-without-terminal hardening (ARCH-011/018: fail closed, never silently skip/complete) ===

    // A translation stream that STARTS (Started + partials) but ends with NO TranslationFinal — unreachable
    // via the B.2 fakes; a real SSE that ends without response.completed produces this.
    private sealed class TranslationStartedNoFinalProvider : ITranslationProvider
    {
        public async IAsyncEnumerable<TranslationEvent> TranslateAsync(
            TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TranslationStarted(DateTimeOffset.UtcNow);
            yield return new TranslationPartial("hol", DateTimeOffset.UtcNow);
            // stream ends — no TranslationFinal, no TranslationFailed
        }
    }

    // A TTS stream that STARTS (Started + first audio + a chunk) but ends with NO TtsComplete.
    private sealed class TtsStartedNoCompleteProvider : ITtsProvider
    {
        public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
            TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TtsStarted(DateTimeOffset.UtcNow);
            yield return new TtsFirstAudio("audio/mpeg", DateTimeOffset.UtcNow);
            yield return new TtsAudioChunk(new byte[] { 1, 2 }, 0, DateTimeOffset.UtcNow);
            // stream ends — no TtsComplete, no TtsFailed
        }
    }

    // An STT stream that emits a partial but NO final (a lost final — a real failure per Q4).
    private sealed class SttPartialNoFinalProvider : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new SttStarted(DateTimeOffset.UtcNow);
            yield return new SttPartial("hello", DateTimeOffset.UtcNow);
            // stream ends — partial seen, no final
        }
    }

    // An STT stream that ends cleanly with NO partial and NO final (silence — valid per Q4 → Completed).
    private sealed class SttSilenceProvider : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new SttStarted(DateTimeOffset.UtcNow);
            // stream ends — no partial, no final
        }
    }

    [Fact]
    public async Task translation_stream_without_final_fails_closed()
    {
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials), // a real STT final → translation invoked
            new TranslationStartedNoFinalProvider(),
            ttsSpy,
            Params());

        Assert.Equal("translation.unknown", events.OfType<Error>().Single().ProviderError.Code);
        Assert.False(events.OfType<Error>().Single().ProviderError.Retryable);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "source"); // source kept
        Assert.DoesNotContain(events, e => e is Transcript t && t.Segment.Role == "target" && t.Segment.IsFinal); // no target final
        Assert.Equal(0, ttsSpy.Calls); // TTS skipped — translation never finalized
    }

    [Fact]
    public async Task tts_stream_without_complete_fails_closed()
    {
        var events = await Run(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new TtsStartedNoCompleteProvider(),
            Params());

        Assert.Equal("tts.unknown", events.OfType<Error>().Single().ProviderError.Code);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "source" && t.Segment.IsFinal); // both transcripts kept
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "target" && t.Segment.IsFinal);
    }

    [Fact]
    public async Task stt_partials_without_final_fails_closed()
    {
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider());
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(new SttPartialNoFinalProvider(), translationSpy, ttsSpy, Params());

        Assert.Equal("stt.unknown", events.OfType<Error>().Single().ProviderError.Code); // lost final = real failure
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal(0, translationSpy.Calls); // downstream never invoked
        Assert.Equal(0, ttsSpy.Calls);
    }

    [Fact]
    public async Task stt_clean_end_without_final_completes()
    {
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider());
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(new SttSilenceProvider(), translationSpy, ttsSpy, Params());

        // Silence (no partial, no final) is valid recording → Completed, no error (Q4).
        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
        Assert.Equal(0, translationSpy.Calls);
        Assert.Equal(0, ttsSpy.Calls);
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
            clock,
            NullLogger<CascadeStreamingOrchestrator>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in orch.RunAsync(Params(), EmptyFrames(), cts.Token))
            {
            }
        });
    }

    // === 075 (supersedes 057c) — tts.started anchored at TTS request-INITIATION, not the provider event ===

    // A settable clock the round-trip TTS fake advances, so the to-first-audio latency is a controlled delta.
    private sealed class SettableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    // Models the live cascade-TTS timing: advances the shared clock by `roundTrip` (the request→provider trip)
    // BEFORE the first event, then yields TtsStarted+TtsFirstAudio SYNCHRONOUSLY (the live ≈0 symptom — both at
    // the same clock value), then advances `synthesis` before TtsComplete. The orchestrator stamps tts.started at
    // initiation (pre-enumeration), so these advances land BETWEEN the start anchor and the provider stamps.
    private sealed class RoundTripTtsProvider(SettableClock clock, TimeSpan roundTrip, TimeSpan synthesis) : ITtsProvider
    {
        public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
            TtsRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            clock.UtcNow += roundTrip;
            yield return new TtsStarted(clock.UtcNow);
            yield return new TtsFirstAudio("audio/mpeg", clock.UtcNow); // synchronous with TtsStarted (the ≈0 case)
            yield return new TtsAudioChunk(new byte[] { 1, 2 }, 0, clock.UtcNow);
            clock.UtcNow += synthesis;
            yield return new TtsComplete("audio/mpeg", clock.UtcNow);
        }
    }

    private static async Task<List<CascadeOutputEvent>> RunWithClock(SettableClock clock, ITtsProvider tts)
    {
        var orch = new CascadeStreamingOrchestrator(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            tts, new LatencyEventFactory(clock), clock,
            NullLogger<CascadeStreamingOrchestrator>.Instance);

        var events = new List<CascadeOutputEvent>();
        await foreach (var e in orch.RunAsync(Params(), EmptyFrames(), CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static readonly TimeSpan TtsRoundTrip = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TtsSynthesis = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task tts_started_stamped_at_request_initiation()
    {
        // ⭐ tts.started is anchored at TTS request-INITIATION (clock = Base, before the provider round-trip),
        // NOT on the provider's first event arrival. The provider then advances +300 ms and yields
        // TtsStarted+TtsFirstAudio synchronously (the live ≈0 case). So TtsFirstAudioMs reflects the REAL
        // to-first-audio latency (300 ms), not ≈0. (ARCH-013 — honest instrumentation.)
        var clock = new SettableClock(Base);
        var events = await RunWithClock(clock, new RoundTripTtsProvider(clock, TtsRoundTrip, TtsSynthesis));

        var started = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TtsStarted).Event;
        var firstAudio = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TtsFirstAudio).Event;
        Assert.Equal(Base, started.Timestamp);                                  // stamped at initiation, not the provider event
        Assert.Equal(300d, (firstAudio.Timestamp - started.Timestamp).TotalMilliseconds); // real round-trip, not ≈0
        // No negative stage gap: tts.started (initiation) lands at/after translation.final (TTS initiates only
        // after translation produced the target text).
        var translationFinal = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TranslationFinal).Event;
        Assert.True(started.Timestamp >= translationFinal.Timestamp,
            $"tts.started ({started.Timestamp:o}) must be at/after translation.final ({translationFinal.Timestamp:o})");
    }

    [Fact]
    public async Task tts_first_audio_still_on_provider_event()
    {
        // Honesty: only the START anchor moved — tts.first_audio is STILL stamped on the real provider
        // first-audio event (clock = Base + round-trip), never synthesized or back-dated. A one-directional
        // regression guard (not a RED-phase pin — it's green under the old code too): it FAILS if a future
        // change wrongly moves tts.first_audio to initiation as well.
        var clock = new SettableClock(Base);
        var events = await RunWithClock(clock, new RoundTripTtsProvider(clock, TtsRoundTrip, TtsSynthesis));

        var firstAudio = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TtsFirstAudio).Event;
        Assert.Equal(Base.Add(TtsRoundTrip), firstAudio.Timestamp); // the real provider first-audio moment, unchanged
    }

    [Fact]
    public async Task tts_complete_measures_from_initiation()
    {
        // The TTS stage now BEGINS at initiation, so TtsCompleteMs spans initiation→complete (round-trip +
        // synthesis = 500 ms), closing the previously-uncounted translation.final→provider-ack gap.
        var clock = new SettableClock(Base);
        var events = await RunWithClock(clock, new RoundTripTtsProvider(clock, TtsRoundTrip, TtsSynthesis));

        var started = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TtsStarted).Event;
        var complete = events.OfType<Latency>().Single(l => l.Event.Name == LatencyEventNames.TtsComplete).Event;
        Assert.Equal(500d, (complete.Timestamp - started.Timestamp).TotalMilliseconds); // from initiation, not the provider event
    }

    // ===== I.1 — cascade auto-finalize on Deepgram utterance-end (Phase I; auto-VAD) =====

    private static readonly SttEvent UttEnd = new SttUtteranceEnd(Base);

    [Fact]
    public async Task auto_vad_finalizes_turn_on_first_utterance_end()
    {
        // auto-VAD ON: the FIRST utterance-end finalizes the whole turn (one turn per recording, ending on
        // detected silence) — the SttFinal AFTER the utterance-end is never processed. The REAL stt.final is
        // stamped (no synthesized terminal); the turn Completes via the SAME post-loop terminal as a stop.
        var events = await Run(
            new ScriptedSttProvider(new SttFinal("hello", Base), UttEnd, new SttFinal("world", Base)),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        var sourceFinals = events.OfType<Transcript>().Where(t => t.Segment.Role == "source" && t.Segment.IsFinal).ToList();
        Assert.Single(sourceFinals);                       // only "hello" — "world" past the utterance-end is dropped
        Assert.Equal("hello", sourceFinals[0].Segment.Text);
        Assert.Single(events.OfType<Latency>().Where(l => l.Event.Name == LatencyEventNames.SttFinal)); // ONE real final stamp
    }

    [Fact]
    public async Task manual_mode_ignores_utterance_end()
    {
        // auto-VAD OFF (regression): the utterance-end marker is IGNORED — the turn finalizes on stream-end
        // (stop) as today, so BOTH finals are processed (the multi-segment-per-turn behavior is preserved).
        var events = await Run(
            new ScriptedSttProvider(new SttFinal("hello", Base), UttEnd, new SttFinal("world", Base)),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params(autoVad: false));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        var sourceFinals = events.OfType<Transcript>().Where(t => t.Segment.Role == "source" && t.Segment.IsFinal).ToList();
        Assert.Equal(2, sourceFinals.Count);               // both "hello" and "world" — utterance-end did not finalize
    }

    [Fact]
    public async Task auto_vad_utterance_end_after_only_empty_finals_completes_silence()
    {
        // J.6 (SUPERSEDES the old §31-composed empty_transcript-on-auto-VAD): an utterance-end after ONLY
        // empty/whitespace finals in auto-VAD is a silence/gap (the VAD-delimited continuous loop) → Completed-
        // silence, NOT a cascade.empty_transcript failure (a false error would pollute the comparison errorCount).
        // Manual mode still fails (manual_empty_only_finals_still_fails); a lost final still → stt.unknown.
        var events = await Run(
            new ScriptedSttProvider(new SttFinal("   ", Base), UttEnd),
            new FakeTranslationProvider(),
            new FakeTtsProvider(),
            Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
    }

    [Fact]
    public async Task auto_vad_utterance_end_pure_silence_completes()
    {
        // §31 silence: an utterance-end with NO final at all (pure silence) → valid Completed (not a failure).
        var events = await Run(
            new ScriptedSttProvider(UttEnd),
            new FakeTranslationProvider(),
            new FakeTtsProvider(),
            Params(autoVad: true));

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
    }

    [Fact]
    public async Task auto_vad_utterance_end_after_dangling_partial_fails_unknown()
    {
        // §22 fail-closed under auto-VAD: a partial arrived but NO final before the utterance-end (Deepgram VAD
        // can fire mid-utterance) → a lost final → stt.unknown (the pendingPartial branch of the shared
        // TerminalFailure), NOT a false Completed. Pins the §22 invariant for the new auto-VAD terminal.
        var events = await Run(
            new ScriptedSttProvider(new SttPartial("hel", Base), UttEnd),
            new FakeTranslationProvider(),
            new FakeTtsProvider(),
            Params(autoVad: true));

        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal("stt.unknown", events.OfType<Error>().Single().ProviderError.Code); // lost final = real failure
    }

    // === 052 — empty-final tolerance (skip spurious empty/whitespace finals; fail empty_transcript only
    // when NO non-empty final ever arrives). Deepgram emits empty finals around real speech (leading
    // silence / VAD boundary / a trailing empty after the real content) — failing on one killed correct
    // turns (the live trailing-empty case). ===

    // Emits SttStarted then one SttFinal per supplied string (no partials needed for these cases).
    private sealed class ScriptedFinalsSttProvider(params string[] finals) : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new SttStarted(DateTimeOffset.UtcNow);
            foreach (var text in finals)
            {
                yield return new SttFinal(text, DateTimeOffset.UtcNow);
            }
        }
    }

    [Fact]
    public async Task real_final_then_trailing_empty_succeeds()
    {
        // ⭐ The exact live case: a fully-correct turn translated + played, then a TRAILING empty final
        // arrived — it must be skipped, NOT fail the turn.
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal));
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete));

        var events = await Run(new ScriptedFinalsSttProvider("hola", ""), translationSpy, ttsSpy, Params());

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);   // the trailing empty did NOT fail the correct turn
        Assert.Equal(1, translationSpy.Calls);            // the real final drove translation; the empty one didn't
        Assert.Equal(1, ttsSpy.Calls);
    }

    [Fact]
    public async Task empty_leading_final_then_real_final_succeeds()
    {
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal));
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete));

        var events = await Run(new ScriptedFinalsSttProvider("", "hola"), translationSpy, ttsSpy, Params());

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
        Assert.Equal(1, translationSpy.Calls);            // the leading empty was skipped, "hola" translated
        Assert.Equal(1, ttsSpy.Calls);
        Assert.Contains(events, e => e is Transcript t && t.Segment.Role == "source" && t.Segment.IsFinal && t.Segment.Text == "hola");
    }

    [Fact]
    public async Task all_empty_finals_fail_empty_transcript_at_stream_end()
    {
        // No non-empty final EVER → genuinely empty → fail closed at stream end (translation/TTS never run).
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider());
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider());

        var events = await Run(new ScriptedFinalsSttProvider("", "  "), translationSpy, ttsSpy, Params());

        Assert.Equal("cascade.empty_transcript", events.OfType<Error>().Single().ProviderError.Code);
        Assert.Equal(TurnStatus.Failed, Assert.IsType<Done>(events[^1]).Status);
        Assert.Equal(0, translationSpy.Calls);            // never translated an empty final — the teeth
        Assert.Equal(0, ttsSpy.Calls);
    }

    [Fact]
    public async Task single_nonempty_final_succeeds_regression()
    {
        var events = await Run(
            new ScriptedFinalsSttProvider("hola"),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            Params());

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);
    }

    // === 069 Bug B — a trailing spurious empty PARTIAL must not false-fail a successful turn ===

    [Fact]
    public async Task real_final_then_trailing_empty_partial_succeeds()
    {
        // ⭐ The live artifact (069): a fully-correct turn (final translation + completed TTS), then a TRAILING
        // spurious empty PARTIAL (Deepgram teardown noise, §30) for the next segment. Today it sets
        // pendingPartial → the stream-end fail-closed (§22) emits stt.unknown → Done(Failed), false-failing a
        // successful turn (+ poisoning errorCount). The empty partial must be SKIPPED (mirrors §31's empty-final
        // skip): no pendingPartial, no empty source segment, no error — the turn Completes.
        var translationSpy = new CountingTranslationProvider(new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal));
        var ttsSpy = new CountingTtsProvider(new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete));

        var events = await Run(
            new ScriptedSttProvider(new SttFinal("hola", Base), new SttPartial("", Base)),
            translationSpy, ttsSpy, Params());

        Assert.Equal(TurnStatus.Completed, Assert.IsType<Done>(events[^1]).Status);
        Assert.DoesNotContain(events, e => e is Error);   // no spurious stt.unknown
        // The trailing empty partial is not emitted as a source segment (cleans the transcript trail).
        Assert.DoesNotContain(events, e => e is Transcript t
            && t.Segment.Role == "source" && !t.Segment.IsFinal && t.Segment.Text == "");
        Assert.Equal(1, translationSpy.Calls);            // the real final drove translation; the empty partial didn't
        Assert.Equal(1, ttsSpy.Calls);
    }
}
