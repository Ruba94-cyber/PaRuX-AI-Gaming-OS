using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SteamGameDiscoveryTests
{
    [Fact]
    public void Discovers_valid_local_manifest_without_modifying_any_source_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            string steamapps = Path.Combine(root, "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "Demo"));
            string manifest = Path.Combine(steamapps, "appmanifest_42.acf");
            const string original = "\"AppState\"\n{\n\"appid\" \"42\"\n\"name\" \"Demo Game\"\n\"installdir\" \"Demo\"\n}";
            File.WriteAllText(manifest, original);
            var provider = new SteamGameDiscoveryProvider(root);

            var game = Assert.Single(provider.Discover(CancellationToken.None));
            Assert.Equal("steam", game.ProviderId);
            Assert.Equal("42", game.ProviderGameId);
            Assert.Equal("Demo Game", game.Name);
            Assert.Equal(Path.Combine(steamapps, "common", "Demo"), game.InstallDirectory);
            Assert.Equal(original, File.ReadAllText(manifest));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Ignores_uninstalled_and_traversal_manifests_without_scanning_other_directories()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            string steamapps = Path.Combine(root, "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "Safe"));
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"appid\" \"1\"\n\"name\" \"Path Escape\"\n\"installdir\" \"../outside\"");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_2.acf"), "\"appid\" \"2\"\n\"name\" \"Missing\"\n\"installdir\" \"NotInstalled\"");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_3.acf"), "\"appid\" \"3\"\n\"name\" \"Safe\"\n\"installdir\" \"Safe\"");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_4.acf"), new string('A', 300_000));
            var game = Assert.Single(new SteamGameDiscoveryProvider(root).Discover(CancellationToken.None));
            Assert.Equal("3", game.ProviderGameId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancelled_scan_does_not_start_execution_or_change_libraries()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new SteamGameDiscoveryProvider(root).DiscoverAsync(cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class SteamGameDiscoverySecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public SteamGameDiscoverySecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Steam_discovery_requires_desktop_owner()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/gaming/library/discover/steam")).StatusCode);
    }
}
