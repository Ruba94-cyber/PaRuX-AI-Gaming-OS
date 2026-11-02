using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Deterministic exit-risk rules; no Windows snapshot or live game is needed.</summary>
public sealed class OwnedGameChildExitGuardTests
{
    private static readonly DateTime Started = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Exited = Started.AddMinutes(18);
    private const int ParentPid = 2351;

    [Fact]
    public void Live_direct_child_of_verified_launch_suppresses_automatic_AI_return()
    {
        var children = new[]
        {
            new OwnedGameChildExitGuard.Candidate(3100, ParentPid, Started.AddSeconds(8), true, true)
        };
        Assert.Equal(OwnedGameChildExitGuard.Result.LiveDirectChild,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited, children, true));
    }

    [Fact]
    public void Unrelated_process_and_stale_reused_parent_PID_do_not_claim_child_ownership()
    {
        var children = new[]
        {
            new OwnedGameChildExitGuard.Candidate(3100, 9999, Started.AddSeconds(8), true, true),
            new OwnedGameChildExitGuard.Candidate(3101, ParentPid, Started.AddMinutes(-20), true, true),
            new OwnedGameChildExitGuard.Candidate(3102, ParentPid, Started.AddMinutes(20), true, true),
            new OwnedGameChildExitGuard.Candidate(3103, ParentPid, Started.AddSeconds(20), false, true)
        };
        Assert.Equal(OwnedGameChildExitGuard.Result.NoLiveDirectChild,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited, children, true));
    }

    [Fact]
    public void Incomplete_or_unreadable_snapshot_fails_closed()
    {
        var unreadable = new[]
        {
            new OwnedGameChildExitGuard.Candidate(3100, ParentPid, null, true, false)
        };
        Assert.Equal(OwnedGameChildExitGuard.Result.Inconclusive,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited, unreadable, true));
        Assert.Equal(OwnedGameChildExitGuard.Result.Inconclusive,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited,
                Array.Empty<OwnedGameChildExitGuard.Candidate>(), complete: false));
        Assert.Equal(OwnedGameChildExitGuard.Result.Inconclusive,
            OwnedGameChildExitGuard.Evaluate(0, Started, Exited,
                Array.Empty<OwnedGameChildExitGuard.Candidate>(), complete: true));
    }

    [Fact]
    public void No_live_direct_children_from_a_complete_snapshot_allows_verified_exit()
    {
        Assert.Equal(OwnedGameChildExitGuard.Result.NoLiveDirectChild,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited,
                Array.Empty<OwnedGameChildExitGuard.Candidate>(), complete: true));
    }

    [Fact]
    public void Unreadable_unrelated_process_does_not_block_a_verified_parent()
    {
        var unrelated = new[]
        {
            new OwnedGameChildExitGuard.Candidate(3100, 7777, null, true, false)
        };
        Assert.Equal(OwnedGameChildExitGuard.Result.NoLiveDirectChild,
            OwnedGameChildExitGuard.Evaluate(ParentPid, Started, Exited, unrelated, true));
    }
}
