using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.7b — SessionSummaryService (ARCH-009 on-demand summary / ARCH-013 per-turn→per-mode aggregation /
// ARCH-005 fills SessionSummary/ModeSummary/WerSummary). IMPORTANT tier (ARCH-020): aggregation math.
//
// Composes the REAL MetricsAggregator (the seam under reuse — never re-implement metric math) + a
// deterministic IClock. Turns carry literal ARCH-013 wire-name LatencyEvents so the aggregator
// produces known TurnMetrics; the service averages those. Pins: average over non-null only (absent
// on all → null, never 0/throw — lesson §7), cascade-stage fields null for realtime turns, WER
// unbounded (no clamp >1.0 — lesson §10), pure compute (no mutation of the session).
public class SessionSummaryServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ComputedAt = Base.AddMinutes(10);

    private sealed class FakeClock(DateTimeOffset fixedNow) : IClock
    {
        public DateTimeOffset UtcNow => fixedNow;
    }

    private static SessionSummaryService Service(DateTimeOffset? now = null) =>
        new(new MetricsAggregator(), new FakeClock(now ?? ComputedAt));

    private static LatencyEvent Ev(string name, DateTimeOffset ts) =>
        new(name, LatencyStage.Stt, ts, 0, ClockSource.Server, new Dictionary<string, string>());

    // A cascade turn whose aggregator metrics equal the requested values. tts.first_audio is the
    // cascade "first output audio", so SpeechEndToFirstAudio reads from it; ttsStart is derived back
    // from it. A null metric omits its events so the aggregator yields null for that field.
    private static InterpretationTurn CascadeTurn(
        string turnId,
        double? sttFinalMs = 200, double? translationFinalMs = 300, double? ttsFirstAudioMs = 150,
        double speechEndToFirstAudioMs = 500, double speechEndToPlaybackMs = 800,
        decimal? costPerMin = null, int errorCount = 0, double? wer = null)
    {
        var speechEnd = Base.AddMilliseconds(1000);
        var events = new List<LatencyEvent>
        {
            Ev(LatencyEventNames.TurnRecordingStarted, Base),
            Ev(LatencyEventNames.TurnRecordingStopped, speechEnd),
            Ev(LatencyEventNames.PlaybackStarted, speechEnd.AddMilliseconds(speechEndToPlaybackMs)),
        };

        if (sttFinalMs is { } stt)
        {
            var sttOrigin = Base.AddMilliseconds(50);
            events.Add(Ev(LatencyEventNames.CascadeAudioReceived, sttOrigin));
            events.Add(Ev(LatencyEventNames.SttFinal, sttOrigin.AddMilliseconds(stt)));
        }
        if (translationFinalMs is { } tr)
        {
            var trStart = Base.AddMilliseconds(1100);
            events.Add(Ev(LatencyEventNames.TranslationStarted, trStart));
            events.Add(Ev(LatencyEventNames.TranslationFinal, trStart.AddMilliseconds(tr)));
        }
        if (ttsFirstAudioMs is { } tts)
        {
            var firstAudio = speechEnd.AddMilliseconds(speechEndToFirstAudioMs);
            events.Add(Ev(LatencyEventNames.TtsStarted, firstAudio.AddMilliseconds(-tts)));
            events.Add(Ev(LatencyEventNames.TtsFirstAudio, firstAudio));
        }

        return Turn(turnId, InterpretationMode.Cascade, events, costPerMin, errorCount, wer);
    }

    // A realtime turn: universal + realtime events only, NO cascade stage events (so the cascade-only
    // ModeSummary fields aggregate to null). realtime.first_audio_delta is the first output audio.
    private static InterpretationTurn RealtimeTurn(
        string turnId, double speechEndToFirstAudioMs = 400, double speechEndToPlaybackMs = 600,
        decimal? costPerMin = null, int errorCount = 0, double? wer = null)
    {
        var speechEnd = Base.AddMilliseconds(1000);
        var events = new List<LatencyEvent>
        {
            Ev(LatencyEventNames.TurnRecordingStarted, Base),
            Ev(LatencyEventNames.TurnRecordingStopped, speechEnd),
            Ev(LatencyEventNames.RealtimeFirstAudioDelta, speechEnd.AddMilliseconds(speechEndToFirstAudioMs)),
            Ev(LatencyEventNames.PlaybackStarted, speechEnd.AddMilliseconds(speechEndToPlaybackMs)),
        };
        return Turn(turnId, InterpretationMode.Realtime, events, costPerMin, errorCount, wer);
    }

    private static InterpretationTurn Turn(
        string turnId, InterpretationMode mode, List<LatencyEvent> events,
        decimal? costPerMin, int errorCount, double? wer)
    {
        var dir = new LanguageDirection(LanguageCode.En, LanguageCode.Es);
        var cost = costPerMin is null
            ? null
            : new CostEstimate("p", "m", "composite", 0m, costPerMin,
                new Dictionary<string, decimal>(), "v", Array.Empty<string>());
        var werResult = wer is null
            ? null
            : new WerResult("p", "r", "h", "r", "h", 0, 0, 0, 2, wer.Value);
        var errors = new List<ProviderError>();
        for (var i = 0; i < errorCount; i++)
        {
            errors.Add(new ProviderError("p", "stt", "stt.failed", "msg", false, null));
        }

        // A real interpretation turn carries ≥1 transcript segment; the J.6 0-transcript exclusion keys on
        // this, so the content-turn fixtures include one (silence/failed-empty variants strip it via `with`).
        var transcripts = new List<TranscriptSegment>
        {
            new("src-0", "source", "content", IsFinal: true, "deepgram", Base, ClockSource.Server),
        };

        return new InterpretationTurn(
            turnId, mode, dir, Base, Base.AddSeconds(2), 2000,
            transcripts, events, cost, werResult, errors,
            TurnStatus.Completed, "gpt-5-nano", "alloy");
    }

    private static InterpretationSession Session(params InterpretationTurn[] turns)
    {
        var dir = new LanguageDirection(LanguageCode.En, LanguageCode.Es);
        var profile = new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy");
        var config = new SessionConfig(InterpretationMode.Cascade, dir, profile);
        return new InterpretationSession(
            "session_x", null, Base, null, config,
            turns.ToList(), new List<ModeTransitionEvent>(), null, "2026-05-28-payg-estimates");
    }

    // 1 — empty session: zero counts, all mode/WER summaries null, ComputedAt + version echoed, no throw.
    [Fact]
    public void empty_session_returns_zero_summary()
    {
        var summary = Service().Compute(Session());

        Assert.Equal(0, summary.TurnCount);
        Assert.Null(summary.Realtime);
        Assert.Null(summary.Cascade);
        Assert.Null(summary.Wer);
        Assert.Equal(ComputedAt, summary.ComputedAt);
        Assert.Equal("2026-05-28-payg-estimates", summary.PricingConfigVersion);
    }

    // 2 — one cascade turn: Cascade populated (avg = that turn's metrics, cost/min, ErrorCount=1); Realtime null.
    [Fact]
    public void single_cascade_turn_populates_cascade_summary()
    {
        var summary = Service().Compute(Session(CascadeTurn(
            "t1", sttFinalMs: 200, translationFinalMs: 300, ttsFirstAudioMs: 150,
            speechEndToFirstAudioMs: 500, speechEndToPlaybackMs: 800, costPerMin: 0.05m, errorCount: 1)));

        Assert.Null(summary.Realtime);
        var c = summary.Cascade;
        Assert.NotNull(c);
        Assert.Equal(1, c!.TurnCount);
        Assert.Equal(1250, c.AvgSpeechEndToFirstAudioMs); // stt.final-anchored (057a): first_audio(1500) - stt.final(250)
        Assert.Equal(800, c.AvgSpeechEndToPlaybackMs);
        Assert.Equal(200, c.AvgSttFinalMs);
        Assert.Equal(300, c.AvgTranslationFinalMs);
        Assert.Equal(150, c.AvgTtsFirstAudioMs);
        Assert.Equal(0.05m, c.EstimatedCostPerMinuteUsd);
        Assert.Equal(1, c.ErrorCount);
        Assert.Equal(1, summary.TurnCount);
    }

    // 3 — one realtime turn: cascade-only fields null; universal avgs present; Cascade null. (ARCH-005 line 326)
    [Fact]
    public void single_realtime_turn_cascade_fields_null()
    {
        var summary = Service().Compute(Session(RealtimeTurn(
            "t1", speechEndToFirstAudioMs: 400, speechEndToPlaybackMs: 600)));

        Assert.Null(summary.Cascade);
        var r = summary.Realtime;
        Assert.NotNull(r);
        Assert.Equal(1, r!.TurnCount);
        Assert.Equal(400, r.AvgSpeechEndToFirstAudioMs);
        Assert.Equal(600, r.AvgSpeechEndToPlaybackMs);
        Assert.Null(r.AvgSttFinalMs);
        Assert.Null(r.AvgTranslationFinalMs);
        Assert.Null(r.AvgTtsFirstAudioMs);
    }

    // 4 — two cascade turns: each Avg* is the arithmetic mean of the two. Pins the averaging math.
    [Fact]
    public void multi_turn_averages_metrics()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("t1", sttFinalMs: 200, translationFinalMs: 300, ttsFirstAudioMs: 150,
                speechEndToFirstAudioMs: 500, speechEndToPlaybackMs: 800),
            CascadeTurn("t2", sttFinalMs: 400, translationFinalMs: 500, ttsFirstAudioMs: 250,
                speechEndToFirstAudioMs: 700, speechEndToPlaybackMs: 1000)));

        var c = summary.Cascade!;
        Assert.Equal(2, c.TurnCount);
        Assert.Equal(300, c.AvgSttFinalMs);            // (200+400)/2
        Assert.Equal(400, c.AvgTranslationFinalMs);    // (300+500)/2
        Assert.Equal(200, c.AvgTtsFirstAudioMs);       // (150+250)/2
        Assert.Equal(1250, c.AvgSpeechEndToFirstAudioMs); // stt.final-anchored (057a): both turns -> 1250
        Assert.Equal(900, c.AvgSpeechEndToPlaybackMs);   // (800+1000)/2 (recording.stopped anchor, unchanged)
    }

    // 057(a) — the held-recording.stopped scenario the cascade re-anchor fixes (the session-avg must agree
    // with 056-c1's per-turn re-anchor): a user who keeps the button down PAST first-audio. The old
    // recording.stopped anchor yields a NEGATIVE responsiveness; the stt.final anchor (the real cascade
    // speech-end signal) yields the correct positive value.
    [Fact]
    public void cascade_avg_first_audio_anchors_on_stt_final_not_held_recording_stopped()
    {
        var events = new List<LatencyEvent>
        {
            Ev(LatencyEventNames.TurnRecordingStarted, Base),
            Ev(LatencyEventNames.SttFinal, Base.AddMilliseconds(800)),              // speech finalized
            Ev(LatencyEventNames.TtsFirstAudio, Base.AddMilliseconds(1200)),        // first audio out
            Ev(LatencyEventNames.TurnRecordingStopped, Base.AddMilliseconds(1500)), // button held late
        };

        var summary = Service().Compute(Session(Turn("t1", InterpretationMode.Cascade, events, null, 0, null)));

        // 1200 - 800 (stt.final) = +400, NOT 1200 - 1500 (recording.stopped) = -300.
        Assert.Equal(400, summary.Cascade!.AvgSpeechEndToFirstAudioMs);
    }

    // 5 — mixed-mode session: both ModeSummaries populated, each counting only its mode; top TurnCount = total.
    [Fact]
    public void mixed_mode_session_splits_summaries()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("c1"), RealtimeTurn("r1"), CascadeTurn("c2")));

        Assert.Equal(3, summary.TurnCount);
        Assert.NotNull(summary.Cascade);
        Assert.NotNull(summary.Realtime);
        Assert.Equal(2, summary.Cascade!.TurnCount);
        Assert.Equal(1, summary.Realtime!.TurnCount);
    }

    // 6 — null-metric handling: mean over present values only; a metric null on every turn → null.
    [Fact]
    public void null_metric_excluded_from_average()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("t1", sttFinalMs: 200, translationFinalMs: null, ttsFirstAudioMs: 150),
            CascadeTurn("t2", sttFinalMs: null, translationFinalMs: null, ttsFirstAudioMs: 250)));

        var c = summary.Cascade!;
        Assert.Equal(200, c.AvgSttFinalMs);       // only t1 present
        Assert.Null(c.AvgTranslationFinalMs);     // null on both -> null (n/a, never 0/throw)
        Assert.Equal(200, c.AvgTtsFirstAudioMs);  // (150+250)/2
    }

    // 7 — ErrorCount sums per mode (total normalized errors), NOT count-of-failed-turns.
    [Fact]
    public void error_count_sums_per_mode()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("t1", errorCount: 2), CascadeTurn("t2", errorCount: 3)));

        Assert.Equal(5, summary.Cascade!.ErrorCount);
    }

    // 8 — WER session-level over both modes; unbounded (the >1.0 sample is included, never clamped);
    // turns without WER excluded; none anywhere → null.
    [Fact]
    public void wer_summary_aggregates_all_turns_unbounded()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("t1", wer: 0.5), CascadeTurn("t2", wer: null), RealtimeTurn("t3", wer: 1.5)));

        Assert.NotNull(summary.Wer);
        Assert.Equal(2, summary.Wer!.SampleCount);   // t1 + t3 (t2 excluded)
        Assert.Equal(1.0, summary.Wer.AvgWer);       // (0.5+1.5)/2 = 1.0 — clamping would give 0.75

        Assert.Null(Service().Compute(Session(CascadeTurn("a"), CascadeTurn("b"))).Wer);
    }

    // 9 — cost/min averaged over non-null per-turn rates; all-null → null.
    [Fact]
    public void cost_per_minute_averaged_over_non_null()
    {
        var summary = Service().Compute(Session(
            CascadeTurn("t1", costPerMin: 0.04m), CascadeTurn("t2", costPerMin: null),
            CascadeTurn("t3", costPerMin: 0.06m)));

        Assert.Equal(0.05m, summary.Cascade!.EstimatedCostPerMinuteUsd);  // (0.04+0.06)/2

        var allNull = Service().Compute(Session(
            CascadeTurn("a", costPerMin: null), CascadeTurn("b", costPerMin: null)));
        Assert.Null(allNull.Cascade!.EstimatedCostPerMinuteUsd);
    }

    // 10 — ComputedAt == injected clock; PricingConfigVersion == the session's (ARCH-009 staleness).
    [Fact]
    public void computed_at_and_version_from_inputs()
    {
        var now = Base.AddHours(3);
        var summary = Service(now).Compute(Session(CascadeTurn("t1")));

        Assert.Equal(now, summary.ComputedAt);
        Assert.Equal("2026-05-28-payg-estimates", summary.PricingConfigVersion);
    }

    // Pure compute: Compute does NOT mutate the session or write session.Summary (B.9 owns the snapshot).
    [Fact]
    public void compute_does_not_mutate_session()
    {
        var session = Session(CascadeTurn("t1"));
        var turnCountBefore = session.Turns.Count;

        var summary = Service().Compute(session);

        Assert.NotNull(summary);                             // it actually computed...
        Assert.Equal(1, summary.TurnCount);
        Assert.Null(session.Summary);                        // ...without writing the snapshot (B.9 owns /end)
        Assert.Equal(turnCountBefore, session.Turns.Count);  // source turn list left untouched
    }

    // F.4 — 11: a standalone WER-evaluation turn (IsEvaluation=true) is EXCLUDED from the per-mode
    // ModeSummary across ALL its fields — TurnCount, the averages, AND ErrorCount — so F.3's
    // Realtime-vs-Cascade comparison counts only real interpretation turns (the user-requested fix).
    [Fact]
    public void summarize_mode_excludes_evaluation_turns()
    {
        // 2 real cascade interpretation turns (stt 200/400, no errors) + 1 cascade eval turn whose
        // skewing stt (9999ms) + 5 errors would corrupt every per-mode field if it were counted.
        var evalTurn = CascadeTurn("eval1", sttFinalMs: 9999, errorCount: 5) with { IsEvaluation = true };
        var summary = Service().Compute(Session(
            CascadeTurn("t1", sttFinalMs: 200),
            CascadeTurn("t2", sttFinalMs: 400),
            evalTurn));

        Assert.Equal(3, summary.TurnCount);    // top-level counts ALL turns incl. eval (Q1); only per-mode excludes
        var c = summary.Cascade!;
        Assert.Equal(2, c.TurnCount);          // the eval turn is not an interpretation turn → not 3
        Assert.Equal(300, c.AvgSttFinalMs);    // (200+400)/2 — NOT (200+400+9999)/3
        Assert.Equal(0, c.ErrorCount);         // the eval turn's 5 errors are excluded too
    }

    // J.6 — a 0-transcript COMPLETED turn (an auto-VAD silence/gap) is EXCLUDED from the per-mode
    // ModeSummary (TurnCount/averages) so a phantom silence turn can't inflate the comparison — like §29's
    // IsEvaluation exclusion. A 0-transcript FAILED turn (a real early failure) is KEPT so its error still
    // surfaces in ErrorCount (the exclusion is scoped to Completed, NOT a blanket 0-transcript drop).
    // 097 refinement: the cascade-silence turn here is also COST-NULL (genuine silence prices to nothing) —
    // which is why it stays excluded under the refined `&& CostEstimate==null` predicate.
    [Fact]
    public void summarize_mode_excludes_empty_silence_turn_but_keeps_failed_empty()
    {
        var content = CascadeTurn("t1", sttFinalMs: 200);                                   // Completed, 1 transcript
        var silence = CascadeTurn("silence") with { Transcripts = new List<TranscriptSegment>() }; // Completed, 0 transcripts, cost null
        var failedEmpty = CascadeTurn("failed") with                                        // Failed, 0 transcripts, 1 error
        {
            Status = TurnStatus.Failed,
            Transcripts = new List<TranscriptSegment>(),
            Errors = new List<ProviderError> { new("deepgram", "stt", "stt.unknown", "msg", false, null) },
        };

        var summary = Service().Compute(Session(content, silence, failedEmpty));

        Assert.Equal(3, summary.TurnCount);   // top-level counts ALL turns (silence + failed-empty included)
        var c = summary.Cascade!;
        Assert.Equal(2, c.TurnCount);         // content + failed-empty; the Completed-silence turn is excluded
        Assert.Equal(1, c.ErrorCount);        // the failed-empty turn's error STILL surfaces (not hidden)
    }

    // 097 / Finding-A — a COMPLETED realtime turn legitimately persists with 0 transcripts (realtime
    // transcripts live FE-store-side, not on the turn) but a REAL CostEstimate (billed audio tokens). It
    // must be INCLUDED in the ModeSummary so its cost feeds the per-mode Cost/min — the §39 0-transcript
    // silence exclusion must NOT swallow it. Refines §39: a cost-bearing turn is real evidence, not silence.
    // Mirrors the user's live session shape (session_20260601T170454Z_*: completed realtime turns, 0
    // transcripts, real cost — previously blanking realtime.estimatedCostPerMinuteUsd).
    [Fact]
    public void summarize_includes_completed_realtime_turn_with_cost_and_no_transcripts()
    {
        var realtimeCostTurn = RealtimeTurn(
            "rt", speechEndToFirstAudioMs: 400, speechEndToPlaybackMs: 600, costPerMin: 0.30m)
            with
        { Transcripts = new List<TranscriptSegment>() };

        var summary = Service().Compute(Session(realtimeCostTurn));

        var r = summary.Realtime;
        Assert.NotNull(r);
        Assert.Equal(1, r!.TurnCount);
        Assert.Equal(0.30m, r.EstimatedCostPerMinuteUsd); // the turn's cost feeds the per-mode Cost/min
        Assert.Equal(400, r.AvgSpeechEndToFirstAudioMs);  // latency aggregates too (it's a real turn)
        Assert.Equal(1, summary.TurnCount);
    }

    // 097 — the discriminator is COST, not MODE: a COMPLETED 0-transcript turn carrying a real CostEstimate
    // is INCLUDED regardless of mode (billed work is real evidence, never silence). Pins the cost-null rule
    // against the mode-scoping alternative (`Mode != Realtime`), which would wrongly exclude this turn.
    [Fact]
    public void summarize_includes_cost_bearing_zero_transcript_turn_regardless_of_mode()
    {
        var cascadeCostNoTranscripts = CascadeTurn("c-cost", costPerMin: 0.05m)
            with
        { Transcripts = new List<TranscriptSegment>() };

        var summary = Service().Compute(Session(cascadeCostNoTranscripts));

        var c = summary.Cascade;
        Assert.NotNull(c);
        Assert.Equal(1, c!.TurnCount);
        Assert.Equal(0.05m, c.EstimatedCostPerMinuteUsd);
    }

    // F.4 — 12: the SAME eval turn that ModeSummary excludes is STILL included in the session-level
    // WerSummary — eval turns are where the WER comes from, so the exclusion is per-mode ONLY.
    [Fact]
    public void summarize_wer_still_includes_evaluation_turns()
    {
        var evalTurn = CascadeTurn("eval1", wer: 0.3) with { IsEvaluation = true };
        var summary = Service().Compute(Session(
            CascadeTurn("t1", wer: null),
            CascadeTurn("t2", wer: null),
            evalTurn));

        Assert.Equal(2, summary.Cascade!.TurnCount);  // eval turn excluded from the per-mode count...
        Assert.NotNull(summary.Wer);
        Assert.Equal(1, summary.Wer!.SampleCount);     // ...but its WER is the one WER sample
        Assert.Equal(0.3, summary.Wer.AvgWer);
    }
}
