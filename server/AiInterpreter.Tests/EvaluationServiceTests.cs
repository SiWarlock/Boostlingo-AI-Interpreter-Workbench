using System.Runtime.CompilerServices;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.Fakes;
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
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "es"));

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
        Func<string, string, string, WerResult>? compute = null,
        ISttProvider? stt = null)
    {
        var wer = new WerCalculator();
        // The latency-factory clock is internal to the helper — F.1a call sites don't pass one (the WER
        // tests don't assert latency timestamps; the transcribe tests assert latencyEvents non-empty).
        var clock = new FixedClock();
        return new EvaluationService(
            phrases, store, writer, compute ?? wer.Compute,
            stt ?? new FakeSttProvider(), new LatencyEventFactory(clock), clock, new DeepgramOptions());
    }

    // A scripted STT double yielding exactly the supplied events (ADD-2: multiple SttFinals).
    private sealed class ScriptedSttProvider(params SttEvent[] events) : ISttProvider
    {
        public async IAsyncEnumerable<SttEvent> TranscribeAsync(
            SttRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var ev in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return ev;
            }
        }
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

    // ---- F.4: the /wer attach marks the turn IsEvaluation atomically with the WerResult ----

    // A turnId'd WER compute flips IsEvaluation=true on the SAME read-modify-write that attaches the
    // WerResult — so a WER-scored turn is marked an evaluation turn at the defining moment (then
    // SessionSummaryService excludes it from the per-mode comparison). The marker and the score are
    // set in one transform — never one without the other.
    [Fact]
    public async Task wer_with_turn_id_marks_turn_is_evaluation()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        Assert.False(turn.IsEvaluation); // precondition: a freshly created turn is not an eval turn
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, turn.TurnId, "en-001", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        var stored = store.Get(session.SessionId)!.Turns.Single(t => t.TurnId == turn.TurnId);
        Assert.True(stored.IsEvaluation);   // marked at the eval-defining moment...
        Assert.NotNull(stored.WerResult);   // ...atomically with the score
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
        Assert.Null(stored.WerResult);       // untouched
        Assert.False(stored.IsEvaluation);   // F.4: the marker is set ONLY on the turnId attach path, not on bare compute
    }

    // ---- G.4 (090) — additive explicit-reference path {reference, hypothesis} ----

    // #1 — an explicit reference is scored by the canonical WerCalculator (NOT sourced from the store); the
    // asymmetric reference != hypothesis != store-text pins the arg order (reference is _compute arg #2).
    [Fact]
    public async Task explicit_reference_computes_against_supplied_reference()
    {
        var clock = new FixedClock();
        string? capturedPhraseId = null, capturedReference = null, capturedHypothesis = null;
        var service = Service(
            PhraseStore(Phrase("en-001", "stored reference text")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()),
            compute: (phraseId, reference, hypothesis) =>
            {
                capturedPhraseId = phraseId;
                capturedReference = reference;
                capturedHypothesis = hypothesis;
                return new WerResult(phraseId, reference, hypothesis, reference, hypothesis, 1, 0, 0, 2, 0.5);
            });

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, "hello word", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Equal("hello world", capturedReference);   // the supplied reference (arg #2), not the store text
        Assert.Equal("hello word", capturedHypothesis);   // arg #3
        Assert.Equal(string.Empty, capturedPhraseId);     // no phraseId on the explicit path
        Assert.Equal("hello world", outcome.Result!.Reference);
    }

    // #2 (SECURITY) — an over-cap reference (the n-dimension of the n×m DP matrix) is rejected BEFORE compute.
    // §27 only capped the hypothesis (m); the external reference reopens the n-dimension DoS, so it is bounded too.
    [Fact]
    public async Task explicit_reference_over_cap_rejected_before_compute()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var oversized = new string('a', EvaluationService.MaxHypothesisChars + 1);
        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, "hi", oversized), default);

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Equal(0, calls); // the n-dimension never reached the DP allocation
    }

    // #3 — a whitespace/empty reference on the explicit branch is rejected 400 BEFORE compute (never the
    // calculator's ArgumentException on an empty normalized reference, ARCH-015).
    [Fact]
    public async Task explicit_reference_empty_rejected_before_compute()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, "hello", "   "), default);

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Equal(0, calls);
    }

    // #4 — the hypothesis cap (the m-dimension, §27) still applies on the explicit path (checked first).
    [Fact]
    public async Task hypothesis_cap_still_applies_on_explicit_path()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var oversizedHyp = new string('a', EvaluationService.MaxHypothesisChars + 1);
        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, oversizedHyp, "a valid reference"), default);

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Equal(0, calls);
    }

    // #5 — strictly additive: with NO Reference (default null), the store-by-phraseId path is byte-identical
    // (reference sourced from the store, not the request).
    [Fact]
    public async Task phraseid_path_unchanged_when_no_reference()
    {
        var clock = new FixedClock();
        var store = PhraseStore(Phrase("en-001", "the quick brown fox"));
        var service = Service(store, new SessionStore(clock), new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", "the quick fox"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Equal("the quick brown fox", outcome.Result!.Reference); // from the store
        Assert.True(outcome.Result.Wer > 0.0);
    }

    // #6 (⭐ soak-correctness) — explicit reference + NO turnId: compute-and-return only. The session's REAL
    // turn stays un-attached + NOT marked IsEvaluation (else it would be excluded from the per-mode
    // comparison, §29/§39). This is the soak path — its measured turns must remain IN the comparison.
    [Fact]
    public async Task explicit_reference_no_turn_id_no_attach_no_eval_marking()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, null, null, "hello word", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Null(outcome.Persist);
        var stored = store.Get(session.SessionId)!.Turns.Single(t => t.TurnId == turn.TurnId);
        Assert.Null(stored.WerResult);
        Assert.False(stored.IsEvaluation);
    }

    // #7 — the optional turnId-attach still works on the explicit branch (orthogonal, unchanged behavior).
    [Fact]
    public async Task explicit_reference_with_turn_id_attaches_and_marks_eval()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var service = Service(PhraseStore(Phrase("en-001", "hello world")), store,
            new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest(session.SessionId, turn.TurnId, null, "hello world", "hello world"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        var stored = store.Get(session.SessionId)!.Turns.Single(t => t.TurnId == turn.TurnId);
        Assert.NotNull(stored.WerResult);
        Assert.True(stored.IsEvaluation);
    }

    // #8 — precedence (Q1 = reference-wins): when BOTH a valid phraseId and an explicit reference are present,
    // the explicit reference is scored (the store is NOT consulted).
    [Fact]
    public async Task both_phraseid_and_reference_reference_wins()
    {
        var clock = new FixedClock();
        var store = PhraseStore(Phrase("en-001", "stored reference text"));
        var service = Service(store, new SessionStore(clock), new SessionPersistenceWriter(NewTempDir()));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, "en-001", "explicit hyp", "explicit reference"), default);

        Assert.Equal(EvaluationWerStatus.Computed, outcome.Status);
        Assert.Equal("explicit reference", outcome.Result!.Reference); // reference-wins
    }

    // #9 — neither phraseId nor reference supplied → Invalid (400), NOT PhraseNotFound (404). Preserves the
    // pre-slice "no identifier → 400" contract once PhraseId is relaxed to optional (strictly-additive honesty;
    // 404 "phrase not found" is semantically wrong for "you didn't specify one").
    [Fact]
    public async Task neither_phraseid_nor_reference_invalid_400()
    {
        var clock = new FixedClock();
        var calls = 0;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()), compute: SpyCompute(() => calls++));

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, "hello"), default); // no phraseId, no reference

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Equal(0, calls);
    }

    // #10 (security-reviewer 090) — a punctuation-only reference normalizes to empty (WerCalculator strips
    // \p{P}), which the calculator rejects via ArgumentException; the service converts it to the same 400 as
    // whitespace/cap, NEVER a 500. Uses the REAL WerCalculator (the throw can't be reproduced with the spy).
    [Fact]
    public async Task explicit_reference_punctuation_only_invalid_400()
    {
        var clock = new FixedClock();
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir())); // real WerCalculator (no compute override)

        var outcome = await service.ComputeWerAsync(
            new WerRequest("session_x", null, null, "hello", "..."), default);

        Assert.Equal(EvaluationWerStatus.Invalid, outcome.Status);
        Assert.Null(outcome.Result);
    }

    // ---- Feature B: transcribe (STT-only) ----

    // #11 — a successful STT yields the hypothesis from its final + provider/model identity + latency.
    [Fact]
    public async Task transcribe_returns_hypothesis_from_stt_final()
    {
        var clock = new FixedClock();
        // STT-only is STRUCTURAL: EvaluationService has no ITranslationProvider/ITtsProvider dependency,
        // so no translation/TTS stage can be reached (nothing to spy — the absence is the guarantee).
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()),
            stt: new FakeSttProvider(FakeSttBehavior.SuccessWithPartials, final: "hello world"));

        var outcome = await service.TranscribeAsync(
            new byte[] { 1, 2, 3, 4 }, "webm", LanguageCode.En, "session_x", default);

        Assert.Equal(EvaluationTranscribeStatus.Ok, outcome.Status);
        Assert.Equal("hello world", outcome.Response!.Hypothesis);
        Assert.Equal("deepgram", outcome.Response.SttProvider);
        Assert.Equal("nova-3", outcome.Response.SttModel); // DeepgramOptions default
        // Stamped on real arrival (no synthesis): a first_partial (partials arrive) + exactly one final.
        var names = outcome.Response.LatencyEvents.Select(e => e.Name).ToList();
        Assert.Contains(LatencyEventNames.SttFirstPartial, names);
        Assert.Single(names, LatencyEventNames.SttFinal);
    }

    // ADD-2 — Deepgram REST can segment a phrase into >1 final; the hypothesis JOINS them (single space),
    // never last-only (which would silently truncate the hypothesis → wrong WER). (lesson §16 family.)
    [Fact]
    public async Task transcribe_joins_multiple_stt_finals()
    {
        var clock = new FixedClock();
        var t = clock.UtcNow;
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()),
            stt: new ScriptedSttProvider(
                new SttStarted(t), new SttFinal("hello", t), new SttFinal("world", t)));

        var outcome = await service.TranscribeAsync(
            new byte[] { 1 }, "webm", LanguageCode.En, "session_x", default);

        Assert.Equal(EvaluationTranscribeStatus.Ok, outcome.Status);
        Assert.Equal("hello world", outcome.Response!.Hypothesis); // joined, not "world"
        // stt.final stamped ONCE on the first final, not per-final (consistent with the cascade idiom).
        Assert.Single(outcome.Response.LatencyEvents, e => e.Name == LatencyEventNames.SttFinal);
    }

    // #14 — an STT failure maps to a sanitized outcome carrying the preserved provider error code.
    [Fact]
    public async Task transcribe_stt_failed_surfaces_provider_error()
    {
        var clock = new FixedClock();
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), new SessionStore(clock),
            new SessionPersistenceWriter(NewTempDir()),
            stt: new FakeSttProvider(FakeSttBehavior.PartialsThenError));

        var outcome = await service.TranscribeAsync(
            new byte[] { 1 }, "webm", LanguageCode.En, "session_x", default);

        Assert.Equal(EvaluationTranscribeStatus.SttFailed, outcome.Status);
        Assert.NotNull(outcome.Error);
        Assert.Equal("stt.upstream_unavailable", outcome.Error!.Code); // preserved, not re-derived
    }

    // #15 — transcribe is stateless: it neither persists the audio nor touches the session store.
    [Fact]
    public async Task transcribe_does_not_persist_audio_or_touch_store()
    {
        var clock = new FixedClock();
        var store = new SessionStore(clock);
        var session = store.Create(SampleConfig(), "v-test");
        var turn = store.CreateTurn(session.SessionId)!;
        var dataDir = NewTempDir();
        var service = Service(
            PhraseStore(Phrase("en-001", "hello world")), store, new SessionPersistenceWriter(dataDir),
            stt: new FakeSttProvider(FakeSttBehavior.SuccessWithPartials));

        await service.TranscribeAsync(new byte[] { 1, 2, 3 }, "webm", LanguageCode.En, session.SessionId, default);

        Assert.Empty(Directory.GetFiles(dataDir));                  // no session JSON written
        var stored = store.Get(session.SessionId)!;
        Assert.Single(stored.Turns);                                // no new turn created
        Assert.Null(stored.Turns[0].WerResult);                     // existing turn untouched
        Assert.Equal(turn.TurnId, stored.Turns[0].TurnId);
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
