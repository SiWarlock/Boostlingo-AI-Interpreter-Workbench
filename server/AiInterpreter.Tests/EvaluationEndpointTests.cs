using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiInterpreter.Api.Providers.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    // ---- Feature B: POST /transcribe wire (multipart) ----

    private static MultipartFormDataContent TranscribeForm(
        byte[] audio, string contentType, string language = "en", string fileName = "clip.webm")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("session_x"), "SessionId" },
            { new StringContent("en_001"), "PhraseId" },
            { new StringContent(language), "Language" },
        };
        var file = new ByteArrayContent(audio);
        // Parse (not the single-arg ctor) so a parameterized media type like "audio/webm; codecs=opus"
        // (the MediaRecorder format) is sent as a proper Content-Type with its parameter, exercising the
        // server's MIME-param stripping (lesson §23). The ctor rejects parameters.
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Add(file, "Audio", fileName);
        return content;
    }

    [Fact]
    public async Task transcribe_returns_200_hypothesis()
    {
        using var client = CreateClient();

        var resp = await client.PostAsync(
            "/api/evaluation/transcribe", TranscribeForm(new byte[] { 1, 2, 3, 4 }, "audio/webm"));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("hypothesis").GetString())); // fake STT → "hello world"
        Assert.Equal("deepgram", body.GetProperty("sttProvider").GetString());
    }

    // #12 (SECURITY pin) — an oversized upload is rejected (413) BEFORE any provider call. A throwing STT
    // proves no provider was reached: if validation didn't gate, the throw would surface as 500, not 413.
    [Fact]
    public async Task transcribe_oversized_413_before_provider_call()
    {
        using var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("USE_FAKE_PROVIDERS", "true");
            b.UseSetting("EVAL_MAX_UPLOAD_BYTES", "10");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ISttProvider>();
                s.AddSingleton<ISttProvider, ThrowingSttProvider>();
            });
        }).CreateClient();

        var resp = await client.PostAsync(
            "/api/evaluation/transcribe", TranscribeForm(new byte[64], "audio/webm"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // 413, not 500 → provider never called
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cascade.invalid_audio", body.GetProperty("code").GetString());
    }

    // #13 — empty + off-allowlist content-type → 415; a base type carrying MIME params is accepted (the
    // MediaRecorder "audio/webm; codecs=opus" path — params stripped before the allowlist, lesson §23).
    [Fact]
    public async Task transcribe_unsupported_or_empty_type_415_and_mime_params_stripped()
    {
        using var client = CreateClient();

        var unsupported = await client.PostAsync(
            "/api/evaluation/transcribe", TranscribeForm(new byte[] { 1, 2, 3 }, "text/plain", fileName: "notes.txt"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode); // 415

        var empty = await client.PostAsync(
            "/api/evaluation/transcribe", TranscribeForm(Array.Empty<byte>(), "audio/webm"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, empty.StatusCode); // 415 (empty)

        var withParams = await client.PostAsync(
            "/api/evaluation/transcribe", TranscribeForm(new byte[] { 1, 2, 3 }, "audio/webm; codecs=opus"));
        withParams.EnsureSuccessStatusCode(); // 200 — base type on the allowlist, params stripped server-side
    }

    private sealed class ThrowingSttProvider : ISttProvider
    {
        public IAsyncEnumerable<SttEvent> TranscribeAsync(SttRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("STT must not be reached when upload validation rejects.");
    }
}
