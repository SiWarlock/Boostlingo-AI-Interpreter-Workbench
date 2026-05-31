namespace AiInterpreter.Api.Sessions;

/// <summary>
/// H.3-backend — a LIGHTWEIGHT summary of a persisted session for the <c>GET /api/sessions</c> history
/// list (ARCH-009 / ARCH-016 read tier). Option B (payload hygiene): the list returns these summaries,
/// NOT the full <see cref="InterpretationSession"/>[] (turns + latency events + transcripts). The history
/// view drills into per-session detail via the existing <c>GET /{id}</c>. Projected from a persisted
/// session via <see cref="FromSession"/>; serialized camelCase via <c>Common/JsonDefaults</c> like every
/// other contract (a field change pairs with the ARCH-009 / Appendix A edit).
/// </summary>
public sealed record SessionListItem(
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int TurnCount,
    IReadOnlyList<InterpretationMode> Modes)
{
    /// <summary>
    /// Projects a persisted session → its lightweight list summary. <c>TurnCount</c> is the TOTAL turn
    /// count (informational — the precise per-mode breakdown lives on <c>GET /{id}/summary</c>);
    /// <c>Modes</c> is the DISTINCT interpretation modes the turns used, in first-seen order (empty for a
    /// turnless session).
    /// </summary>
    public static SessionListItem FromSession(InterpretationSession session) => new(
        session.SessionId,
        session.Label,
        session.StartedAt,
        session.EndedAt,
        session.Turns.Count,
        DistinctModes(session.Turns));

    private static IReadOnlyList<InterpretationMode> DistinctModes(IReadOnlyList<InterpretationTurn> turns)
    {
        var seen = new List<InterpretationMode>();
        foreach (var turn in turns)
        {
            if (!seen.Contains(turn.Mode))
            {
                seen.Add(turn.Mode);
            }
        }

        return seen;
    }
}
