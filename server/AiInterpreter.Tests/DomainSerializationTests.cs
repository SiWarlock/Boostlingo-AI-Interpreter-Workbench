using System.Globalization;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// Pins the ARCH-005 domain model's JSON serialization contract against the ARCH-009 (API) +
// ARCH-016 (persisted) camelCase examples, using the shared JsonDefaults.Options that A.5 (HTTP)
// and B.7 (persistence) reuse — so API and persisted JSON cannot diverge. Round-trip equality is
// asserted on the serialized JSON (record `==` is reference-based over List/Dictionary members).
public class DomainSerializationTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    [Fact]
    public void enums_serialize_as_camelCase_strings()
    {
        Assert.Equal("\"realtime\"", JsonSerializer.Serialize(InterpretationMode.Realtime, Json));
        Assert.Equal("\"en\"", JsonSerializer.Serialize(LanguageCode.En, Json));
        Assert.Equal("\"stt\"", JsonSerializer.Serialize(LatencyStage.Stt, Json));
        Assert.Equal("\"server\"", JsonSerializer.Serialize(ClockSource.Server, Json));
        Assert.Equal("\"readyForTurn\"", JsonSerializer.Serialize(SessionStatus.ReadyForTurn, Json));
    }

    [Fact]
    public void session_serializes_camelCase_matching_arch009()
    {
        var json = JsonSerializer.Serialize(BuildFullSession(), Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("sessionId", out _));
        Assert.True(root.TryGetProperty("startedAt", out _));
        Assert.True(root.TryGetProperty("pricingConfigVersion", out _));
        Assert.True(root.TryGetProperty("config", out var config));
        Assert.Equal("cascade", config.GetProperty("currentMode").GetString());
        Assert.Equal("en", config.GetProperty("direction").GetProperty("source").GetString());
        Assert.Equal("es", config.GetProperty("direction").GetProperty("target").GetString());
        Assert.True(config.GetProperty("providerProfile").TryGetProperty("realtimeModel", out _));
        Assert.True(config.GetProperty("providerProfile").TryGetProperty("ttsVoice", out _));
    }

    [Fact]
    public void latencyEvent_serializes_matching_arch013()
    {
        var ev = new LatencyEvent(
            "stt.final",
            LatencyStage.Stt,
            new DateTimeOffset(2026, 5, 28, 15, 30, 5, 123, TimeSpan.Zero),
            912,
            ClockSource.Server,
            new Dictionary<string, string> { ["provider"] = "deepgram", ["model"] = "nova-3" });

        var json = JsonSerializer.Serialize(ev, Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("stt.final", root.GetProperty("name").GetString());
        Assert.Equal("stt", root.GetProperty("stage").GetString());
        Assert.Equal(912, root.GetProperty("relativeMs").GetInt64());
        Assert.Equal("server", root.GetProperty("clockSource").GetString());
        Assert.Equal("deepgram", root.GetProperty("metadata").GetProperty("provider").GetString());
    }

    [Fact]
    public void dateTimeOffset_is_iso8601()
    {
        var session = BuildFullSession();
        var json = JsonSerializer.Serialize(session, Json);
        using var doc = JsonDocument.Parse(json);
        var startedAt = doc.RootElement.GetProperty("startedAt").GetString();

        Assert.NotNull(startedAt);
        var parsed = DateTimeOffset.Parse(startedAt!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(session.StartedAt.ToUnixTimeMilliseconds(), parsed.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void round_trip_preserves_equality()
    {
        var session = BuildFullSession();
        var json1 = JsonSerializer.Serialize(session, Json);
        var back = JsonSerializer.Deserialize<InterpretationSession>(json1, Json);

        Assert.NotNull(back);
        var json2 = JsonSerializer.Serialize(back, Json);
        Assert.Equal(json1, json2);

        Assert.Equal(session.SessionId, back!.SessionId);
        Assert.Equal(session.Turns[0].Transcripts[0].Text, back.Turns[0].Transcripts[0].Text);
        Assert.Equal(session.Turns[0].LatencyEvents[0].Stage, back.Turns[0].LatencyEvents[0].Stage);
        Assert.Equal(session.Turns[0].CostEstimate!.EstimatedUsd, back.Turns[0].CostEstimate!.EstimatedUsd);
        Assert.Equal(session.Turns[0].Errors[0].Code, back.Turns[0].Errors[0].Code);
    }

    // F.4 — the new IsEvaluation marker serializes camelCase ("isEvaluation"), defaults false on a normal
    // turn, and round-trips true through JsonDefaults (the persisted-JSON contract changed — ARCH-005/016).
    [Fact]
    public void is_evaluation_round_trips_through_json()
    {
        var normalTurn = BuildFullSession().Turns[0];              // constructed without the arg → default
        var evalTurn = normalTurn with { IsEvaluation = true };

        using var normalDoc = JsonDocument.Parse(JsonSerializer.Serialize(normalTurn, Json));
        Assert.True(normalDoc.RootElement.TryGetProperty("isEvaluation", out var normalFlag)); // camelCase key
        Assert.False(normalFlag.GetBoolean());                    // default false (interpretation turn)

        var evalJson = JsonSerializer.Serialize(evalTurn, Json);
        using var evalDoc = JsonDocument.Parse(evalJson);
        Assert.True(evalDoc.RootElement.GetProperty("isEvaluation").GetBoolean());

        var back = JsonSerializer.Deserialize<InterpretationTurn>(evalJson, Json);
        Assert.NotNull(back);
        Assert.True(back!.IsEvaluation);                          // round-trips true
        Assert.Equal(evalJson, JsonSerializer.Serialize(back, Json));
    }

    [Fact]
    public void nullable_fields_behave()
    {
        var session = new InterpretationSession(
            "session_min",
            null,
            new DateTimeOffset(2026, 5, 28, 15, 30, 0, TimeSpan.Zero),
            null,
            BuildConfig(),
            new List<InterpretationTurn>(),
            new List<ModeTransitionEvent>(),
            null,
            "2026-05-28-payg-estimates");

        var json = JsonSerializer.Serialize(session, Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ARCH-016 example writes nulls EXPLICITLY ("summary": null) — NOT omitted (no
        // WhenWritingNull). TryGetProperty pins the key as PRESENT (a WhenWritingNull regression
        // would omit it) AND null.
        Assert.True(root.TryGetProperty("summary", out var summary));
        Assert.Equal(JsonValueKind.Null, summary.ValueKind);
        Assert.True(root.TryGetProperty("endedAt", out var endedAt));
        Assert.Equal(JsonValueKind.Null, endedAt.ValueKind);
        Assert.True(root.TryGetProperty("label", out var label));
        Assert.Equal(JsonValueKind.Null, label.ValueKind);
        // empty lists serialize as []
        Assert.Equal(JsonValueKind.Array, root.GetProperty("turns").ValueKind);
        Assert.Equal(0, root.GetProperty("turns").GetArrayLength());
    }

    private static SessionConfig BuildConfig() => new(
        InterpretationMode.Cascade,
        new LanguageDirection(LanguageCode.En, LanguageCode.Es),
        new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy"));

    private static InterpretationSession BuildFullSession()
    {
        var ts = new DateTimeOffset(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);

        var turn = new InterpretationTurn(
            "turn_001",
            InterpretationMode.Cascade,
            new LanguageDirection(LanguageCode.En, LanguageCode.Es),
            ts,
            ts.AddSeconds(3),
            3000,
            new List<TranscriptSegment>
            {
                new("seg_1", "source", "hello world", true, "deepgram", ts, ClockSource.Server),
                new("seg_2", "target", "hola mundo", true, "openai", ts.AddMilliseconds(900), ClockSource.Server),
            },
            new List<LatencyEvent>
            {
                new("stt.final", LatencyStage.Stt, ts.AddMilliseconds(912), 912, ClockSource.Server,
                    new Dictionary<string, string> { ["provider"] = "deepgram" }),
            },
            new CostEstimate(
                "openai", "gpt-5-nano", "tokens", 0.0012m, 0.07m,
                new Dictionary<string, decimal> { ["inputTokens"] = 12m, ["outputTokens"] = 8m },
                "2026-05-28-payg-estimates", new[] { "estimate only" }),
            new WerResult("phrase_1", "hello world", "hello word", "hello world", "hello word", 1, 0, 0, 2, 0.5),
            new List<ProviderError>
            {
                new("openai", "translation", "translation.rate_limited", "Rate limited", true, 429),
            },
            TurnStatus.Completed,
            "gpt-5-nano",
            "alloy");

        return new InterpretationSession(
            "session_abc123",
            "Demo run 1",
            ts,
            ts.AddMinutes(6),
            BuildConfig(),
            new List<InterpretationTurn> { turn },
            new List<ModeTransitionEvent>
            {
                new("trans_1", InterpretationMode.Realtime, InterpretationMode.Cascade,
                    new LanguageDirection(LanguageCode.En, LanguageCode.Es), ts, ClockSource.Server, "turn_001"),
            },
            new SessionSummary(
                1,
                new ModeSummary(0, null, null, null, 0, null, null, null),
                new ModeSummary(1, 1500.0, 1800.0, 0.07m, 1, 912.0, 1700.0, 300.0),
                new WerSummary(1, 0.5),
                ts.AddMinutes(6),
                "2026-05-28-payg-estimates"),
            "2026-05-28-payg-estimates");
    }
}
