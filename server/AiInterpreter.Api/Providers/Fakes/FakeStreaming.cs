namespace AiInterpreter.Api.Providers.Fakes;

/// <summary>
/// Shared pacing + cancellation helper for the streaming fakes (ARCH-012). Called before each
/// <c>yield</c>: it applies the configurable per-event delay AND observes the
/// <see cref="CancellationToken"/> in one place — deterministic <see cref="OperationCanceledException"/>
/// even at <see cref="TimeSpan.Zero"/> (the explicit throw also avoids the CS1998 "async method
/// lacks await" warning under warnings-as-errors).
/// </summary>
internal static class FakeStreaming
{
    public static async Task PaceAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(delay, ct);
    }
}
