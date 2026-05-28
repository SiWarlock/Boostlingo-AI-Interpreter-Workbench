namespace AiInterpreter.Api.Common;

/// <summary>
/// Injectable clock abstraction (ARCH-005) so time-dependent logic — latency origins, session
/// timestamps — is deterministic under test (inject a fixed-time fake).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by the system wall clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
