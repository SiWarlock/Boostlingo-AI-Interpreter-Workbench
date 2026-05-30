using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;

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

    private readonly EvaluationPhraseStore _phrases;
    private readonly SessionStore _store;
    private readonly SessionPersistenceWriter _writer;
    private readonly Func<string, string, string, WerResult> _compute;

    public EvaluationService(
        EvaluationPhraseStore phrases, WerCalculator wer, SessionStore store, SessionPersistenceWriter writer)
        : this(phrases, store, writer, wer.Compute)
    {
    }

    // Test seam (InternalsVisibleTo): inject the WER compute delegate so a spy can prove the cap runs
    // before Compute (no DP matrix allocated). Production uses the public ctor (WerCalculator.Compute).
    internal EvaluationService(
        EvaluationPhraseStore phrases,
        SessionStore store,
        SessionPersistenceWriter writer,
        Func<string, string, string, WerResult> compute)
    {
        _phrases = phrases;
        _store = store;
        _writer = writer;
        _compute = compute;
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

        var phrase = string.IsNullOrEmpty(request.PhraseId) ? null : _phrases.GetById(request.PhraseId);
        if (phrase is null)
        {
            return EvaluationWerOutcome.PhraseNotFound();
        }

        // Reference is sourced from the STORE by phraseId (never from the request); arg order is
        // (phraseId, reference, hypothesis) — ADD-1 pins both.
        var wer = _compute(request.PhraseId, phrase.ReferenceText, request.Hypothesis ?? string.Empty);

        if (string.IsNullOrEmpty(request.TurnId))
        {
            return EvaluationWerOutcome.Computed(wer, persist: null);
        }

        // Q1=(a): attach to the turn + best-effort persist so the WER reaches the session JSON for F.3.
        // UpdateTurn is unconditional (FinalizeTurn would refuse an already-terminal turn); null =>
        // unknown session/turn (404 — don't silently drop a persist target).
        var updated = _store.UpdateTurn(request.SessionId, request.TurnId, turn => turn with { WerResult = wer });
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
}
