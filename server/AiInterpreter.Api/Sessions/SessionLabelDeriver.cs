namespace AiInterpreter.Api.Sessions;

/// <summary>
/// 077 — derives a meaningful session <c>Label</c> for the history list when the user didn't type one.
/// Applied at the end-persist seam (<see cref="SessionService.EndAsync"/>) when <c>Label</c> is blank, so
/// the list/detail/persisted-file show a readable label instead of the raw session id.
///
/// Order: a non-blank user label always wins; else the session's FIRST source-role final transcript (in
/// turn order) → a snippet truncated to <see cref="MaxSnippetChars"/> + an ellipsis (only when truncated);
/// else (no source utterance) a mode + direction fallback (e.g. <c>Cascade · EN→ES</c>). Pure + deterministic.
///
/// The label string is JUST the utterance snippet — the history row renders the mode chips + timestamp
/// separately, so they are NOT stuffed into the label.
/// </summary>
public static class SessionLabelDeriver
{
    private const int MaxSnippetChars = 40;
    private const string SourceRole = "source"; // matches the cascade RoleSource + the FE realtime producer
    private const string Ellipsis = "…";

    public static string DeriveSessionLabel(InterpretationSession session)
    {
        // A user-typed label always wins — only fill when blank/whitespace.
        if (!string.IsNullOrWhiteSpace(session.Label))
        {
            return session.Label;
        }

        // The FIRST source-role FINAL transcript across the turns (turn order, then within-turn order),
        // skipping empty/whitespace finals → a truncated snippet.
        var firstSource = session.Turns
            .SelectMany(turn => turn.Transcripts)
            .FirstOrDefault(seg => seg.IsFinal && seg.Role == SourceRole && !string.IsNullOrWhiteSpace(seg.Text));
        if (firstSource is not null)
        {
            return Truncate(firstSource.Text.Trim());
        }

        // No source utterance → mode + direction fallback.
        return ModeDirectionFallback(session);
    }

    // Hard cut at MaxSnippetChars; the ellipsis is appended ONLY when the text was actually truncated.
    private static string Truncate(string text) =>
        text.Length <= MaxSnippetChars ? text : text[..MaxSnippetChars] + Ellipsis;

    // "{modes} · {SOURCE}→{TARGET}" — distinct turn modes in a stable order (Realtime before Cascade),
    // '+'-joined; a turnless session falls back to the session's current mode.
    private static string ModeDirectionFallback(InterpretationSession session)
    {
        var present = session.Turns.Select(turn => turn.Mode).ToHashSet();
        var modes = new List<InterpretationMode>();
        if (present.Contains(InterpretationMode.Realtime)) modes.Add(InterpretationMode.Realtime);
        if (present.Contains(InterpretationMode.Cascade)) modes.Add(InterpretationMode.Cascade);

        var modeLabel = modes.Count > 0
            ? string.Join("+", modes)
            : session.Config.CurrentMode.ToString();

        var direction = session.Config.Direction;
        return $"{modeLabel} · {Upper(direction.Source)}→{Upper(direction.Target)}";
    }

    private static string Upper(LanguageCode code) => code.ToString().ToUpperInvariant();
}
