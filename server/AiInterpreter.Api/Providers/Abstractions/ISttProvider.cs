using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>STT request (ARCH-012). AudioFrames is the live frame stream; the blob fallback wraps one frame.</summary>
public sealed record SttRequest(
    IAsyncEnumerable<AudioFrame> AudioFrames,
    string ContentType,
    string Encoding,
    int SampleRate,
    LanguageCode SourceLanguage,
    string SttLanguage,
    string SessionId,
    string TurnId);

/// <summary>
/// Streaming STT contract (ARCH-012). Implemented by the B.2 fakes + the C real Deepgram provider;
/// consumed by the B.4 cascade orchestrator. Emits a normalized <see cref="SttEvent"/> stream.
/// </summary>
public interface ISttProvider
{
    IAsyncEnumerable<SttEvent> TranscribeAsync(SttRequest request, CancellationToken ct);
}
