namespace AiInterpreter.Api.Config;

/// <summary>
/// The <c>GET /api/config</c> response (ARCH-009). Reports capability flags from provider-key
/// PRESENCE only — never key values (safety invariant #1 / ARCH-019). Serialized camelCase via
/// <c>JsonDefaults</c>; consumed by D.2 (SessionSetup/ModeToggle config-gating + model selectors).
/// </summary>
public sealed record ConfigResponse(
    RealtimeCapability Realtime,
    CascadeCapability Cascade,
    string[] Languages,
    string PricingConfigVersion);

public sealed record RealtimeCapability(bool Configured, string[] Models);

public sealed record CascadeCapability(
    SttCapability Stt,
    TranslationCapability Translation,
    TtsCapability Tts);

public sealed record SttCapability(bool Configured, string Provider, string Model);

public sealed record TranslationCapability(bool Configured, string Provider, string[] Models);

public sealed record TtsCapability(bool Configured, string Provider, string Model);
