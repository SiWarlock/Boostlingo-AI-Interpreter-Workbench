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
}
