using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>TTS request (ARCH-012).</summary>
public sealed record TtsRequest(
    string Text,
    LanguageCode TargetLanguage,
    string Voice,
    string Model,
    string ResponseFormat,
    string? Instructions,
    string SessionId,
    string TurnId);

/// <summary>
/// Streaming TTS contract (ARCH-012). The C real provider streams audio chunks; consumed by the
/// B.4 cascade orchestrator.
/// </summary>
public interface ITtsProvider
{
    IAsyncEnumerable<TtsEvent> SynthesizeAsync(TtsRequest request, CancellationToken ct);
}
