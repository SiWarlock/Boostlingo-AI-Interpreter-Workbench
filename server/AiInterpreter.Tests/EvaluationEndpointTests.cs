using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiInterpreter.Tests;

// F.1 — Evaluation endpoint wire tests (ARCH-009) via WebApplicationFactory. Feature A: the
// GET /phrases + POST /wer HTTP envelope (200/400/404) + the ARCH-019 no-LoadError-leak degrade.
// Feature B (transcribe 413/415/200) lands in the F.1b cycle. Fake providers; no real keys.
public sealed class EvaluationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EvaluationEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? extra = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("USE_FAKE_PROVIDERS", "true");
            extra?.Invoke(builder);
        }).CreateClient();

    [Fact]
    public async Task get_phrases_returns_200_list()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/evaluation/phrases");

        response.EnsureSuccessStatusCode();
        var phrases = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, phrases.ValueKind);
        Assert.True(phrases.GetArrayLength() > 0);
        Assert.NotNull(phrases[0].GetProperty("phraseId").GetString());
    }

    // #2 — store fails to load -> empty list, and NO LoadError / path fragment leaks (ARCH-019, lesson §10).
    [Fact]
    public async Task get_phrases_store_not_loaded_returns_empty_no_error_leak()
    {
        using var client = CreateClient(b =>
            b.UseSetting("EVALUATION_PHRASES_PATH", "/nonexistent/aiw-no-such-phrases.json"));

        var response = await client.GetAsync("/api/evaluation/phrases");

        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", raw.Replace(" ", string.Empty).Trim());
        Assert.DoesNotContain("nonexistent", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LoadError", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task wer_perfect_match_returns_200_zero()
    {
        using var client = CreateClient();
        var phrases = await (await client.GetAsync("/api/evaluation/phrases"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var phraseId = phrases[0].GetProperty("phraseId").GetString()!;
        var reference = phrases[0].GetProperty("referenceText").GetString()!;

        var response = await client.PostAsJsonAsync("/api/evaluation/wer", new
        {
            sessionId = "session_x",
            phraseId,
            hypothesis = reference,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.0, body.GetProperty("result").GetProperty("wer").GetDouble());
    }

    [Fact]
    public async Task wer_hypothesis_over_cap_returns_400()
    {
        using var client = CreateClient();
        var phrases = await (await client.GetAsync("/api/evaluation/phrases"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var phraseId = phrases[0].GetProperty("phraseId").GetString()!;

        var response = await client.PostAsJsonAsync("/api/evaluation/wer", new
        {
            sessionId = "session_x",
            phraseId,
            hypothesis = new string('a', 2001),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("evaluation.invalid_phrase", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task wer_unknown_phrase_returns_404()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/evaluation/wer", new
        {
            sessionId = "session_x",
            phraseId = "no-such-phrase",
            hypothesis = "anything",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("evaluation.phrase_not_found", body.GetProperty("code").GetString());
    }

    // A turnId targeting an unknown session/turn → 404 turn.not_found (don't silently drop the persist
    // target). Pins the third error branch of the controller switch at the wire envelope.
    [Fact]
    public async Task wer_with_unknown_turn_returns_404()
    {
        using var client = CreateClient();
        var phrases = await (await client.GetAsync("/api/evaluation/phrases"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var phraseId = phrases[0].GetProperty("phraseId").GetString()!;
        var reference = phrases[0].GetProperty("referenceText").GetString()!;

        var response = await client.PostAsJsonAsync("/api/evaluation/wer", new
        {
            sessionId = "session_does_not_exist",
            turnId = "turn_does_not_exist",
            phraseId,
            hypothesis = reference,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turn.not_found", body.GetProperty("code").GetString());
    }
}
