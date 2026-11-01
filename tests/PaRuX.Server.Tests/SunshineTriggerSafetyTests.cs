using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SunshineTriggerSafetyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:47989/api/config", true)]
    [InlineData("https://localhost:47990/api/config", true)]
    [InlineData("http://[::1]:47989/", true)]
    [InlineData("http://192.168.1.20:47989/api/config", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://example.com/status", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Status_url_is_only_ever_loopback_http(string? url, bool expected) =>
        Assert.Equal(expected, SunshineSessionTriggerProvider.IsLoopbackHttpUrl(url));

    [Fact]
    public async Task Non_loopback_profile_url_is_ignored_and_never_contacted()
    {
        var profile = new GamingProfileDto();
        profile.TriggerSettings["sunshine_url"] = "http://192.0.2.1:9/never";
        var provider = new SunshineSessionTriggerProvider();
        // Must return promptly (no 3 s connection attempt to a non-loopback address) and never throw.
        var task = provider.IsTriggeredAsync(profile, CancellationToken.None);
        Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task);
        await task;
    }
}
