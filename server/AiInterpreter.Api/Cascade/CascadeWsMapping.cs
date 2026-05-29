using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// The pure, deterministic surface of <see cref="CascadeWebSocketEndpoint"/> (C.4a) — the ARCH-009 server-message
/// mapping, the cost emit/degrade decision, the turn-lifecycle stamp, and the turn assembly. The endpoint is a thin
/// transport shell (socket accept, frame loop, PCM-&gt;AudioFrame Channel bridge — manual-smoke); these helpers hold
/// the decision logic and are unit-TDD'd (lesson §18/§20 over a WS boundary). The SECURITY <c>start</c>-frame
/// validation lives separately in <see cref="CascadeStartValidation"/>. <c>internal</c> + InternalsVisibleTo keeps
/// these unit-reachable.
/// </summary>
internal static class CascadeWsMapping
{
    /// <summary>Maps a <see cref="CascadeOutputEvent"/> to the exact ARCH-009 server message (camelCase via JsonDefaults).</summary>
    public static object ToServerMessage(CascadeOutputEvent ev, string turnId) => ev switch
    {
        Transcript t => (object)new { type = "transcript", segment = t.Segment },
        Latency l => new { type = "latency", @event = l.Event },
        Audio a => new { type = "audio", contentType = a.ContentType, seq = a.Seq, base64 = Convert.ToBase64String(a.Bytes) },
        Error e => new { type = "error", error = e.ProviderError },
        Done d => new { type = "done", turnId, status = d.Status },
        _ => throw new ArgumentOutOfRangeException(nameof(ev), ev.GetType().Name, "Unknown cascade output event."),
    };

    /// <summary>The <c>cost</c> message on a successful estimate; <c>null</c> when unavailable (B.5 degrade — no message, no crash).</summary>
    public static object? ToCostMessageOrNull(Result<CostEstimate> cost) =>
        cost.IsSuccess ? new { type = "cost", estimate = cost.Value } : null;

    /// <summary>Stamps a turn-lifecycle event (<c>turn.recording.*</c>) as <see cref="LatencyStage.Overall"/>, server-clock.</summary>
    public static LatencyEvent RecordingEvent(LatencyEventFactory factory, string name, DateTimeOffset origin) =>
        factory.Stamp(name, LatencyStage.Overall, ClockSource.Server, origin);

    /// <summary>
    /// Assembles the final <see cref="InterpretationTurn"/> on <c>done</c> by folding the accumulated cascade events
    /// (transcripts/latency/errors) onto the base turn + the computed cost + completion time. Pure; the best-effort
    /// persist (B.7 <c>SessionPersistenceWriter</c>) is the shell's job.
    /// </summary>
    public static InterpretationTurn AssembleTurn(
        InterpretationTurn baseTurn,
        IReadOnlyList<CascadeOutputEvent> events,
        Result<CostEstimate> cost,
        DateTimeOffset completedAt)
    {
        var transcripts = new List<TranscriptSegment>(baseTurn.Transcripts);
        var latency = new List<LatencyEvent>(baseTurn.LatencyEvents);
        var errors = new List<ProviderError>(baseTurn.Errors);
        var status = baseTurn.Status;

        foreach (var ev in events)
        {
            switch (ev)
            {
                case Transcript t: transcripts.Add(t.Segment); break;
                case Latency l: latency.Add(l.Event); break;
                case Error e: errors.Add(e.ProviderError); break;
                case Done d: status = d.Status; break;
            }
        }

        return baseTurn with
        {
            Transcripts = transcripts,
            LatencyEvents = latency,
            Errors = errors,
            Status = status,
            CostEstimate = cost.IsSuccess ? cost.Value : null,
            CompletedAt = completedAt,
        };
    }
}
