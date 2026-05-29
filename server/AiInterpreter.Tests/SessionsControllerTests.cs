using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiInterpreter.Tests;

// B.9c-i — SessionsController session-lifecycle routes + SessionService (ARCH-009 / ARCH-008 /
// ARCH-016 / ARCH-018). HTTP round-trip via WebApplicationFactory over the REAL store/summary/writer
// with a temp SESSION_DATA_DIR (fresh per test). Pins: full-record responses (ARCH-005-as-JSON,
// camelCase enums), unknown-id -> sanitized 404 UiError (session.not_found, never Result.Error),
// /end MUST-persist + degrade-to-UiError on write failure (in-memory end still happens).
//
// In the HostEnv collection: sets process-wide env vars + boots the host -> serialize against the
// other host/env tests.
[Collection("HostEnv")]
public class SessionsControllerTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "aiw-sessions-ctrl", Guid.NewGuid().ToString("N"));
        _tempPaths.Add(d);
        return d;
    }

    private static WebApplicationFactory<Program> Factory(string sessionDataDir)
    {
        Environment.SetEnvironmentVariable("SESSION_DATA_DIR", sessionDataDir);
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        return new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SESSION_DATA_DIR", null);
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", null);
        foreach (var p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    private static CreateSessionRequest SampleRequest(InterpretationMode mode = InterpretationMode.Cascade) =>
        new(Label: "Demo run 1", Mode: mode,
            Direction: new LanguageDirection(LanguageCode.En, LanguageCode.Es),
            RealtimeModel: "gpt-realtime", TranslationModel: "gpt-5.4-nano");

    private static async Task<(HttpClient Client, string SessionId)> CreatedSession(
        WebApplicationFactory<Program> factory, InterpretationMode mode = InterpretationMode.Cascade)
    {
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/sessions", SampleRequest(mode), JsonDefaults.Options);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
        return (client, id);
    }

    // 1 — POST /api/sessions: server id + a ProviderProfile assembled from request models + Options;
    // enums serialize camelCase.
    [Fact]
    public async Task create_session_returns_session_with_assembled_profile()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/api/sessions", SampleRequest(InterpretationMode.Realtime), JsonDefaults.Options);

        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Matches("^[A-Za-z0-9_-]+$", root.GetProperty("sessionId").GetString()!);
        Assert.True(root.TryGetProperty("startedAt", out _));
        Assert.Equal("realtime", root.GetProperty("config").GetProperty("currentMode").GetString());

        var profile = root.GetProperty("config").GetProperty("providerProfile");
        Assert.Equal("openai", profile.GetProperty("realtimeProvider").GetString());
        Assert.Equal("gpt-realtime", profile.GetProperty("realtimeModel").GetString());
        Assert.Equal("deepgram", profile.GetProperty("sttProvider").GetString());
        Assert.Equal("nova-3", profile.GetProperty("sttModel").GetString());
        Assert.Equal("multi", profile.GetProperty("sttLanguage").GetString());
        Assert.Equal("gpt-5.4-nano", profile.GetProperty("translationModel").GetString());
        Assert.Equal("gpt-4o-mini-tts", profile.GetProperty("ttsModel").GetString());
        Assert.Equal("alloy", profile.GetProperty("ttsVoice").GetString());
    }

    // 2 — GET /{id} returns the created session (JSON-string eq of POST vs GET bodies, lesson §2).
    [Fact]
    public async Task get_session_returns_created_session()
    {
        using var factory = Factory(TempDir());
        var client = factory.CreateClient();
        var posted = await (await client.PostAsJsonAsync("/api/sessions", SampleRequest(), JsonDefaults.Options))
            .Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(posted).RootElement.GetProperty("sessionId").GetString();

        var resp = await client.GetAsync($"/api/sessions/{id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(posted, await resp.Content.ReadAsStringAsync());
    }

    // 3 — GET unknown id -> 404 + sanitized UiError (session.not_found); no Result/exception leak.
    [Fact]
    public async Task get_unknown_session_returns_sanitized_404()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient().GetAsync("/api/sessions/session_doesnotexist");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(body, JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
    }

    // 4 — POST /{id}/end: EndedAt set, Summary snapshotted, a JSON file written, persistedPath returned.
    [Fact]
    public async Task end_session_computes_summary_persists_and_returns()
    {
        var dir = TempDir();
        using var factory = Factory(dir);
        var (client, id) = await CreatedSession(factory);

        var resp = await client.PostAsync($"/api/sessions/{id}/end", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var session = root.GetProperty("session");
        Assert.Equal(JsonValueKind.String, session.GetProperty("endedAt").ValueKind);
        Assert.Equal(JsonValueKind.Object, session.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("persistenceWarning").ValueKind);
        // persistedPath is the FILENAME only (no absolute path / data-dir disclosure — ARCH-019).
        var persistedPath = root.GetProperty("persistedPath").GetString()!;
        Assert.StartsWith("session_", persistedPath, StringComparison.Ordinal);
        Assert.EndsWith(".json", persistedPath, StringComparison.Ordinal);
        Assert.DoesNotContain('/', persistedPath);
        Assert.DoesNotContain('\\', persistedPath);
        Assert.Single(Directory.GetFiles(dir, "*.json"));
    }

    // 5 — /end on an unknown id -> 404 UiError.
    [Fact]
    public async Task end_unknown_session_returns_sanitized_404()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient().PostAsync("/api/sessions/session_nope/end", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
    }

    // 6 — a write failure degrades to a persistence.failed UiError warning (no path leak) while the
    // in-memory end still happens. SESSION_DATA_DIR under a regular file -> CreateDirectory throws.
    [Fact]
    public async Task end_persistence_failure_degrades_to_uierror()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "x");
        _tempPaths.Add(blocker);
        using var factory = Factory(Path.Combine(blocker, "sessions"));
        var (client, id) = await CreatedSession(factory);

        var resp = await client.PostAsync($"/api/sessions/{id}/end", null);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("session").GetProperty("endedAt").ValueKind);
        Assert.Equal("persistence.failed", root.GetProperty("persistenceWarning").GetProperty("code").GetString());
        Assert.DoesNotContain(blocker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", body, StringComparison.Ordinal);
    }

    // 7 — GET /{id}/summary recomputes a SessionSummary; unknown id -> 404 UiError.
    [Fact]
    public async Task summary_recompute_returns_session_summary()
    {
        using var factory = Factory(TempDir());
        var (client, id) = await CreatedSession(factory);

        var resp = await client.GetAsync($"/api/sessions/{id}/summary");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("turnCount").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("computedAt", out _));

        var unknown = await client.GetAsync("/api/sessions/session_nope/summary");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // 8 — boundary input validation (ARCH-019): an oversized model string is rejected (400) before it
    // can inflate the in-memory store / persisted JSON.
    [Fact]
    public async Task create_rejects_oversized_model_string()
    {
        using var factory = Factory(TempDir());
        var bad = SampleRequest() with { RealtimeModel = new string('x', 5000) };

        var resp = await factory.CreateClient().PostAsJsonAsync("/api/sessions", bad, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== B.9c-ii — turn lifecycle (backend owns turnId) =====

    private static readonly DateTimeOffset T = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);

    private static LatencyEvent Ev(string name) =>
        new(name, LatencyStage.Stt, T, 100, ClockSource.Browser, new Dictionary<string, string> { ["k"] = "v" });

    private static async Task<string> CreateTurn(HttpClient client, string sessionId)
    {
        var resp = await client.PostAsync($"/api/sessions/{sessionId}/turns", content: null);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("turnId").GetString()!;
    }

    // 9 — POST …/turns: backend-generated turnId; the turn exists with the session's mode/direction.
    [Fact]
    public async Task create_turn_returns_backend_turn_id()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory, InterpretationMode.Realtime);

        var resp = await client.PostAsync($"/api/sessions/{sessionId}/turns", content: null);

        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var turnId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("turnId").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]+$", turnId);

        // The turn is in the session, inheriting mode/direction + StartedAt.
        var session = await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadAsStringAsync();
        var turn = JsonDocument.Parse(session).RootElement.GetProperty("turns").EnumerateArray().Single();
        Assert.Equal(turnId, turn.GetProperty("turnId").GetString());
        Assert.Equal("realtime", turn.GetProperty("mode").GetString());
        Assert.Equal("en", turn.GetProperty("direction").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.String, turn.GetProperty("startedAt").ValueKind);
    }

    // 10 — create-turn on an unknown session → 404 session.not_found.
    [Fact]
    public async Task create_turn_unknown_session_404()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient().PostAsync("/api/sessions/session_nope/turns", content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
    }

    // 11 — POST …/events appends LatencyEvents to the turn (clockSource preserved).
    [Fact]
    public async Task append_events_adds_latency_to_turn()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory);
        var turnId = await CreateTurn(client, sessionId);

        var req = new AppendEventsRequest(new List<LatencyEvent> { Ev("turn.recording.started"), Ev("tts.first_audio") });
        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/{turnId}/events", req, JsonDefaults.Options);

        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var session = await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadAsStringAsync();
        var turn = JsonDocument.Parse(session).RootElement.GetProperty("turns").EnumerateArray().Single();
        var events = turn.GetProperty("latencyEvents").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("turn.recording.started", events[0].GetProperty("name").GetString());
        Assert.Equal("browser", events[0].GetProperty("clockSource").GetString());
    }

    // 12 — events on an unknown turn (known session) → 404 turn.not_found.
    [Fact]
    public async Task append_events_unknown_turn_404()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory);

        var req = new AppendEventsRequest(new List<LatencyEvent> { Ev("stt.final") });
        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/turn_nope/events", req, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("turn.not_found", ui!.Code);
    }

    // 12b — events on an UNKNOWN SESSION → 404 session.not_found (the other half of the two-404 split).
    [Fact]
    public async Task append_events_unknown_session_404()
    {
        using var factory = Factory(TempDir());

        var req = new AppendEventsRequest(new List<LatencyEvent> { Ev("stt.final") });
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/api/sessions/session_nope/turns/turn_x/events", req, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
    }

    // 13 — an oversized events batch is rejected (400) at the boundary (ARCH-019).
    [Fact]
    public async Task append_events_oversized_rejected()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory);
        var turnId = await CreateTurn(client, sessionId);

        var many = Enumerable.Range(0, 600).Select(_ => Ev("stt.final")).ToList();
        var resp = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/turns/{turnId}/events", new AppendEventsRequest(many), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // 14 — POST …/complete finalizes the turn (Status=Completed, CompletedAt) + writes a JSON file;
    // ARCH-005 invariant #1 satisfied (mode/direction/startedAt + ≥1 latency from /events).
    [Fact]
    public async Task complete_turn_finalizes_and_persists()
    {
        var dir = TempDir();
        using var factory = Factory(dir);
        var (client, sessionId) = await CreatedSession(factory);
        var turnId = await CreateTurn(client, sessionId);
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/{turnId}/events",
            new AppendEventsRequest(new List<LatencyEvent> { Ev("turn.recording.started") }), JsonDefaults.Options);

        var complete = new CompleteTurnRequest(AudioDurationMs: 2000, Transcripts: null, Status: null);
        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/{turnId}/complete", complete, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var turn = doc.RootElement.GetProperty("turn");
        Assert.Equal("completed", turn.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, turn.GetProperty("completedAt").ValueKind);
        Assert.Equal(2000, turn.GetProperty("audioDurationMs").GetInt64());
        Assert.True(turn.GetProperty("latencyEvents").GetArrayLength() >= 1);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("persistenceWarning").ValueKind);
        Assert.Single(Directory.GetFiles(dir, "*.json"));
    }

    // 15 — per-turn persist is best-effort: a write failure → 200 + persistence.failed warning (no path
    // leak); the turn is still Completed in memory (ARCH-016).
    [Fact]
    public async Task complete_turn_persistence_failure_degrades()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "x");
        _tempPaths.Add(blocker);
        using var factory = Factory(Path.Combine(blocker, "sessions"));
        var (client, sessionId) = await CreatedSession(factory);
        var turnId = await CreateTurn(client, sessionId);

        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/{turnId}/complete",
            new CompleteTurnRequest(null, null, null), JsonDefaults.Options);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("completed", doc.RootElement.GetProperty("turn").GetProperty("status").GetString());
        Assert.Equal("persistence.failed", doc.RootElement.GetProperty("persistenceWarning").GetProperty("code").GetString());
        Assert.DoesNotContain(blocker, body, StringComparison.Ordinal);
    }

    // 16 — complete on an unknown turn (known session) → 404 turn.not_found.
    [Fact]
    public async Task complete_turn_unknown_404()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory);

        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/turn_nope/complete",
            new CompleteTurnRequest(null, null, null), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("turn.not_found", ui!.Code);
    }

    // 16b — complete on an UNKNOWN SESSION → 404 session.not_found (the other half of the two-404 split).
    [Fact]
    public async Task complete_turn_unknown_session_404()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/api/sessions/session_nope/turns/turn_x/complete", new CompleteTurnRequest(null, null, null), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
    }

    // 17 — oversized transcripts on /complete → 400 (boundary cap, origin A.3).
    [Fact]
    public async Task complete_turn_rejects_oversized_collections()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory);
        var turnId = await CreateTurn(client, sessionId);

        var transcripts = Enumerable.Range(0, 600)
            .Select(i => new TranscriptSegment($"seg{i}", "source", "x", true, "deepgram", T, ClockSource.Server))
            .ToList();
        var resp = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns/{turnId}/complete",
            new CompleteTurnRequest(null, transcripts, null), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
