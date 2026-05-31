using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// Normalized, transport-agnostic output of <see cref="CascadeStreamingOrchestrator"/> (ARCH-011).
/// The orchestrator knows nothing about WebSockets or JSON — C.4's <c>CascadeWebSocketEndpoint</c>
/// adapts these to the ARCH-009 wire messages. Area-local; not persisted/serialized in B.4.
/// </summary>
public abstract record CascadeOutputEvent;

/// <summary>A source or target transcript segment (partial or final).</summary>
public sealed record Transcript(TranscriptSegment Segment) : CascadeOutputEvent;

/// <summary>A server-stamped latency event (ARCH-013), produced on real first arrival.</summary>
public sealed record Latency(LatencyEvent Event) : CascadeOutputEvent;

/// <summary>A streamed TTS audio chunk (raw bytes — streamed to the browser, never persisted; safety rule #3).</summary>
public sealed record Audio(byte[] Bytes, int Seq, string ContentType) : CascadeOutputEvent;

/// <summary>A normalized, UI-safe provider/cascade error (ARCH-018).</summary>
public sealed record Error(ProviderError ProviderError) : CascadeOutputEvent;

/// <summary>Terminal event — the turn's final status.</summary>
public sealed record Done(TurnStatus Status) : CascadeOutputEvent;

/// <summary>
/// Per-turn cascade parameters (the server-side view of the ARCH-009 WS <c>start</c> message). The
/// live audio frames are passed to <see cref="CascadeStreamingOrchestrator.RunAsync"/> separately
/// (a streaming input, like the <c>CancellationToken</c>). Timeouts are <c>TimeSpan?</c>; the
/// orchestrator resolves nulls to the ARCH-012 defaults (STT 30s / translation 20s / TTS 30s).
/// C.4 converts the flat <c>*_TIMEOUT_SECONDS</c> env vars into these at the config boundary.
/// </summary>
public sealed record CascadeStartParams(
    string SessionId,
    string TurnId,
    LanguageDirection Direction,
    string Encoding,
    int SampleRate,
    string TranslationModel,
    string TtsVoice,
    string TtsModel = "gpt-4o-mini-tts",
    TimeSpan? SttTimeout = null,
    TimeSpan? TranslationTimeout = null,
    TimeSpan? TtsTimeout = null,
    // I.1 (Phase I) — auto-VAD: true ⇒ the orchestrator auto-finalizes the turn on Deepgram's utterance-end
    // (detected silence); false (default) ⇒ finalize on the client `stop` (today's manual behavior). A
    // per-turn WS `start` flag (REVISES ARCH-003's no-VAD decision, auto-VAD additive + manual preserved).
    bool AutoVad = false);
