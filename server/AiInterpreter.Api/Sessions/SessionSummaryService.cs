using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// Pure, read-only aggregation (ARCH-009 on-demand summary / ARCH-013 per-turn → per-mode metrics).
/// Computes a <see cref="SessionSummary"/> from an <see cref="InterpretationSession"/>: a per-mode
/// <see cref="ModeSummary"/> (latency averaged from <see cref="MetricsAggregator"/> per turn over
/// non-null values, averaged cost/min, summed error count), a session-level <see cref="WerSummary"/>
/// (unbounded), and turn counts.
///
/// Reuses the B.3 <see cref="MetricsAggregator"/> per turn — never re-implements metric math. Does
/// NOT mutate the session or write <c>session.Summary</c>: the cached snapshot on <c>/end</c> is
/// B.9's persistence step. <c>ComputedAt</c> (stamped from <see cref="IClock"/>) disambiguates
/// staleness vs a cached snapshot (ARCH-009).
/// </summary>
public sealed class SessionSummaryService
{
    private readonly MetricsAggregator _metrics;
    private readonly IClock _clock;

    public SessionSummaryService(MetricsAggregator metrics, IClock clock)
    {
        _metrics = metrics;
        _clock = clock;
    }

    public SessionSummary Compute(InterpretationSession session) =>
        new(
            TurnCount: session.Turns.Count,
            Realtime: SummarizeMode(session.Turns, InterpretationMode.Realtime),
            Cascade: SummarizeMode(session.Turns, InterpretationMode.Cascade),
            Wer: SummarizeWer(session.Turns),
            ComputedAt: _clock.UtcNow,
            PricingConfigVersion: session.PricingConfigVersion);

    // A ModeSummary exists iff the mode has >= 1 INTERPRETATION turn; an empty mode -> null (not a
    // zero-filled record). F.4: standalone WER-evaluation turns (IsEvaluation) are excluded here so the
    // Realtime-vs-Cascade comparison's TurnCount, latency/cost averages, AND ErrorCount reflect real
    // interpretation turns only — one filter covers every per-mode field. (SummarizeWer keeps them.)
    private ModeSummary? SummarizeMode(IReadOnlyList<InterpretationTurn> allTurns, InterpretationMode mode)
    {
        var turns = allTurns.Where(t => t.Mode == mode && !t.IsEvaluation && !IsEmptySilence(t)).ToList();
        if (turns.Count == 0)
        {
            return null;
        }

        // Per-turn TurnMetrics from the reused aggregator; the cascade-stage fields are already null
        // for realtime turns (no such events), so the averages are null there too.
        var perTurnMetrics = turns.Select(t => _metrics.Compute(t.LatencyEvents)).ToList();

        return new ModeSummary(
            TurnCount: turns.Count,
            AvgSpeechEndToFirstAudioMs: Average(perTurnMetrics.Select(m => m.SpeechEndToFirstAudioMs)),
            AvgSpeechEndToPlaybackMs: Average(perTurnMetrics.Select(m => m.SpeechEndToPlaybackMs)),
            EstimatedCostPerMinuteUsd: Average(turns.Select(t => t.CostEstimate?.EstimatedUsdPerMinute)),
            ErrorCount: turns.Sum(t => t.Errors.Count),
            AvgSttFinalMs: Average(perTurnMetrics.Select(m => m.SttFinalMs)),
            AvgTranslationFinalMs: Average(perTurnMetrics.Select(m => m.TranslationFinalMs)),
            AvgTtsFirstAudioMs: Average(perTurnMetrics.Select(m => m.TtsFirstAudioMs)));
    }

    // J.6 — a COMPLETED turn that captured NO transcript is an auto-VAD silence/gap (the continuous loop
    // re-armed on a silent stretch). Exclude it from the per-mode comparison so a phantom silence turn can't
    // inflate TurnCount/averages. Scoped to Completed on purpose: a FAILED 0-transcript turn (a real early
    // failure, e.g. an immediate SttFailed) is KEPT so its error still surfaces in ErrorCount.
    // 097 (refines §39) — ALSO require CostEstimate==null: a cost-bearing turn produced billable work, so it
    // is real evidence, NOT silence. Every realtime turn persists with 0 transcripts (realtime transcripts
    // live FE-store-side, not on the turn) but a real cost — without this clause the §39 rule excluded EVERY
    // realtime turn, blanking the realtime per-mode Cost/min (Finding A). Genuine cascade silence is cost-null
    // (no STT final → no composite cost), so it still matches and stays excluded.
    private static bool IsEmptySilence(InterpretationTurn t) =>
        t.Status == TurnStatus.Completed && t.Transcripts.Count == 0 && t.CostEstimate is null;

    // Session-level (both modes). WER is unbounded — never clamp > 1.0 when averaging (lesson §10).
    private static WerSummary? SummarizeWer(IReadOnlyList<InterpretationTurn> turns)
    {
        var wers = turns.Where(t => t.WerResult is not null).Select(t => t.WerResult!.Wer).ToList();
        return wers.Count == 0 ? null : new WerSummary(wers.Count, wers.Average());
    }

    // Mean over the present (non-null) values; null when none are present (n/a — never 0 or throw).
    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }
}
