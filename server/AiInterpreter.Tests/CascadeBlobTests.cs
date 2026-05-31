using System.Text.Json;
using AiInterpreter.Api.Cascade;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Fakes;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// C.5 — CascadeOrchestrator (the blob-fallback adapter). The blob path is the documented NON-streaming
// fallback: it REUSES the streaming CascadeStreamingOrchestrator (pre-recorded STT via container-encoding
// routing -> the same per-segment translation->TTS -> Done) and COLLECTS the event stream into a single
// turn + cost + (response-only) audio. NOT a re-implementation (ARCH-008/011; lesson §8/§21).
//
// Driven against the B.2 fakes (no real keys). The pure CascadeWsMapping glue + the fail-closed paths are
// reused from C.4a/C.4b and not re-tested here.
public class CascadeBlobTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 29, 16, 0, 0, TimeSpan.Zero);
    private static readonly LanguageDirection EnToEs = new(LanguageCode.En, LanguageCode.Es);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    // Captures the SttRequest.Encoding the orchestrator built, to prove the blob path routes pre-recorded
    // (a container encoding), NOT the live linear16 path.
    private sealed class EncodingCapturingSttProvider(ISttProvider inner) : ISttProvider
    {
        public string? CapturedEncoding { get; private set; }

        public IAsyncEnumerable<SttEvent> TranscribeAsync(SttRequest request, CancellationToken ct)
        {
            CapturedEncoding = request.Encoding;
            return inner.TranscribeAsync(request, ct);
        }
    }

    private static CostEstimator PricedEstimator()
    {
        var pricing = PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        Assert.True(pricing.IsSuccess, pricing.Error);
        return new CostEstimator(pricing);
    }

    private static SessionConfig Config() => new(
        InterpretationMode.Cascade, EnToEs,
        new ProviderProfile("openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy"));

    // Builds an orchestrator over the given fakes + a store seeded with one Ready turn; returns the
    // orchestrator, the store, and the seeded (sessionId, turnId).
    private static (CascadeOrchestrator Orch, SessionStore Store, string SessionId, string TurnId) Build(
        ISttProvider stt, ITranslationProvider translation, ITtsProvider tts, CostEstimator estimator)
    {
        var clock = new FakeClock(Base);
        var factory = new LatencyEventFactory(clock);
        var streaming = new CascadeStreamingOrchestrator(stt, translation, tts, factory, clock);
        var store = new SessionStore(clock);
        var writer = new SessionPersistenceWriter(Path.Combine(Path.GetTempPath(), "aiw-blob-tests"));
        var orch = new CascadeOrchestrator(streaming, store, writer, estimator, clock);

        var session = store.Create(Config(), "v1");
        var turn = store.CreateTurn(session.SessionId)!;
        return (orch, store, session.SessionId, turn.TurnId);
    }

    private static CascadeBlobParams Params(string sessionId, string turnId, string encoding = "webm") =>
        new(sessionId, turnId, EnToEs, encoding, "gpt-5-nano", "alloy");

    [Fact]
    public async Task blob_runs_prerecorded_cascade_and_collects_turn()
    {
        var stt = new EncodingCapturingSttProvider(new FakeSttProvider(FakeSttBehavior.SuccessWithPartials));
        var (orch, _, sid, tid) = Build(stt,
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            PricedEstimator());

        var result = await orch.RunBlobTurnAsync(Params(sid, tid), new byte[] { 1, 2, 3, 4 }, default);

        Assert.NotNull(result);
        Assert.Equal(TurnStatus.Completed, result!.Turn.Status);
        Assert.Contains(result.Turn.Transcripts, t => t.Role == "source");
        Assert.Contains(result.Turn.Transcripts, t => t.Role == "target" && t.IsFinal);
        Assert.NotEmpty(result.Turn.LatencyEvents);
        Assert.NotEqual("linear16", stt.CapturedEncoding); // pre-recorded REST route, not live PCM
        Assert.NotEmpty(result.AudioBytes); // TTS audio delivered in-body (the non-streaming fallback)
    }

    [Fact]
    public async Task blob_stage_failure_keeps_upstream()
    {
        var (orch, _, sid, tid) = Build(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.Error),
            new FakeTtsProvider(),
            PricedEstimator());

        var result = await orch.RunBlobTurnAsync(Params(sid, tid), new byte[] { 1, 2, 3 }, default);

        Assert.Equal(TurnStatus.Failed, result!.Turn.Status);
        Assert.Contains(result.Turn.Transcripts, t => t.Role == "source"); // upstream kept
        Assert.DoesNotContain(result.Turn.Transcripts, t => t.Role == "target" && t.IsFinal);
        Assert.Contains(result.Turn.Errors, e => e.Code.StartsWith("translation."));
    }

    [Fact]
    public async Task blob_empty_transcript_short_circuits()
    {
        var (orch, _, sid, tid) = Build(
            new FakeSttProvider(FakeSttBehavior.EmptyFinal),
            new FakeTranslationProvider(),
            new FakeTtsProvider(),
            PricedEstimator());

        var result = await orch.RunBlobTurnAsync(Params(sid, tid), new byte[] { 1 }, default);

        Assert.Equal(TurnStatus.Failed, result!.Turn.Status);
        Assert.Contains(result.Turn.Errors, e => e.Code == "cascade.empty_transcript");
    }

    [Fact]
    public async Task blob_cost_unavailable_stt_not_priceable()
    {
        // The blob path does NOT price the STT stage: the pre-recorded route carries no audio-duration
        // signal in the SttEvent contract, and a processing wall-clock would be a ~30x undercount (the
        // streaming-honesty posture forbids a knowingly-wrong stage metric, lesson §9). STT minutes are
        // left unsupplied -> EstimateStt unavailable -> the composite degrades wholesale -> null cost.
        // Asserted with a FULLY-PRICED estimator, so this proves the degrade is STT-structural (not a
        // missing-pricing artifact). No crash. (Documented Phase-C/G.5 limitation; WS path prices fully.)
        var (orch, _, sid, tid) = Build(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            PricedEstimator());

        var result = await orch.RunBlobTurnAsync(Params(sid, tid), new byte[] { 1, 2 }, default);

        Assert.NotNull(result);                  // no crash
        Assert.Null(result!.Turn.CostEstimate);  // blob never prices STT -> whole cost unavailable, never fabricated
    }

    [Fact]
    public async Task blob_audio_in_response_but_not_persisted()
    {
        var (orch, store, sid, tid) = Build(
            new FakeSttProvider(FakeSttBehavior.SuccessWithPartials),
            new FakeTranslationProvider(FakeTranslationBehavior.TokenStreamThenFinal),
            new FakeTtsProvider(FakeTtsBehavior.ChunkedThenComplete),
            PricedEstimator());

        var result = await orch.RunBlobTurnAsync(Params(sid, tid), new byte[] { 1, 2 }, default);

        Assert.NotEmpty(result!.AudioBytes); // audio delivered in the response body

        // The persisted/stored turn carries NO audio (invariant #3) — serialize + sentinel-scan.
        var storedTurn = store.Get(sid)!.Turns.Single();
        var json = JsonSerializer.Serialize(storedTurn, JsonDefaults.Options);
        Assert.DoesNotContain("\"audio\":", json, StringComparison.OrdinalIgnoreCase); // §11 key-form: no audio field
        Assert.DoesNotContain("\"audioBase64\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(result.AudioBytes), json); // the audio bytes never persisted
        Assert.Contains(storedTurn.Transcripts, t => t.Role == "target"); // but transcripts are persisted
    }

    [Fact]
    public async Task blob_returns_null_for_unknown_turn()
    {
        var (orch, _, sid, _) = Build(
            new FakeSttProvider(), new FakeTranslationProvider(), new FakeTtsProvider(), PricedEstimator());

        Assert.Null(await orch.RunBlobTurnAsync(Params(sid, "turn_missing"), new byte[] { 1 }, default));
    }
}
