using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using AiInterpreter.Api.Providers.Abstractions;
using Deepgram;
using Deepgram.Clients.Interfaces.v2;
using Microsoft.Extensions.Options;
using DeepgramPrerecordedSchema = Deepgram.Models.Listen.v1.REST.PreRecordedSchema;
using DeepgramWsCloseResponse = Deepgram.Models.Listen.v2.WebSocket.CloseResponse;
using DeepgramWsErrorResponse = Deepgram.Models.Listen.v2.WebSocket.ErrorResponse;
using DeepgramWsResultResponse = Deepgram.Models.Listen.v2.WebSocket.ResultResponse;

namespace AiInterpreter.Api.Providers.Deepgram;

/// <summary>
/// Real Deepgram STT provider (C.1) behind the unchanged B.1 <see cref="ISttProvider"/> seam. Two transport paths,
/// routed by the request's <c>Encoding</c> (Q1): raw <c>linear16</c> PCM -> live WebSocket (interim + final); any other
/// (recorded-container) encoding -> the pre-recorded REST fallback (single final, no interim — Deepgram auto-detects the
/// container, so no transcoding, ARCH-030/011).
///
/// This class is the network TRANSPORT SHELL only — all decision logic lives in the pure, unit-tested
/// <see cref="DeepgramSttMapping"/>. The shell itself is manual-smoke (ARCH-020 posture). The callback-based SDK WS
/// client is bridged to the <see cref="ISttProvider"/> async-enumerable contract via an unbounded
/// <see cref="Channel{T}"/> (Q4): callbacks write mapped events; the iterator reads + yields; cancellation/teardown
/// always closes the socket (no leak — ties to G.4 stability).
/// </summary>
public sealed class DeepgramSttProvider : ISttProvider
{
    private const string Linear16 = "linear16";

    private readonly DeepgramOptions _options;

    public DeepgramSttProvider(IOptions<DeepgramOptions> options) => _options = options.Value;

    public IAsyncEnumerable<SttEvent> TranscribeAsync(SttRequest request, CancellationToken ct) =>
        string.Equals(request.Encoding, Linear16, StringComparison.OrdinalIgnoreCase)
            ? TranscribeLiveAsync(request, ct)
            : TranscribePrerecordedAsync(request, ct);

    // --- live WebSocket path: callback SDK -> Channel<SttEvent> bridge ---

    private async IAsyncEnumerable<SttEvent> TranscribeLiveAsync(
        SttRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SttEvent>();
        var client = ClientFactory.CreateListenWebSocketClient(_options.ApiKey ?? string.Empty);

        await client.Subscribe((object? _, DeepgramWsResultResponse result) =>
            channel.Writer.TryWrite(DeepgramSttMapping.ToSttEvent(result, DateTimeOffset.UtcNow)));
        await client.Subscribe((object? _, DeepgramWsErrorResponse _) =>
        {
            // The SDK error message is intentionally NOT propagated (ToFailed's SafeMessage is fixed-per-code).
            channel.Writer.TryWrite(DeepgramSttMapping.ToFailed(
                new InvalidOperationException("deepgram websocket error"), DateTimeOffset.UtcNow));
            channel.Writer.TryComplete();
        });
        await client.Subscribe((object? _, DeepgramWsCloseResponse _) => channel.Writer.TryComplete());

        yield return new SttStarted(DateTimeOffset.UtcNow);

        // Stop() is called on BOTH the graceful close (after SendFinalize, to trigger the server CloseResponse
        // that completes the channel) AND on iterator teardown/cancellation. The Deepgram SDK v6.6.1 Stop() is
        // NOT safe to call twice — a double-Stop derefs an already-disposed field internally, logging a benign
        // but alarming `[Error] Stop: NullReferenceException` (the real-key-smoke noise that derailed a
        // diagnosis). Guard so the SDK Stop() runs at most once (Interlocked — the graceful-close caller and
        // the teardown caller can race; whichever wins sets the latch synchronously before awaiting Stop()).
        var stopped = 0;
        async Task StopOnceAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0)
            {
                try
                {
                    await client.Stop();
                }
                catch
                {
                    // best-effort socket close on teardown/cancellation — no leak (G.4).
                }
            }
        }

        var pump = PumpAudioAsync(client, request, channel.Writer, StopOnceAsync, ct);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
            {
                yield return ev;
            }
        }
        finally
        {
            await StopOnceAsync(); // idempotent — a no-op if the graceful close already stopped the client.
            await pump; // PumpAudioAsync never throws (failures surface as SttFailed).
        }
    }

    private async Task PumpAudioAsync(
        IListenWebSocketClient client,
        SttRequest request,
        ChannelWriter<SttEvent> writer,
        Func<Task> stopOnceAsync,
        CancellationToken ct)
    {
        try
        {
            await client.Connect(DeepgramSttMapping.BuildLiveSchema(request, _options));
            await foreach (var frame in request.AudioFrames.WithCancellation(ct))
            {
                client.Send(frame.Bytes.ToArray());
            }

            await client.SendFinalize();
            await stopOnceAsync(); // graceful close -> server flushes finals then emits CloseResponse -> completes channel.
        }
        catch (OperationCanceledException)
        {
            // caller cancellation — the iterator's finally closes the socket; no SttFailed for a clean cancel.
        }
        catch (Exception ex)
        {
            writer.TryWrite(DeepgramSttMapping.ToFailed(ex, DateTimeOffset.UtcNow));
        }
        finally
        {
            writer.TryComplete(); // never let the reader hang if the SDK emits no terminal event.
        }
    }

    // --- pre-recorded REST fallback path: drain the blob -> single response -> parse ---

    private async IAsyncEnumerable<SttEvent> TranscribePrerecordedAsync(
        SttRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        SttFailed? failure = null;
        IReadOnlyList<SttEvent> events = Array.Empty<SttEvent>();

        try
        {
            var audio = await DrainAsync(request.AudioFrames, ct);
            var client = ClientFactory.CreateListenRESTClient(_options.ApiKey ?? string.Empty);
            var schema = new DeepgramPrerecordedSchema
            {
                Model = _options.Model,
                Language = request.SttLanguage,
                SmartFormat = _options.SmartFormat,
            };

            // Linked CTS bridges the caller's token to the SDK's CTS-typed parameter AND enforces the
            // configured per-stage timeout (ARCH-012). A timeout fires linked (not ct) -> the OCE falls
            // through to the catch-all below -> ProviderErrorMapper -> stt.timeout (retryable).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var response = await client.TranscribeFile(audio, schema, linked);
            events = DeepgramSttMapping.ParsePrerecorded(response, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation propagates; not a misleading stt.timeout SttFailed.
        }
        catch (Exception ex)
        {
            failure = DeepgramSttMapping.ToFailed(ex, DateTimeOffset.UtcNow);
        }

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        foreach (var ev in events)
        {
            yield return ev;
        }
    }

    private static async Task<byte[]> DrainAsync(IAsyncEnumerable<AudioFrame> frames, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct))
        {
            buffer.Write(frame.Bytes.Span);
        }

        return buffer.ToArray();
    }
}
