using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>No store, installed game, process, GPU or registry is touched.</summary>
public sealed class GamingStoreLaunchAdmissionTests
{
    [Fact]
    public async Task Already_in_Gaming_never_starts_store_discovery()
    {
        int called = 0;
        var result = await ManualGameSessionService.GuardedStoreLaunchDiscoveryAsync(
            _ => { called++; return Task.FromResult<IReadOnlyList<DiscoveredGameDto>>(
                Array.Empty<DiscoveredGameDto>()); },
            () => false, CancellationToken.None);
        Assert.Equal(0, called);
        Assert.Null(result);
    }

    [Fact]
    public async Task Transition_during_store_discovery_cannot_authorize_stale_launch()
    {
        bool ready = true;
        var result = await ManualGameSessionService.GuardedStoreLaunchDiscoveryAsync(
            _ =>
            {
                ready = false;
                return Task.FromResult<IReadOnlyList<DiscoveredGameDto>>(
                    new[] { new DiscoveredGameDto("steam", "730", "Demo", @"C:\Test") });
            },
            () => ready, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Installed_title_snapshot_remains_available_in_AiReady()
    {
        IReadOnlyList<DiscoveredGameDto> items =
            new[] { new DiscoveredGameDto("gog", "10", "Demo", @"C:\Demo") };
        var result = await ManualGameSessionService.GuardedStoreLaunchDiscoveryAsync(
            _ => Task.FromResult(items), () => true, CancellationToken.None);
        Assert.Same(items, result);
    }

    [Fact]
    public async Task Explicit_caller_cancellation_is_not_mistaken_for_Gaming()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ManualGameSessionService.GuardedStoreLaunchDiscoveryAsync(
                _ => Task.FromResult<IReadOnlyList<DiscoveredGameDto>>(
                    Array.Empty<DiscoveredGameDto>()), () => true, cancel.Token));
    }
}
