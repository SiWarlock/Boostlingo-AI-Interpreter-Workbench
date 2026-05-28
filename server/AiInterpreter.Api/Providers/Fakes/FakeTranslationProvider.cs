using System.Runtime.CompilerServices;
using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Providers.Fakes;

public enum FakeTranslationBehavior
{
    TokenStreamThenFinal,
    ImmediateFinalOnly,
    Error,
}

/// <summary>
/// Deterministic translation test double (ARCH-012). Streams token deltas then a final (or an
/// immediate final, or a failure), per the configured variant.
/// </summary>
public sealed class FakeTranslationProvider : ITranslationProvider
{
    private readonly FakeTranslationBehavior _behavior;
    private readonly TimeSpan _delayPerEvent;
    private readonly IReadOnlyList<string> _deltas;
    private readonly string _final;
    private readonly ProviderError _error;

    public FakeTranslationProvider(
        FakeTranslationBehavior behavior = FakeTranslationBehavior.TokenStreamThenFinal,
        TimeSpan? delayPerEvent = null,
        IReadOnlyList<string>? deltas = null,
        string final = "hola mundo",
        ProviderError? error = null)
    {
        _behavior = behavior;
        _delayPerEvent = delayPerEvent ?? TimeSpan.Zero;
        _deltas = deltas ?? ["hola", " mundo"];
        _final = final;
        _error = error ?? new ProviderError(
            "fake-openai", "translation", "translation.upstream_unavailable",
            "The translation provider is temporarily unavailable.", Retryable: true);
    }

    public async IAsyncEnumerable<TranslationEvent> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new TranslationStarted(DateTimeOffset.UtcNow);

        if (_behavior == FakeTranslationBehavior.Error)
        {
            await FakeStreaming.PaceAsync(_delayPerEvent, ct);
            yield return new TranslationFailed(_error, DateTimeOffset.UtcNow);
            yield break;
        }

        if (_behavior == FakeTranslationBehavior.TokenStreamThenFinal)
        {
            foreach (var delta in _deltas)
            {
                await FakeStreaming.PaceAsync(_delayPerEvent, ct);
                yield return new TranslationPartial(delta, DateTimeOffset.UtcNow);
            }
        }

        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        // Arbitrary non-null token counts so cost/metrics consumers see plausible values.
        yield return new TranslationFinal(_final, InputTokens: 12, OutputTokens: 8, DateTimeOffset.UtcNow);
    }
}
