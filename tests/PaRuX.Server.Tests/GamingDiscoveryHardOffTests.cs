using Microsoft.AspNetCore.Http;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>In-memory discovery delegates; no real store, ROM or installer.</summary>
public sealed class GamingDiscoveryHardOffTests
{
    [Fact]
    public async Task Already_in_Gaming_never_calls_provider()
    {
        int calls = 0;
        IResult result = await GamingEndpoints.GuardedDiscoveryAsync(
            _ => { calls++; return Task.FromResult("unreachable"); }, () => true,
            CancellationToken.None, "Steam store");
        Assert.Equal(0, calls);
        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Mid_scan_transition_cannot_publish_stale_success()
    {
        bool gaming = false;
        IResult result = await GamingEndpoints.GuardedDiscoveryAsync(
            _ => { gaming = true; return Task.FromResult("stale"); }, () => gaming,
            CancellationToken.None, "Epic store");
        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Read_with_AI_available_succeeds()
    {
        IResult result = await GamingEndpoints.GuardedDiscoveryAsync(
            _ => Task.FromResult("ready"), () => false,
            CancellationToken.None, "Local game folder");
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mislabeled_as_Gaming()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        IResult result = await GamingEndpoints.GuardedDiscoveryAsync(
            ct => Task.FromCanceled<string>(ct), () => false, cts.Token, "GOG store");
        Assert.Equal(499, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
