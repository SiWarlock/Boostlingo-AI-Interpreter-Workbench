using AiInterpreter.Api.Cascade;

namespace AiInterpreter.Tests;

// C.4b SECURITY commit — the WS Origin allow/deny decision (ARCH-019). A WebSocket upgrade BYPASSES the
// CORS middleware, so CascadeWebSocketEndpoint must validate the Origin header itself before accepting
// the socket. This pins the pure decision helper; the actual 403-before-accept HTTP write is the shell
// (manual-smoke). Exact ordinal match against the configured FRONTEND_ORIGIN (browsers send a canonical
// Origin: scheme://host[:port], no trailing slash, no path) — null/empty/whitespace always deny.
public class CascadeOriginValidationTests
{
    private const string Allowed = "http://localhost:5173";

    [Theory]
    [InlineData("http://localhost:5173", true)]   // exact match
    [InlineData("http://localhost:5173/", false)] // trailing slash — browsers never send one
    [InlineData("http://localhost:3000", false)]  // wrong port
    [InlineData("https://localhost:5173", false)] // wrong scheme
    [InlineData("http://evil.example", false)]    // wrong host
    [InlineData("HTTP://LOCALHOST:5173", false)]  // case differs — ordinal match
    [InlineData("null", false)]                   // the literal "null" Origin (opaque/file origins) is not allowed
    [InlineData("", false)]                        // empty
    [InlineData("   ", false)]                      // whitespace
    public void origin_allow_deny_matrix(string origin, bool expected)
    {
        Assert.Equal(expected, CascadeOriginValidation.IsAllowedOrigin(origin, Allowed));
    }

    [Fact]
    public void null_origin_denied()
    {
        // A missing Origin header (null) is rejected — a same-origin/no-Origin request to the WS endpoint
        // is not the browser SPA we allow.
        Assert.False(CascadeOriginValidation.IsAllowedOrigin(null, Allowed));
    }
}
