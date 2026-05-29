using System.Collections.Concurrent;
using AiInterpreter.Api.Common;

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

    /// <summary>Records a mode transition. Returns false (no throw) if the session id is unknown.</summary>
    public bool RecordModeTransition(string sessionId, ModeTransitionEvent transition)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.Gate) entry.Session.ModeTransitions.Add(transition);
        return true;
    }

    /// <summary>Stamps <c>EndedAt</c> from the clock and returns the ended session, or null if unknown.</summary>
    public InterpretationSession? End(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.Gate)
        {
            entry.Session = entry.Session with { EndedAt = _clock.UtcNow };
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
}
