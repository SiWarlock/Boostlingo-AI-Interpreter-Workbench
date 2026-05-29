using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiInterpreter.Tests;

// B.9a — GlobalExceptionHandler (ARCH-018/019, safety invariant #4 boundary). SAFETY slice: test 1 is
// the centerpiece — an UNHANDLED exception must not leak a stack/secret to the client (the default
// ASP.NET error page would). The handler is thin: it routes through B.8's ErrorSanitizer (which logs
// the original server-side) and serializes the safe UiError. Unit-tested directly via DefaultHttpContext
// + a MemoryStream body (no controllers needed — the handler is middleware).
//
// In the HostEnv collection: test 5 boots a WebApplicationFactory, which must serialize against the
// other env-var-touching host tests (process-wide env vars) — same guard as HostIntegrationTests.
[Collection("HostEnv")]
public class GlobalExceptionHandlerTests
{
    private static async Task<(bool Handled, DefaultHttpContext Ctx, string Body)> Handle(Exception ex)
    {
        var sanitizer = new ErrorSanitizer(NullLogger<ErrorSanitizer>.Instance);
        var handler = new GlobalExceptionHandler(sanitizer);

        var ctx = new DefaultHttpContext();
        var stream = new MemoryStream();
        ctx.Response.Body = stream;

        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        stream.Position = 0;
        var body = await new StreamReader(stream).ReadToEndAsync();
        return (handled, ctx, body);
    }

    // 1 (SAFETY #4) — an unhandled exception carrying a secret + stack -> the written body contains
    // neither; code=internal.error, retryable=false.
    [Fact]
    public async Task unhandled_exception_body_excludes_secret_and_stack()
    {
        var (_, _, body) = await Handle(new Exception("auth for sk-SENTINEL-123\n  at Foo.Bar()"));

        Assert.DoesNotContain("sk-SENTINEL-123", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Foo.Bar", body, StringComparison.Ordinal);

        var ui = JsonSerializer.Deserialize<UiError>(body, JsonDefaults.Options);
        Assert.NotNull(ui);
        Assert.Equal("internal.error", ui!.Code);
        Assert.False(ui.Retryable);
    }

    // 2 — a generic exception -> response status 500 (the sanitizer's default).
    [Fact]
    public async Task response_status_is_500_for_generic_exception()
    {
        var (_, ctx, _) = await Handle(new Exception("boom"));

        Assert.Equal(500, ctx.Response.StatusCode);
    }

    // 3 — the body is the UiError wire shape: camelCase code/safeMessage/retryable present; NO
    // server-only/leak keys (httpStatusCode / provider / stackTrace / exception / detail).
    [Fact]
    public async Task response_body_is_uierror_shape()
    {
        var (_, _, body) = await Handle(new Exception("boom"));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("code", out _));
        Assert.True(root.TryGetProperty("safeMessage", out _));
        Assert.True(root.TryGetProperty("retryable", out _));
        Assert.False(root.TryGetProperty("httpStatusCode", out _));
        Assert.False(root.TryGetProperty("provider", out _));
        Assert.False(root.TryGetProperty("stackTrace", out _));
        Assert.False(root.TryGetProperty("exception", out _));
        Assert.False(root.TryGetProperty("detail", out _));
    }

    // 4 — Content-Type is JSON and the handler reports the exception handled.
    [Fact]
    public async Task content_type_is_json_and_returns_true()
    {
        var (handled, ctx, _) = await Handle(new Exception("boom"));

        Assert.True(handled);
        Assert.StartsWith("application/json", ctx.Response.ContentType ?? string.Empty, StringComparison.Ordinal);
    }

    // 5 (wiring) — the handler is actually registered in the host as an IExceptionHandler (proves
    // Program.cs called AddExceptionHandler<GlobalExceptionHandler>()). app.UseExceptionHandler() is
    // verified by the host booting (HostIntegrationTests) + code review.
    [Fact]
    public void handler_registered_as_exception_handler()
    {
        using var factory = new WebApplicationFactory<Program>();

        var handlers = factory.Services.GetServices<IExceptionHandler>();

        Assert.Contains(handlers, h => h is GlobalExceptionHandler);
    }
}
