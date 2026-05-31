using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.7a — SessionStore + SessionPersistenceWriter (ARCH-016 persistence / ARCH-019 security /
// ARCH-008 layering). SAFETY slice: the sentinel + path-traversal tests are the safety pins for
// root CLAUDE.md Key safety rules #1 (standard keys), #2 (ephemeral ek_ secret), #3 (raw audio),
// #5 (path-traversal guard on a server-generated id matching ^[A-Za-z0-9_-]+$ under SESSION_DATA_DIR).
//
// Round-trip equality is asserted on the serialized JSON, never record == (record == is
// reference-based over the List/Dictionary members — lesson §2).
public class SessionPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset fixedNow) : IClock
    {
        public DateTimeOffset UtcNow => fixedNow;
    }

    // Sentinel values that must NEVER appear in persisted JSON. If a future field starts leaking a
    // key/secret/audio blob, the substring scans below fire (drift-proof defense).
    private const string SentinelApiKey = "sk-SENTINEL-DO-NOT-PERSIST";
    private const string SentinelEphemeral = "ek_SENTINEL-DO-NOT-PERSIST";

    // Each test gets a fresh, not-yet-existing dir (exercises the writer's create-if-absent path);
    // tracked for best-effort cleanup on Dispose.
    private readonly List<string> _tempPaths = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiw-session-tests", Guid.NewGuid().ToString("N"));
        _tempPaths.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best-effort cleanup of OS temp — ignore */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup — ignore */ }
        }
    }

    // A realistic, non-trivial session: config + one completed turn carrying transcripts, a latency
    // event, a cost estimate, and a normalized error. The session model (ARCH-005) has NO field for
    // a key / ephemeral secret / raw audio — structurally safe; the sentinel tests make that explicit.
    private static InterpretationSession BuildFullSession(string sessionId = "session_abc12345")
    {
        var direction = new LanguageDirection(LanguageCode.En, LanguageCode.Es);
        var profile = new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5.4-nano", "openai", "gpt-4o-mini-tts", "alloy");
        var config = new SessionConfig(InterpretationMode.Cascade, direction, profile);

        var transcript = new TranscriptSegment(
            "seg1", "source", "hola mundo", true, "deepgram", T, ClockSource.Server);
        var latency = new LatencyEvent(
            "stt.final", LatencyStage.Stt, T, 912, ClockSource.Server,
            new Dictionary<string, string> { ["provider"] = "deepgram" });
        var cost = new CostEstimate(
            "cascade", "gpt-5.4-nano", "composite", 0.0012m, 0.05m,
            new Dictionary<string, decimal> { ["audioMinutes"] = 0.5m },
            "2026-05-28-payg-estimates", new[] { "estimate only, not provider invoice" });
        var error = new ProviderError("openai", "tts", "tts.failed", "TTS unavailable", false, 503);

        var turn = new InterpretationTurn(
            "turn1", InterpretationMode.Cascade, direction, T, T.AddSeconds(2), 2000,
            new List<TranscriptSegment> { transcript },
            new List<LatencyEvent> { latency },
            cost, null, new List<ProviderError> { error },
            TurnStatus.Completed, "gpt-5.4-nano", "alloy");

        return new InterpretationSession(
            sessionId, "Demo run 1", T, T.AddMinutes(1), config,
            new List<InterpretationTurn> { turn },
            new List<ModeTransitionEvent>(), null, "2026-05-28-payg-estimates");
    }

    // 1 — round-trip: write -> read -> deserialize -> re-serialize equals the original serialization;
    // legitimate content (transcripts/latency/cost) survives. (ARCH-016)
    [Fact]
    public async Task round_trip_preserves_session()
    {
        var writer = new SessionPersistenceWriter(NewTempDir());
        var original = BuildFullSession();

        var result = await writer.WriteAsync(original);

        Assert.True(result.IsSuccess, result.Error);
        var written = await File.ReadAllTextAsync(result.Value);
        var back = JsonSerializer.Deserialize<InterpretationSession>(written, JsonDefaults.Options);

        Assert.NotNull(back);
        // Field-level (not record ==) — lesson §2: re-serialize and compare to the original contract.
        Assert.Equal(
            JsonSerializer.Serialize(original, JsonDefaults.Options),
            JsonSerializer.Serialize(back, JsonDefaults.Options));
        Assert.Single(back!.Turns);
        Assert.Equal("hola mundo", back.Turns[0].Transcripts[0].Text);
        Assert.Equal("stt.final", back.Turns[0].LatencyEvents[0].Name);
        Assert.Equal(0.0012m, back.Turns[0].CostEstimate!.EstimatedUsd);
    }

    // 2 — sentinel #1: no standard API key (or any apiKey property) in the persisted JSON; legitimate
    // content present so the pass is not trivially-empty. (root CLAUDE.md safety rule #1 / ARCH-019)
    [Fact]
    public async Task sentinel_json_excludes_api_key()
    {
        var writer = new SessionPersistenceWriter(NewTempDir());

        var result = await writer.WriteAsync(BuildFullSession());

        Assert.True(result.IsSuccess, result.Error);
        var json = await File.ReadAllTextAsync(result.Value);

        Assert.DoesNotContain(SentinelApiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
        // Not trivially empty — the legitimate persisted content is present (ARCH-016 persist list).
        Assert.Contains("hola mundo", json, StringComparison.Ordinal);
        Assert.Contains("stt.final", json, StringComparison.Ordinal);
    }

    // 3 — sentinel #2: no ephemeral Realtime client secret (ek_...) in the persisted JSON.
    // (root CLAUDE.md safety rule #2 / ARCH-016 inv. 6b)
    [Fact]
    public async Task sentinel_json_excludes_ephemeral_secret()
    {
        var writer = new SessionPersistenceWriter(NewTempDir());

        var result = await writer.WriteAsync(BuildFullSession());

        Assert.True(result.IsSuccess, result.Error);
        var json = await File.ReadAllTextAsync(result.Value);

        Assert.DoesNotContain(SentinelEphemeral, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ek_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("clientsecret", json, StringComparison.OrdinalIgnoreCase);
    }

    // 4 — sentinel #3: no raw audio bytes / base64 blob in the persisted JSON. Raw audio rides on
    // CascadeOutputEvent.Audio.Bytes / TtsAudioChunk.Bytes (Providers/Cascade) — the session model
    // references NEITHER. (root CLAUDE.md safety rule #3 / ARCH-016 inv. 8)
    [Fact]
    public async Task sentinel_json_excludes_raw_audio()
    {
        var writer = new SessionPersistenceWriter(NewTempDir());

        var result = await writer.WriteAsync(BuildFullSession());

        Assert.True(result.IsSuccess, result.Error);
        var json = await File.ReadAllTextAsync(result.Value);

        Assert.DoesNotContain("\"bytes\"", json, StringComparison.OrdinalIgnoreCase);
        // Key-form (with the colon) so this can't alias the legitimate "audioMinutes" cost unit
        // that IS persisted (review) — we're guarding against an audio-bytes object, not a substring.
        Assert.DoesNotContain("\"audio\":", json, StringComparison.OrdinalIgnoreCase);
        // Legitimate transcript content is still there — we persist transcripts, never audio.
        Assert.Contains("hola mundo", json, StringComparison.Ordinal);
    }

    // 5 — path-traversal: a sessionId outside ^[A-Za-z0-9_-]+$ is rejected before any file write; no
    // stray .json escapes SESSION_DATA_DIR. (root CLAUDE.md safety rule #5 / ARCH-019 §9)
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task path_traversal_sessionid_rejected(string badId)
    {
        var dir = NewTempDir();
        var writer = new SessionPersistenceWriter(dir);

        var result = await writer.WriteAsync(BuildFullSession(badId));

        Assert.False(result.IsSuccess);
        Assert.Contains("persistence.failed", result.Error);
        // No file was written anywhere under the data dir as a side effect of the rejected id.
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir, "*.json").Length == 0);
    }

    // 6 — defense-in-depth layer 2: a server-generated id resolves to a canonical path that stays
    // under SESSION_DATA_DIR. (root CLAUDE.md safety rule #5 / ARCH-016)
    [Fact]
    public async Task resolved_path_stays_under_data_dir()
    {
        var dir = NewTempDir();
        var writer = new SessionPersistenceWriter(dir);

        var result = await writer.WriteAsync(BuildFullSession());

        Assert.True(result.IsSuccess, result.Error);
        var dirFull = Path.GetFullPath(dir);
        var fileFull = Path.GetFullPath(result.Value);
        // Mirror the writer's layer-(b) guard EXACTLY: the separator-terminated prefix is what closes
        // the /data/sessions vs /data/sessions-evil prefix-collision (security/quality review).
        var dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar)
            ? dirFull
            : dirFull + Path.DirectorySeparatorChar;
        Assert.StartsWith(dirPrefix, fileFull, StringComparison.Ordinal);
    }

    // 7 — degrade-don't-crash: an unwritable data dir surfaces persistence.failed (Result.Failure),
    // never an unhandled exception. (ARCH-018, lesson §3)
    [Fact]
    public async Task io_failure_degrades_to_persistence_failed()
    {
        // A regular file standing where the data dir should be — CreateDirectory under a file throws
        // IOException on every platform.
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-blocker-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(blocker);
        await File.WriteAllTextAsync(blocker, "x");
        var writer = new SessionPersistenceWriter(Path.Combine(blocker, "sessions"));

        var result = await writer.WriteAsync(BuildFullSession());

        Assert.False(result.IsSuccess);
        Assert.Contains("persistence.failed", result.Error);
    }

    // 8 — store: Create generates a valid server-side id, get-by-id returns it, turns add + update.
    [Fact]
    public void store_create_generates_valid_id()
    {
        var store = new SessionStore(new FakeClock(T));
        var config = BuildFullSession().Config;

        var session = store.Create(config, "2026-05-28-payg-estimates", "Demo run 1");

        Assert.Matches("^[A-Za-z0-9_-]+$", session.SessionId);
        Assert.Equal(T, session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Same(session, store.Get(session.SessionId));

        var turn = BuildFullSession().Turns[0];
        Assert.True(store.AddTurn(session.SessionId, turn));
        Assert.Single(store.Get(session.SessionId)!.Turns);

        var updated = turn with { Status = TurnStatus.Failed };
        Assert.True(store.UpdateTurn(session.SessionId, updated));
        Assert.Equal(TurnStatus.Failed, store.Get(session.SessionId)!.Turns[0].Status);

        // Operations on an unknown session id are no-ops returning false (never throw).
        Assert.False(store.AddTurn("session_missing", turn));
        Assert.Null(store.Get("session_missing"));
    }

    // 8b — F.4: a freshly created turn is NOT an evaluation turn (IsEvaluation default false). Only the
    // /wer attach flips it (EvaluationService); an interpretation turn never gets marked at creation.
    [Fact]
    public void created_turn_defaults_is_evaluation_false()
    {
        var store = new SessionStore(new FakeClock(T));
        var session = store.Create(BuildFullSession().Config, "2026-05-28-payg-estimates");

        var turn = store.CreateTurn(session.SessionId);

        Assert.NotNull(turn);
        Assert.False(turn!.IsEvaluation);
    }

    // 9 — store: End stamps EndedAt from the clock; mode transitions are recorded. (acceptance: end
    // a session, record mode transitions)
    [Fact]
    public void store_end_and_mode_transition()
    {
        var endClock = new FakeClock(T.AddMinutes(5));
        var store = new SessionStore(endClock);
        var session = store.Create(BuildFullSession().Config, "2026-05-28-payg-estimates");

        var transition = new ModeTransitionEvent(
            "tr1", InterpretationMode.Cascade, InterpretationMode.Realtime,
            new LanguageDirection(LanguageCode.En, LanguageCode.Es), T.AddMinutes(1),
            ClockSource.Server, "turn1");
        Assert.True(store.RecordModeTransition(session.SessionId, transition));

        var ended = store.End(session.SessionId);

        Assert.NotNull(ended);
        Assert.Equal(T.AddMinutes(5), ended!.EndedAt);
        Assert.Single(store.Get(session.SessionId)!.ModeTransitions);
        Assert.Equal(T.AddMinutes(5), store.Get(session.SessionId)!.EndedAt);
    }

    // 10 — store thread-safety: concurrent turn appends do not lose or corrupt turns (HTTP + WS touch
    // a session concurrently). (acceptance: thread-safe for concurrent HTTP + WS access)
    [Fact]
    public void store_concurrent_turn_adds()
    {
        var store = new SessionStore(new FakeClock(T));
        var session = store.Create(BuildFullSession().Config, "2026-05-28-payg-estimates");
        var template = BuildFullSession().Turns[0];

        Parallel.For(0, 200, i =>
            store.AddTurn(session.SessionId, template with { TurnId = $"turn{i}" }));

        Assert.Equal(200, store.Get(session.SessionId)!.Turns.Count);
        Assert.Equal(200, store.Get(session.SessionId)!.Turns.Select(t => t.TurnId).Distinct().Count());
    }
}
