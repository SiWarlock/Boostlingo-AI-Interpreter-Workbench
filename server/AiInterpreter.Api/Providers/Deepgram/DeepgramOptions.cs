namespace AiInterpreter.Api.Providers.Deepgram;

/// <summary>
/// Deepgram STT provider configuration (ARCH-012 enumerated). Bound from the "Deepgram" config
/// section via <c>IOptions</c> (wired in A.5). Inline defaults are the single source of truth —
/// production with no env override == exactly what <c>OptionsBindingTests</c> assert.
/// </summary>
public sealed class DeepgramOptions
{
    public const string SectionName = "Deepgram";

    /// <summary>
    /// Standard Deepgram API key — backend-only, never serialized to the SPA (ARCH-019).
    /// Supplied via the <c>DEEPGRAM_API_KEY</c> env var (flat→section bridge in A.5).
    /// </summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.deepgram.com";
    public string WebSocketUrl { get; set; } = "wss://api.deepgram.com/v1/listen";
    public string Model { get; set; } = "nova-3";
    public string Language { get; set; } = "multi";
    public bool SmartFormat { get; set; } = true;
    public string Encoding { get; set; } = "linear16";
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 1;
    public bool InterimResults { get; set; } = true;
    public int UtteranceEndMs { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 30;
}
