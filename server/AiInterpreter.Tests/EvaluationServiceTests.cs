using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// F.1 — EvaluationService unit tests (ARCH-009/015/019). Feature A: GET /phrases listing + POST /wer
// compute, with the SECURITY hypothesis-length cap pinned BEFORE WerCalculator.Compute (ARCH-019
// DP-matrix DoS) via an injected compute spy, plus the (Q1=a) turn-attach + best-effort persist +
// degrade-on-fail. Feature B (transcribe) lands in the F.1b cycle. No real keys (fake STT only).
public sealed class EvaluationServiceTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private static SessionConfig SampleConfig() => new(
        InterpretationMode.Cascade,
        new LanguageDirection(LanguageCode.En, LanguageCode.Es),
        new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5.4-nano", "openai", "gpt-4o-mini-tts", "es"));

    // Writes a temp phrases JSON file and returns a store loaded from it (mirrors B.6's content load).
    private static EvaluationPhraseStore PhraseStore(params EvaluationPhrase[] phrases)
    {
        var path = Path.Combine(Path.GetTempPath(), "aiw-eval-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(phrases, JsonDefaults.Options));
        return new EvaluationPhraseStore(path);
    }

    private static EvaluationPhrase Phrase(string id, string text, LanguageCode lang = LanguageCode.En) =>
        new(id, lang, text, "test");

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiw-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A data dir that cannot be created (a FILE sits where the dir should be) -> WriteAsync degrades to
    // Result.Failure (the SessionPersistenceTests technique).
    private static string BadDir()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "aiw-eval-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        return Path.Combine(blocker, "sessions");
    }

    private static EvaluationService Service(
        EvaluationPhraseStore phrases,
        SessionStore store,
        SessionPersistenceWriter writer,
        Func<string, string, string, WerResult>? compute = null)
    {
        var wer = new WerCalculator();
        return new EvaluationService(phrases, store, writer, compute ?? wer.Compute);
    }

    // ---- #1 GET /phrases ----

    [Fact]
    public void get_phrases_returns_loaded_phrases()
    {
        var clock = new FixedClock();
        var store = PhraseStore(Phrase("en-001", "hello world"), Phrase("es-001", "hola mundo", LanguageCode.Es));
        var service = Service(store, new SessionStore(clock), new SessionPersistenceWriter(NewTempDir()));

        var phrases = service.GetPhrases();

        Assert.Equal(2, phrases.Count);
        Assert.Contains(phrases, p => p.PhraseId == "en-001" && p.ReferenceText == "hello world");
    }

    // ---- #3 happy compute ----

    [Fact]
    public async Task wer_perfect_match_returns_zero()
    {
        var clock = new FixedClock();
        var store = PhraseStore(Phrase("en-001", "hello world"));
        var service = Service(store, new SessionStore(clock), new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.Equal(0.0, outcome.Result!.Wer);
    }

    // ---- ADD-1: sources the reference from the store by phraseId + correct Compute arg order ----

    [Fact]
    public async Task wer_sources_reference_from_phrase_store_arg_order()
    {
        var clock = new FixedClock();
        var store = PhraseStore(Phrase("en-001", "the quick brown fox"));
        var service = Service(store, new SessionStore(clock), new SessionPersistenceWriter(NewTempDir()));

        // Asymmetric: reference (from the store) != hypothesis (from the request). A swapped
        // Compute(reference, hypothesis) arg order, or sourcing the reference from the request, would
        // survive every perfect-match test but fail here — and silently corrupt every F.3 WER number.
        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", "the quick fox"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Equal("the quick brown fox", outcome.Result!.Reference); // sourced from the store
        Assert.Equal("the quick fox", outcome.Result.Hypothesis);       // from the request
        Assert.True(outcome.Result.Wer > 0.0);                          // one deletion ("brown")
    }

    // ---- #4 unknown phrase -> not found, NO compute ----

    [Fact]
    public async Task wer_unknown_phrase_id_returns_not_found_without_compute()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "missing", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.PhraseNotFound, outcome.Status);
        Assert.Equal(0, calls);
    }

    // ---- #5 SECURITY: over-cap -> invalid BEFORE compute (the DoS pin) ----

    [Fact]
    public async Task wer_hypothesis_over_cap_is_invalid_before_compute()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var oversized = new string('a', EvaluationService.MaxHypothesisChars + 1);
        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", oversized), default);

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal(0, calls); // the DP matrix was never allocated
    }

    // ---- #6 at-cap boundary computes (inclusive) ----

    [Fact]
    public async Task wer_at_cap_boundary_computes()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var atCap = new string('a', EvaluationService.MaxHypothesisChars);
        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", atCap), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Equal(1, calls);
    }

    // ---- #7 turnId present -> attach + persist (Q1=a) ----

    [Fact]
    public async Task wer_with_turn_id_attaches_and_persists()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, turn.TurnId, "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.NotNull(outcome.Persist);
        Assert.True(outcome.Persist!.IsSuccess, outcome.Persist.Error);
        var stored = store.Get(session.SessionId)!.Turns.Single(t => t.TurnId == turn.TurnId);
        Assert.NotNull(stored.WerResult);
        Assert.Equal(0.0, stored.WerResult!.Wer);
    }

    // ---- #8 unknown turnId -> not found ----

    [Fact]
    public async Task wer_with_unknown_turn_id_returns_turn_not_found()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, "turn_missing", "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.TurnNotFound, outcome.Status);
    }

    // ---- #9 persist failure degrades (still returns the WerResult) ----

    [Fact]
    public async Task wer_persist_failure_degrades_not_throws()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(BadDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, turn.TurnId, "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.NotNull(outcome.Persist);
        Assert.False(outcome.Persist!.IsSuccess); // degraded -> the controller surfaces a persistenceWarning
    }

    // ---- #10 no turnId -> compute only, store untouched ----

    [Fact]
    public async Task wer_without_turn_id_does_not_write_store()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, null, "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Null(outcome.Persist);
        var stored = store.Get(session.SessionId)!.Turns.Single(t => t.TurnId == turn.TurnId);
        Assert.Null(stored.WerResult); // untouched
    }

    // A compute spy that counts invocations and returns a stand-in WerResult. The stand-in WER (0.0 over
    // a 1-word reference) is irrelevant to the spy's tests — they assert the CALL COUNT (compute ran or
    // didn't), not the value (the real WerCalculator is exercised by the non-spy tests #3 / ADD-1).
    private static Func<string, string, string, WerResult> SpyCompute(Action onCall) =>
        (phraseId, reference, hypothesis) =>
        {
            onCall();
            return new WerResult(phraseId, reference, hypothesis, reference, hypothesis, 0, 0, 0, 1, 0.0);
        };
}
