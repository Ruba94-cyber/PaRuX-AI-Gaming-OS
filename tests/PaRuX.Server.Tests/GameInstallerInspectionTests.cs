using System.Net;
using System.Net.Http.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameInstallerInspectionTests
{
    [Fact]
    public async Task Computes_SHA256_of_user_selected_local_archive_without_executing_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-installer-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "fixture.zip");
            await File.WriteAllTextAsync(path, "abc");
            var service = new GameInstallerInspectionService(() => false);

            GameInstallerInspectionResult result =
                await service.InspectAsync(new GameInstallerInspectionRequest(path), CancellationToken.None);

            Assert.Equal("fixture.zip", result.FileName);
            Assert.Equal(3, result.SizeBytes);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", result.Sha256);
            Assert.Contains("NOT a malware", result.Advisory, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("abc", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Gaming_always_refuses_inspection_before_opening_any_file()
    {
        var service = new GameInstallerInspectionService(() => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InspectAsync(new GameInstallerInspectionRequest("not-a-file.exe"), CancellationToken.None));
    }

    [Fact]
    public void Rejects_network_relative_and_noninstaller_paths()
    {
        Assert.False(GameInstallerInspectionService.ValidLocalPackagePath("installer.exe"));
        Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(@"\\server\share\installer.exe"));
        Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(Path.Combine(
            Path.GetTempPath(), "missing.zip")));
    }

    [Fact]
    public async Task Gaming_transition_cancels_an_in_progress_hash_lease()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-installer-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "fixture.msi");
            await File.WriteAllBytesAsync(path, new byte[32768]);
            using var gaming = new CancellationTokenSource();
            gaming.Cancel();
            var service = new GameInstallerInspectionService(() => false, () => gaming.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.InspectAsync(new GameInstallerInspectionRequest(path), CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class GameInstallerInspectionSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GameInstallerInspectionSecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Local_installer_hash_endpoint_requires_desktop_owner()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/gaming/installers/inspect", new GameInstallerInspectionRequest("missing.exe"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
