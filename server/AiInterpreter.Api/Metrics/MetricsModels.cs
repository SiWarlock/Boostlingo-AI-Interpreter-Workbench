namespace AiInterpreter.Api.Metrics;

/// <summary>
/// Per-turn computed latency metrics (ARCH-013). Area-local <b>computed</b> type — NOT persisted on
/// <c>InterpretationTurn</c> and not a wire DTO in B.3 (B.7's SessionSummaryService averages these
/// into <c>ModeSummary</c>; C.4's WS <c>latency</c> emit surfaces them). Every field is
/// <c>null</c> (→ "n/a") when its endpoint event(s) are absent — never an error. Values are
/// milliseconds as <c>double?</c> to match <c>ModeSummary</c>'s <c>double?</c> averages.
/// </summary>
public sealed record TurnMetrics
{
    // Universal (both modes).
    public double? SpeechEndToFirstAudioMs { get; init; }
    public double? SpeechEndToPlaybackMs { get; init; }
    public double? TotalTurnMs { get; init; }
    public double? AudioDurationMs { get; init; }

    // Cascade stage metrics.
    public double? SttFirstPartialMs { get; init; }
    public double? SttFinalMs { get; init; }
    public double? TranslationFirstTokenMs { get; init; }
    public double? TranslationFinalMs { get; init; }
    public double? TtsFirstAudioMs { get; init; }
    public double? TtsCompleteMs { get; init; }

    // Realtime metrics.
    public double? RealtimeConnectMs { get; init; }
    public double? RealtimeFirstAudioDeltaMs { get; init; }
    public double? RealtimeFirstTranscriptDeltaMs { get; init; }
    public double? RealtimePlaybackMs { get; init; }
}
