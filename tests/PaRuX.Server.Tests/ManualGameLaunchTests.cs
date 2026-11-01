using System.Diagnostics;
using System.Net;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualGameLaunchTests
{
    [Fact]
    public void Fresh_profiles_are_manual_and_do_not_launch_store_apps_unprompted()
    {
        var local = GamingProfileStore.BuildDefaultLocalPreset();
        var remote = GamingProfileStore.BuildDefaultRemotePreset();
        Assert.True(local.Enabled);
        Assert.Equal("manual", local.TriggerProvider);
        Assert.Equal("none", local.LauncherProvider);
        Assert.Equal("native", local.InputProvider);
        Assert.False(remote.Enabled);
    }

    [Fact]
    public void Existing_customized_trigger_and_launcher_are_preserved()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-profile-preserve-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(root, "profiles.json");
            var store = new GamingProfileStore(file);
            var existing = store.Get("preset-local-console")!;
            existing.TriggerProvider = "display";
            existing.LauncherProvider = "playnite";
            store.Save(existing);
            var reloaded = new GamingProfileStore(file).Get("preset-local-console")!;
            Assert.Equal("display", reloaded.TriggerProvider);
            Assert.Equal("playnite", reloaded.LauncherProvider);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Manual_game_path_validation_requires_existing_local_regular_exe()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-launch-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string valid = Path.Combine(root, "fixture.exe");
            string script = Path.Combine(root, "fixture.cmd");
            File.WriteAllText(valid, "test data, not executable");
            File.WriteAllText(script, "test data");
            Assert.True(ManualGameSessionService.IsSafeLocalExecutable(valid));
            Assert.False(ManualGameSessionService.IsSafeLocalExecutable(script));
            Assert.False(ManualGameSessionService.IsSafeLocalExecutable(Path.Combine(root, "missing.exe")));
            Assert.False(ManualGameSessionService.IsSafeLocalExecutable("game.exe"));
            Assert.False(ManualGameSessionService.IsSafeLocalExecutable(@"\\server\share\game.exe"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Manual_game_launch_rejects_a_regular_exe_under_an_existing_junction()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "parux-game-parent-link-" + Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "game");
        string link = Path.Combine(root, "linked-game");
        Directory.CreateDirectory(installed);
        try
        {
            File.WriteAllText(Path.Combine(installed, "game.exe"), "fixture only");
            try { Directory.CreateSymbolicLink(link, installed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or PlatformNotSupportedException)
            {
                return; // Link creation is privileged on some Windows machines.
            }
            Assert.False(ManualGameSessionService.IsSafeLocalExecutable(
                Path.Combine(link, "game.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("730", true)]
    [InlineData("123456789012345", true)]
    [InlineData("730 -console", false)]
    [InlineData("730/launch", false)]
    [InlineData("steam://rungameid/730", false)]
    [InlineData("", false)]
    [InlineData("1234567890123456", false)]
    public void Steam_launch_only_accepts_numeric_installed_game_ids(string id, bool expected)
    {
        Assert.Equal(expected, ManualGameSessionService.ValidSteamAppId(id));
    }

    [Fact]
    public void A_different_executable_is_not_the_launched_process()
    {
        using Process process = Process.GetCurrentProcess();
        Assert.False(ManualGameSessionService.VerifyOwnedProcess(
            process, Path.Combine(Path.GetTempPath(), "not-the-process.exe")));
    }
}

public sealed class ManualGameLaunchSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public ManualGameLaunchSecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Steam_launch_requires_desktop_owner_authorization()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/gaming/library/discover/steam/730/launch", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Gog_launch_requires_desktop_owner_authorization()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/gaming/library/discover/gog/1207659123/launch", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Manual_game_launch_requires_desktop_owner_authorization()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/gaming/library/nonexistent/launch", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Manual_TV_routing_hint_cannot_bypass_desktop_owner_authorization()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/gaming/library/nonexistent/launch?displayId=DISPLAY2", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
