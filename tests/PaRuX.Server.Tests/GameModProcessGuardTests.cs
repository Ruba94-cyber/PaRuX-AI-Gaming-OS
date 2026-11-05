using System;
using System.IO;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Read-only validation of a selected game's executable identity.
/// No game or fixture executable is ever started.
/// </summary>
public sealed class GameModProcessGuardTests
{
    [Fact]
    public void Valid_regular_local_executable_is_accepted_without_launching_it()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "parux-mod-exe-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string game = Path.Combine(folder, "SyntheticModGame.exe");
            File.WriteAllText(game, "inert synthetic fixture bytes, never executed");
            Assert.Equal(Path.GetFullPath(game), GameModProcessGuard.ValidateExecutable(game));
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-exe.txt")]
    [InlineData("game.lnk")]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData("relative/game.exe")]
    public void Non_local_or_non_executable_targets_are_refused(string candidate)
    {
        Assert.Throws<ArgumentException>(() =>
            GameModProcessGuard.ValidateExecutable(candidate));
    }

    [Fact]
    public void Unknown_game_process_name_has_no_running_match_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(),
            "parux-test-nonexistent-game-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.False(GameModProcessGuard.IsRunning(path));
    }

    [Fact]
    public void Own_running_Windows_test_process_is_detected_without_launching_any_executable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        string actual = current.MainModule?.FileName ?? "";
        if (!actual.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        Assert.True(GameModProcessGuard.IsRunning(Path.GetFullPath(actual)));
    }

    [Fact]
    public void Process_lookup_refuses_to_pretend_Windows_support_on_other_platforms()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() =>
            GameModProcessGuard.IsRunning(Path.Combine(Path.GetTempPath(), "SyntheticModGame.exe")));
    }
}
