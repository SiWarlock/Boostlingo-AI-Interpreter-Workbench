using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>Translation request (ARCH-012).</summary>
public sealed record TranslationRequest(
    string Text,
    LanguageCode SourceLanguage,
    LanguageCode TargetLanguage,
    string Model,
    string SessionId,
    string TurnId);

/// <summary>
/// Streaming translation contract (ARCH-012). The C real provider streams tokens via the Responses
/// API (never a blocking call); consumed by the B.4 cascade orchestrator.
/// </summary>
public interface ITranslationProvider
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken ct);
}
