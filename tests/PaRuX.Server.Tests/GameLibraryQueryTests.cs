using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameLibraryQueryTests
{
    private static DiscoveredGameDto G(string store, string id, string name, string dir = @"C:\Games\x", string state = GameInstallStates.Installed) =>
        new(store, id, name, dir, state);

    [Theory]
    [InlineData("The Witcher® 3: Wild Hunt", "the witcher 3 wild hunt")]
    [InlineData("  DOOM  ", "doom")]
    [InlineData(null, "")]
    [InlineData("™ ®", "")]
    public void Names_normalize_to_letters_and_digits(string? input, string expected) =>
        Assert.Equal(GameLibraryQuery.NormalizeName(expected), GameLibraryQuery.NormalizeName(input));

    [Fact]
    public void Same_title_in_two_stores_is_detected_by_name_or_install_directory()
    {
        var steam = G("steam", "292030", "The Witcher® 3: Wild Hunt", @"C:\Steam\common\W3");
        var gog = G("gog", "1207664643", "The Witcher 3: Wild Hunt", @"D:\GOG\W3");
        Assert.True(GameLibraryQuery.IsSameTitleInAnotherStore(steam, gog));
        Assert.True(GameLibraryQuery.IsSameTitleInAnotherStore(
            G("steam", "1", "Alpha", @"C:\Shared\Game"), G("epic", "e", "Different Name", @"c:\shared\game\")));
        Assert.Equal(new[] { "GOG" }, GameLibraryQuery.OtherStores(steam, new[] { steam, gog }));
    }

    [Fact]
    public void Same_store_short_names_and_unrelated_titles_are_not_duplicates()
    {
        Assert.False(GameLibraryQuery.IsSameTitleInAnotherStore(G("steam", "1", "Doom"), G("steam", "2", "Doom")));
        Assert.False(GameLibraryQuery.IsSameTitleInAnotherStore(G("steam", "1", "Ab", @"C:\a"), G("gog", "2", "Ab", @"C:\b")));
        Assert.False(GameLibraryQuery.IsSameTitleInAnotherStore(G("steam", "1", "Portal", @"C:\a"), G("gog", "2", "Hades", @"C:\b")));
    }

    [Fact]
    public void Filter_combines_text_store_and_install_state()
    {
        var all = new[]
        {
            G("steam", "1", "Portal 2"), G("gog", "2", "Portal"), G("steam", "3", "Hades", state: GameInstallStates.Updating),
            G("epic", "4", "Fortnite", state: GameInstallStates.Unknown),
        };
        Assert.Equal(2, GameLibraryQuery.Filter(all, "portal", "", "").Count);
        Assert.Single(GameLibraryQuery.Filter(all, "portal", "steam", ""));
        Assert.Single(GameLibraryQuery.Filter(all, "", "", GameInstallStates.Updating));
        Assert.Equal(4, GameLibraryQuery.Filter(all, null, null, null).Count);
        Assert.Empty(GameLibraryQuery.Filter(all, "portal", "epic", ""));
    }

    [Theory]
    [InlineData("4", "installed")]
    [InlineData("6", "installed")]          // fully installed, update required: still playable
    [InlineData("1030", "updating")]        // fully installed + update started
    [InlineData("1026", "updating")]        // update required + started, not fully installed
    [InlineData("2", "incomplete")]
    [InlineData("36", "incomplete")]        // files missing
    [InlineData("132", "incomplete")]       // files corrupt
    [InlineData("1", "incomplete")]
    [InlineData("abc", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("-4", "unknown")]
    public void Steam_state_flags_map_conservatively(string? flags, string expected) =>
        Assert.Equal(expected, SteamGameDiscoveryProvider.InstallStateFromFlags(flags));

    [Fact]
    public void Launch_result_distinguishes_request_process_start_and_verified_identity()
    {
        Assert.Equal(GameStartStates.None, new ManualGameLaunchResult(false, false, "x").StartState);
        Assert.Equal(GameStartStates.Requested, new ManualGameLaunchResult(true, false, "x").StartState);
        Assert.Equal(GameStartStates.Verified, new ManualGameLaunchResult(true, true, "x").StartState);
        Assert.Equal(GameStartStates.ProcessStarted,
            (new ManualGameLaunchResult(true, false, "x") { StartState = GameStartStates.ProcessStarted }).StartState);
        var json = System.Text.Json.JsonSerializer.Serialize(new ManualGameLaunchResult(true, false, "x") { StartState = GameStartStates.ProcessStarted });
        Assert.Equal(GameStartStates.ProcessStarted,
            System.Text.Json.JsonSerializer.Deserialize<ManualGameLaunchResult>(json)!.StartState);
    }
}
