using AiInterpreter.Api.Common;
using AiInterpreter.Api.Cost;
using AiInterpreter.Api.Providers.Deepgram;
using AiInterpreter.Api.Providers.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// Orchestrates the session-lifecycle use cases (ARCH-008) over the existing seams — keeps
/// <c>SessionsController</c> thin. <see cref="ISessionService"/> is the controller's test seam
/// (lesson §15). Returns nullable for unknown-id (the controller maps → 404 <c>session.not_found</c>).
/// </summary>
public interface ISessionService
{
    // Async because creating a session FLUSHES any prior un-ended (abandoned/refreshed) session through the
    // EndAsync finalize+persist seam first (Flow H, ARCH-017) — so an abandoned session still produces its
    // JSON artifact. The flush degrades on persist failure and never blocks the new session (lesson §11).
    Task<InterpretationSession> CreateAsync(CreateSessionRequest request);
    InterpretationSession? Get(string sessionId);
    Task<EndSessionOutcome?> EndAsync(string sessionId, CancellationToken cancellationToken = default);
    SessionSummary? Summary(string sessionId);

    // Turn lifecycle (B.9c-ii). Null returns: CreateTurn → session unknown; AppendEvents/CompleteTurnAsync
    // → turn unknown (the controller checks session existence first for the right 404 code).
    string? CreateTurn(string sessionId);
    InterpretationTurn? AppendEvents(string sessionId, string turnId, IReadOnlyList<LatencyEvent> events);
    Task<CompleteTurnOutcome?> CompleteTurnAsync(
        string sessionId, string turnId, CompleteTurnRequest request, CancellationToken cancellationToken = default);

    // Mode switch (050 / Flow G, Finding 2c): validate the target mode against the enum allowlist
    // (off-enum/blank → InvalidMode → 400), update the session's CurrentMode + record a
    // ModeTransitionEvent; NotFound if the session id is unknown. Synchronous — the in-memory swap is what
    // the 2c fix needs (CreateTurn stamps from CurrentMode); the transition persists at the next /complete
    // or /end write, like the rest of the in-memory store.
    SwitchModeOutcome SwitchMode(string sessionId, string? requestedMode);
}

/// <summary>The in-memory result of ending a session: the ended session + the MUST-write outcome
/// (the controller maps the <see cref="Persist"/> result to a path or a safe persistence warning).</summary>
public sealed record EndSessionOutcome(InterpretationSession Session, Result<string> Persist);

/// <summary>The in-memory result of completing a turn: the completed turn + the best-effort-write outcome.</summary>
public sealed record CompleteTurnOutcome(InterpretationTurn Turn, Result<string> Persist);

/// <summary>The outcome of a mode-switch request (050 / Flow G): a <see cref="SwitchModeStatus"/> the
/// controller maps to 200/400/404 + the updated session (non-null only on
/// <see cref="SwitchModeStatus.Ok"/>).</summary>
public sealed record SwitchModeOutcome(SwitchModeStatus Status, InterpretationSession? Session);

/// <summary>Mode-switch result discriminator: <see cref="Ok"/> (switched), <see cref="NotFound"/> (unknown
/// session → 404), <see cref="InvalidMode"/> (target not in the enum allowlist → 400).</summary>
public enum SwitchModeStatus { Ok, NotFound, InvalidMode }

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
    private readonly CostEstimator _costEstimator;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        SessionStore store,
        SessionSummaryService summaryService,
        SessionPersistenceWriter writer,
        IClock clock,
        IOptions<DeepgramOptions> deepgram,
        IOptions<OpenAiTtsOptions> tts,
        Result<PricingOptions> pricing,
        CostEstimator costEstimator,
        ILogger<SessionService> logger)
    {
        _store = store;
        _summaryService = summaryService;
        _writer = writer;
        _clock = clock;
        _deepgram = deepgram.Value;
        _tts = tts.Value;
        _pricing = pricing;
        _costEstimator = costEstimator;
        _logger = logger;
    }

    public async Task<InterpretationSession> CreateAsync(CreateSessionRequest request)
    {
        // Flow H (ARCH-017): flush any prior un-ended (refreshed/abandoned) session FIRST — before the new
        // session is registered, so it can't flush itself — reusing the EndAsync finalize+persist seam.
        await FlushStaleSessionsAsync();

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

    // Flow-H stale-session flush (E.5): end + persist every prior un-ended session via the EndAsync seam.
    private async Task FlushStaleSessionsAsync()
    {
        // ActiveSessionIds() is a materialized snapshot, so ending each (which swaps EndedAt) mid-iteration
        // is safe. Reuse EndAsync (no duplicated persistence) — it degrades on a persist failure (returns a
        // failed Result, never throws), so a flush can never block the new session.
        foreach (var staleId in _store.ActiveSessionIds())
        {
            // CancellationToken.None: the abandoned session's owed artifact write must COMPLETE and must not
            // be abortable by the (possibly-cancelled) new request — else the stale session would end in
            // memory with no JSON artifact, the exact half-state this flush exists to prevent.
            var outcome = await EndAsync(staleId, CancellationToken.None);

            // Null-safe: EndAsync returns nullable, though an id from ActiveSessionIds always resolves (the
            // store has no eviction). A persist failure degrades (lesson §11) — EndedAt already flipped, only
            // the disk write failed — so log it (single-lined, §13) + continue; never block the new session.
            if (outcome is not null && !outcome.Persist.IsSuccess)
            {
                _logger.LogWarning("Stale-session flush persist failed for {SessionId}: {Error}",
                    staleId, outcome.Persist.Error?.ReplaceLineEndings(" "));
            }
        }
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

    public SwitchModeOutcome SwitchMode(string sessionId, string? requestedMode)
    {
        var session = _store.Get(sessionId);
        if (session is null)
        {
            return new SwitchModeOutcome(SwitchModeStatus.NotFound, null);
        }

        if (!TryParseMode(requestedMode, out var target))
        {
            return new SwitchModeOutcome(SwitchModeStatus.InvalidMode, null);
        }

        // No-op switch (target == current): idempotent, NO transition recorded (Q2). Build the transition
        // only on an actual change; the store swaps CurrentMode either way (the same value on a no-op is
        // harmless) and records iff transition is non-null — one path. fromMode/direction are read from the
        // live session just before the store gate: a benign single-user/between-turns TOCTOU (flagged Step 9).
        var current = session.Config;
        ModeTransitionEvent? transition = target == current.CurrentMode
            ? null
            : new ModeTransitionEvent(
                TransitionId: GenerateTransitionId(),
                FromMode: current.CurrentMode,
                ToMode: target,
                DirectionAtTransition: current.Direction,
                OccurredAt: _clock.UtcNow,
                ClockSource: ClockSource.Server,
                TriggeredByTurnId: null);

        // Non-null: Get just resolved the id and the store has no eviction (the EndAsync/SetSummary precedent).
        var updated = _store.SwitchMode(sessionId, target, transition)!;
        return new SwitchModeOutcome(SwitchModeStatus.Ok, updated);
    }

    // Parse + allowlist the requested mode (lesson §27 chokepoint): reject null/blank, non-enum strings, and
    // out-of-range numeric strings (Enum.TryParse accepts "99" as an UNDEFINED value, so Enum.IsDefined gates
    // it). Lenient case (frontend always sends lowercase) — harmless, the wire value is the only producer.
    private static bool TryParseMode(string? raw, out InterpretationMode mode)
    {
        mode = default;
        return !string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse(raw, ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }

    // transition_<short-id>: a GUID segment, like the store's session/turn ids (not path-bound, but kept
    // consistent with the ^[A-Za-z0-9_-]+$ id form).
    private static string GenerateTransitionId() => "transition_" + Guid.NewGuid().ToString("N")[..8];

    public InterpretationTurn? AppendEvents(string sessionId, string turnId, IReadOnlyList<LatencyEvent> events) =>
        _store.UpdateTurn(sessionId, turnId, turn => turn with
        {
            LatencyEvents = [.. turn.LatencyEvents, .. events],
        });

    public async Task<CompleteTurnOutcome?> CompleteTurnAsync(
        string sessionId, string turnId, CompleteTurnRequest request, CancellationToken cancellationToken = default)
    {
        // The realtime model (E.2b per-turn cost) — SessionConfig is immutable post-create, so reading it
        // outside the FinalizeTurn lock is safe; null if the session is unknown (FinalizeTurn returns null too).
        var realtimeModel = _store.Get(sessionId)?.Config.ProviderProfile.RealtimeModel;

        // Idempotent terminal finalize (C.4b): the WS terminal path can collide with this HTTP /complete, so
        // route through FinalizeTurn — an already-terminal turn is returned unchanged (no status overwrite).
        var result = _store.FinalizeTurn(sessionId, turnId, turn =>
        {
            var finalized = turn with
            {
                AudioDurationMs = request.AudioDurationMs ?? turn.AudioDurationMs,
                Transcripts = request.Transcripts ?? turn.Transcripts,
                // /complete always produces a TERMINAL turn: honor a client-reported Failed, otherwise
                // Completed. A non-terminal client value (Ready/Recording/…) can't drag the turn backwards.
                Status = request.Status == TurnStatus.Failed ? TurnStatus.Failed : TurnStatus.Completed,
                CompletedAt = _clock.UtcNow,
            };

            // Realtime per-turn cost (E.2b): cascade turns are priced by the C.4 WS — never here. Computed
            // INSIDE the transform so cost lands atomically with the terminal status (idempotent re-complete
            // skips it). Degrades to null on Unavailable (never 0 — lesson §9/§12).
            return finalized.Mode == InterpretationMode.Realtime && realtimeModel is not null
                ? finalized with { CostEstimate = EstimateRealtimeCost(realtimeModel, finalized, request) }
                : finalized;
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

    // Realtime per-turn cost via the existing CostEstimator.EstimateRealtime (E.2b — wiring, not new math).
    // Input seconds from the merged turn duration; output seconds from the optional client-reported field.
    private CostEstimate? EstimateRealtimeCost(string realtimeModel, InterpretationTurn turn, CompleteTurnRequest request)
    {
        // 053-C2a — the exact-count path needs the DC's audio-token counts; the legacy estimate needs a
        // positive audio duration. With NEITHER signal there's nothing to price → null, never a synthetic
        // $0.00 (lesson §9/§12 — absent data is "unavailable", never a zero charge).
        var hasTokens = request.InputAudioTokens is not null || request.OutputAudioTokens is not null;
        if (!hasTokens && turn.AudioDurationMs <= 0)
        {
            return null;
        }

        CostUsage usage;
        decimal? outputSeconds = null;
        if (hasTokens)
        {
            // ⚠ Deliberate naming FLIP across the boundary — do NOT "align" these into a bug: the REQUEST
            // mirrors the OpenAI wire (InputAudioTokens = input_token_details.audio_tokens), while CostUsage
            // mirrors its own seconds-fields (AudioInputTokens sits beside AudioInputSeconds). Each name is
            // right for its own context; this map is the seam.
            usage = new CostUsage
            {
                AudioInputTokens = request.InputAudioTokens,
                AudioOutputTokens = request.OutputAudioTokens,
                CachedAudioInputTokens = request.CachedAudioInputTokens,
            };
        }
        else
        {
            outputSeconds = request.OutputAudioDurationMs is { } outputMs ? outputMs / 1000m : (decimal?)null;
            usage = new CostUsage
            {
                AudioInputSeconds = turn.AudioDurationMs / 1000m,
                AudioOutputSeconds = outputSeconds,
            };
        }

        var result = _costEstimator.EstimateRealtime(realtimeModel, usage, turn.AudioDurationMs);
        if (!result.IsSuccess)
        {
            // Honest degrade: no estimate rather than a synthetic 0 (lesson §9/§12); the summary tolerates null.
            return null;
        }

        var cost = result.Value;
        if (!hasTokens && outputSeconds is null)
        {
            // Seconds-estimate path only: the exact-count path carries the real output tokens, so this
            // "output not reported" disclosure applies solely to the duration-estimate fallback
            // (streaming-honesty / ARCH-014 — never SILENTLY price the output side as 0).
            cost = cost with
            {
                Assumptions = [.. cost.Assumptions, "Realtime output audio duration not reported; output cost not included."],
            };
        }

        return cost;
    }

    private string PricingVersion() =>
        _pricing.IsSuccess ? _pricing.Value.Version ?? "unavailable" : "unavailable";
}
