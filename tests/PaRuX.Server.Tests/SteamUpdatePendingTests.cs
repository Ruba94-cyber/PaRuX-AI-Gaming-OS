using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>GAM-006: Steam's own manifest says a newer build is waiting. Local data only; no store is contacted.</summary>
public sealed class SteamUpdatePendingTests
{
    [Theory]
    [InlineData("4", "100", "100", false)]       // fully installed, same build
    [InlineData("4", "100", "101", true)]        // target build differs from the installed one
    [InlineData("6", "100", "100", true)]        // fully installed + update required
    [InlineData("6", null, null, true)]
    [InlineData("4", "0", "101", false)]         // unknown installed build is not evidence
    [InlineData("4", "100", "0", false)]
    [InlineData("4", "abc", "101", false)]
    [InlineData("1030", "100", "101", false)]    // update started: that is the "updating" install state
    [InlineData("260", "100", "101", false)]     // update running
    [InlineData("1", "100", "101", false)]       // uninstalled
    [InlineData("36", "100", "101", false)]      // files missing is a repair, not an update
    [InlineData("2", "100", "101", false)]       // not fully installed
    [InlineData("", "100", "101", false)]
    [InlineData(null, "100", "101", false)]
    public void Pending_update_is_read_from_the_manifest_flags_and_build_ids(string? flags, string? build, string? target, bool expected) =>
        Assert.Equal(expected, SteamGameDiscoveryProvider.UpdatePendingFromManifest(flags, build, target));

    [Fact]
    public void Discovery_reports_the_pending_update_and_the_catalog_carries_it_to_the_installation_without_changing_the_install_state()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            string steamapps = Path.Combine(root, "steamapps");
            foreach ((string id, string name, string flags, string build, string target) in new[]
            {
                ("1", "Updated Quest", "4", "10", "10"),
                ("2", "Waiting Quest", "4", "10", "11"),
            })
            {
                Directory.CreateDirectory(Path.Combine(steamapps, "common", name));
                File.WriteAllText(Path.Combine(steamapps, "appmanifest_" + id + ".acf"),
                    "\"AppState\"\n{\n\"appid\" \"" + id + "\"\n\"name\" \"" + name + "\"\n\"installdir\" \"" + name
                    + "\"\n\"StateFlags\" \"" + flags + "\"\n\"buildid\" \"" + build + "\"\n\"TargetBuildID\" \"" + target + "\"\n}");
            }

            IReadOnlyList<DiscoveredGameDto> games = new SteamGameDiscoveryProvider(root).Discover(CancellationToken.None);
            Assert.False(games.Single(g => g.Name == "Updated Quest").UpdatePending);
            Assert.True(games.Single(g => g.Name == "Waiting Quest").UpdatePending);

            IReadOnlyList<GameTitleDto> titles = GameTitleCatalog.Build(games, Array.Empty<ManualGameDto>(),
                new Dictionary<string, GameTitleState>());
            GameInstallationDto waiting = titles.Single(t => t.Name == "Waiting Quest").Installations.Single();
            Assert.True(waiting.UpdatePending);
            Assert.Equal(GameInstallStates.Installed, waiting.InstallState); // still playable and still "installed"
            Assert.False(titles.Single(t => t.Name == "Updated Quest").Installations.Single().UpdatePending);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_pending_update_changes_how_the_tile_looks_so_a_refresh_rebinds_it()
    {
        GameInstallationDto Installation(bool pending) =>
            new("steam:1", GameInstallationSources.Steam, "1", ManualEntryKinds.Game, "Quest")
            { InstallState = GameInstallStates.Installed, UpdatePending = pending };
        GameTitleDto Title(bool pending) => new("quest", "Quest", false, Array.Empty<string>(), 0, null, null, new[] { Installation(pending) });
        Assert.False(GameTileListModel.SameFace(Title(false), Title(true)));
        Assert.True(GameTileListModel.SameFace(Title(true), Title(true)));
    }
}
