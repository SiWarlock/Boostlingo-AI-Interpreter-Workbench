namespace AiInterpreter.Api.Providers.Abstractions;

/// <summary>
/// Normalized, UI-safe provider error (ARCH-005 / ARCH-012 / ARCH-018). Carries no stack traces,
/// no secrets, no raw provider payloads — only a safe message + a normalized
/// <c>&lt;stage&gt;.&lt;reason&gt;</c> code. The sanitizer (B.8) produces these; they persist on the
/// turn (ARCH-016 inv. 10).
/// </summary>
public sealed record ProviderError(
    string Provider,
    string Stage,
    string Code,
    string SafeMessage,
    bool Retryable,
    int? HttpStatusCode = null);
