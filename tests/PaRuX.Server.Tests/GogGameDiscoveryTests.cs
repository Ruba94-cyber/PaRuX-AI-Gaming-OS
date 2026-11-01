using System.Net;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GogGameDiscoveryTests
{
    [Fact]
    public void Valid_local_entries_are_deduplicated_and_sorted_without_registry_writes()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-gog-discovery-" + Guid.NewGuid().ToString("N"));
        string a = Path.Combine(root, "Game A");
        string b = Path.Combine(root, "Game B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        try
        {
            var provider = new GogGameDiscoveryProvider(_ =>
                new GogGameDiscoveryProvider.RegisteredGame[]
                {
                    new("20", "Zulu", b),
                    new("10", "Alpha", a),
                    new("10", "Duplicate", a)
                });
            var games = provider.Discover(CancellationToken.None);
            Assert.Equal(2, games.Count);
            Assert.Equal(new[] { "10", "20" }, games.Select(g => g.ProviderGameId).ToArray());
            Assert.All(games, g => Assert.Equal("gog", g.ProviderId));
            Assert.Equal(Path.GetFullPath(a), games[0].InstallDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Incomplete_and_untrusted_install_paths_are_rejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-gog-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider = new GogGameDiscoveryProvider(_ =>
                new GogGameDiscoveryProvider.RegisteredGame[]
                {
                    new("not-numeric", "Invalid", root),
                    new("1", "Missing", Path.Combine(root, "missing")),
                    new("2", "Remote", @"\\server\share\game"),
                    new("3", "", root),
                    new("4", "Valid game", root)
                });
            var only = Assert.Single(provider.Discover(CancellationToken.None));
            Assert.Equal("4", only.ProviderGameId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancellation_stops_before_registry_enumeration()
    {
        int reads = 0;
        var provider = new GogGameDiscoveryProvider(_ =>
        {
            Interlocked.Increment(ref reads);
            return Array.Empty<GogGameDiscoveryProvider.RegisteredGame>();
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DiscoverAsync(cts.Token));
        Assert.Equal(0, reads);
    }
}

public sealed class GogGameDiscoverySecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GogGameDiscoverySecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Gog_discovery_requires_desktop_owner()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/gaming/library/discover/gog")).StatusCode);
    }
}
