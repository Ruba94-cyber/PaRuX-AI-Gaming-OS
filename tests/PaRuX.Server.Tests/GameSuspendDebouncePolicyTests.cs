using System;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSuspendDebouncePolicyTests
{
    private static readonly GameAutoSuspendRule Rule = new(
        "synthetic-game", true, true, 25, 85);

    private static GameSuspendDebounceResult Sample(
        GameSuspendDebounceState? prior, int seconds,
        string? stamp = "synthetic-pid-start-1",
        bool focusLost = true, AntiCheatAssessment antiCheat = AntiCheatAssessment.VerifiedAbsent,
        bool identityVerified = true)
        => GameSuspendDebouncePolicy.Observe(prior, Rule, stamp,
            identityVerified, antiCheat, focusLost, null, null,
            "synthetic-game.exe", TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Continuous_focus_loss_requires_fresh_consecutive_samples()
    {
        var first = Sample(null, 0);
        Assert.False(first.Decision.Eligible);
        Assert.NotNull(first.NextState);
        var second = Sample(first.NextState, 1);
        Assert.False(second.Decision.Eligible);
        var third = Sample(second.NextState, 3);
        Assert.True(third.Decision.Eligible);
        Assert.Equal(GameSuspendTrigger.FocusLost, third.Decision.Trigger);
        Assert.Equal(TimeSpan.FromSeconds(3), third.ContinuouslyObserved);
    }

    [Fact]
    public void Stale_gap_and_backwards_clock_restart_stability_window()
    {
        var initial = Sample(null, 0);
        var stale = Sample(initial.NextState, 4);
        Assert.False(stale.Decision.Eligible);
        Assert.Equal(TimeSpan.Zero, stale.ContinuouslyObserved);
        var backwards = Sample(stale.NextState, 2);
        Assert.False(backwards.Decision.Eligible);
        Assert.Equal(TimeSpan.Zero, backwards.ContinuouslyObserved);
    }

    [Fact]
    public void Process_identity_change_and_focus_recovery_reset_candidate()
    {
        var initial = Sample(null, 0);
        var second = Sample(initial.NextState, 2);
        var changedPid = Sample(second.NextState, 3, stamp: "synthetic-pid-start-2");
        Assert.False(changedPid.Decision.Eligible);
        Assert.Equal(TimeSpan.Zero, changedPid.ContinuouslyObserved);
        var focusRestored = Sample(changedPid.NextState, 4, focusLost: false);
        Assert.Null(focusRestored.NextState);
        Assert.False(focusRestored.Decision.Eligible);
    }

    [Fact]
    public void Unknown_anticheat_or_unverified_identity_does_not_accumulate()
    {
        var initial = Sample(null, 0);
        Assert.Null(Sample(initial.NextState, 1, antiCheat: AntiCheatAssessment.Unknown).NextState);
        Assert.Null(Sample(initial.NextState, 1, identityVerified: false).NextState);
        Assert.Null(Sample(initial.NextState, 1, stamp: null).NextState);
    }

    [Fact]
    public void Only_a_new_stable_condition_can_follow_a_recovered_condition()
    {
        var initial = Sample(null, 0);
        var next = Sample(initial.NextState, 1, focusLost: false);
        Assert.Null(next.NextState);
        var resumed = Sample(next.NextState, 2);
        Assert.False(resumed.Decision.Eligible);
        Assert.Equal(TimeSpan.Zero, resumed.ContinuouslyObserved);
    }
}
