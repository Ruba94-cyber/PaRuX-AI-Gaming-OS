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
}
