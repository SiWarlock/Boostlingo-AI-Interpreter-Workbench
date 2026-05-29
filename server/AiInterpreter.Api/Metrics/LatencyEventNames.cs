namespace AiInterpreter.Api.Metrics;

/// <summary>
/// Canonical ARCH-013 latency event-name vocabulary — the single source of the metric strings the
/// <see cref="MetricsAggregator"/> keys on and that producers (B.4 cascade orchestrator, E.4
/// realtime mapping) stamp via <see cref="LatencyEventFactory"/>. No magic event strings elsewhere.
/// </summary>
public static class LatencyEventNames
{
    // Turn lifecycle. recording.*/playback.* are browser-clock media events; completed is server.
    public const string TurnRecordingStarted = "turn.recording.started";
    public const string TurnRecordingStopped = "turn.recording.stopped";
    public const string TurnCompleted = "turn.completed";
    public const string PlaybackStarted = "playback.started";

    // Cascade stage markers — stage-start origins + terminals.
    public const string CascadeAudioReceived = "cascade.audio.received"; // STT stage origin
    public const string SttStarted = "stt.started";
    public const string SttFirstPartial = "stt.first_partial";
    public const string SttFinal = "stt.final";
    public const string TranslationStarted = "translation.started";       // translation stage origin
    public const string TranslationFirstToken = "translation.first_token";
    public const string TranslationFinal = "translation.final";
    public const string TtsStarted = "tts.started";                       // tts stage origin
    public const string TtsFirstAudio = "tts.first_audio";
    public const string TtsComplete = "tts.complete";

    // Realtime markers. realtime.session.connecting is the connect-latency origin — emitted by E.4
    // at WebRTC connect-start; until then realtime_connect_ms is honestly n/a (no synthesis).
    public const string RealtimeSessionConnecting = "realtime.session.connecting";
    public const string RealtimeSessionConnected = "realtime.session.connected";
    public const string RealtimeFirstAudioDelta = "realtime.first_audio_delta";
    public const string RealtimeFirstTranscriptDelta = "realtime.first_transcript_delta";
}
