using System.Diagnostics;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OwnedGameProcessTests
{
    private static WorkloadOrchestrator.OwnedGameProcess Self(long version = 7)
    {
        using Process me = Process.GetCurrentProcess();
        return new(me.Id, me.StartTime.ToUniversalTime(), me.MainModule!.FileName, version);
    }

    [Fact]
    public void Identity_requires_matching_start_time_and_image_path()
    {
        var owned = new WorkloadOrchestrator.OwnedGameProcess(10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), @"C:\Games\a\game.exe", 1);
        Assert.True(WorkloadOrchestrator.MatchesOwnedIdentity(owned, owned.StartUtc.AddSeconds(1), @"c:\games\a\GAME.EXE"));
        Assert.False(WorkloadOrchestrator.MatchesOwnedIdentity(owned, owned.StartUtc.AddMinutes(5), owned.ImagePath)); // PID reused later
        Assert.False(WorkloadOrchestrator.MatchesOwnedIdentity(owned, owned.StartUtc, @"C:\Games\b\game.exe"));
        Assert.False(WorkloadOrchestrator.MatchesOwnedIdentity(owned, owned.StartUtc, null));
    }

    [Fact]
    public void Live_owned_process_validates_only_for_the_same_session()
    {
        var owned = Self(7);
        Assert.Equal(owned.Pid, WorkloadOrchestrator.ValidateOwnedProcess(owned, 7));
        Assert.Null(WorkloadOrchestrator.ValidateOwnedProcess(owned, 8));
        Assert.Null(WorkloadOrchestrator.ValidateOwnedProcess(owned, null));
        Assert.Null(WorkloadOrchestrator.ValidateOwnedProcess(null, 7));
    }

    [Fact]
    public void Wrong_creation_time_or_missing_process_fails_closed()
    {
        var owned = Self(3);
        Assert.Null(WorkloadOrchestrator.ValidateOwnedProcess(owned with { StartUtc = owned.StartUtc.AddHours(-3) }, 3));
        Assert.Null(WorkloadOrchestrator.ValidateOwnedProcess(owned with { Pid = int.MaxValue }, 3));
    }
}
