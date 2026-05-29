using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Security;
using Microsoft.Extensions.Logging;

namespace AiInterpreter.Tests;

// B.8 — ErrorSanitizer (ARCH-018/019, safety invariant #4). SAFETY slice: tests 1 + 4 are the
// invariant centerpiece — the SafeMessage (and every serialized field) must contain NO secret, NO
// stack, NO raw Result.Error/path. Safe-by-construction: the sanitizer never interpolates untrusted
// input into the output; it returns a fixed-safe message per code and logs the original server-side
// only (lesson §5 extended to the global boundary).
public class ErrorSanitizerTests
{
    private const string SecretKey = "sk-SENTINEL-KEY-123";
    private const string StackText = "at Foo.Bar()";

    // Captures what the sanitizer logs so we can prove the ORIGINAL is preserved server-side while
    // the returned UiError is safe.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = new();
        public readonly List<Exception?> Exceptions = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }

    private static ErrorSanitizer Sanitizer() =>
        new(new CapturingLogger<ErrorSanitizer>());

    private static string Json(UiError e) => JsonSerializer.Serialize(e, JsonDefaults.Options);

    // 1 (SAFETY #4) — an exception carrying a secret + stack -> a UiError whose EVERY serialized field
    // contains neither the secret nor the stack text.
    [Fact]
    public void sanitize_exception_strips_secret_and_stack()
    {
        var ex = new Exception($"auth failed for {SecretKey}\n  {StackText}");

        var ui = Sanitizer().Sanitize(ex);
        var json = Json(ui);

        Assert.DoesNotContain(SecretKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", json, StringComparison.Ordinal);
        Assert.DoesNotContain(StackText, json, StringComparison.Ordinal);
        Assert.DoesNotContain("at Foo", json, StringComparison.Ordinal);
        Assert.Equal("internal.error", ui.Code);
        Assert.False(ui.Retryable);
    }

    // 2 — an arbitrary exception -> a fixed safe message (not the exception text) + internal.error +
    // status 500 (server-side).
    [Fact]
    public void sanitize_exception_generic_message()
    {
        var ui = Sanitizer().Sanitize(new Exception("connection string Password=hunter2 failed"));

        Assert.Equal("internal.error", ui.Code);
        Assert.Equal(500, ui.HttpStatusCode);
        Assert.DoesNotContain("hunter2", Json(ui), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ui.SafeMessage));
        Assert.DoesNotContain("connection string", ui.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 3 — an already-safe ProviderError projects to a UiError; Provider is dropped (not on UiError),
    // Code/SafeMessage/Stage/Retryable + status carry through.
    [Fact]
    public void provider_error_projects_to_uierror()
    {
        var pe = new ProviderError("deepgram", "stt", "stt.rate_limited",
            "The stt provider is rate-limited; please retry shortly.", Retryable: true, HttpStatusCode: 429);

        var ui = Sanitizer().ToUiError(pe);

        Assert.Equal("stt.rate_limited", ui.Code);
        Assert.Equal("stt", ui.Stage);
        Assert.True(ui.Retryable);
        Assert.Equal(429, ui.HttpStatusCode);
        Assert.Equal(pe.SafeMessage, ui.SafeMessage);
        // The internal provider identity never crosses the wire: no "provider" JSON KEY (key-form so
        // we don't alias the legitimate word "provider" inside the safe message) + no provider value.
        var json = Json(ui);
        Assert.DoesNotContain("\"provider\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deepgram", json, StringComparison.OrdinalIgnoreCase);
    }

    // 4 (SAFETY) — a failed Result whose Error embeds a path + secret -> a UiError that echoes
    // NEITHER; a fixed-safe message per code; code preserved.
    [Fact]
    public void result_error_string_not_echoed()
    {
        var failed = Result.Failure("persistence.failed: C:\\secret\\sk-abc\\session.json");

        var ui = Sanitizer().SanitizeResult("persistence.failed", failed);
        var json = Json(ui);

        Assert.Equal("persistence.failed", ui.Code);
        Assert.DoesNotContain("sk-abc", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("session.json", json, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ui.SafeMessage));
    }

    // 5 — Retryable + the server-side HTTP status round-trip (projected 429 retryable vs generic 500
    // non-retryable).
    [Fact]
    public void retryable_and_status_preserved()
    {
        var sanitizer = Sanitizer();

        var retry = sanitizer.ToUiError(
            new ProviderError("deepgram", "stt", "stt.rate_limited", "msg", Retryable: true, HttpStatusCode: 429));
        Assert.True(retry.Retryable);
        Assert.Equal(429, retry.HttpStatusCode);

        var generic = sanitizer.Sanitize(new Exception("boom"));
        Assert.False(generic.Retryable);
        Assert.Equal(500, generic.HttpStatusCode);
    }

    // 6 — the ORIGINAL (full) error is logged server-side, while the returned UiError stays safe.
    [Fact]
    public void original_logged_server_side()
    {
        var logger = new CapturingLogger<ErrorSanitizer>();
        var sanitizer = new ErrorSanitizer(logger);
        var ex = new Exception($"auth failed for {SecretKey}\n  {StackText}");

        var ui = sanitizer.Sanitize(ex);

        // Exception path — the exact original exception reached the logger (diagnosis intact)...
        Assert.Contains(ex, logger.Exceptions);
        // ...while the client-facing surface stays safe.
        Assert.DoesNotContain(SecretKey, Json(ui), StringComparison.Ordinal);

        // Result path — fresh logger so the assertion pins exactly this call's logging (can't pass
        // spuriously off a prior entry). The raw Result.Error (path + secret) is logged server-side,
        // never surfaced on the UiError.
        var resultLogger = new CapturingLogger<ErrorSanitizer>();
        var rui = new ErrorSanitizer(resultLogger).SanitizeResult(
            "persistence.failed", Result.Failure("persistence.failed: C:\\secret\\sk-abc\\session.json"));
        Assert.Single(resultLogger.Messages);
        Assert.Contains("sk-abc", resultLogger.Messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abc", Json(rui), StringComparison.Ordinal);
    }

    // 7 — turnId flows into UiError.TurnId when supplied; null when omitted.
    [Fact]
    public void turn_id_carried_when_supplied()
    {
        var sanitizer = Sanitizer();

        Assert.Equal("turn-7", sanitizer.Sanitize(new Exception("x"), turnId: "turn-7").TurnId);
        Assert.Null(sanitizer.Sanitize(new Exception("x")).TurnId);
    }
}
