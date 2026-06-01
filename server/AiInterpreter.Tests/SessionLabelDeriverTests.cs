using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// 077 — the pure session-label deriver (a blank Label is auto-derived at end-persist from the first
// source-final transcript snippet, with a mode+direction fallback; a user-typed label always wins).
public class SessionLabelDeriverTests
{
    private static readonly DateTimeOffset T = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly LanguageDirection EnToEs = new(LanguageCode.En, LanguageCode.Es);

    private static TranscriptSegment Seg(string role, string text, bool isFinal) =>
        new($"seg-{role}", role, text, isFinal, "deepgram", T, ClockSource.Server);

    private static InterpretationTurn Turn(InterpretationMode mode, params TranscriptSegment[] transcripts) =>
        new("turn1", mode, EnToEs, T, T.AddSeconds(2), 2000,
            transcripts.ToList(), new List<LatencyEvent>(), null, null, new List<ProviderError>(),
            TurnStatus.Completed, null, null);

    private static InterpretationSession Session(string? label, params InterpretationTurn[] turns)
    {
        var profile = new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy");
        return new InterpretationSession(
            "session_abc", label, T, T.AddMinutes(1),
            new SessionConfig(InterpretationMode.Cascade, EnToEs, profile),
            turns.ToList(), new List<ModeTransitionEvent>(), null, "v1");
    }

    // 1 — a non-blank user label always wins (even when a transcript exists).
    [Fact]
    public void user_label_wins()
    {
        var s = Session("My demo", Turn(InterpretationMode.Cascade, Seg("source", "Hola que tal", true)));
        Assert.Equal("My demo", SessionLabelDeriver.DeriveSessionLabel(s));
    }

    // 2 — blank label + a long source-final transcript → first 40 chars + ellipsis (truncated).
    [Fact]
    public void derives_from_first_source_final_transcript()
    {
        var text = "The quick brown fox jumps over the lazy dog"; // 43 chars > 40 → truncates
        var s = Session(null, Turn(InterpretationMode.Cascade, Seg("source", text, true)));

        var label = SessionLabelDeriver.DeriveSessionLabel(s);

        Assert.Equal(41, label.Length);        // 40 snippet chars + the single ellipsis char
        Assert.EndsWith("…", label);
        Assert.Equal(text[..40], label[..40]); // the first 40 chars of the source utterance
    }

    // 3 — a short source-final transcript → returned as-is, NO ellipsis.
    [Fact]
    public void no_truncation_marker_when_short()
    {
        var s = Session(null, Turn(InterpretationMode.Cascade, Seg("source", "Hello there", true)));
        Assert.Equal("Hello there", SessionLabelDeriver.DeriveSessionLabel(s));
    }

    // 4 — target-role + non-final segments are skipped; the FIRST source-FINAL wins.
    [Fact]
    public void skips_non_source_and_non_final()
    {
        var s = Session(null, Turn(InterpretationMode.Cascade,
            Seg("target", "Hola mundo (target)", true),  // wrong role
            Seg("source", "partial not final", false),   // not final
            Seg("source", "the real source final", true)));

        Assert.Equal("the real source final", SessionLabelDeriver.DeriveSessionLabel(s));
    }

    // 4b — across TURNS, the FIRST source-final in turn order wins (turn 1 over turn 2) — the session's
    // opening utterance is the label.
    [Fact]
    public void derives_from_first_source_final_in_turn_order()
    {
        var s = Session(null,
            Turn(InterpretationMode.Cascade, Seg("source", "First utterance", true)),
            Turn(InterpretationMode.Cascade, Seg("source", "Second utterance", true)));

        Assert.Equal("First utterance", SessionLabelDeriver.DeriveSessionLabel(s));
    }

    // 4c — an empty/whitespace source-final is skipped; the next non-empty source-final wins, else the fallback.
    [Fact]
    public void skips_empty_source_final()
    {
        var withLater = Session(null, Turn(InterpretationMode.Cascade,
            Seg("source", "   ", true),                  // empty source-final → skipped
            Seg("source", "Real first utterance", true)));
        Assert.Equal("Real first utterance", SessionLabelDeriver.DeriveSessionLabel(withLater));

        // ONLY an empty source-final → falls through to the mode + direction fallback.
        var onlyEmpty = Session(null, Turn(InterpretationMode.Cascade, Seg("source", "", true)));
        Assert.Equal("Cascade · EN→ES", SessionLabelDeriver.DeriveSessionLabel(onlyEmpty));
    }

    // 5 — no source transcript → the mode + direction fallback (stable order, Realtime before Cascade).
    [Fact]
    public void mode_direction_fallback_when_no_transcript()
    {
        var cascade = Session(null, Turn(InterpretationMode.Cascade)); // no transcripts
        Assert.Equal("Cascade · EN→ES", SessionLabelDeriver.DeriveSessionLabel(cascade));

        var both = Session(null,
            Turn(InterpretationMode.Realtime),
            Turn(InterpretationMode.Cascade));
        Assert.Equal("Realtime+Cascade · EN→ES", SessionLabelDeriver.DeriveSessionLabel(both));
    }

    // 6 — a whitespace-only label is treated as blank (derives, not returned as-is).
    [Fact]
    public void whitespace_label_treated_as_blank()
    {
        var s = Session("   ", Turn(InterpretationMode.Cascade, Seg("source", "Derived not blank", true)));
        Assert.Equal("Derived not blank", SessionLabelDeriver.DeriveSessionLabel(s));
    }

    // 7 — the derived value always fits the MaxLength(512) persistence constraint.
    [Fact]
    public void result_within_maxlength()
    {
        var s = Session(null, Turn(InterpretationMode.Cascade, Seg("source", new string('x', 1000), true)));
        Assert.True(SessionLabelDeriver.DeriveSessionLabel(s).Length <= 512);
    }

    // 8 — the exact truncation boundary: 40 chars → no ellipsis; 41 chars → first 40 + the ellipsis.
    [Fact]
    public void truncation_boundary_at_40_chars()
    {
        var exactly40 = new string('a', 40);
        Assert.Equal(exactly40, SessionLabelDeriver.DeriveSessionLabel(
            Session(null, Turn(InterpretationMode.Cascade, Seg("source", exactly40, true)))));

        var fortyOne = new string('b', 41);
        var label = SessionLabelDeriver.DeriveSessionLabel(
            Session(null, Turn(InterpretationMode.Cascade, Seg("source", fortyOne, true))));
        Assert.Equal(new string('b', 40) + "…", label);
        Assert.Equal(41, label.Length);
    }

    // 9 — a truly TURNLESS blank session (zero turns) → the fallback uses Config.CurrentMode + direction.
    [Fact]
    public void zero_turns_blank_falls_back_to_current_mode()
    {
        var s = Session(null); // no turns at all → modes.Count == 0 path
        Assert.Equal("Cascade · EN→ES", SessionLabelDeriver.DeriveSessionLabel(s)); // Config.CurrentMode = Cascade
    }
}
