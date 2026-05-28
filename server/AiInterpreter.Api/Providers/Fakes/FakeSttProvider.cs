using System.Runtime.CompilerServices;
using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Providers.Fakes;

public enum FakeSttBehavior
{
    SuccessWithPartials,
    EmptyFinal,
    PartialsThenError,
}

/// <summary>
/// Deterministic STT test double (ARCH-012). Variant-by-constructor; configurable per-event delay;
/// honors cancellation via <see cref="FakeStreaming.PaceAsync"/> before each event. Lives in the Api
/// project so the C.4 DI swap can inject it for a keyless local run.
/// </summary>
public sealed class FakeSttProvider : ISttProvider
{
    private readonly FakeSttBehavior _behavior;
    private readonly TimeSpan _delayPerEvent;
    private readonly IReadOnlyList<string> _partials;
    private readonly string _final;
    private readonly ProviderError _error;

    public FakeSttProvider(
        FakeSttBehavior behavior = FakeSttBehavior.SuccessWithPartials,
        TimeSpan? delayPerEvent = null,
        IReadOnlyList<string>? partials = null,
        string final = "hello world",
        ProviderError? error = null)
    {
        _behavior = behavior;
        _delayPerEvent = delayPerEvent ?? TimeSpan.Zero;
        _partials = partials ?? ["hel", "hello", "hello wor"];
        _final = final;
        _error = error ?? new ProviderError(
            "fake-stt", "stt", "stt.upstream_unavailable",
            "The stt provider is temporarily unavailable.", Retryable: true);
    }

    public async IAsyncEnumerable<SttEvent> TranscribeAsync(
        SttRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new SttStarted(DateTimeOffset.UtcNow);

        if (_behavior is FakeSttBehavior.SuccessWithPartials or FakeSttBehavior.PartialsThenError)
        {
            foreach (var partial in _partials)
            {
                await FakeStreaming.PaceAsync(_delayPerEvent, ct);
                yield return new SttPartial(partial, DateTimeOffset.UtcNow);
            }
        }

        if (_behavior == FakeSttBehavior.PartialsThenError)
        {
            await FakeStreaming.PaceAsync(_delayPerEvent, ct);
            yield return new SttFailed(_error, DateTimeOffset.UtcNow);
            yield break;
        }

        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new SttFinal(
            _behavior == FakeSttBehavior.EmptyFinal ? string.Empty : _final,
            DateTimeOffset.UtcNow);
    }
}
