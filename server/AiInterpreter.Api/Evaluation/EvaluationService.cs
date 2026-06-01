using System.Text;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Metrics;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Sessions;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Evaluation;

/// <summary>
/// Evaluation orchestration (ARCH-009/015) over the landed B.6 <see cref="EvaluationPhraseStore"/> +
/// <see cref="WerCalculator"/>. Thin-controller collaborator (ARCH-008). F.1a covers phrase listing +
/// WER compute (with the ARCH-019 hypothesis-length cap before the DP allocation, and the Q1=(a)
/// turn-attach + best-effort persist); F.1b adds STT-only transcribe.
///
/// <para><b>Security (ARCH-019):</b> the hypothesis cap is this service's FIRST action in
/// <see cref="ComputeWerAsync"/> — the service is the single WER chokepoint, so an oversized hypothesis
/// can never reach <see cref="WerCalculator.Compute"/>'s n×m DP matrix (a memory-DoS surface). The WER
/// compute is routed through an injectable delegate so the cap-before-compute property is unit-pinnable.</para>
/// </summary>
public sealed class EvaluationService
{
    /// <summary>Max hypothesis length (chars, inclusive) accepted by <see cref="ComputeWerAsync"/>
    /// before the DP allocation (ARCH-019). ~500 words; over this → <c>evaluation.invalid_phrase</c>.</summary>
    public const int MaxHypothesisChars = 2000;

    private const string SttProviderLabel = "deepgram";

    private readonly EvaluationPhraseStore _phrases;
    private readonly SessionStore _store;
    private readonly SessionPersistenceWriter _writer;
    private readonly Func<string, string, string, WerResult> _compute;
    private readonly ISttProvider _stt;
    private readonly LatencyEventFactory _latencyFactory;
    private readonly IClock _clock;
    private readonly DeepgramOptions _deepgramOptions;

    public EvaluationService(
        EvaluationPhraseStore phrases, WerCalculator wer, SessionStore store, SessionPersistenceWriter writer,
        ISttProvider stt, LatencyEventFactory latencyFactory, IClock clock, IOptions<DeepgramOptions> deepgramOptions)
        : this(phrases, store, writer, wer.Compute, stt, latencyFactory, clock, deepgramOptions.Value)
    {
    }

    // Test seam (InternalsVisibleTo): inject the WER compute delegate so a spy can prove the cap runs
    // before Compute (no DP matrix allocated). Production uses the public ctor (WerCalculator.Compute).
    internal EvaluationService(
        EvaluationPhraseStore phrases,
        SessionStore store,
        SessionPersistenceWriter writer,
        Func<string, string, string, WerResult> compute,
        ISttProvider stt,
        LatencyEventFactory latencyFactory,
        IClock clock,
        DeepgramOptions deepgramOptions)
    {
        _phrases = phrases;
        _store = store;
        _writer = writer;
        _compute = compute;
        _stt = stt;
        _latencyFactory = latencyFactory;
        _clock = clock;
        _deepgramOptions = deepgramOptions;
    }

    /// <summary>The scripted evaluation phrases (ARCH-015); empty when the store failed to load
    /// (degrade-don't-crash) — the caller never surfaces the store's <c>LoadError</c> (ARCH-019).</summary>
    public IReadOnlyList<EvaluationPhrase> GetPhrases() => _phrases.Phrases;

    /// <summary>
    /// Computes WER for <paramref name="request"/> against the scripted reference. Caps the hypothesis
    /// length BEFORE the DP allocation (ARCH-019). When <c>TurnId</c> is supplied, attaches the result to
    /// the turn (server-authoritative) and best-effort persists (ARCH-016 / Q1=a); a persist failure
    /// degrades (the result is still returned) rather than crashing.
    /// </summary>
    public async Task<EvaluationWerOutcome> ComputeWerAsync(WerRequest request, CancellationToken cancellationToken)
    {
        // SECURITY (ARCH-019): cap the hypothesis FIRST — the calculator allocates an n×m DP matrix, so
        // an unbounded hypothesis is a memory-DoS surface. The service is the single WER chokepoint.
        if ((request.Hypothesis?.Length ?? 0) > MaxHypothesisChars)
        {
            return EvaluationWerOutcome.Invalid();
        }

        // No identifier at all (neither an explicit reference nor a phraseId) is an invalid request, not a
        // "phrase not found" — preserve the pre-090 no-identifier→400 contract now that PhraseId is optional.
        if (request.Reference is null && string.IsNullOrEmpty(request.PhraseId))
        {
            return EvaluationWerOutcome.Invalid();
        }

        string reference;
        string phraseIdForResult;

        if (request.Reference is not null)
        {
            // G.4 (090) explicit-reference path (the measurement-workbench/soak use case) — reference-wins
            // over phraseId, bypassing the store. SECURITY (ARCH-019, §27 extended): the reference is now an
            // EXTERNAL input — the n-dimension of the n×m DP matrix that §27 capped only on m (hypothesis) —
            // so it MUST be bounded (cap) + non-empty (the calculator throws on an empty normalized
            // reference) BEFORE the allocation. Both rejects → the same evaluation.invalid_phrase 400.
            if (string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Length > MaxHypothesisChars)
            {
                return EvaluationWerOutcome.Invalid();
            }

            reference = request.Reference;
            phraseIdForResult = request.PhraseId ?? string.Empty;
        }
        else
        {
            // Default path (unchanged): the reference is sourced from the STORE by phraseId, never from the
            // request (the neither-identifier guard above guarantees PhraseId is non-empty here).
            var phrase = _phrases.GetById(request.PhraseId!);
            if (phrase is null)
            {
                return EvaluationWerOutcome.PhraseNotFound();
            }

            reference = phrase.ReferenceText;
            phraseIdForResult = request.PhraseId!;
        }

        // Arg order is (phraseId, reference, hypothesis) — ADD-1 pins both.
        WerResult wer;
        try
        {
            wer = _compute(phraseIdForResult, reference, request.Hypothesis ?? string.Empty);
        }
        catch (ArgumentException)
        {
            // A reference that normalizes to EMPTY (e.g. punctuation-only "..." — WerCalculator strips \p{P})
            // is a degenerate CALLER input, not a server fault: the calculator throws its empty-reference
            // precondition (ARCH-015). Convert it to the same evaluation.invalid_phrase 400 as the
            // whitespace/cap rejects — never a 500. The reference is already capped, so the throw precedes the
            // n×m DP allocation (no DoS). (security-reviewer 090: client-input must not surface as a 500.)
            return EvaluationWerOutcome.Invalid();
        }

        if (string.IsNullOrEmpty(request.TurnId))
        {
            return EvaluationWerOutcome.Computed(wer, persist: null);
        }

        // Q1=(a): attach to the turn + best-effort persist so the WER reaches the session JSON for F.3.
        // UpdateTurn is unconditional (FinalizeTurn would refuse an already-terminal turn); null =>
        // unknown session/turn (404 — don't silently drop a persist target). F.4: mark IsEvaluation in the
        // SAME transform — a WER-scored turn IS an evaluation turn (SessionSummaryService excludes it from
        // the per-mode comparison). The marker and the score are set atomically, never one without the other.
        var updated = _store.UpdateTurn(
            request.SessionId, request.TurnId, turn => turn with { WerResult = wer, IsEvaluation = true });
        if (updated is null)
        {
            return EvaluationWerOutcome.TurnNotFound();
        }

        var session = _store.Get(request.SessionId);
        if (session is null)
        {
            // The session vanished between the attach and this read. No eviction path exists today, but
            // don't depend on that structural assumption: the WER is already computed + attached
            // in-memory, so report it with a degraded persist rather than NRE-ing (degrade-don't-crash).
            return EvaluationWerOutcome.Computed(
                wer, Result<string>.Failure("persistence.failed: session unavailable"));
        }

        var persist = await _writer.WriteAsync(session, cancellationToken); // degrades, never throws (lesson §11)
        return EvaluationWerOutcome.Computed(wer, persist);
    }

    /// <summary>
    /// Runs STT-only over an uploaded blob (ARCH-009 transcribe) and returns the hypothesis + provider
    /// identity + latency. Wraps the blob as a single <see cref="AudioFrame"/> through the SAME
    /// <see cref="ISttProvider"/> the cascade uses (the derived container encoding routes the pre-recorded
    /// REST path, C.1); collects the <see cref="SttFinal"/> text (joined across multiple finals) into the
    /// hypothesis. NO translation/TTS (structural — this service has no such dependency). Latency events
    /// are stamped on REAL arrival only (<c>stt.first_partial</c> if a partial arrives, <c>stt.final</c>
    /// on the first final) — never synthesized (forbidden-pattern #3). Stateless: never persists the audio
    /// nor writes the store (invariant #3). Upload validation is the controller's job (it runs first).
    /// </summary>
    public async Task<EvaluationTranscribeOutcome> TranscribeAsync(
        byte[] audio, string encoding, LanguageCode language, string sessionId, CancellationToken cancellationToken)
    {
        var origin = _clock.UtcNow;
        var request = new SttRequest(
            OneFrameAsync(audio, cancellationToken),
            $"audio/{encoding}",
            encoding,
            SampleRate: 0, // pre-recorded REST: Deepgram auto-detects the container; sample rate is moot
            language,
            language.ToString().ToLowerInvariant(),
            sessionId,
            TurnId: "evaluation"); // transcribe is turnless — a placeholder label, never a store key

        var latencyEvents = new List<LatencyEvent>();
        var hypothesis = new StringBuilder();
        var firstPartialStamped = false;
        var finalStamped = false;

        await foreach (var ev in _stt.TranscribeAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            switch (ev)
            {
                case SttPartial when !firstPartialStamped:
                    firstPartialStamped = true;
                    latencyEvents.Add(Stamp(LatencyEventNames.SttFirstPartial, origin));
                    break;

                case SttFinal final:
                    if (!finalStamped)
                    {
                        finalStamped = true;
                        latencyEvents.Add(Stamp(LatencyEventNames.SttFinal, origin));
                    }

                    // Deepgram REST may segment a phrase into >1 final; JOIN them (single space), never
                    // last-only (which would silently truncate the hypothesis → wrong WER).
                    if (!string.IsNullOrEmpty(final.Text))
                    {
                        if (hypothesis.Length > 0)
                        {
                            hypothesis.Append(' ');
                        }

                        hypothesis.Append(final.Text);
                    }

                    break;

                case SttFailed failed:
                    return EvaluationTranscribeOutcome.Failed(failed.Error);
            }
        }

        return EvaluationTranscribeOutcome.Ok(new TranscribeResponse(
            hypothesis.ToString(), SttProviderLabel, _deepgramOptions.Model, latencyEvents));
    }

    private LatencyEvent Stamp(string name, DateTimeOffset origin) =>
        _latencyFactory.Stamp(
            name, LatencyStage.Stt, ClockSource.Server, origin,
            new Dictionary<string, string> { ["provider"] = SttProviderLabel });

    // Wraps the uploaded blob as a one-element AudioFrame stream (mirrors CascadeOrchestrator.OneFrameAsync).
    private async IAsyncEnumerable<AudioFrame> OneFrameAsync(
        byte[] audio, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return new AudioFrame(audio, _clock.UtcNow);
    }
}
