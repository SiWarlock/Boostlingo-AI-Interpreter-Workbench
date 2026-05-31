using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// C.4b robustness — SessionStore terminal-idempotency guard (ARCH-016 write tiers). The WS terminal
// persist path now collides with the HTTP /complete and /end paths; a second completion must NOT
// overwrite a terminal status / drift CompletedAt, and a second End must NOT re-stamp EndedAt. The
// guard lives at the store (one place covers both the WS and HTTP collision paths).
public class SessionStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 5, 29, 10, 5, 0, TimeSpan.Zero);

    // A clock whose UtcNow can be advanced between calls — proves a second End does not re-stamp.
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private static SessionConfig Config() => new(
        InterpretationMode.Cascade,
        new LanguageDirection(LanguageCode.En, LanguageCode.Es),
        new ProviderProfile("openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy"));

    [Fact]
    public void finalize_turn_is_idempotent_does_not_overwrite_terminal()
    {
        var clock = new MutableClock { UtcNow = T1 };
        var store = new SessionStore(clock);
        var session = store.Create(Config(), "v1");
        var turn = store.CreateTurn(session.SessionId)!;

        // First finalize → Completed at T1; transform applied.
        var first = store.FinalizeTurn(session.SessionId, turn.TurnId,
            t => t with { Status = TurnStatus.Completed, CompletedAt = T1 });
        Assert.NotNull(first);
        Assert.True(first!.Applied);
        Assert.Equal(TurnStatus.Completed, first.Turn.Status);

        // Second finalize tries to flip it to Failed at T2 → MUST be a no-op (idempotent).
        var second = store.FinalizeTurn(session.SessionId, turn.TurnId,
            t => t with { Status = TurnStatus.Failed, CompletedAt = T2 });
        Assert.NotNull(second);
        Assert.False(second!.Applied);                              // already terminal — transform skipped
        Assert.Equal(TurnStatus.Completed, second.Turn.Status);     // status NOT overwritten
        Assert.Equal(T1, second.Turn.CompletedAt);                  // CompletedAt did NOT drift

        // Store state reflects the protected turn.
        var stored = store.Get(session.SessionId)!.Turns.Single();
        Assert.Equal(TurnStatus.Completed, stored.Status);
        Assert.Equal(T1, stored.CompletedAt);
    }

    [Fact]
    public void finalize_turn_idempotent_blocks_failed_terminal()
    {
        // The guard blocks BOTH terminal statuses — pin the Failed branch too (not just Completed).
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        var session = store.Create(Config(), "v1");
        var turn = store.CreateTurn(session.SessionId)!;

        store.FinalizeTurn(session.SessionId, turn.TurnId, t => t with { Status = TurnStatus.Failed, CompletedAt = T1 });
        var second = store.FinalizeTurn(session.SessionId, turn.TurnId,
            t => t with { Status = TurnStatus.Completed, CompletedAt = T2 });

        Assert.False(second!.Applied);
        Assert.Equal(TurnStatus.Failed, second.Turn.Status); // a Failed turn can't be flipped to Completed
        Assert.Equal(T1, second.Turn.CompletedAt);
    }

    [Fact]
    public void finalize_turn_returns_null_for_unknown_turn()
    {
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        var session = store.Create(Config(), "v1");

        Assert.Null(store.FinalizeTurn(session.SessionId, "turn_missing", t => t with { Status = TurnStatus.Completed }));
        Assert.Null(store.FinalizeTurn("session_missing", "turn_x", t => t with { Status = TurnStatus.Completed }));
    }

    [Fact]
    public void end_session_is_idempotent_does_not_restamp()
    {
        var clock = new MutableClock { UtcNow = T1 };
        var store = new SessionStore(clock);
        var session = store.Create(Config(), "v1");

        var firstEnd = store.End(session.SessionId);
        Assert.Equal(T1, firstEnd!.EndedAt);

        // Advance the clock and end again — EndedAt MUST stay at the first end time.
        clock.UtcNow = T2;
        var secondEnd = store.End(session.SessionId);
        Assert.Equal(T1, secondEnd!.EndedAt); // not re-stamped to T2
    }

    // 050 (Finding 2c / Flow G) — the atomic SwitchMode primitive: swap config.CurrentMode AND record
    // the transition under ONE gate hold (so a concurrent CreateTurn can't observe the new mode without
    // the transition also recorded). RecordModeTransition is the wired single-owner of the append.
    private static ModeTransitionEvent Transition(InterpretationMode from, InterpretationMode to) =>
        new("transition_t1", from, to,
            new LanguageDirection(LanguageCode.En, LanguageCode.Es), T1, ClockSource.Server, null);

    [Fact]
    public void switch_mode_swaps_current_mode_and_records_transition()
    {
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        var session = store.Create(Config(), "v1"); // starts Cascade

        var updated = store.SwitchMode(
            session.SessionId, InterpretationMode.Realtime,
            Transition(InterpretationMode.Cascade, InterpretationMode.Realtime));

        Assert.NotNull(updated);
        Assert.Equal(InterpretationMode.Realtime, updated!.Config.CurrentMode);
        var recorded = Assert.Single(updated.ModeTransitions);
        Assert.Equal(InterpretationMode.Cascade, recorded.FromMode);
        Assert.Equal(InterpretationMode.Realtime, recorded.ToMode);

        // The live store reflects both the swap and the recorded transition.
        var stored = store.Get(session.SessionId)!;
        Assert.Equal(InterpretationMode.Realtime, stored.Config.CurrentMode);
        Assert.Single(stored.ModeTransitions);
    }

    [Fact]
    public void switch_mode_with_null_transition_swaps_mode_without_recording()
    {
        // The no-op path primitive (Step-2.5 Q2): swap the mode but record NO transition.
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        var session = store.Create(Config(), "v1"); // Cascade

        var updated = store.SwitchMode(session.SessionId, InterpretationMode.Realtime, transition: null);

        Assert.Equal(InterpretationMode.Realtime, updated!.Config.CurrentMode);
        Assert.Empty(updated.ModeTransitions);
    }

    [Fact]
    public void switch_mode_returns_null_for_unknown_session()
    {
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        Assert.Null(store.SwitchMode("session_missing", InterpretationMode.Realtime, transition: null));
    }

    [Fact]
    public void turn_created_after_switch_inherits_new_mode()
    {
        // The 2c fix at the store level: CreateTurn stamps config.CurrentMode, so a turn created AFTER
        // a switch must carry the NEW mode (the stale-mode root cause).
        var store = new SessionStore(new MutableClock { UtcNow = T1 });
        var session = store.Create(Config(), "v1"); // Cascade
        store.SwitchMode(session.SessionId, InterpretationMode.Realtime, transition: null);

        var turn = store.CreateTurn(session.SessionId)!;

        Assert.Equal(InterpretationMode.Realtime, turn.Mode);
    }
}
