using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiInterpreter.Tests;

// C.5 — CascadeController (POST /api/cascade/turn) HTTP boundary, via WebApplicationFactory (the
// SessionsControllerTests precedent). The deep provider/network path is manual-smoke; this pins the WIRE
// contract a real MVC boot is needed for: multipart binding + the SAFETY upload-validation -> HTTP-status
// mapping (413 oversized / 415 unsupported; invariant #5) + the happy-path 200 turn response. No API keys
// are set, so the cascade runs against the B.2 fakes (key-presence DI -> fakes).
[Collection("HostEnv")]
public class CascadeControllerTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "aiw-cascade-ctrl", Guid.NewGuid().ToString("N"));
        _tempPaths.Add(d);
        return d;
    }

    private WebApplicationFactory<Program> Factory(long? maxUploadBytes = null)
    {
        Environment.SetEnvironmentVariable("SESSION_DATA_DIR", TempDir());
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        Environment.SetEnvironmentVariable("CASCADE_MAX_UPLOAD_BYTES", maxUploadBytes?.ToString());
        return new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SESSION_DATA_DIR", null);
        Environment.SetEnvironmentVariable("PRICING_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("CASCADE_MAX_UPLOAD_BYTES", null);
        foreach (var p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
            }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    private static MultipartFormDataContent Multipart(
        string? sessionId, string? turnId, byte[] audio, string contentType, string fileName = "clip.webm")
    {
        var content = new MultipartFormDataContent();
        if (sessionId is not null) content.Add(new StringContent(sessionId), "SessionId");
        if (turnId is not null) content.Add(new StringContent(turnId), "TurnId");
        content.Add(new StringContent("en"), "Source");
        content.Add(new StringContent("es"), "Target");
        content.Add(new StringContent("gpt-5.4-nano"), "TranslationModel");
        content.Add(new StringContent("alloy"), "TtsVoice");
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "Audio", fileName);
        return content;
    }

    // Creates a session + a turn via the real HTTP endpoints; returns the client + the ids.
    private static async Task<(HttpClient Client, string SessionId, string TurnId)> SeedTurn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var create = new CreateSessionRequest("Blob run", InterpretationMode.Cascade,
            new LanguageDirection(LanguageCode.En, LanguageCode.Es), "gpt-realtime", "gpt-5.4-nano");
        var sessionResp = await client.PostAsJsonAsync("/api/sessions", create, JsonDefaults.Options);
        var sessionId = JsonDocument.Parse(await sessionResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
        var turnResp = await client.PostAsync($"/api/sessions/{sessionId}/turns", null);
        var turnId = JsonDocument.Parse(await turnResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("turnId").GetString()!;
        return (client, sessionId, turnId);
    }

    [Fact]
    public async Task post_turn_runs_blob_cascade_and_returns_turn_with_audio()
    {
        using var factory = Factory();
        var (client, sessionId, turnId) = await SeedTurn(factory);

        var resp = await client.PostAsync("/api/cascade/turn",
            Multipart(sessionId, turnId, new byte[] { 1, 2, 3, 4 }, "audio/webm"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("completed", doc.RootElement.GetProperty("turn").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("audioBase64").ValueKind); // audio delivered in-body
    }

    [Fact]
    public async Task post_turn_oversized_upload_is_413()
    {
        using var factory = Factory(maxUploadBytes: 10); // tiny cap so a small payload trips it
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/cascade/turn",
            Multipart("s1", "t1", new byte[64], "audio/webm"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // 413
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("cascade.invalid_audio", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task post_turn_unsupported_type_is_415()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/cascade/turn",
            Multipart("s1", "t1", new byte[] { 1, 2, 3 }, "text/plain", "notes.txt"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode); // 415
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("cascade.invalid_audio", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task post_turn_oversized_id_is_400()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/cascade/turn",
            Multipart(new string('x', 257), "t1", new byte[] { 1, 2, 3 }, "audio/webm"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400 — id cap (§16)
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("cascade.invalid_request", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task post_turn_unknown_turn_is_404()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        // A valid upload but a turn that was never created -> the orchestrator returns null -> 404.
        var resp = await client.PostAsync("/api/cascade/turn",
            Multipart("session_nope", "turn_nope", new byte[] { 1, 2, 3 }, "audio/webm"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("turn.not_found", doc.RootElement.GetProperty("code").GetString());
    }
}
