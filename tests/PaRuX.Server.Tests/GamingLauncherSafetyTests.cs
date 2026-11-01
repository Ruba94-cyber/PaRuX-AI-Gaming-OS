using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingLauncherSafetyTests
{
    [Fact]
    public async Task Playnite_rejects_unverified_existing_process_without_launching_another()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-playnite-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixture = Path.Combine(root, "Playnite.FullscreenApp.exe");
            File.WriteAllText(fixture, "Fixture only. Never execute.");
            var profile = new GamingProfileDto
            {
                SessionType = "local",
                LauncherProvider = "playnite",
                LauncherSettings = new Dictionary<string, string> { ["path"] = fixture }
            };
            // An unrelated process must not be treated as our chosen launcher,
            // even when the process enumeration seam returns it.
            var provider = new PlayniteLauncherProvider(() => new[] { Process.GetCurrentProcess() });
            Assert.False(await provider.LaunchAsync(profile, CancellationToken.None));
            Assert.Equal("Fixture only. Never execute.", File.ReadAllText(fixture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancelled_launch_never_invokes_an_external_launcher()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var profile = new GamingProfileDto
        {
            SessionType = "local",
            LauncherProvider = "custom",
            LauncherSettings = new Dictionary<string, string>
            {
                ["path"] = @"C:\never-start\game.exe"
            }
        };

        Assert.False(await new CustomExecutableLauncherProvider().LaunchAsync(profile, cancelled.Token));
        Assert.False(await new SteamBigPictureLauncherProvider().LaunchAsync(profile, cancelled.Token));
        Assert.False(await new PlayniteLauncherProvider(() =>
            throw new InvalidOperationException("Cancelled launch must not probe processes"))
            .LaunchAsync(profile, cancelled.Token));
    }

    [Theory]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData("relative.exe")]
    [InlineData(@"C:\missing\app.cmd")]
    [InlineData(@"C:\missing\app.lnk")]
    public async Task Custom_launcher_rejects_unsafe_or_unverified_paths(string path)
    {
        var profile = new GamingProfileDto
        {
            SessionType = "local",
            LauncherProvider = "custom",
            LauncherSettings = new Dictionary<string, string> { ["path"] = path }
        };

        Assert.False(await new CustomExecutableLauncherProvider().LaunchAsync(profile, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_XOutput_does_not_poll_external_processes()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var profile = new GamingProfileDto
        {
            SessionType = "local",
            InputProvider = "xoutput"
        };

        Assert.False(await new XOutputInputProvider(() =>
            throw new InvalidOperationException("Cancelled input start must not probe processes"))
            .StartAsync(profile, cancelled.Token));
    }
}
