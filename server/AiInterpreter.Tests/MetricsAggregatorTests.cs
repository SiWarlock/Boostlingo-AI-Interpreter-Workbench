using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.3 — latency model + metrics aggregator (ARCH-013). THIN-IF-NEEDED tier (ARCH-020): cover the
// MUST pairs + cross-clock + nice->n/a, no gold-plating.
//
// Design pins enforced here:
//  - The aggregator computes from ABSOLUTE Timestamp subtraction, NOT from the per-event RelativeMs
//    (brief Q1). Every constructed event carries a deliberately-wrong RelativeMs sentinel so any
//    aggregator that reads RelativeMs would compute garbage and fail.
//  - Event names are the literal ARCH-013 wire strings (not the LatencyEventNames constants), so a
//    typo in a constant makes the aggregator look up the wrong name -> metric null -> test fails.
//    The constants are the aggregator's single source; the literals here pin the vocabulary.
public class MetricsAggregatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);

    // The aggregator must IGNORE RelativeMs and use Timestamp; a wrong sentinel proves it.
    private const long RelMsIgnored = 999_999;

    private sealed class FakeClock(DateTimeOffset fixedNow) : IClock
    {
        public DateTimeOffset UtcNow => fixedNow;
    }

    private static LatencyEvent Ev(
        string name, LatencyStage stage, int offsetMs, ClockSource clock = ClockSource.Server)
        => new(name, stage, Base.AddMilliseconds(offsetMs), RelMsIgnored, clock, new Dictionary<string, string>());

    // --- LatencyEventFactory ---

    [Fact]
    public void create_stamps_relative_ms_from_origin()
    {
        var factory = new LatencyEventFactory(new FakeClock(Base));
        var metadata = new Dictionary<string, string> { ["provider"] = "deepgram", ["model"] = "nova-3" };

        var ev = factory.Create(
            "stt.final", LatencyStage.Stt, ClockSource.Server,
            timestamp: Base.AddMilliseconds(912), origin: Base, metadata: metadata);

        Assert.Equal(912, ev.RelativeMs);
        Assert.Equal("stt.final", ev.Name);
        Assert.Equal(LatencyStage.Stt, ev.Stage);
        Assert.Equal(ClockSource.Server, ev.ClockSource);
        Assert.Equal(Base.AddMilliseconds(912), ev.Timestamp);
        Assert.Equal("deepgram", ev.Metadata["provider"]);
        Assert.Equal("nova-3", ev.Metadata["model"]);
    }

    [Fact]
    public void stamp_uses_injected_clock()
    {
        var fixedNow = Base.AddMilliseconds(500);
        var factory = new LatencyEventFactory(new FakeClock(fixedNow));

        var ev = factory.Stamp("tts.first_audio", LatencyStage.Tts, ClockSource.Server, origin: Base);

        Assert.Equal(fixedNow, ev.Timestamp);
        Assert.Equal(500, ev.RelativeMs);
    }

    [Fact]
    public void create_event_before_origin_clamps_relative_ms_to_zero()
    {
        var factory = new LatencyEventFactory(new FakeClock(Base));

        var ev = factory.Create(
            "stt.final", LatencyStage.Stt, ClockSource.Server,
            timestamp: Base.AddMilliseconds(-5), origin: Base);

        Assert.Equal(0, ev.RelativeMs); // single-event relativeMs is monotonic vs a same-clock origin
    }

    // --- MetricsAggregator ---

    // Full cascade turn (no clock skew — all offsets off one base instant; clock-source set per the
    // ARCH-013 rule but with the same underlying wall clock, so subtraction is exact). Cross-clock
    // skew is isolated in its own test.
    private static LatencyEvent[] CascadeTurn() =>
    [
        Ev("turn.recording.started", LatencyStage.Capture, 0, ClockSource.Browser),
        Ev("cascade.audio.received", LatencyStage.Stt, 50),                 // STT stage origin
        Ev("stt.first_partial", LatencyStage.Stt, 250),                    // nice
        Ev("turn.recording.stopped", LatencyStage.Capture, 1000, ClockSource.Browser),
        Ev("stt.final", LatencyStage.Stt, 1100),
        Ev("translation.started", LatencyStage.Translation, 1120),          // translation stage origin
        Ev("translation.first_token", LatencyStage.Translation, 1300),     // nice
        Ev("translation.final", LatencyStage.Translation, 1500),
        Ev("tts.started", LatencyStage.Tts, 1520),                          // tts stage origin
        Ev("tts.first_audio", LatencyStage.Tts, 1700),
        Ev("playback.started", LatencyStage.Playback, 1800, ClockSource.Browser),
        Ev("tts.complete", LatencyStage.Tts, 2000),                        // nice
        Ev("turn.completed", LatencyStage.Capture, 2100),
    ];

    [Fact]
    public void compute_cascade_must_pairs_computes_universal_and_stage_metrics()
    {
        var m = new MetricsAggregator().Compute(CascadeTurn());

        // Universal (ARCH-013)
        Assert.Equal(1000d, m.AudioDurationMs);          // recording.stopped - recording.started
        Assert.Equal(2100d, m.TotalTurnMs);              // turn.completed - recording.started
        Assert.Equal(700d, m.SpeechEndToFirstAudioMs);   // tts.first_audio - recording.stopped
        Assert.Equal(800d, m.SpeechEndToPlaybackMs);     // playback.started - recording.stopped

        // Cascade stage (each measured from its stage-start origin)
        Assert.Equal(200d, m.SttFirstPartialMs);         // stt.first_partial - cascade.audio.received
        Assert.Equal(1050d, m.SttFinalMs);               // stt.final - cascade.audio.received
        Assert.Equal(180d, m.TranslationFirstTokenMs);   // translation.first_token - translation.started
        Assert.Equal(380d, m.TranslationFinalMs);        // translation.final - translation.started
        Assert.Equal(180d, m.TtsFirstAudioMs);           // tts.first_audio - tts.started
        Assert.Equal(480d, m.TtsCompleteMs);             // tts.complete - tts.started
    }

    [Fact]
    public void compute_cross_clock_pair_uses_absolute_timestamps()
    {
        // Browser speech-end + server first-audio; the +30ms in the server offset is clock skew.
        // The aggregator subtracts absolute Timestamps and tolerates the skew (no clamp, no throw).
        var serverAhead = new[]
        {
            Ev("turn.recording.stopped", LatencyStage.Capture, 1000, ClockSource.Browser),
            Ev("tts.first_audio", LatencyStage.Tts, 1730, ClockSource.Server),
        };

        Assert.Equal(730d, new MetricsAggregator().Compute(serverAhead).SpeechEndToFirstAudioMs);

        // Server-BEHIND skew makes the raw diff slightly negative. The aggregator preserves it
        // (NOT clamped to 0 — that's the factory's single-event rule, test 3) and does not throw.
        // This is the ARCH-013 skew-honesty contract: small skew is disclosed, not hidden. Guards
        // against a stray Math.Max(0,…) silently sneaking into the aggregator.
        var serverBehind = new[]
        {
            Ev("turn.recording.stopped", LatencyStage.Capture, 1000, ClockSource.Browser),
            Ev("tts.first_audio", LatencyStage.Tts, 990, ClockSource.Server),
        };

        Assert.Equal(-10d, new MetricsAggregator().Compute(serverBehind).SpeechEndToFirstAudioMs);
    }

    [Fact]
    public void compute_missing_nice_tier_events_yield_null_not_error()
    {
        // Drop the three NICE events: stt.first_partial, translation.first_token, tts.complete.
        var events = CascadeTurn()
            .Where(e => e.Name is not ("stt.first_partial" or "translation.first_token" or "tts.complete"))
            .ToArray();

        var m = new MetricsAggregator().Compute(events);

        Assert.Null(m.SttFirstPartialMs);
        Assert.Null(m.TranslationFirstTokenMs);
        Assert.Null(m.TtsCompleteMs);

        // MUST metrics still computed.
        Assert.Equal(1050d, m.SttFinalMs);
        Assert.Equal(380d, m.TranslationFinalMs);
        Assert.Equal(180d, m.TtsFirstAudioMs);
        Assert.Equal(2100d, m.TotalTurnMs);
        Assert.Equal(700d, m.SpeechEndToFirstAudioMs);
    }

    [Fact]
    public void compute_realtime_must_pairs_computes_realtime_metrics()
    {
        // Realtime turn: connected, speech-end, first-audio-delta, playback, completed.
        // No turn.recording.started, no realtime.session.connecting, no first_transcript_delta.
        var events = new[]
        {
            Ev("realtime.session.connected", LatencyStage.Realtime, 0),
            Ev("turn.recording.stopped", LatencyStage.Capture, 500, ClockSource.Browser),
            Ev("realtime.first_audio_delta", LatencyStage.Realtime, 1200),
            Ev("playback.started", LatencyStage.Playback, 1300, ClockSource.Browser),
            Ev("turn.completed", LatencyStage.Realtime, 1500),
        };

        var m = new MetricsAggregator().Compute(events);

        // Realtime metrics computed from present endpoints.
        Assert.Equal(700d, m.RealtimeFirstAudioDeltaMs);  // first_audio_delta - recording.stopped
        Assert.Equal(100d, m.RealtimePlaybackMs);         // playback.started - first_audio_delta

        // Realtime metrics whose endpoints are absent -> null (never error).
        Assert.Null(m.RealtimeConnectMs);                 // no realtime.session.connecting origin
        Assert.Null(m.RealtimeFirstTranscriptDeltaMs);    // nice; absent

        // Output-audio selection falls back to realtime.first_audio_delta for the universal metric.
        Assert.Equal(700d, m.SpeechEndToFirstAudioMs);

        // Cascade stage metrics are null by absence (mode-implicit n/a).
        Assert.Null(m.SttFinalMs);
        Assert.Null(m.TranslationFinalMs);
        Assert.Null(m.TtsFirstAudioMs);
        Assert.Null(m.AudioDurationMs);                   // no turn.recording.started
    }

    [Fact]
    public void compute_empty_event_list_all_metrics_null_no_throw()
    {
        var m = new MetricsAggregator().Compute(Array.Empty<LatencyEvent>());

        // Representative of each tier — all null, and the call did not throw.
        Assert.Null(m.SpeechEndToFirstAudioMs);
        Assert.Null(m.TotalTurnMs);
        Assert.Null(m.SttFinalMs);
        Assert.Null(m.RealtimeFirstAudioDeltaMs);
    }
}
