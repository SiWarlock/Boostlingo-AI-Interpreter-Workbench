using System.Collections.Concurrent;
using System.Linq;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// In-memory store of live <see cref="InterpretationSession"/> state (ARCH-008 application service).
/// It is the ONLY source of the server-side session id — the ARCH-016 / ARCH-019 path-traversal
/// guard depends on that id matching <c>^[A-Za-z0-9_-]+$</c>. Thread-safe for the concurrent HTTP
/// (B.9) + WS (C.4) access the same session sees: a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by id, plus a per-session lock guarding the mutable turn/transition lists and the
/// <see cref="End"/> record-swap (the record is immutable but holds mutable <see cref="List{T}"/>).
/// </summary>
public sealed class SessionStore
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public SessionStore(IClock clock) => _clock = clock;

    // The session record + its mutation gate. Session is replaced (not mutated) for init-only scalar
    // fields like EndedAt; the Turns/ModeTransitions lists are mutated in place under the gate.
    private sealed class Entry
    {
        public required InterpretationSession Session { get; set; }
        public object Gate { get; } = new();
    }

    /// <summary>Creates a session with a fresh server-generated id and registers it.</summary>
    public InterpretationSession Create(SessionConfig config, string pricingConfigVersion, string? label = null)
    {
        var session = new InterpretationSession(
            SessionId: GenerateSessionId(),
            Label: label,
            StartedAt: _clock.UtcNow,
            EndedAt: null,
            Config: config,
            Turns: new List<InterpretationTurn>(),
            ModeTransitions: new List<ModeTransitionEvent>(),
            Summary: null,
            PricingConfigVersion: pricingConfigVersion);

        _sessions[session.SessionId] = new Entry { Session = session };
        return session;
    }

    /// <summary>
    /// Ids of all currently un-ended sessions (<c>EndedAt == null</c>) — the Flow-H stale-flush set (E.5).
    /// Enumeration over the <see cref="ConcurrentDictionary{TKey,TValue}"/> is snapshot-safe and each session
    /// reference is read atomically (a concurrent <see cref="End"/> swap is seen whole, never torn). A pure
    /// read: the caller ends each via the existing <see cref="End"/>/persist seam (its own gate).
    /// </summary>
    public IReadOnlyList<string> ActiveSessionIds() =>
        [.. _sessions.Where(kvp => kvp.Value.Session.EndedAt is null).Select(kvp => kvp.Key)];

    /// <summary>
    /// Returns the LIVE session reference (not a snapshot) by id, or null if unknown. Reads are
    /// lock-free: the <see cref="InterpretationSession"/> reference is read atomically (a concurrent
    /// <see cref="End"/> record-swap is seen whole, never torn — so <c>EndedAt</c> flips all-or-nothing),
    /// but enumerating the returned mutable <c>Turns</c>/<c>ModeTransitions</c> while another thread
    /// mutates them is a live view, not a frozen copy. Acceptable for the single-node MVP; B.9/C.4
    /// decide whether a given read path needs a copied snapshot.
    /// </summary>
    public InterpretationSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.Session : null;

    /// <summary>Appends a turn. Returns false (no throw) if the session id is unknown.</summary>
    public bool AddTurn(string sessionId, InterpretationTurn turn)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.Gate) entry.Session.Turns.Add(turn);
        return true;
    }

    /// <summary>Replaces an existing turn (matched by <c>TurnId</c>). False if the session or turn is unknown.</summary>
    public bool UpdateTurn(string sessionId, InterpretationTurn turn)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.Gate)
        {
            var turns = entry.Session.Turns;
            var idx = turns.FindIndex(t => t.TurnId == turn.TurnId);
            if (idx < 0) return false;
            turns[idx] = turn;
        }
        return true;
    }

    /// <summary>
    /// Creates an empty turn with a backend-generated id (the store is the id source, like
    /// <c>sessionId</c>), inheriting the session's current mode + direction (Flow G transitions are
    /// recorded separately), <c>StartedAt</c> from the clock, <c>Status=Ready</c>; appends + returns it.
    /// Null if the session is unknown.
    /// </summary>
    public InterpretationTurn? CreateTurn(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            var config = entry.Session.Config;
            var turn = new InterpretationTurn(
                TurnId: GenerateTurnId(),
                Mode: config.CurrentMode,
                Direction: config.Direction,
                StartedAt: _clock.UtcNow,
                CompletedAt: null,
                AudioDurationMs: 0, // 0 = not yet known; /complete overwrites it

                Transcripts: new List<TranscriptSegment>(),
                LatencyEvents: new List<LatencyEvent>(),
                CostEstimate: null,
                WerResult: null,
                Errors: new List<ProviderError>(),
                Status: TurnStatus.Ready,
                TranslationModelUsed: null,
                TtsVoiceUsed: null);
            entry.Session.Turns.Add(turn);
            return turn;
        }
    }

    /// <summary>
    /// Atomic read-modify-write of a turn (matched by <c>turnId</c>) under the gate: applies
    /// <paramref name="transform"/> and stores the result. Returns the updated turn, or null if the
    /// session or turn is unknown. (Used by B.9c-ii append-events / complete.)
    /// </summary>
    public InterpretationTurn? UpdateTurn(
        string sessionId, string turnId, Func<InterpretationTurn, InterpretationTurn> transform)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            var turns = entry.Session.Turns;
            var idx = turns.FindIndex(t => t.TurnId == turnId);
            if (idx < 0) return null;
            var updated = transform(turns[idx]);
            turns[idx] = updated;
            return updated;
        }
    }

    /// <summary>
    /// Idempotently finalizes a turn to a terminal status (C.4b). If the turn is ALREADY terminal
    /// (<see cref="TurnStatus.Completed"/>/<see cref="TurnStatus.Failed"/>), the <paramref name="transform"/>
    /// is NOT applied and the existing turn is returned with <c>Applied=false</c> — so a second completion
    /// (the WS terminal path colliding with the HTTP <c>/complete</c>, or a double call on either) can't
    /// overwrite the terminal status / drift <c>CompletedAt</c>, and the caller can skip a redundant persist.
    /// A fresh turn returns <c>Applied=true</c>. Null if the session or turn is unknown. (ARCH-016.)
    /// </summary>
    public FinalizeResult? FinalizeTurn(
        string sessionId, string turnId, Func<InterpretationTurn, InterpretationTurn> transform)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            var turns = entry.Session.Turns;
            var idx = turns.FindIndex(t => t.TurnId == turnId);
            if (idx < 0) return null;

            var existing = turns[idx];
            if (existing.Status is TurnStatus.Completed or TurnStatus.Failed)
            {
                return new FinalizeResult(existing, Applied: false); // idempotent — already terminal
            }

            var updated = transform(existing);
            turns[idx] = updated;
            return new FinalizeResult(updated, Applied: true);
        }
    }

    /// <summary>Records a mode transition. Returns false (no throw) if the session id is unknown.</summary>
    public bool RecordModeTransition(string sessionId, ModeTransitionEvent transition)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.Gate) entry.Session.ModeTransitions.Add(transition);
        return true;
    }

    /// <summary>
    /// Stamps <c>EndedAt</c> from the clock and returns the ended session, or null if unknown. Idempotent
    /// (C.4b): an already-ended session is returned unchanged (no re-stamp) so a second <c>/end</c> — or the
    /// WS terminal path colliding with the HTTP end path — can't move the end time.
    /// </summary>
    public InterpretationSession? End(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            if (entry.Session.EndedAt is null)
            {
                entry.Session = entry.Session with { EndedAt = _clock.UtcNow };
            }

            return entry.Session;
        }
    }

    /// <summary>Snapshots a computed <c>Summary</c> into the session (B.9c-i <c>/end</c>) so a later
    /// <see cref="Get"/> reflects it; returns the updated session, or null if unknown.</summary>
    public InterpretationSession? SetSummary(string sessionId, SessionSummary summary)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            entry.Session = entry.Session with { Summary = summary };
            return entry.Session;
        }
    }

    // session_<short-id>: the short-id is a lowercase-hex GUID segment, so the full id matches the
    // path-traversal allowlist ^[A-Za-z0-9_-]+$ (ARCH-016 / ARCH-019).
    private static string GenerateSessionId() => "session_" + Guid.NewGuid().ToString("N")[..8];

    // turn_<short-id>: backend-owned (ARCH-009); a GUID segment avoids a per-session counter. The
    // illustrative ARCH-009 "turn_001" is not a contract.
    private static string GenerateTurnId() => "turn_" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// The outcome of <see cref="SessionStore.FinalizeTurn"/> (C.4b): the resulting turn + whether the
/// transform was <see cref="Applied"/> (false when the turn was already terminal — the caller skips a
/// redundant persist). Area-local; not serialized.
/// </summary>
public sealed record FinalizeResult(InterpretationTurn Turn, bool Applied);
