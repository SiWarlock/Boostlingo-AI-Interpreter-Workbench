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

    // === Group 4 — J.2 bidirectional interpreter instruction (Phase J) ===

    // The exact one-direction En→Es render. 098 (§39 re-assert-in-place): the template is RESTRUCTURED for
    // Finding B (conduit framing + target-language lock + explicit question-handling + a direction-safe
    // example), so the exact render includes all four — with every "{target}" filled to "Spanish" via the
    // existing substitution.
    private const string OneDirectionEnToEs =
        "You are ONLY a translation conduit. You are NEVER a conversational party. " +
        "Render the speaker's words from English to Spanish: ALWAYS output ONLY the Spanish translation — " +
        "never any other language, never your own words, no commentary, no preamble. " +
        "If the speaker asks a question or speaks directly to you, translate the QUESTION itself into Spanish — " +
        "NEVER answer, respond to, explain, or add anything. " +
        "For example, translate a question as the question in Spanish, never as an answer.";

    [Fact]
    public void renders_bidirectional_detect_both_render_other()
    {
        // bidirectional:true ⇒ a detect-EN-or-ES → render-the-OTHER instruction: names BOTH languages, carries
        // the "other" intent, and has NO one-direction placeholders / no hardcoded single direction.
        var bidir = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs, bidirectional: true);

        Assert.Contains("English", bidir, StringComparison.Ordinal);
        Assert.Contains("Spanish", bidir, StringComparison.Ordinal);
        Assert.Contains("other", bidir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{source}", bidir, StringComparison.Ordinal);
        Assert.DoesNotContain("{target}", bidir, StringComparison.Ordinal);
        Assert.DoesNotContain("from English to Spanish", bidir, StringComparison.Ordinal); // not hardcoded one-way
        Assert.NotEqual(RealtimeClientSecretMapping.RenderInstructions(null, EnToEs, bidirectional: false), bidir);
    }

    [Fact]
    public void renders_one_direction_exact_render_and_overload_default()
    {
        // Pins (a) the EXACT one-direction render — now the 098-hardened template (OneDirectionEnToEs carries
        // the forbid-answering clause) — and (b) the overload default: the 2-arg path == bidirectional:false,
        // so the J.2 `bidirectional` param still defaults to the one-direction behavior (no API regression).
        var viaFalse = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs, bidirectional: false);
        var viaDefault = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs);

        Assert.Equal(OneDirectionEnToEs, viaFalse);
        Assert.Equal(viaDefault, viaFalse); // the new param defaults to the existing one-direction behavior
    }

    // === Group 5 — 098 / Finding-B: the interpreter instruction must FORBID answering ===

    [Fact]
    public void bidirectional_instructions_forbid_answering()
    {
        // Finding B (user live-test 2026-06-01): a spoken question ("What is your name?") was ANSWERED in
        // ENGLISH with meta-commentary instead of TRANSLATED — the prior "speak only the translation, no
        // commentary" was violated outright. The restructured template must (1) frame as a pure conduit,
        // (2) lock the OUTPUT to the other language, (3) explicitly translate-not-answer a question, with
        // (4) a concrete example. Pins WIRING + CONTENT (clauses present + survive render); effectiveness is
        // eval-observed (lead/user re-test the live mint).
        var bidir = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs, bidirectional: true);

        // 1 — emphatic conduit framing.
        Assert.Contains("ONLY a translation conduit", bidir, StringComparison.Ordinal);
        Assert.Contains("NEVER a conversational party", bidir, StringComparison.Ordinal);
        // 2 — target-language lock (it replied in English; output must be the OTHER language only).
        Assert.Contains("ONLY the translation in the OTHER language", bidir, StringComparison.Ordinal);
        // 3 — explicit question-handling.
        Assert.Contains("NEVER answer", bidir, StringComparison.Ordinal);
        // 4 — the concrete acceptance example (translate the question, don't answer it).
        Assert.Contains("¿Cómo te llamas?", bidir, StringComparison.Ordinal);
        // The detect-both base survives (additive, not a replacement).
        Assert.Contains("English", bidir, StringComparison.Ordinal);
        Assert.Contains("Spanish", bidir, StringComparison.Ordinal);
        Assert.Contains("other", bidir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void one_direction_instructions_forbid_answering()
    {
        // The same restructure on the one-direction / fixed-direction template (the user's actual repro path) —
        // AND the {target} placeholder survives: the lock + question-handling fill "Spanish" (no literal
        // "{target}"/"{source}" leaks through).
        var oneDir = RealtimeClientSecretMapping.RenderInstructions(null, EnToEs, bidirectional: false);

        Assert.Contains("ONLY a translation conduit", oneDir, StringComparison.Ordinal);
        Assert.Contains("NEVER a conversational party", oneDir, StringComparison.Ordinal);
        Assert.Contains("ONLY the Spanish translation", oneDir, StringComparison.Ordinal); // target-lang lock + {target} filled
        Assert.Contains("NEVER answer", oneDir, StringComparison.Ordinal);
        Assert.DoesNotContain("{target}", oneDir, StringComparison.Ordinal);   // no stray placeholder
        Assert.DoesNotContain("{source}", oneDir, StringComparison.Ordinal);
    }

    [Fact]
    public void build_request_body_threads_bidirectional_into_instructions()
    {
        // The flag threads end-to-end: BuildRequestBody(bidirectional:true) ⇒ session.instructions == the bidir render.
        var options = new RealtimeOptions { Voice = "marin", TranscriptionModel = "gpt-4o-transcribe" };

        var json = JsonSerializer.Serialize(
            RealtimeClientSecretMapping.BuildRequestBody(EnToEs, "gpt-realtime", options, bidirectional: true), JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        var instructions = doc.RootElement.GetProperty("session").GetProperty("instructions").GetString();

        Assert.Equal(
            RealtimeClientSecretMapping.RenderInstructions(options.InstructionsTemplate, EnToEs, bidirectional: true),
            instructions);
        Assert.Contains("other", instructions!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from English to Spanish", instructions!, StringComparison.Ordinal);
    }

    [Fact]
    public void token_request_absent_bidirectional_defaults_false()
    {
        // A mint body WITHOUT `bidirectional` deserializes to false (trailing default — back-compat with today's
        // FE); an explicit `bidirectional:true` is honored.
        var off = JsonSerializer.Deserialize<RealtimeTokenRequest>(
            "{\"sessionId\":\"s1\",\"direction\":{\"source\":\"en\",\"target\":\"es\"}}", JsonDefaults.Options);
        Assert.NotNull(off);
        Assert.False(off!.Bidirectional);

        var on = JsonSerializer.Deserialize<RealtimeTokenRequest>(
            "{\"sessionId\":\"s1\",\"direction\":{\"source\":\"en\",\"target\":\"es\"},\"bidirectional\":true}", JsonDefaults.Options);
        Assert.True(on!.Bidirectional);
    }
}
