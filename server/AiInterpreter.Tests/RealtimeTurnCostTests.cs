using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// E.2b — realtime per-turn cost wiring at /complete (SessionService.CompleteTurnAsync).
//
// Wires the already-tested CostEstimator.EstimateRealtime into the realtime finalize path: a Realtime-mode
// turn gets a CostEstimate; cascade is untouched (WS-priced); degrade-to-null on Unavailable; cost lands
// inside the idempotent FinalizeTurn transform. Tests construct SessionService directly with a real
// CostEstimator over the committed pricing.json (the same fixture CostEstimatorTests uses), so the math is
// the real estimator's — this slice only proves the WIRING (model/seconds flow + branch + degrade).
public class RealtimeTurnCostTests
{
    private static readonly DateTimeOffset T = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
    private const decimal Million = 1_000_000m;

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    [Fact]
    public async Task realtime_complete_sets_cost_estimate()
    {
        var svc = Build();
        var cost = await CompleteRealtimeTurn(svc, "gpt-realtime", audioDurationMs: 10_000);

        Assert.NotNull(cost);
        Assert.Equal("openai", cost!.Provider);
        Assert.Equal("gpt-realtime", cost.Model);
        Assert.Equal("tokens", cost.PricingBasis); // realtime bills on audio tokens; the audio-seconds live in Units

        // Input-only (no output reported): 10s × 50 tokens/s ÷ 1e6 × 32.0 USD/Mtok.
        var f = CostEstimator.RealtimeTokensPerAudioSecond;
        Assert.Equal(10m * f / Million * 32.0m, cost.EstimatedUsd);
        Assert.Contains(cost.Assumptions,
            a => a.Contains("output audio duration not reported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task realtime_complete_cost_model_keyed()
    {
        var svc = Build();
        var full = await CompleteRealtimeTurn(svc, "gpt-realtime", audioDurationMs: 10_000);
        var mini = await CompleteRealtimeTurn(svc, "gpt-realtime-mini", audioDurationMs: 10_000);

        Assert.NotNull(full);
        Assert.NotNull(mini);
        Assert.Equal("gpt-realtime-mini", mini!.Model);
        // mini input rate 10.0 vs full 32.0 → cheaper; proves the per-session model flows into the estimate.
        Assert.True(mini.EstimatedUsd < full!.EstimatedUsd, $"mini {mini.EstimatedUsd} should be < full {full.EstimatedUsd}");
    }

    [Fact]
    public async Task cascade_complete_leaves_cost_untouched()
    {
        var svc = Build();
        var cost = await CompleteTurn(svc, InterpretationMode.Cascade, "gpt-realtime", audioDurationMs: 10_000);

        // Cascade turns are priced by the C.4 WS; /complete applies no realtime cost (branch guard).
        Assert.Null(cost);
    }

    [Fact]
    public async Task realtime_complete_pricing_absent_degrades_null()
    {
        var svc = Build(Result<PricingOptions>.Failure("no config"));
        var session = await svc.CreateAsync(Req(InterpretationMode.Realtime, "gpt-realtime"));
        var turnId = svc.CreateTurn(session.SessionId)!;

        var outcome = await svc.CompleteTurnAsync(session.SessionId, turnId, new CompleteTurnRequest(10_000, null, null));

        Assert.NotNull(outcome);
        Assert.Null(outcome!.Turn.CostEstimate);                  // EstimateRealtime → Unavailable → null (no 0, no throw)
        Assert.Equal(TurnStatus.Completed, outcome.Turn.Status);  // the turn still completes
    }

    [Fact]
    public async Task realtime_complete_idempotent_cost()
    {
        var svc = Build();
        var session = await svc.CreateAsync(Req(InterpretationMode.Realtime, "gpt-realtime"));
        var turnId = svc.CreateTurn(session.SessionId)!;

        var first = await svc.CompleteTurnAsync(session.SessionId, turnId, new CompleteTurnRequest(10_000, null, null, 4_000));
        var firstCost = first!.Turn.CostEstimate;
        Assert.NotNull(firstCost);

        // A second /complete with DIFFERENT durations: the turn is already terminal → returned unchanged
        // (cost not recomputed, duration not re-merged) — the C.4b idempotent finalize.
        var second = await svc.CompleteTurnAsync(session.SessionId, turnId, new CompleteTurnRequest(99_999, null, null, 99_999));

        // The already-terminal turn is returned unchanged — the SAME object, so the SAME CostEstimate
        // reference (the transform was not re-applied; a coincidentally-equal recompute can't pass this).
        Assert.Same(firstCost, second!.Turn.CostEstimate);
        Assert.Equal(10_000, second.Turn.AudioDurationMs);
    }

    [Fact]
    public async Task realtime_complete_zero_duration_degrades_null()
    {
        var svc = Build();
        var session = await svc.CreateAsync(Req(InterpretationMode.Realtime, "gpt-realtime"));
        var turnId = svc.CreateTurn(session.SessionId)!;

        // No audioDurationMs reported (turn keeps CreateTurn's 0 default) → no usable audio signal → null,
        // never a synthetic $0.00 (lesson §9/§12). The turn still completes.
        var outcome = await svc.CompleteTurnAsync(session.SessionId, turnId, new CompleteTurnRequest(null, null, null));

        Assert.NotNull(outcome);
        Assert.Null(outcome!.Turn.CostEstimate);
        Assert.Equal(TurnStatus.Completed, outcome.Turn.Status);
    }

    [Fact]
    public async Task realtime_complete_counts_output_seconds()
    {
        var f = CostEstimator.RealtimeTokensPerAudioSecond;

        // Output reported → both input and output priced; no "not reported" disclosure.
        var withOutput = await CompleteRealtimeTurn(Build(), "gpt-realtime", audioDurationMs: 10_000, outputAudioDurationMs: 5_000);
        Assert.NotNull(withOutput);
        Assert.Equal(10m * f / Million * 32.0m + 5m * f / Million * 64.0m, withOutput!.EstimatedUsd);
        Assert.DoesNotContain(withOutput.Assumptions,
            a => a.Contains("output audio duration not reported", StringComparison.OrdinalIgnoreCase));

        // Output absent → input still priced; output disclosed-unavailable (never silently a 0 output charge).
        var inputOnly = await CompleteRealtimeTurn(Build(), "gpt-realtime", audioDurationMs: 10_000);
        Assert.NotNull(inputOnly);
        Assert.Equal(10m * f / Million * 32.0m, inputOnly!.EstimatedUsd);
        Assert.Contains(inputOnly.Assumptions,
            a => a.Contains("output audio duration not reported", StringComparison.OrdinalIgnoreCase));
    }

    // === helpers ===

    private static Task<CostEstimate?> CompleteRealtimeTurn(
        SessionService svc, string realtimeModel, long audioDurationMs, long? outputAudioDurationMs = null) =>
        CompleteTurn(svc, InterpretationMode.Realtime, realtimeModel, audioDurationMs, outputAudioDurationMs);

    private static async Task<CostEstimate?> CompleteTurn(
        SessionService svc, InterpretationMode mode, string realtimeModel, long audioDurationMs, long? outputAudioDurationMs = null)
    {
        var session = await svc.CreateAsync(Req(mode, realtimeModel));
        var turnId = svc.CreateTurn(session.SessionId)!;
        var outcome = await svc.CompleteTurnAsync(
            session.SessionId, turnId, new CompleteTurnRequest(audioDurationMs, null, null, outputAudioDurationMs));
        return outcome!.Turn.CostEstimate;
    }

    private static CreateSessionRequest Req(InterpretationMode mode, string realtimeModel) =>
        new(Label: "rt-cost", Mode: mode, Direction: new LanguageDirection(LanguageCode.En, LanguageCode.Es),
            RealtimeModel: realtimeModel, TranslationModel: "gpt-5-nano");

    private static SessionService Build(Result<PricingOptions>? pricing = null)
    {
        var resolved = pricing ?? PricingLoader.Load(Path.Combine(AppContext.BaseDirectory, "pricing.json"));
        var clock = new FakeClock(T);
        var store = new SessionStore(clock);
        var summary = new SessionSummaryService(new MetricsAggregator(), clock);
        var writer = new SessionPersistenceWriter(Path.Combine(Path.GetTempPath(), "aiw-rt-cost-tests"));
        return new SessionService(
            store, summary, writer, clock,
            Options.Create(new DeepgramOptions()), Options.Create(new OpenAiTtsOptions()),
            resolved, new CostEstimator(resolved), NullLogger<SessionService>.Instance);
    }
}
