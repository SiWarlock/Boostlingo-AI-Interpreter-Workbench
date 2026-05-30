using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Realtime;

/// <summary>
/// Browser → broker request for an ephemeral Realtime credential (ARCH-009 §6
/// <c>POST /api/realtime/client-secret</c>). The browser supplies the session id, the language
/// direction (drives the interpreter instructions), and an optional model override; it NEVER sees the
/// standard key (invariant #1). <see cref="Model"/> is allow-listed against
/// <see cref="RealtimeModelCatalog"/> at the broker (E.6 sends a catalog model here).
/// </summary>
public sealed record RealtimeTokenRequest(string SessionId, LanguageDirection Direction, string? Model);

/// <summary>
/// Broker → browser response (ARCH-009 §6). Carries ONLY the short-lived ephemeral client secret
/// (<c>ek_…</c>), its expiry, and the resolved model — never the standard key (invariant #1). The
/// <see cref="ClientSecret"/> is response-only: it is never persisted or logged (invariant #2).
/// </summary>
public sealed record RealtimeTokenResponse(string ClientSecret, string ExpiresAt, string Model);

/// <summary>
/// Internal service → controller outcome (area-local, never serialized): exactly one of
/// <see cref="Response"/> (success) or <see cref="Error"/> (a normalized, already-safe
/// <see cref="ProviderError"/>) is non-null. Mirrors the <c>CascadeController</c> result→DTO boundary
/// (Step-2.5 Q2): the controller maps <see cref="Error"/> via <c>ErrorSanitizer.ToUiError</c> +
/// <c>StatusCode(err.HttpStatusCode ?? 502, …)</c>, keeping code/stage/retryable/status intact.
/// </summary>
public sealed record RealtimeMintOutcome(RealtimeTokenResponse? Response, ProviderError? Error)
{
    public static RealtimeMintOutcome Ok(RealtimeTokenResponse response) => new(response, null);

    public static RealtimeMintOutcome Fail(ProviderError error) => new(null, error);
}

/// <summary>
/// The closed set of selectable Realtime models (ARCH-010 / ARCH-014). The broker allow-lists the
/// resolved model against this set (Step-2.5 Q4) so an off-catalog model fails closed at the mint
/// boundary instead of reaching OpenAI. Single area-owned source; <c>ConfigService</c> currently keeps
/// its own private copy for the capability catalog (flagged for a future unify — Step 9).
/// </summary>
public static class RealtimeModelCatalog
{
    // ARCH-010 / ARCH-014 allow-listed realtime models; extend when OpenAI adds a variant (E.6 selector reads this).
    public static readonly IReadOnlySet<string> Models =
        new HashSet<string>(StringComparer.Ordinal) { "gpt-realtime", "gpt-realtime-mini" };
}
