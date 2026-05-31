using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Metrics;

/// <summary>
/// Computes per-turn <see cref="TurnMetrics"/> from a turn's <see cref="LatencyEvent"/> list
/// (ARCH-013). Pure, stateless, <b>never throws</b>. Metrics are computed from <b>absolute
/// <c>Timestamp</c> subtraction</b> so cross-clock pairs (browser speech-end → server first-audio)
/// are wall-clock-correct; small clock skew is preserved and disclosed, never clamped (ARCH-013).
/// A metric is <c>null</c> (→ "n/a") whenever an endpoint event is absent.
/// </summary>
public sealed class MetricsAggregator
{
    public TurnMetrics Compute(IReadOnlyList<LatencyEvent> events)
    {
        // First occurrence wins per event name (each type is stamped once per turn on first arrival).
        var byName = new Dictionary<string, LatencyEvent>();
        foreach (var e in events)
        {
            byName.TryAdd(e.Name, e);
        }

        LatencyEvent? Get(string name) => byName.GetValueOrDefault(name);

        var recordingStarted = Get(LatencyEventNames.TurnRecordingStarted);
        var recordingStopped = Get(LatencyEventNames.TurnRecordingStopped);
        var playbackStarted = Get(LatencyEventNames.PlaybackStarted);
        var realtimeFirstAudioDelta = Get(LatencyEventNames.RealtimeFirstAudioDelta);

        // First output-audio event for the universal speech-end → first-audio metric: cascade TTS,
        // else realtime audio delta, else playback start (ARCH-013 "first output-audio event (or
        // playback start)").
        var firstOutputAudio =
            Get(LatencyEventNames.TtsFirstAudio)
            ?? realtimeFirstAudioDelta
            ?? playbackStarted;

        // 057(a) — the speech-end anchor for the first-audio responsiveness metric is stt.final (the
        // STT-finalized utterance = the real cascade speech-end signal), NOT the manual recording.stop
        // button. Realtime emits no stt.final, so it falls back to recording.stopped — self-selecting by
        // mode with no branch, and mirroring the 056-c1 per-turn re-anchor so the session-avg agrees.
        // Only this metric moves; SpeechEndToPlaybackMs keeps recording.stopped.
        var firstAudioSpeechEnd = Get(LatencyEventNames.SttFinal) ?? recordingStopped;

        return new TurnMetrics
        {
            // Universal.
            SpeechEndToFirstAudioMs = Between(firstAudioSpeechEnd, firstOutputAudio),
            SpeechEndToPlaybackMs = Between(recordingStopped, playbackStarted),
            TotalTurnMs = Between(recordingStarted, Get(LatencyEventNames.TurnCompleted)),
            AudioDurationMs = Between(recordingStarted, recordingStopped),

            // Cascade stage — each measured from its stage-start origin.
            SttFirstPartialMs = Between(Get(LatencyEventNames.CascadeAudioReceived), Get(LatencyEventNames.SttFirstPartial)),
            SttFinalMs = Between(Get(LatencyEventNames.CascadeAudioReceived), Get(LatencyEventNames.SttFinal)),
            TranslationFirstTokenMs = Between(Get(LatencyEventNames.TranslationStarted), Get(LatencyEventNames.TranslationFirstToken)),
            TranslationFinalMs = Between(Get(LatencyEventNames.TranslationStarted), Get(LatencyEventNames.TranslationFinal)),
            TtsFirstAudioMs = Between(Get(LatencyEventNames.TtsStarted), Get(LatencyEventNames.TtsFirstAudio)),
            TtsCompleteMs = Between(Get(LatencyEventNames.TtsStarted), Get(LatencyEventNames.TtsComplete)),

            // Realtime.
            RealtimeConnectMs = Between(Get(LatencyEventNames.RealtimeSessionConnecting), Get(LatencyEventNames.RealtimeSessionConnected)),
            RealtimeFirstAudioDeltaMs = Between(recordingStopped, realtimeFirstAudioDelta),
            RealtimeFirstTranscriptDeltaMs = Between(recordingStopped, Get(LatencyEventNames.RealtimeFirstTranscriptDelta)),
            RealtimePlaybackMs = Between(realtimeFirstAudioDelta, playbackStarted),
        };
    }

    // Wall-clock millisecond difference between two events' absolute Timestamps, or null if either
    // endpoint is absent. Deliberately NOT clamped — cross-clock skew may yield a small negative,
    // which ARCH-013 discloses rather than hides (contrast the factory's single-event clamp).
    private static double? Between(LatencyEvent? from, LatencyEvent? to)
        => from is null || to is null ? null : (to.Timestamp - from.Timestamp).TotalMilliseconds;
}
