using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// E.5-backend — stale-session auto-end/flush (Flow H, ARCH-017). Creating a new session flushes any prior
// un-ended (refreshed/abandoned) session through the existing EndAsync finalize+persist seam, so an
// abandoned session still produces its JSON artifact; the flush degrades on persist failure and never
// blocks the new session (lesson §11). Tests construct SessionService directly over the committed pricing.
public class StaleSessionFlushTests
{
    private static readonly DateTimeOffset T = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    [Fact]
    public async Task create_flushes_prior_unended_session()
    {
        var (svc, store, dir) = Build();
        var a = await svc.CreateAsync(Req());
        Assert.Null(store.Get(a.SessionId)!.EndedAt); // A starts un-ended

        var b = await svc.CreateAsync(Req());

        Assert.NotNull(b);
        Assert.NotNull(store.Get(a.SessionId)!.EndedAt);            // A auto-ended by the flush
        Assert.Null(store.Get(b.SessionId)!.EndedAt);              // B is the new active session
        Assert.True(SessionFileExists(dir, a.SessionId), "A's JSON artifact should be persisted");
        Assert.False(SessionFileExists(dir, b.SessionId), "B is active — not persisted on create");
    }

    [Fact]
    public async Task create_does_not_reflush_ended_session()
    {
        var (svc, store, _) = Build();
        var a = await svc.CreateAsync(Req());
        await svc.EndAsync(a.SessionId);
        var endedAt = store.Get(a.SessionId)!.EndedAt;
        Assert.NotNull(endedAt);
        // An already-ended session is excluded from the flush set, so the next create can't re-flush it
        // (End is idempotent + persist overwrites, so exclusion from the set is the observable guarantee).
        Assert.DoesNotContain(a.SessionId, store.ActiveSessionIds());

        var b = await svc.CreateAsync(Req());

        Assert.NotNull(b);
        Assert.Equal(endedAt, store.Get(a.SessionId)!.EndedAt);     // EndedAt untouched (not re-ended)
        Assert.DoesNotContain(a.SessionId, store.ActiveSessionIds());
    }

    [Fact]
    public async Task flush_persist_failure_does_not_block_new_session()
    {
        // Writer pointed UNDER a file → Directory.CreateDirectory throws → EndAsync persist degrades (no throw).
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-flush-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var logger = new CapturingLogger();
            var (svc, store, _) = Build(writerDir: Path.Combine(blocker, "sessions"), logger: logger);

            var a = await svc.CreateAsync(Req());
            var b = await svc.CreateAsync(Req()); // creating B flushes A; A's persist FAILS

            Assert.NotNull(b);                                          // the new session is never collateral
            Assert.NotNull(store.Get(a.SessionId)!.EndedAt);           // A's in-memory end still flipped (partial-but-safe)
            Assert.Contains(logger.Warnings, w => w.Contains(a.SessionId, StringComparison.Ordinal)); // degrade logged
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task create_with_no_prior_session_is_unaffected()
    {
        var (svc, store, dir) = Build();

        var a = await svc.CreateAsync(Req()); // first-ever create — nothing to flush

        Assert.NotNull(a);
        Assert.Null(store.Get(a.SessionId)!.EndedAt); // A is active, not flushed
        Assert.Empty(JsonFiles(dir));                 // no flush persist happened
    }

    [Fact]
    public async Task create_flushes_all_prior_unended()
    {
        var (svc, store, _) = Build();
        // Two leaked un-ended priors injected via the store primitive (which does NOT flush), simulating
        // multiple abandoned sessions — defensively flush ALL un-ended, not just one.
        var a1 = store.Create(StoreConfig(), "test-version");
        var a2 = store.Create(StoreConfig(), "test-version");
        Assert.Equal(2, store.ActiveSessionIds().Count);

        var b = await svc.CreateAsync(Req());

        Assert.NotNull(store.Get(a1.SessionId)!.EndedAt);
        Assert.NotNull(store.Get(a2.SessionId)!.EndedAt);
        Assert.Null(store.Get(b.SessionId)!.EndedAt);
        Assert.Equal(new[] { b.SessionId }, store.ActiveSessionIds()); // only the new session remains active
    }

    // === helpers ===

    private static (SessionService Service, SessionStore Store, string Dir) Build(
        string? writerDir = null, ILogger<SessionService>? logger = null)
    {
        var clock = new FakeClock(T);
        var store = new SessionStore(clock);
        var summary = new SessionSummaryService(new MetricsAggregator(), clock);
        var dir = writerDir ?? Path.Combine(Path.GetTempPath(), "aiw-flush-tests", Guid.NewGuid().ToString("N"));
        var writer = new SessionPersistenceWriter(dir);
        var pricing = PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        var service = new SessionService(
            store, summary, writer, clock,
            Options.Create(new DeepgramOptions()), Options.Create(new OpenAiTtsOptions()),
            pricing, new CostEstimator(pricing), logger ?? NullLogger<SessionService>.Instance);
        return (service, store, dir);
    }

    private static CreateSessionRequest Req() =>
        new(Label: "flush", Mode: InterpretationMode.Cascade,
            Direction: new LanguageDirection(LanguageCode.En, LanguageCode.Es),
            RealtimeModel: "gpt-realtime", TranslationModel: "gpt-5-nano");

    private static SessionConfig StoreConfig() =>
        new(InterpretationMode.Cascade, new LanguageDirection(LanguageCode.En, LanguageCode.Es),
            new ProviderProfile("openai", "gpt-realtime", "deepgram", "nova-3", "multi",
                "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy"));

    private static string[] JsonFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();

    private static bool SessionFileExists(string dir, string sessionId) =>
        JsonFiles(dir).Any(f => File.ReadAllText(f).Contains(sessionId, StringComparison.Ordinal));

    private sealed class CapturingLogger : ILogger<SessionService>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
