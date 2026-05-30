using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Realtime;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// E.1 — RealtimeClientSecretMapping pure-surface tests (ARCH-010 mint body + response parse).
//
// The §18/§20 pattern applied to a non-streaming upstream call: all decision logic (request-body build,
// instructions render, GA response parse, epoch→ISO format) lives here, unit-TDD'd without a network
// round-trip. The transport (Bearer/timeout/retry) is RealtimeClientSecretServiceTests; the wire contract
// is RealtimeControllerTests.
public class RealtimeClientSecretMappingTests
{
    private static readonly LanguageDirection EnToEs = new(LanguageCode.En, LanguageCode.Es);

    // === Group 1 — the ARCH-010 GA client_secrets request body ===

    [Fact]
    public void builds_ga_body_with_arch010_shape()
    {
        var options = new RealtimeOptions
        {
            ExpirySeconds = 600,
            Voice = "marin",
            TranscriptionModel = "gpt-4o-transcribe",
        };

        var json = JsonSerializer.Serialize(
            RealtimeClientSecretMapping.BuildRequestBody(EnToEs, "gpt-realtime", options), JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // expires_after: { anchor: "created_at", seconds: <ExpirySeconds> }
        var expiresAfter = root.GetProperty("expires_after");
        Assert.Equal("created_at", expiresAfter.GetProperty("anchor").GetString());
        Assert.Equal(600, expiresAfter.GetProperty("seconds").GetInt32());

        var session = root.GetProperty("session");
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.Equal("gpt-realtime", session.GetProperty("model").GetString());

        // output_modalities == ["audio"]
        var modalities = session.GetProperty("output_modalities");
        Assert.Equal(JsonValueKind.Array, modalities.ValueKind);
        Assert.Equal("audio", Assert.Single(modalities.EnumerateArray()).GetString());

        var input = session.GetProperty("audio").GetProperty("input");
        // turn_detection must be EXPLICIT null (VAD off — manual turns; ARCH-010), not omitted.
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        Assert.Equal("gpt-4o-transcribe", input.GetProperty("transcription").GetProperty("model").GetString());

        Assert.Equal("marin", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());

        // The interpreter instructions are present AND wired from the direction (English→Spanish), not blank —
        // proves the direction → builder → field path, not just RenderInstructions in isolation.
        var instructions = session.GetProperty("instructions").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("English", instructions!, StringComparison.Ordinal);
        Assert.Contains("Spanish", instructions!, StringComparison.Ordinal);
    }

    // === Group 2 — interpreter instructions from the language direction ===

    [Fact]
    public void renders_instructions_from_direction()
    {
        // Null template → the ARCH-010 default prompt, with the direction filled in.
        var defaulted = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs);
        Assert.Contains("from English to Spanish", defaulted, StringComparison.Ordinal);

        // A custom template substitutes {source}/{target} with the language names.
        var custom = RealtimeClientSecretMapping.RenderInstructions("Interpret from {source} to {target}.", EnToEs);
        Assert.Equal("Interpret from English to Spanish.", custom);

        // Direction is honored both ways (ES→EN).
        var reversed = RealtimeClientSecretMapping.RenderInstructions(null, new LanguageDirection(LanguageCode.Es, LanguageCode.En));
        Assert.Contains("from Spanish to English", reversed, StringComparison.Ordinal);
    }

    // === Group 3 — GA response parse (tolerant of BOTH the GA top-level + legacy nested shapes) ===

    [Fact]
    public void parses_ga_top_level_value_and_epoch()
    {
        // The GA client_secrets endpoint returns the secret at the TOP LEVEL: { value, expires_at, session }.
        const long epoch = 1756310386;
        var body = $"{{\"value\":\"ek_abc123\",\"expires_at\":{epoch},\"session\":{{\"id\":\"sess_1\"}}}}";

        var secret = RealtimeClientSecretMapping.ParseResponse(body);

        Assert.NotNull(secret);
        Assert.Equal("ek_abc123", secret!.Value.Value);
        Assert.Equal(epoch, secret.Value.ExpiresAtEpoch);

        // Epoch → round-trip ISO-8601 UTC.
        var iso = RealtimeClientSecretMapping.ToIso8601(secret.Value.ExpiresAtEpoch);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(epoch), DateTimeOffset.Parse(iso));
    }

    [Fact]
    public void parses_legacy_nested_value_and_epoch()
    {
        // Robustness (lesson §18/§20): also accept the legacy /v1/realtime/sessions nested shape
        // { client_secret: { value, expires_at } } so a shape difference can't silently break minting.
        const long epoch = 1756310386;
        var body = $"{{\"id\":\"sess_1\",\"client_secret\":{{\"value\":\"ek_nested\",\"expires_at\":{epoch}}}}}";

        var secret = RealtimeClientSecretMapping.ParseResponse(body);

        Assert.NotNull(secret);
        Assert.Equal("ek_nested", secret!.Value.Value);
        Assert.Equal(epoch, secret.Value.ExpiresAtEpoch);
    }

    [Fact]
    public void parse_returns_null_when_no_value()
    {
        // A 200 with neither a top-level nor a nested value → null (the service maps null → realtime.unknown,
        // never fabricates a secret).
        Assert.Null(RealtimeClientSecretMapping.ParseResponse("{\"session\":{\"id\":\"sess_1\"}}"));
        Assert.Null(RealtimeClientSecretMapping.ParseResponse("not json at all"));
    }
}
