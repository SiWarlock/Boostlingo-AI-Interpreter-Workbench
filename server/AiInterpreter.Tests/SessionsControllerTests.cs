using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
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
            RealtimeModel: "gpt-realtime", TranslationModel: "gpt-5-nano");

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
        Assert.Equal("gpt-5-nano", profile.GetProperty("translationModel").GetString());
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

    // ===== F.4 — eval-turn exclusion, end-to-end (Step 7.5 wiring) =====

    // 18 — a real session with 2 interpretation turns + 1 WER-scored eval turn (created like F.2's panel
    // does: POST /turns then POST /wer with that turnId) → GET /summary's per-mode turnCount EXCLUDES the
    // eval turn (the comparison is exact), while the session-level WerSummary INCLUDES its score. Proves
    // the IsEvaluation marker is set at /wer and observed by SummarizeMode on the real HTTP path.
    [Fact]
    public async Task summary_endpoint_excludes_eval_turn_e2e()
    {
        using var factory = Factory(TempDir());
        var (client, sessionId) = await CreatedSession(factory); // cascade mode

        await CreateTurn(client, sessionId);                     // interpretation turn 1
        await CreateTurn(client, sessionId);                     // interpretation turn 2
        var evalTurnId = await CreateTurn(client, sessionId);    // the to-be eval turn (cascade mode)

        // Mark the third turn an evaluation turn by scoring WER against it (F.2's exact flow).
        var phrases = await (await client.GetAsync("/api/evaluation/phrases"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var phraseId = phrases[0].GetProperty("phraseId").GetString()!;
        var reference = phrases[0].GetProperty("referenceText").GetString()!;
        var wer = await client.PostAsJsonAsync("/api/evaluation/wer", new
        {
            sessionId,
            turnId = evalTurnId,
            phraseId,
            hypothesis = reference,
        });
        wer.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"/api/sessions/{sessionId}/summary")).Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("turnCount").GetInt32());                  // top-level counts all turns
        Assert.Equal(2, root.GetProperty("cascade").GetProperty("turnCount").GetInt32()); // per-mode excludes eval
        Assert.Equal(1, root.GetProperty("wer").GetProperty("sampleCount").GetInt32());    // WER keeps it
    }

    // ===== 050 — POST /{id}/mode mode-switch endpoint (Finding 2c / Flow G) =====

    // Posts the RAW request body so the exact frontend wire shape ({"mode":"realtime"}, camelCase key +
    // lowercase enum, shipped at 7dc398e) is pinned, not just a serializer round-trip.
    private static StringContent ModeBody(string rawJson) => new(rawJson, Encoding.UTF8, "application/json");

    // M1 — a valid switch: 200 with the updated session; config.currentMode flips AND a server-derived
    // ModeTransitionEvent is recorded (fromMode/toMode/clockSource=server/triggeredByTurnId=null).
    [Fact]
    public async Task switch_mode_updates_current_mode_and_records_transition()
    {
        using var factory = Factory(TempDir());
        var (client, id) = await CreatedSession(factory, InterpretationMode.Cascade);

        var resp = await client.PostAsync($"/api/sessions/{id}/mode", ModeBody("{\"mode\":\"realtime\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("realtime", root.GetProperty("config").GetProperty("currentMode").GetString());
        var transitions = root.GetProperty("modeTransitions");
        Assert.Equal(1, transitions.GetArrayLength());
        var tr = transitions[0];
        Assert.Equal("cascade", tr.GetProperty("fromMode").GetString());
        Assert.Equal("realtime", tr.GetProperty("toMode").GetString());
        Assert.Equal("server", tr.GetProperty("clockSource").GetString());
        Assert.Equal(JsonValueKind.Null, tr.GetProperty("triggeredByTurnId").ValueKind);
        Assert.Matches("^[A-Za-z0-9_-]+$", tr.GetProperty("transitionId").GetString()!);
        // directionAtTransition = the session's current direction (load-bearing shared-contract field).
        var dir = tr.GetProperty("directionAtTransition");
        Assert.Equal("en", dir.GetProperty("source").GetString());
        Assert.Equal("es", dir.GetProperty("target").GetString());
    }

    // M2 — an invalid target mode -> a sanitized 400 session.invalid_mode UiError (NOT a framework
    // ProblemDetails): the DTO carries the raw string so the service chokepoint owns the rejection
    // (lesson §27 pattern) across off-enum, empty, out-of-range-numeric, and a missing key.
    [Theory]
    [InlineData("{\"mode\":\"bogus\"}")]
    [InlineData("{\"mode\":\"\"}")]
    [InlineData("{\"mode\":\"99\"}")]
    [InlineData("{}")]
    public async Task switch_mode_invalid_target_returns_sanitized_400(string body)
    {
        using var factory = Factory(TempDir());
        var (client, id) = await CreatedSession(factory, InterpretationMode.Cascade);

        var resp = await client.PostAsync($"/api/sessions/{id}/mode", ModeBody(body));
        var payload = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(payload, JsonDefaults.Options);
        Assert.Equal("session.invalid_mode", ui!.Code);
        Assert.DoesNotContain("Exception", payload, StringComparison.OrdinalIgnoreCase);
    }

    // M3 — /mode on an unknown id -> 404 session.not_found UiError.
    [Fact]
    public async Task switch_mode_unknown_session_returns_sanitized_404()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient()
            .PostAsync("/api/sessions/session_nope/mode", ModeBody("{\"mode\":\"realtime\"}"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(await resp.Content.ReadAsStringAsync(), JsonDefaults.Options);
        Assert.Equal("session.not_found", ui!.Code);
    }

    // M4 — a no-op switch (target == current) is idempotent: 200, mode unchanged, NO transition recorded
    // (Step-2.5 Q2 — a redundant toggle must not pollute the Flow-G timeline).
    [Fact]
    public async Task switch_mode_noop_same_mode_records_no_transition()
    {
        using var factory = Factory(TempDir());
        var (client, id) = await CreatedSession(factory, InterpretationMode.Cascade);

        var resp = await client.PostAsync($"/api/sessions/{id}/mode", ModeBody("{\"mode\":\"cascade\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("cascade", root.GetProperty("config").GetProperty("currentMode").GetString());
        Assert.Equal(0, root.GetProperty("modeTransitions").GetArrayLength());
    }

    // M5 — THE 2c fix end-to-end: a turn created AFTER a switch is stamped with the NEW mode (the root
    // cause was CreateTurn stamping a stale CurrentMode). create cascade -> /mode realtime -> /turns ->
    // the new turn's mode is realtime.
    [Fact]
    public async Task turn_created_after_switch_is_stamped_with_new_mode()
    {
        using var factory = Factory(TempDir());
        var (client, id) = await CreatedSession(factory, InterpretationMode.Cascade);

        await client.PostAsync($"/api/sessions/{id}/mode", ModeBody("{\"mode\":\"realtime\"}"));
        var turnId = await CreateTurn(client, id);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"/api/sessions/{id}")).Content.ReadAsStringAsync());
        var turn = doc.RootElement.GetProperty("turns").EnumerateArray()
            .Single(t => t.GetProperty("turnId").GetString() == turnId);
        Assert.Equal("realtime", turn.GetProperty("mode").GetString());
    }

    // M6 — the recorded transition lands in the persisted session JSON (modeTransitions) at /end, with
    // invariants intact (metadata only — no secret, no raw-audio field).
    [Fact]
    public async Task switch_mode_transition_persists_in_session_json()
    {
        var dir = TempDir();
        using var factory = Factory(dir);
        var (client, id) = await CreatedSession(factory, InterpretationMode.Cascade);

        await client.PostAsync($"/api/sessions/{id}/mode", ModeBody("{\"mode\":\"realtime\"}"));
        await client.PostAsync($"/api/sessions/{id}/end", null);

        var file = Assert.Single(Directory.GetFiles(dir, "*.json"));
        var json = await File.ReadAllTextAsync(file);
        using var doc = JsonDocument.Parse(json);
        var transitions = doc.RootElement.GetProperty("modeTransitions");
        Assert.Equal(1, transitions.GetArrayLength());
        Assert.Equal("realtime", transitions[0].GetProperty("toMode").GetString());
        Assert.DoesNotContain("sk-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ek_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"audio\"", json, StringComparison.Ordinal);
    }

    // ===== H.3-backend — GET /api/sessions (persisted-session history list, Option B summaries) =====

    // Pre-seed a persisted session_*.json into the data dir BEFORE the host boots (deterministic StartedAt,
    // independent of the host's SystemClock) so the ordering + summary assertions are reproducible.
    private static async Task SeedSession(
        string dir, string sessionId, DateTimeOffset startedAt, string? label, bool rich = false,
        params InterpretationMode[] turnModes)
    {
        var direction = new LanguageDirection(LanguageCode.En, LanguageCode.Es);
        var profile = new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy");
        var config = new SessionConfig(InterpretationMode.Cascade, direction, profile);

        var turns = new List<InterpretationTurn>();
        var i = 0;
        foreach (var mode in turnModes)
        {
            var transcripts = rich
                ? new List<TranscriptSegment>
                {
                    new($"seg{i}", "source", "hola mundo", true, "deepgram", startedAt, ClockSource.Server),
                }
                : new List<TranscriptSegment>();
            turns.Add(new InterpretationTurn(
                $"turn{i}", mode, direction, startedAt, startedAt.AddSeconds(2), 2000,
                transcripts, new List<LatencyEvent>(), null, null, new List<ProviderError>(),
                TurnStatus.Completed, "gpt-5-nano", "alloy"));
            i++;
        }

        var session = new InterpretationSession(
            sessionId, label, startedAt, startedAt.AddMinutes(1), config,
            turns, new List<ModeTransitionEvent>(), null, "2026-05-28-payg-estimates");
        var write = await new SessionPersistenceWriter(dir).WriteAsync(session);
        Assert.True(write.IsSuccess, write.Error);
    }

    // L1 — GET /api/sessions returns the persisted sessions as a summary array ordered most-recent-first
    // (StartedAt desc). Pre-seeded out of order to prove the endpoint sorts, not the disk enumeration.
    [Fact]
    public async Task list_sessions_returns_persisted_summaries_ordered_desc()
    {
        var dir = TempDir();
        await SeedSession(dir, "session_a", T, "oldest", turnModes: InterpretationMode.Cascade);
        await SeedSession(dir, "session_b", T.AddMinutes(5), "newest", turnModes: InterpretationMode.Realtime);
        await SeedSession(dir, "session_c", T.AddMinutes(2), "middle", turnModes: InterpretationMode.Cascade);
        using var factory = Factory(dir);

        var resp = await factory.CreateClient().GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("sessionId").GetString()).ToArray();
        Assert.Equal(new[] { "session_b", "session_c", "session_a" }, ids);
    }

    // L2 — an empty data dir (no persisted sessions) → 200 with an empty array (NOT 404/500). A fresh
    // clone with no history yet must list nothing cleanly.
    [Fact]
    public async Task list_sessions_empty_dir_returns_empty_array()
    {
        using var factory = Factory(TempDir());

        var resp = await factory.CreateClient().GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // L3 — a reader failure (SESSION_DATA_DIR resolves to a regular FILE, not a dir) → a sanitized
    // UiError (sessions.read_failed), never a 500 stack/path leak. Pins the Result→DTO mapping (§16).
    [Fact]
    public async Task list_sessions_reader_failure_returns_sanitized_uierror()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "x");
        _tempPaths.Add(blocker);
        using var factory = Factory(blocker); // SESSION_DATA_DIR points at a file

        var resp = await factory.CreateClient().GetAsync("/api/sessions");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var ui = JsonSerializer.Deserialize<UiError>(body, JsonDefaults.Options);
        Assert.Equal("sessions.read_failed", ui!.Code);
        Assert.DoesNotContain(blocker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
    }

    // L4 — the list item is a LIGHTWEIGHT summary (Q1=B payload hygiene): it carries sessionId/label/
    // startedAt/endedAt/turnCount/modes and does NOT embed the full turns/latencyEvents/transcripts; the
    // read-side sentinel holds (no sk-/ek_/raw-audio in the response).
    [Fact]
    public async Task list_sessions_item_is_lightweight_summary_and_sentinel_clean()
    {
        var dir = TempDir();
        await SeedSession(dir, "session_full", T, "Demo run", rich: true,
            turnModes: new[] { InterpretationMode.Cascade, InterpretationMode.Realtime });
        using var factory = Factory(dir);

        var resp = await factory.CreateClient().GetAsync("/api/sessions");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("session_full", item.GetProperty("sessionId").GetString());
        Assert.Equal("Demo run", item.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.String, item.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, item.GetProperty("endedAt").ValueKind);
        Assert.Equal(2, item.GetProperty("turnCount").GetInt32());
        var modes = item.GetProperty("modes").EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.Equal(new[] { "cascade", "realtime" }, modes);

        // Payload hygiene: the summary must NOT carry the heavy per-turn detail.
        Assert.False(item.TryGetProperty("turns", out _));
        Assert.DoesNotContain("latencyEvents", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hola mundo", body, StringComparison.Ordinal); // transcript text not embedded
        // Read-side sentinel.
        Assert.DoesNotContain("sk-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ek_", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"audio\":", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===== 068 — GET /{id} disk-fallback (a past/evicted session reads from disk) =====

    // G1 — a persisted-but-EVICTED session (on disk, NOT in the in-memory store of a fresh host) returns 200
    // + its FULL detail (turns/transcripts) via the by-id disk fallback, instead of 404. The drill-in surface.
    [Fact]
    public async Task get_session_falls_back_to_disk_for_an_evicted_session()
    {
        var dir = TempDir();
        await SeedSession(dir, "session_evicted", T, "Past run", rich: true,
            turnModes: InterpretationMode.Cascade);
        using var factory = Factory(dir); // fresh host → empty in-memory store

        var resp = await factory.CreateClient().GetAsync("/api/sessions/session_evicted");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("session_evicted", root.GetProperty("sessionId").GetString());
        Assert.Equal("Past run", root.GetProperty("label").GetString());
        // Full detail (not a summary) — the drill-in needs the turns/transcripts.
        Assert.Equal(1, root.GetProperty("turns").GetArrayLength());
        Assert.Equal("hola mundo",
            root.GetProperty("turns")[0].GetProperty("transcripts")[0].GetProperty("text").GetString());
        // Sentinel: the disk-read response leaks no secret.
        Assert.DoesNotContain("sk-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ek_", body, StringComparison.Ordinal);
    }

    // G2 — precedence: when a session is BOTH in-memory (live) AND on disk (a stale snapshot), the in-memory
    // copy WINS (it is fresher than the last-persisted state). Guards against a disk-first precedence bug.
    [Fact]
    public async Task get_session_in_memory_wins_over_a_stale_disk_copy()
    {
        var dir = TempDir();
        using var factory = Factory(dir);
        var client = factory.CreateClient();
        // A LIVE session (in-memory; POST does not persist) — label "Demo run 1" from SampleRequest.
        var created = await client.PostAsJsonAsync("/api/sessions", SampleRequest(), JsonDefaults.Options);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
        // Seed a STALE disk copy with the SAME id but a different label.
        await SeedSession(dir, id, T, "STALE DISK COPY");

        var resp = await client.GetAsync($"/api/sessions/{id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Demo run 1", doc.RootElement.GetProperty("label").GetString()); // in-memory, not the disk copy
    }

    // G3 — ⭐ fail-closed invariant: transparent-Get widens the controller's session-existence pre-check, but a
    // MUTATION on an evicted (disk-only) session must NOT resurrect it. Appending events / completing a turn on
    // the evicted id → fail-closed 404 (turn.not_found — it passes the session pre-check then misses the
    // in-memory store), the in-memory store stays empty (no resurrection), and the on-disk file is byte-unchanged
    // (no re-persist / partial write). Guards a future disk-fallback of UpdateTurn from silently enabling it.
    [Fact]
    public async Task completing_or_appending_to_an_evicted_disk_only_session_fail_closes()
    {
        var dir = TempDir();
        await SeedSession(dir, "session_evicted2", T, "Past run", rich: true,
            turnModes: InterpretationMode.Cascade);
        var file = Assert.Single(Directory.GetFiles(dir, "*.json"));
        var before = await File.ReadAllBytesAsync(file);
        using var factory = Factory(dir); // fresh host → empty in-memory store
        var client = factory.CreateClient();

        // Append events to the evicted session's (disk) turn0 → fail-closed 404 (no in-memory turn to mutate).
        var append = await client.PostAsJsonAsync(
            "/api/sessions/session_evicted2/turns/turn0/events",
            new AppendEventsRequest(new List<LatencyEvent> { Ev("stt.final") }), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.NotFound, append.StatusCode);

        // Complete that turn on the evicted session → fail-closed 404.
        var complete = await client.PostAsJsonAsync(
            "/api/sessions/session_evicted2/turns/turn0/complete",
            new CompleteTurnRequest(null, null, null), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);

        // No resurrection / no re-persist: still exactly one file, byte-identical (the writer was never called).
        Assert.Single(Directory.GetFiles(dir, "*.json"));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    // G4 — symmetry: GET /{id}/summary also falls back to disk for an evicted session (Summary routes through
    // the disk-falling-back Get), recomputing from the persisted turns instead of 404ing — so the H.3 drill-in
    // can show a past session's metrics across a restart, consistent with GET /{id}.
    [Fact]
    public async Task get_summary_falls_back_to_disk_for_an_evicted_session()
    {
        var dir = TempDir();
        await SeedSession(dir, "session_evicted3", T, "Past run", rich: true,
            turnModes: InterpretationMode.Cascade);
        using var factory = Factory(dir); // fresh host → empty in-memory store

        var resp = await factory.CreateClient().GetAsync("/api/sessions/session_evicted3/summary");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("turnCount").GetInt32()); // recomputed from the persisted turn
        Assert.True(doc.RootElement.TryGetProperty("computedAt", out _));
    }
}
