using System.Net;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EpicGameLaunchProtocolTests
{
    [Theory]
    [InlineData("Cobra", true)]
    [InlineData("test-epic-app", true)]
    [InlineData("game_2026.1", true)]
    [InlineData("fn:example", false)]
    [InlineData("../game", false)]
    [InlineData("game?x=launch", false)]
    [InlineData("foo&silent=false", false)]
    [InlineData("com.epicgames.launcher://apps/game", false)]
    [InlineData("", false)]
    public void Game_id_accepts_only_an_installed_catalog_identifier(string id, bool allowed)
    {
        Assert.Equal(allowed, EpicGameLaunchProtocol.IsValidGameId(id));
    }

    [Fact]
    public void Documented_protocol_uri_encodes_install_directory_not_untrusted_game_arguments()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "parux-epic-protocol-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "Game & DLC #1");
        try
        {
            Directory.CreateDirectory(directory);
            Assert.True(EpicGameLaunchProtocol.TryBuildInstalledGameUri(directory, out string? uri));
            Assert.Equal("com.epicgames.launcher://apps/" +
                         Uri.EscapeDataString(Path.GetFullPath(directory)) +
                         "?action=launch&silent=true", uri);
            Assert.DoesNotContain("Game & DLC #1?", uri);
            Assert.DoesNotContain("#1?action", uri);
            Assert.DoesNotContain(" /command=", uri);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_relative_and_network_paths_cannot_construct_an_activation()
    {
        Assert.False(EpicGameLaunchProtocol.TryBuildInstalledGameUri("relative/game", out _));
        Assert.False(EpicGameLaunchProtocol.TryBuildInstalledGameUri(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), out _));
        Assert.False(EpicGameLaunchProtocol.TryBuildInstalledGameUri(@"\\server\share\game", out _));
        Assert.False(EpicGameLaunchProtocol.IsValidGameId(new string('a', 201)));
    }

    [Fact]
    public void Epic_activation_refuses_linked_parent_install_folders()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "parux-epic-linked-" + Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "real", "game");
        string shortcut = Path.Combine(root, "linked");
        Directory.CreateDirectory(installed);
        try
        {
            try { Directory.CreateSymbolicLink(shortcut, Path.Combine(root, "real")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or PlatformNotSupportedException)
            {
                return;
            }
            Assert.False(EpicGameLaunchProtocol.TryBuildInstalledGameUri(
                Path.Combine(shortcut, "game"), out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Non_windows_environment_has_no_epic_registered_protocol()
    {
        if (!OperatingSystem.IsWindows())
            Assert.False(EpicGameLaunchProtocol.IsWindowsProtocolRegistered());
    }
}

public sealed class EpicGameLaunchSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public EpicGameLaunchSecurityTests(ApiSecurityTests.PaRuXFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Epic_launch_requires_desktop_owner_even_for_a_known_game_id()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage result = await client.PostAsync(
            "/api/v1/gaming/library/discover/epic/Cobra/launch", null);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }
}
