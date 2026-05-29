using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Sessions;

// Domain model — ARCH-005 (authoritative). All API JSON (ARCH-009) and persisted JSON (ARCH-016)
// are camelCase serializations of these types via Common/JsonDefaults. ProviderError lives in
// Providers/Abstractions/ProviderErrors.cs per ARCH-006.

// --- Core enums ---

public enum InterpretationMode { Realtime, Cascade }

public enum LanguageCode { En, Es }

// Turn-level status (one recorded input -> output cycle).
public enum TurnStatus { Ready, Recording, Captured, Processing, Playing, Completed, Failed }

// Session-level status (whole evaluation session).
public enum SessionStatus { Idle, Configured, Starting, Active, ReadyForTurn, Ending, Ended }

// `Overall` (C.4) is the turn-level stage for the turn.recording.*/turn.completed lifecycle events,
// distinct from the per-stage Capture/Stt/Translation/Tts markers (ARCH-005 / ARCH-013).
public enum LatencyStage { Capture, Realtime, Stt, Translation, Tts, Playback, Persistence, Evaluation, Overall }

public enum ClockSource { Server, Browser }

// --- Core records ---

public sealed record LanguageDirection(LanguageCode Source, LanguageCode Target);

public sealed record ProviderProfile(
    string RealtimeProvider,
    string RealtimeModel,
    string SttProvider,
    string SttModel,
    string SttLanguage,
    string TranslationProvider,
    string TranslationModel,
    string TtsProvider,
    string TtsModel,
    string TtsVoice);

public sealed record SessionConfig(
    InterpretationMode CurrentMode,
    LanguageDirection Direction,
    ProviderProfile ProviderProfile);

public sealed record InterpretationSession(
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionConfig Config,
    List<InterpretationTurn> Turns,
    List<ModeTransitionEvent> ModeTransitions,
    SessionSummary? Summary,
    string PricingConfigVersion);

public sealed record ModeTransitionEvent(
    string TransitionId,
    InterpretationMode FromMode,
    InterpretationMode ToMode,
    LanguageDirection DirectionAtTransition,
    DateTimeOffset OccurredAt,
    ClockSource ClockSource,
    string? TriggeredByTurnId);

public sealed record InterpretationTurn(
    string TurnId,
    InterpretationMode Mode,
    LanguageDirection Direction,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long AudioDurationMs,
    List<TranscriptSegment> Transcripts,
    List<LatencyEvent> LatencyEvents,
    CostEstimate? CostEstimate,
    WerResult? WerResult,
    List<ProviderError> Errors,
    TurnStatus Status,
    string? TranslationModelUsed,
    string? TtsVoiceUsed);

public sealed record TranscriptSegment(
    string SegmentId,
    string Role,
    string Text,
    bool IsFinal,
    string Provider,
    DateTimeOffset Timestamp,
    ClockSource ClockSource);

public sealed record LatencyEvent(
    string Name,
    LatencyStage Stage,
    DateTimeOffset Timestamp,
    long RelativeMs,
    ClockSource ClockSource,
    Dictionary<string, string> Metadata);

public sealed record CostEstimate(
    string Provider,
    string Model,
    string PricingBasis,
    decimal EstimatedUsd,
    decimal? EstimatedUsdPerMinute,
    Dictionary<string, decimal> Units,
    string PricingConfigVersion,
    string[] Assumptions);

public sealed record SessionSummary(
    int TurnCount,
    ModeSummary? Realtime,
    ModeSummary? Cascade,
    WerSummary? Wer,
    DateTimeOffset ComputedAt,
    string PricingConfigVersion);

public sealed record ModeSummary(
    int TurnCount,
    double? AvgSpeechEndToFirstAudioMs,
    double? AvgSpeechEndToPlaybackMs,
    decimal? EstimatedCostPerMinuteUsd,
    int ErrorCount,
    double? AvgSttFinalMs,
    double? AvgTranslationFinalMs,
    double? AvgTtsFirstAudioMs);

public sealed record WerSummary(int SampleCount, double AvgWer);

public sealed record EvaluationPhrase(
    string PhraseId,
    LanguageCode Language,
    string ReferenceText,
    string Category);

public sealed record WerResult(
    string PhraseId,
    string Reference,
    string Hypothesis,
    string NormalizedReference,
    string NormalizedHypothesis,
    int Substitutions,
    int Insertions,
    int Deletions,
    int ReferenceWordCount,
    double Wer);
