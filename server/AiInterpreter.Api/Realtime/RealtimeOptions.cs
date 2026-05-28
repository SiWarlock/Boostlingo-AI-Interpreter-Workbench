namespace AiInterpreter.Api.Realtime;

/// <summary>
/// OpenAI Realtime provider configuration (ARCH-012 enumerated). Bound from the "Realtime" config
/// section via <c>IOptions</c> (wired in A.5). <see cref="ApiKey"/> is the standard OpenAI key —
/// backend-only (ARCH-019); the browser only ever receives the short-lived ephemeral credential
/// (<c>ek_…</c>) minted in E.1, never this key.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-realtime";
    public string Voice { get; set; } = "alloy";
    public string? InstructionsTemplate { get; set; }
    public int ExpirySeconds { get; set; } = 600;
    public int TokenTimeoutSeconds { get; set; } = 10;
    public string TranscriptionModel { get; set; } = "gpt-4o-transcribe";
}
