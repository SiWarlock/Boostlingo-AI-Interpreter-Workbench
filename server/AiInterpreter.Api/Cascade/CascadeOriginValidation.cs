namespace AiInterpreter.Api.Cascade;

/// <summary>
/// SECURITY boundary (C.4b, ARCH-019): the WS <c>Origin</c> allow/deny decision. A WebSocket upgrade
/// BYPASSES the CORS middleware, so <see cref="CascadeWebSocketEndpoint"/> must validate the Origin
/// header itself before accepting the socket. Browsers send a canonical Origin
/// (<c>scheme://host[:port]</c>, no trailing slash, no path), so an <b>exact ordinal</b> match against
/// the configured <c>FRONTEND_ORIGIN</c> (the same value CORS uses) is correct — null / empty /
/// whitespace / the opaque <c>"null"</c> Origin all deny. Pure + unit-TDD'd; kept in its own file so
/// the auth-boundary validation commits in isolation (mirrors the C.4a <see cref="CascadeStartValidation"/>).
/// </summary>
internal static class CascadeOriginValidation
{
    /// <summary>True iff <paramref name="origin"/> exactly (ordinal) equals the allowed origin and is non-blank.</summary>
    public static bool IsAllowedOrigin(string? origin, string allowed) =>
        !string.IsNullOrWhiteSpace(origin) && string.Equals(origin, allowed, StringComparison.Ordinal);
}
