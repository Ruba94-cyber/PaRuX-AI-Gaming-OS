using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingInputSafetyPolicyTests
{
    [Theory]
    [InlineData("local", "native", true)]
    [InlineData("local", "xoutput", true)]
    [InlineData("local", "custom", true)]
    [InlineData("local", "sunshine", false)]
    [InlineData("remote", "sunshine", true)]
    [InlineData("remote", "native", true)]
    [InlineData("remote", "xoutput", false)]
    [InlineData("remote", "custom", false)]
    [InlineData("unsupported", "native", false)]
    public void Local_and_remote_input_overlap_is_checked_before_start(
        string session, string provider, bool allowed)
    {
        var profile = new GamingProfileDto { SessionType = session, InputProvider = provider };
        Assert.Equal(allowed, GamingInputSafetyPolicy.Validate(profile) == null);
    }

    [Fact]
    public async Task Direct_remote_mapper_start_refuses_to_spawn_a_helper()
    {
        var profile = new GamingProfileDto
        {
            SessionType = "remote",
            InputProvider = "xoutput",
            InputSettings = new Dictionary<string, string> { ["path"] = "invalid.exe" }
        };
        var provider = new XOutputInputProvider(() =>
            throw new InvalidOperationException("Must not inspect running XOutput processes"));
        Assert.False(await provider.StartAsync(profile, CancellationToken.None));
        Assert.False(await new CustomExecutableInputProvider().StartAsync(
            new GamingProfileDto { SessionType = "remote", InputProvider = "custom" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Local_sunshine_input_cannot_claim_a_virtual_controller()
    {
        var profile = new GamingProfileDto { SessionType = "local", InputProvider = "sunshine" };
        Assert.False(await new SunshineInputProvider().StartAsync(profile, CancellationToken.None));
    }

    [Fact]
    public void Custom_helper_path_must_be_an_explicit_local_regular_executable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-input-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "safe-helper.exe");
            string shortcut = Path.Combine(dir, "unsafe-helper.cmd");
            File.WriteAllText(exe, "fixture only; never execute");
            File.WriteAllText(shortcut, "fixture only; never execute");

            Assert.True(CustomExecutableInputProvider.IsSafeHelperPath(exe));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(shortcut));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(Path.Combine(dir, "missing.exe")));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(@"\\server\share\helper.exe"));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath("relative.exe"));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(exe + Path.DirectorySeparatorChar + ".."));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Custom_helper_never_claims_an_unstarted_or_invalid_process()
    {
        var provider = new CustomExecutableInputProvider();
        var profile = new GamingProfileDto
        {
            SessionType = "local",
            InputProvider = "custom",
            InputSettings = new Dictionary<string, string> { ["path"] = @"C:\does-not-exist\mapper.exe" }
        };

        Assert.False(await provider.StartAsync(profile, CancellationToken.None));
        Assert.True(await provider.StopAsync(profile, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(await provider.StartAsync(profile, cancelled.Token));
        Assert.True(await provider.StopAsync(profile, CancellationToken.None));
    }

}
