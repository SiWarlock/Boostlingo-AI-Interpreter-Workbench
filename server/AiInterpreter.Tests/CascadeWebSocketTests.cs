using System.Text.Json;
using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// C.4a — cascade WS endpoint deterministic-surface tests (ARCH-009 / ARCH-011 / ARCH-013).
//
// The WS TRANSPORT shell (socket accept, frame loop, PCM->AudioFrame Channel bridge, driving the
// orchestrator) is MANUAL-SMOKE (network boundary, like the provider shells). What is deterministic —
// and TDD'd here — is the pure CascadeWsMapping: the CascadeOutputEvent->WS-message mapping (ARCH-009
// shapes), the cost emit/degrade decision, the turn.recording.* Overall stamp, and the turn assembly.
// The SECURITY start-frame parse + encoding allowlist live in CascadeStartValidationTests. The
// orchestrator (B.4) + providers (C.1/2/3) are NOT re-tested.
public class CascadeWebSocketTests
{
    private static readonly DateTimeOffset When = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    // === Group 1 — CascadeOutputEvent -> ARCH-009 WS server message (camelCase) ===

    [Fact]
    public void transcript_event_maps_to_transcript_message()
    {
        var seg = new TranscriptSegment("src-0", "source", "hola", IsFinal: true, "deepgram", When, ClockSource.Server);

        var root = Serialize(CascadeWsMapping.ToServerMessage(new Transcript(seg), "turn_1"));

        Assert.Equal("transcript", root.GetProperty("type").GetString());
        Assert.Equal("hola", root.GetProperty("segment").GetProperty("text").GetString());
        Assert.Equal("source", root.GetProperty("segment").GetProperty("role").GetString());
    }

    [Fact]
    public void latency_event_maps_to_latency_message()
    {
        var ev = new LatencyEvent("stt.final", LatencyStage.Stt, When, 100, ClockSource.Server, new Dictionary<string, string>());

        var root = Serialize(CascadeWsMapping.ToServerMessage(new Latency(ev), "turn_1"));

        Assert.Equal("latency", root.GetProperty("type").GetString());
        Assert.Equal("stt.final", root.GetProperty("event").GetProperty("name").GetString());
    }

    [Fact]
    public void audio_event_maps_to_base64_message()
    {
        var root = Serialize(CascadeWsMapping.ToServerMessage(new Audio(new byte[] { 1, 2, 3 }, 5, "audio/mpeg"), "turn_1"));

        Assert.Equal("audio", root.GetProperty("type").GetString());
        Assert.Equal("audio/mpeg", root.GetProperty("contentType").GetString());
        Assert.Equal(5, root.GetProperty("seq").GetInt32());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), root.GetProperty("base64").GetString());
    }

    [Fact]
    public void done_event_maps_with_turnid_and_status()
    {
        var root = Serialize(CascadeWsMapping.ToServerMessage(new Done(TurnStatus.Completed), "turn_1"));

        Assert.Equal("done", root.GetProperty("type").GetString());
        Assert.Equal("turn_1", root.GetProperty("turnId").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString()); // TurnStatus -> camelCase enum string
    }

    [Fact]
    public void error_event_crosses_as_sanitized()
    {
        // The ProviderError is already sanitized by the provider; the WS boundary serializes it verbatim and
        // must not re-leak. Use a mapped exception whose message carries a secret-looking token.
        var err = ProviderErrorMapper.Map(new Exception("secret sk-abc123 boom"), "openai", "translation");

        var json = Json(CascadeWsMapping.ToServerMessage(new Error(err), "turn_1"));

        Assert.DoesNotContain("sk-abc123", json); // invariant #4 — no raw provider message leaks
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("translation.unknown", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // === Group 2 — cost emit/degrade (decoupled from token sourcing; that's the shell's concern) ===

    [Fact]
    public void cost_message_emitted_on_success()
    {
        var estimate = new CostEstimate("cascade", "gpt-5-nano", "composite", 0.01m, 0.5m,
            new Dictionary<string, decimal>(), "v1", new[] { "note" });

        var message = CascadeWsMapping.ToCostMessageOrNull(Result<CostEstimate>.Success(estimate));

        Assert.NotNull(message);
        var root = Serialize(message!);
        Assert.Equal("cost", root.GetProperty("type").GetString());
        Assert.Equal("cascade", root.GetProperty("estimate").GetProperty("provider").GetString());
    }

    [Fact]
    public void cost_message_omitted_when_unavailable()
    {
        // B.5 degrade: an unavailable estimate yields NO cost message (no crash, no fabrication).
        var message = CascadeWsMapping.ToCostMessageOrNull(Result<CostEstimate>.Failure("estimate unavailable"));

        Assert.Null(message);
    }

    // === Group 3 — turn.recording.* stamped LatencyStage.Overall (ARCH-013/ARCH-005) ===

    [Fact]
    public void recording_event_stamped_overall()
    {
        var factory = new LatencyEventFactory(new FixedClock(When));

        var ev = CascadeWsMapping.RecordingEvent(factory, LatencyEventNames.TurnRecordingStarted, When);

        Assert.Equal(LatencyStage.Overall, ev.Stage); // new enum member (cross-doc: ARCH-005 + Appendix A)
        Assert.Equal("turn.recording.started", ev.Name);
        Assert.Equal(ClockSource.Server, ev.ClockSource);
    }

    // === Group 4 — turn assembly on done (the persisted shape; persist itself is B.7, best-effort) ===

    [Fact]
    public void turn_assembled_from_accumulated_events()
    {
        var seg = new TranscriptSegment("src-0", "source", "hola", IsFinal: true, "deepgram", When, ClockSource.Server);
        var latency = new LatencyEvent("stt.final", LatencyStage.Stt, When, 10, ClockSource.Server, new Dictionary<string, string>());
        var estimate = new CostEstimate("cascade", "m", "composite", 0.01m, null, new Dictionary<string, decimal>(), "v1", Array.Empty<string>());
        var events = new CascadeOutputEvent[] { new Transcript(seg), new Latency(latency), new Done(TurnStatus.Completed) };

        var turn = CascadeWsMapping.AssembleTurn(BaseTurn(), events, Result<CostEstimate>.Success(estimate), When);

        Assert.Single(turn.Transcripts);
        Assert.Equal("hola", turn.Transcripts[0].Text);
        Assert.Single(turn.LatencyEvents);
        Assert.Equal(TurnStatus.Completed, turn.Status);
        Assert.Equal(estimate, turn.CostEstimate);
        Assert.Equal(When, turn.CompletedAt);
    }

    [Fact]
    public void turn_assembled_without_cost_when_unavailable()
    {
        var events = new CascadeOutputEvent[] { new Done(TurnStatus.Completed) };

        var turn = CascadeWsMapping.AssembleTurn(BaseTurn(), events, Result<CostEstimate>.Failure("unavailable"), When);

        Assert.Null(turn.CostEstimate); // degrade — no cost on the persisted turn, no crash
        Assert.Equal(TurnStatus.Completed, turn.Status);
    }

    // === Group 5 — ContentType clamp (C.4b — provider-sourced content-type hygiene before serialize) ===

    [Theory] // allowlisted audio types pass (case-insensitive, normalized lowercase); anything else → audio/mpeg.
    [InlineData("audio/mpeg", "audio/mpeg")]
    [InlineData("audio/wav", "audio/wav")]
    [InlineData("audio/pcm", "audio/pcm")]
    [InlineData("AUDIO/WAV", "audio/wav")]                       // case-insensitive match, normalized
    [InlineData("  audio/ogg  ", "audio/ogg")]                   // trimmed
    [InlineData("text/html", "audio/mpeg")]                       // non-audio → fallback
    [InlineData("audio/mpeg; codecs=evil", "audio/mpeg")]        // parametrized/garbage → fallback (strips params)
    [InlineData("", "audio/mpeg")]                                // empty → fallback
    [InlineData("not a mime type", "audio/mpeg")]                // garbage → fallback
    public void content_type_clamp(string? input, string expected)
    {
        Assert.Equal(expected, CascadeWsMapping.ClampContentType(input));
    }

    [Fact]
    public void content_type_clamp_null_falls_back()
    {
        Assert.Equal("audio/mpeg", CascadeWsMapping.ClampContentType(null));
    }

    [Fact]
    public void audio_message_content_type_is_clamped()
    {
        // The Audio→WS-message mapping must clamp a garbage provider content-type before it crosses the wire.
        var root = Serialize(CascadeWsMapping.ToServerMessage(new Audio(new byte[] { 1 }, 0, "text/html"), "turn_1"));

        Assert.Equal("audio/mpeg", root.GetProperty("contentType").GetString()); // clamped, not echoed
    }

    // === Group 6 — multi-segment cost accumulation fold (C.4b — pins the C.4a additive undercount fix) ===

    [Fact]
    public void multi_segment_cost_accumulation_sums_additively()
    {
        // Three translated segments: tokens must SUM across segments (a single-segment-overwrite impl would
        // report only the last segment → undercount). Target chars accumulate across the target finals.
        var events = new List<CascadeOutputEvent>
        {
            TranslationFinal(inputTokens: 10, outputTokens: 20),
            TargetFinal("hola"),   // 4
            TranslationFinal(inputTokens: 5, outputTokens: 7),
            TargetFinal("mundo!"), // 6
            TranslationFinal(inputTokens: 1, outputTokens: 2),
            TargetFinal("adios"),  // 5
        };

        var folded = CascadeWsMapping.FoldCostInputs(events);

        Assert.Equal(16, folded.InputTokens);   // 10 + 5 + 1
        Assert.Equal(29, folded.OutputTokens);  // 20 + 7 + 2
        Assert.Equal(15, folded.TargetChars);   // 4 + 6 + 5
    }

    [Fact]
    public void cost_fold_yields_null_tokens_when_no_translation_final()
    {
        // No translation.final events → tokens stay null (cost degrades; never fabricated 0). Source-only
        // transcripts do not contribute target chars.
        var events = new List<CascadeOutputEvent>
        {
            new Transcript(new TranscriptSegment("src-0", "source", "hola", IsFinal: true, "deepgram", When, ClockSource.Server)),
        };

        var folded = CascadeWsMapping.FoldCostInputs(events);

        Assert.Null(folded.InputTokens);
        Assert.Null(folded.OutputTokens);
        Assert.Equal(0, folded.TargetChars);
    }

    [Fact]
    public void cost_fold_ignores_partial_target_transcripts()
    {
        // Only IsFinal target transcripts contribute target chars — interspersed partials must NOT be counted
        // (the orchestrator emits a partial per token + one final per segment).
        var events = new List<CascadeOutputEvent>
        {
            new Transcript(new TranscriptSegment("tgt-0", "target", "ho", IsFinal: false, "openai", When, ClockSource.Server)),
            new Transcript(new TranscriptSegment("tgt-0", "target", "hola", IsFinal: false, "openai", When, ClockSource.Server)),
            TargetFinal("hola"), // 4 — the only contributor
        };

        var folded = CascadeWsMapping.FoldCostInputs(events);

        Assert.Equal(4, folded.TargetChars); // partials (2 + 4 chars) excluded
    }

    private static Latency TranslationFinal(int inputTokens, int outputTokens) =>
        new(new LatencyEvent(
            LatencyEventNames.TranslationFinal, LatencyStage.Translation, When, 0, ClockSource.Server,
            new Dictionary<string, string>
            {
                ["inputTokens"] = inputTokens.ToString(),
                ["outputTokens"] = outputTokens.ToString(),
            }));

    private static Transcript TargetFinal(string text) =>
        new(new TranscriptSegment("tgt-0", "target", text, IsFinal: true, "openai", When, ClockSource.Server));

    // === helpers ===

    private static string Json(object message) => JsonSerializer.Serialize(message, JsonDefaults.Options);

    private static JsonElement Serialize(object message) => JsonDocument.Parse(Json(message)).RootElement.Clone();

    private static InterpretationTurn BaseTurn() => new(
        "turn_1",
        InterpretationMode.Cascade,
        new LanguageDirection(LanguageCode.En, LanguageCode.Es),
        When,
        CompletedAt: null,
        AudioDurationMs: 0,
        Transcripts: new List<TranscriptSegment>(),
        LatencyEvents: new List<LatencyEvent>(),
        CostEstimate: null,
        WerResult: null,
        Errors: new List<ProviderError>(),
        Status: TurnStatus.Recording,
        TranslationModelUsed: null,
        TtsVoiceUsed: null);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
