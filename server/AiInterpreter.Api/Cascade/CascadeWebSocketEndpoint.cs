using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Http;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// The cascade streaming WebSocket endpoint (C.4a, ARCH-009 <c>WS /api/cascade/stream</c>) — the thin TRANSPORT
/// shell (manual-smoke). It accepts the socket, reads the <c>start</c> frame, bridges binary PCM frames into the
/// UNCHANGED B.4 <see cref="CascadeStreamingOrchestrator"/> via a <see cref="Channel{T}"/>, maps the orchestrator's
/// <see cref="CascadeOutputEvent"/>s to ARCH-009 server messages, computes + emits cost before <c>done</c>, and
/// persists the turn best-effort. ALL decision logic is in the pure <see cref="CascadeWsMapping"/> /
/// <see cref="CascadeStartValidation"/> (unit-TDD'd); this glue is exercised by manual smoke (real socket + keys).
///
/// C.4b adds the remaining boundary hardening (Origin validation, ContentType clamp, double-end / stream-without-
/// terminal). C.4a deliberately does NOT do those.
/// </summary>
public sealed class CascadeWebSocketEndpoint(
    CascadeStreamingOrchestrator orchestrator,
    SessionStore store,
    SessionPersistenceWriter writer,
    CostEstimator costEstimator,
    LatencyEventFactory factory,
    IClock clock,
    string allowedOrigin)
{
    private const int ReceiveBufferSize = 16 * 1024;

    // Bounded audio bridge (C.4b SECURITY): backpressure (FullMode.Wait) caps the buffer when a stalled STT
    // consumer can't keep up — an unbounded channel would grow for the whole turn (a per-session DoS;
    // G.4 5-min-no-leak tie). ~256 frames ≈ 5-12s of 20-50ms PCM; backpressure throttles the socket read
    // rather than dropping audio (dropping corrupts the transcription).
    private const int AudioChannelCapacity = 256;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "cascade.invalid_request",
                "A WebSocket upgrade is required for this endpoint.");
            return;
        }

        // The WS upgrade BYPASSES the CORS middleware (ARCH-019), so validate the Origin here, before accept.
        var origin = context.Request.Headers.Origin.ToString();
        if (!CascadeOriginValidation.IsAllowedOrigin(origin, allowedOrigin))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "cascade.forbidden_origin",
                "The request origin is not allowed.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        try
        {
            await RunTurnAsync(socket, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // client/server abort — fall through to a best-effort close.
        }

        await CloseQuietlyAsync(socket);
    }

    private async Task RunTurnAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var startResult = await socket.ReceiveAsync(buffer, ct);
        if (startResult.MessageType != WebSocketMessageType.Text)
        {
            return; // socket closed / non-text before a start frame
        }

        if (!startResult.EndOfMessage)
        {
            // The start frame is a small JSON; a fragmented (> ReceiveBufferSize) frame is oversized — reject
            // WITHOUT buffering more (a DoS guard at the boundary, C.4b; manual-smoke). ARCH-019.
            var oversized = new ProviderError("cascade", "cascade", "cascade.invalid_request",
                "The start frame is too large.", Retryable: false);
            await SendAsync(socket, new { type = "error", error = oversized }, ct);
            return;
        }

        var startText = Encoding.UTF8.GetString(buffer, 0, startResult.Count);
        var parse = CascadeStartValidation.ParseStart(startText);
        if (parse.Error is not null)
        {
            await SendAsync(socket, new { type = "error", error = parse.Error }, ct);
            return;
        }

        var p = parse.Params!;
        var turn = store.Get(p.SessionId)?.Turns.FirstOrDefault(t => t.TurnId == p.TurnId);
        if (turn is null)
        {
            var notFound = new ProviderError("cascade", "cascade", "turn.not_found", "The turn was not found.", Retryable: false);
            await SendAsync(socket, new { type = "error", error = notFound }, ct);
            return;
        }

        var origin = clock.UtcNow;
        var collected = new List<CascadeOutputEvent>();

        // turn.recording.started (Overall) — server-clock, stamped at the start frame.
        await SendLatencyAsync(socket, CascadeWsMapping.RecordingEvent(factory, LatencyEventNames.TurnRecordingStarted, origin), collected, p.TurnId, ct);

        // PCM frames -> bounded Channel<AudioFrame> (the orchestrator's audio stream). The receive pump is the
        // ONLY reader of the socket + ONLY writer of the channel; the main loop is the ONLY sender + ONLY reader
        // of the channel (no concurrent sends -> no send lock; SingleReader/SingleWriter optimizations apply).
        var audio = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(AudioChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        LatencyEvent? recordingStopped = null;
        var pump = PumpAudioAsync(socket, audio.Writer, () => recordingStopped = CascadeWsMapping.RecordingEvent(factory, LatencyEventNames.TurnRecordingStopped, origin), ct);

        await foreach (var ev in orchestrator.RunAsync(p, audio.Reader.ReadAllAsync(ct), ct).WithCancellation(ct))
        {
            if (ev is Done done)
            {
                await EmitTerminalAsync(socket, p, turn, collected, recordingStopped, origin, done, ct);
                // Unblock the pump: after Done nothing reads the channel, so a pump parked on a backpressured
                // WriteAsync (full channel) would never drain — completing the writer throws it out cleanly.
                audio.Writer.TryComplete();
                await pump; // PumpAudioAsync swallows the resulting ChannelClosedException + completes in finally
                return;
            }

            // Audio is streamed to the client but NEVER accumulated/persisted (safety invariant #3); the
            // rest (transcripts/latency/errors) is collected for the persisted turn.
            if (ev is not Audio)
            {
                collected.Add(ev);
            }

            await SendAsync(socket, CascadeWsMapping.ToServerMessage(ev, p.TurnId), ct);
        }

        // Orchestrator stream ended without a Done (defensive — it always yields one); unblock the pump.
        audio.Writer.TryComplete();
        await pump;
    }

    // turn.recording.stopped (stashed at stop time) -> cost (computed, before done) -> done -> persist.
    private async Task EmitTerminalAsync(
        WebSocket socket, CascadeStartParams p, InterpretationTurn turn, List<CascadeOutputEvent> collected,
        LatencyEvent? recordingStopped, DateTimeOffset origin, Done done, CancellationToken ct)
    {
        if (recordingStopped is not null)
        {
            await SendLatencyAsync(socket, recordingStopped, collected, p.TurnId, ct);
        }

        var stoppedAt = recordingStopped?.Timestamp ?? clock.UtcNow;
        // Fold the observed usage from the accumulated events (translation tokens summed additively across
        // segments + target chars) — the C.4b extraction of the C.4a inline accumulation.
        var cost = ComputeCost(p, CascadeWsMapping.FoldCostInputs(collected), origin, stoppedAt);
        var costMessage = CascadeWsMapping.ToCostMessageOrNull(cost);
        if (costMessage is not null)
        {
            await SendAsync(socket, costMessage, ct);
        }

        collected.Add(done);
        await SendAsync(socket, CascadeWsMapping.ToServerMessage(done, p.TurnId), ct);

        await PersistAsync(p, collected, cost, stoppedAt, origin);
    }

    // Best-effort cost from observed usage: STT audio-minutes (recording duration), translation tokens (from the
    // translation.final Metadata, C.4 FORK-1a), and a TTS character proxy (target text length) — audio-streaming
    // mode reports no TTS usage block, so precise TTS audio-token cost is unavailable (documented limitation).
    private Result<CostEstimate> ComputeCost(
        CascadeStartParams p, CascadeCostInputs inputs, DateTimeOffset origin, DateTimeOffset stoppedAt)
    {
        var durationMs = Math.Max(0, (long)(stoppedAt - origin).TotalMilliseconds);
        var audioMinutes = durationMs / 60000m;
        var sttUsage = new CostUsage { AudioMinutes = audioMinutes };
        var translationUsage = new CostUsage { InputTokens = inputs.InputTokens, OutputTokens = inputs.OutputTokens };
        var ttsUsage = new CostUsage { Characters = inputs.TargetChars > 0 ? inputs.TargetChars : null };

        return costEstimator.EstimateCascadeTurn(p.TranslationModel, p.TtsModel, sttUsage, translationUsage, ttsUsage, durationMs);
    }

    private async Task PersistAsync(
        CascadeStartParams p, List<CascadeOutputEvent> collected, Result<CostEstimate> cost, DateTimeOffset completedAt, DateTimeOffset origin)
    {
        var durationMs = Math.Max(0, (long)(completedAt - origin).TotalMilliseconds);
        // Idempotent terminal finalize (C.4b): if the turn is already terminal (the HTTP /complete or a prior
        // WS terminal already landed), FinalizeTurn returns Applied=false and we skip the redundant persist.
        var result = store.FinalizeTurn(p.SessionId, p.TurnId, current =>
            CascadeWsMapping.AssembleTurn(current, collected, cost, completedAt) with
            {
                AudioDurationMs = durationMs,
                TranslationModelUsed = p.TranslationModel,
                TtsVoiceUsed = p.TtsVoice,
            });

        if (result is not { Applied: true })
        {
            return; // turn unknown, or already terminal — no (re-)persist
        }

        var session = store.Get(p.SessionId);
        if (session is not null)
        {
            await writer.WriteAsync(session); // best-effort (Result; never throws) — a write failure does not fail the turn
        }
    }

    private async Task PumpAudioAsync(
        WebSocket socket, ChannelWriter<AudioFrame> writer, Action onStop, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    // One binary message == one PCM frame (the client sends ~20-50ms frames). Fragmented frames
                    // (EndOfMessage == false) are a manual-smoke refinement; the common path is one-message-per-frame.
                    // Backpressure: a bounded channel + WriteAsync stalls this socket read when the orchestrator
                    // (STT consumer) falls behind, capping memory rather than dropping audio (C.4b SECURITY).
                    await writer.WriteAsync(new AudioFrame(buffer[..result.Count], clock.UtcNow), ct);
                }
                else if (result.MessageType == WebSocketMessageType.Text && IsStopFrame(buffer, result.Count))
                {
                    onStop();
                    break;
                }
            }
        }
        catch (Exception)
        {
            // socket/read failure — the orchestrator's audio stream ends; the turn terminates via its own paths.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static bool IsStopFrame(byte[] buffer, int count)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
            return doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "stop";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task SendLatencyAsync(WebSocket socket, LatencyEvent ev, List<CascadeOutputEvent> collected, string turnId, CancellationToken ct)
    {
        var wrapped = new Latency(ev);
        collected.Add(wrapped);
        await SendAsync(socket, CascadeWsMapping.ToServerMessage(wrapped, turnId), ct);
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // Pre-accept HTTP rejection (non-WS request / disallowed Origin) — a UiError-shaped body (ARCH-018/019),
    // never a bare status, so the shape matches the rest of the API's error contract.
    private static async Task RejectAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        var body = new UiError(code, message, Stage: null, Retryable: false, TurnId: null);
        await context.Response.WriteAsJsonAsync(body, JsonDefaults.Options);
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "turn complete", CancellationToken.None);
            }
            catch (Exception)
            {
                // best-effort close
            }
        }
    }
}
