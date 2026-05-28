using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Metrics;

/// <summary>
/// Builds <see cref="LatencyEvent"/>s, stamping <c>relativeMs = round(timestamp − origin)</c>
/// clamped ≥ 0 (ARCH-013). A single event's <c>relativeMs</c> is monotonic against its same-clock
/// origin, so a negative is nonsense and clamped here. Cross-event / cross-clock math belongs to
/// <see cref="MetricsAggregator"/> (which works off absolute <c>Timestamp</c>s and does NOT clamp);
/// <c>relativeMs</c> is only a per-event display/persistence value.
/// </summary>
public sealed class LatencyEventFactory(IClock clock)
{
    /// <summary>Build an event from an explicit <paramref name="timestamp"/>.</summary>
    public LatencyEvent Create(
        string name,
        LatencyStage stage,
        ClockSource clockSource,
        DateTimeOffset timestamp,
        DateTimeOffset origin,
        IReadOnlyDictionary<string, string>? metadata = null)
        => new(
            name,
            stage,
            timestamp,
            RelativeMs(timestamp, origin),
            clockSource,
            metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata));

    /// <summary>
    /// Build an event timestamped at the injected clock's "now" — the server-side stamp-on-arrival
    /// path (deterministic under a fake clock). Producers call this on the real first arrival of a
    /// provider event; they never synthesize or back-date (root forbidden-pattern #3).
    /// </summary>
    public LatencyEvent Stamp(
        string name,
        LatencyStage stage,
        ClockSource clockSource,
        DateTimeOffset origin,
        IReadOnlyDictionary<string, string>? metadata = null)
        => Create(name, stage, clockSource, clock.UtcNow, origin, metadata);

    private static long RelativeMs(DateTimeOffset timestamp, DateTimeOffset origin)
        => Math.Max(0L, (long)Math.Round((timestamp - origin).TotalMilliseconds));
}
