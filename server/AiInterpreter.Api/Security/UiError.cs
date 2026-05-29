using System.Text.Json.Serialization;

namespace AiInterpreter.Api.Security;

/// <summary>
/// Safe, normalized, UI-facing error (ARCH-007 / ARCH-018 / ARCH-019). The frontend projection of an
/// internal/provider error AFTER the B.8 <see cref="ErrorSanitizer"/> — it carries NO stack trace, NO
/// secret, and NO raw provider payload, only a fixed-safe message + a normalized
/// <c>&lt;stage&gt;.&lt;reason&gt;</c> code. Mirrors the ARCH-007 TS <c>UiError</c> wire shape exactly:
/// <c>{ code, safeMessage, stage?, retryable, turnId? }</c> (camelCase via <c>JsonDefaults</c>).
///
/// <see cref="HttpStatusCode"/> is server-only (<c>[JsonIgnore]</c>): B.9's exception handler reads it
/// to set the HTTP response status line; it is NOT part of the wire body (the TS shape has no status
/// field), so the serialized body stays an exact mirror of the TS type.
/// </summary>
public sealed record UiError(
    string Code,
    string SafeMessage,
    string? Stage,
    bool Retryable,
    string? TurnId)
{
    // Not a positional param: excluded from the TS wire shape by [JsonIgnore], but readable via init
    // so B.9's handler can set the response status line from it.
    [JsonIgnore]
    public int? HttpStatusCode { get; init; }
}
