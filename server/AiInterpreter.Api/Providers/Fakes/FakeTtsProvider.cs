using System.Runtime.CompilerServices;
using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Providers.Fakes;

public enum FakeTtsBehavior
{
    ChunkedThenComplete,
    CompleteOnly,
    Error,
}

/// <summary>
/// Deterministic TTS test double (ARCH-012). Emits the first-audio marker then chunked audio (or
/// completes without further chunks, or fails), per the configured variant.
/// </summary>
public sealed class FakeTtsProvider : ITtsProvider
{
    private readonly FakeTtsBehavior _behavior;
    private readonly TimeSpan _delayPerEvent;
    private readonly int _chunkCount;
    private readonly string _contentType;
    private readonly ProviderError _error;

    public FakeTtsProvider(
        FakeTtsBehavior behavior = FakeTtsBehavior.ChunkedThenComplete,
        TimeSpan? delayPerEvent = null,
        int chunkCount = 3,
        string contentType = "audio/mpeg",
        ProviderError? error = null)
    {
        _behavior = behavior;
        _delayPerEvent = delayPerEvent ?? TimeSpan.Zero;
        _chunkCount = chunkCount;
        _contentType = contentType;
        _error = error ?? new ProviderError(
            "fake-openai", "tts", "tts.upstream_unavailable",
            "The tts provider is temporarily unavailable.", Retryable: true);
    }

    public async IAsyncEnumerable<TtsEvent> SynthesizeAsync(
        TtsRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new TtsStarted(DateTimeOffset.UtcNow);

        if (_behavior == FakeTtsBehavior.Error)
        {
            await FakeStreaming.PaceAsync(_delayPerEvent, ct);
            yield return new TtsFailed(_error, DateTimeOffset.UtcNow);
            yield break;
        }

        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new TtsFirstAudio(_contentType, DateTimeOffset.UtcNow);

        if (_behavior == FakeTtsBehavior.ChunkedThenComplete)
        {
            for (var seq = 0; seq < _chunkCount; seq++)
            {
                await FakeStreaming.PaceAsync(_delayPerEvent, ct);
                yield return new TtsAudioChunk([1, 2, 3], seq, DateTimeOffset.UtcNow);
            }
        }

        await FakeStreaming.PaceAsync(_delayPerEvent, ct);
        yield return new TtsComplete(_contentType, DateTimeOffset.UtcNow);
    }
}
