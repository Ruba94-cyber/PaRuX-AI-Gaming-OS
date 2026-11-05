using System;
using System.IO;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// No process is launched. Windows end-to-end checking still requires the
/// real selected game EXE and a desktop-owned Core instance.
/// </summary>
public sealed class LocalGameGuideProcessGateTests
{
    [Fact]
    public void An_externally_started_selected_game_cannot_prepare_AI_guide()
    {
        Assert.False(GamingEndpoints.IsSelectedGameClosed(
            @"C:\Games\Example.exe", path => path!, _ => true));
        Assert.True(GamingEndpoints.IsSelectedGameClosed(
            @"C:\Games\Example.exe", path => path!, _ => false));
    }

    [Fact]
    public void Unknown_missing_or_inaccessible_game_identity_fails_closed()
    {
        Assert.False(GamingEndpoints.IsSelectedGameClosed(null, path => path!, _ => false));
        Assert.False(GamingEndpoints.IsSelectedGameClosed(
            @"C:\Games\Missing.exe", _ => throw new IOException("missing"), _ => false));
        Assert.False(GamingEndpoints.IsSelectedGameClosed(
            @"C:\Games\Protected.exe", path => path!,
            _ => throw new InvalidOperationException("inaccessible process")));
        Assert.False(GamingEndpoints.IsSelectedGameClosed(
            @"C:\Games\Other.exe", path => path!,
            _ => throw new PlatformNotSupportedException("requires Windows")));
    }

    [Fact]
    public void A_game_starting_between_probes_is_blocked_on_the_final_recheck()
    {
        bool gameStarted = false;
        Func<string, bool> probe = _ => gameStarted;
        Assert.True(GamingEndpoints.IsSelectedGameClosed(@"C:\Games\Example.exe", path => path!, probe));
        gameStarted = true;
        Assert.False(GamingEndpoints.IsSelectedGameClosed(@"C:\Games\Example.exe", path => path!, probe));
    }
}
