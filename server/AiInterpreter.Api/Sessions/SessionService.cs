using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// Orchestrates the session-lifecycle use cases (ARCH-008) over the existing seams — keeps
/// <c>SessionsController</c> thin. <see cref="ISessionService"/> is the controller's test seam
/// (lesson §15). Returns nullable for unknown-id (the controller maps → 404 <c>session.not_found</c>).
/// </summary>
public interface ISessionService
{
    InterpretationSession Create(CreateSessionRequest request);
    InterpretationSession? Get(string sessionId);
    Task<EndSessionOutcome?> EndAsync(string sessionId, CancellationToken cancellationToken = default);
    SessionSummary? Summary(string sessionId);

    // Turn lifecycle (B.9c-ii). Null returns: CreateTurn → session unknown; AppendEvents/CompleteTurnAsync
    // → turn unknown (the controller checks session existence first for the right 404 code).
    string? CreateTurn(string sessionId);
    InterpretationTurn? AppendEvents(string sessionId, string turnId, IReadOnlyList<LatencyEvent> events);
    Task<CompleteTurnOutcome?> CompleteTurnAsync(
        string sessionId, string turnId, CompleteTurnRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The in-memory result of ending a session: the ended session + the MUST-write outcome
/// (the controller maps the <see cref="Persist"/> result to a path or a safe persistence warning).</summary>
public sealed record EndSessionOutcome(InterpretationSession Session, Result<string> Persist);

/// <summary>The in-memory result of completing a turn: the completed turn + the best-effort-write outcome.</summary>
public sealed record CompleteTurnOutcome(InterpretationTurn Turn, Result<string> Persist);

/// <inheritdoc cref="ISessionService"/>
public sealed class SessionService : ISessionService
{
    private readonly SessionStore _store;
    private readonly SessionSummaryService _summaryService;
    private readonly SessionPersistenceWriter _writer;
    private readonly IClock _clock;
    private readonly DeepgramOptions _deepgram;
    private readonly OpenAiTtsOptions _tts;
    private readonly Result<PricingOptions> _pricing;

    public SessionService(
        SessionStore store,
        SessionSummaryService summaryService,
        SessionPersistenceWriter writer,
        IClock clock,
        IOptions<DeepgramOptions> deepgram,
        IOptions<OpenAiTtsOptions> tts,
        Result<PricingOptions> pricing)
    {
        _store = store;
        _summaryService = summaryService;
        _writer = writer;
        _clock = clock;
        _deepgram = deepgram.Value;
        _tts = tts.Value;
        _pricing = pricing;
    }

    public InterpretationSession Create(CreateSessionRequest request)
    {
        // Assemble the full ProviderProfile: client supplies the selectable models; the rest comes from
        // the A.2 Options (providers fixed). (ARCH-009 example values are these Options defaults.)
        var profile = new ProviderProfile(
            RealtimeProvider: "openai",
            RealtimeModel: request.RealtimeModel,
            SttProvider: "deepgram",
            SttModel: _deepgram.Model,
            SttLanguage: _deepgram.Language,
            TranslationProvider: "openai",
            TranslationModel: request.TranslationModel,
            TtsProvider: "openai",
            TtsModel: _tts.Model,
            TtsVoice: _tts.Voice);

        var config = new SessionConfig(request.Mode, request.Direction, profile);
        return _store.Create(config, PricingVersion(), request.Label);
    }

    public InterpretationSession? Get(string sessionId) => _store.Get(sessionId);

    public async Task<EndSessionOutcome?> EndAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var ended = _store.End(sessionId); // stamps EndedAt; null if unknown
        if (ended is null)
        {
            return null;
        }

        // Compute the summary on the ended session, snapshot it in (so GET /{id} reflects it), then the
        // MUST write-on-end (ARCH-016). The summary-bearing session is what gets persisted.
        // SetSummary cannot return null here — End just resolved the id and the store has no eviction
        // path. (Double-end is unguarded: two End calls on the same id race; last writer wins on the
        // file. Acceptable for the single-node MVP.)
        var summary = _summaryService.Compute(ended);
        var finalSession = _store.SetSummary(sessionId, summary)!;
        var persist = await _writer.WriteAsync(finalSession, cancellationToken);

        return new EndSessionOutcome(finalSession, persist);
    }

    public SessionSummary? Summary(string sessionId)
    {
        var session = _store.Get(sessionId);
        return session is null ? null : _summaryService.Compute(session);
    }

    public string? CreateTurn(string sessionId) => _store.CreateTurn(sessionId)?.TurnId;

    public InterpretationTurn? AppendEvents(string sessionId, string turnId, IReadOnlyList<LatencyEvent> events) =>
        _store.UpdateTurn(sessionId, turnId, turn => turn with
        {
            LatencyEvents = [.. turn.LatencyEvents, .. events],
        });

    public async Task<CompleteTurnOutcome?> CompleteTurnAsync(
        string sessionId, string turnId, CompleteTurnRequest request, CancellationToken cancellationToken = default)
    {
        // Idempotent terminal finalize (C.4b): the WS terminal path can collide with this HTTP /complete, so
        // route through FinalizeTurn — an already-terminal turn is returned unchanged (no status overwrite).
        var result = _store.FinalizeTurn(sessionId, turnId, turn => turn with
        {
            AudioDurationMs = request.AudioDurationMs ?? turn.AudioDurationMs,
            Transcripts = request.Transcripts ?? turn.Transcripts,
            // /complete always produces a TERMINAL turn: honor a client-reported Failed, otherwise
            // Completed. A non-terminal client value (Ready/Recording/…) can't drag the turn backwards.
            Status = request.Status == TurnStatus.Failed ? TurnStatus.Failed : TurnStatus.Completed,
            CompletedAt = _clock.UtcNow,
        });
        if (result is null)
        {
            return null; // turn unknown (the controller already confirmed the session exists)
        }

        if (!result.Applied)
        {
            // Idempotent re-complete of an already-terminal turn: return the existing turn as a 200-shaped
            // success (no persistenceWarning), skipping the redundant best-effort re-persist (B.9c-ii contract).
            return new CompleteTurnOutcome(result.Turn, Result<string>.Success(string.Empty));
        }

        // Per-turn persistence is BEST-EFFORT (ARCH-016, vs /end's MUST): write the whole session file
        // and report success/failure; never crash the turn flow. Get is non-null here — FinalizeTurn just
        // resolved this session entry and the store has no eviction path.
        var persist = await _writer.WriteAsync(_store.Get(sessionId)!, cancellationToken);
        return new CompleteTurnOutcome(result.Turn, persist);
    }

    private string PricingVersion() =>
        _pricing.IsSuccess ? _pricing.Value.Version ?? "unavailable" : "unavailable";
}
