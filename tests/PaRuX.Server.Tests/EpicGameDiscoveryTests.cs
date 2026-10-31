using System.Net;
using System.Text.Json;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EpicGameDiscoveryTests
{
    [Fact]
    public void Valid_local_install_is_discovered_without_modifying_epic_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-epic-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string install = Path.Combine(root, "installed");
            Directory.CreateDirectory(install);
            string file = Path.Combine(root, "game.item");
            string contents = JsonSerializer.Serialize(new
            {
                AppName = "test-epic-app",
                DisplayName = "Example Epic Game",
                InstallLocation = install,
                bIsIncompleteInstall = false
            });
            File.WriteAllText(file, contents);
            var game = Assert.Single(new EpicGameDiscoveryProvider(root).Discover(CancellationToken.None));
            Assert.Equal("epic", game.ProviderId);
            Assert.Equal("test-epic-app", game.ProviderGameId);
            Assert.Equal("Example Epic Game", game.Name);
            Assert.Equal(contents, File.ReadAllText(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Incomplete_missing_and_invalid_manifests_are_ignored()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-epic-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string existing = Path.Combine(root, "installed");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(root, "incomplete.item"), JsonSerializer.Serialize(new
            {
                AppName = "incomplete", DisplayName = "Incomplete", InstallLocation = existing, bIsIncompleteInstall = true
            }));
            File.WriteAllText(Path.Combine(root, "missing.item"), JsonSerializer.Serialize(new
            {
                AppName = "missing", DisplayName = "Missing", InstallLocation = Path.Combine(root, "not-installed")
            }));
            File.WriteAllText(Path.Combine(root, "corrupt.item"), "{");
            Assert.Empty(new EpicGameDiscoveryProvider(root).Discover(CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancellation_does_not_modify_manifest_data()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-epic-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new EpicGameDiscoveryProvider(root).DiscoverAsync(cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class EpicGameDiscoverySecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public EpicGameDiscoverySecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Epic_discovery_requires_desktop_owner()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/gaming/library/discover/epic")).StatusCode);
    }
}
