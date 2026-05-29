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
}

/// <summary>The in-memory result of ending a session: the ended session + the MUST-write outcome
/// (the controller maps the <see cref="Persist"/> result to a path or a safe persistence warning).</summary>
public sealed record EndSessionOutcome(InterpretationSession Session, Result<string> Persist);

/// <inheritdoc cref="ISessionService"/>
public sealed class SessionService : ISessionService
{
    private readonly SessionStore _store;
    private readonly SessionSummaryService _summaryService;
    private readonly SessionPersistenceWriter _writer;
    private readonly DeepgramOptions _deepgram;
    private readonly OpenAiTtsOptions _tts;
    private readonly Result<PricingOptions> _pricing;

    public SessionService(
        SessionStore store,
        SessionSummaryService summaryService,
        SessionPersistenceWriter writer,
        IOptions<DeepgramOptions> deepgram,
        IOptions<OpenAiTtsOptions> tts,
        Result<PricingOptions> pricing)
    {
        _store = store;
        _summaryService = summaryService;
        _writer = writer;
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

    private string PricingVersion() =>
        _pricing.IsSuccess ? _pricing.Value.Version ?? "unavailable" : "unavailable";
}
