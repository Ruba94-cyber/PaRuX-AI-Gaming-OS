using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameTitleLaunchPolicyTests
{
    private static GameInstallationDto Installation(string id, string state) =>
        new(id, id.Split(':')[0], id.Split(':')[1], "game", "Shared title") { InstallState = state };

    private static GameTitleDto Title(string? preferred, params GameInstallationDto[] installations) =>
        new("shared-title", "Shared title", false, Array.Empty<string>(), 0, null, preferred, installations);

    [Fact]
    public void A_healthy_explicit_default_wins_over_other_installed_copies()
    {
        var steam = Installation("steam:42", GameInstallStates.Installed);
        var emulator = Installation("emulator:42", GameInstallStates.Installed);
        Assert.Same(emulator, GameTitleLaunchPolicy.ChooseForQuickPlay(Title(emulator.InstallationId, steam, emulator)));
    }

    [Theory]
    [InlineData(GameInstallStates.Incomplete)]
    [InlineData(GameInstallStates.Updating)]
    [InlineData("UPDATING")]
    public void A_blocked_default_falls_back_to_an_installed_copy_without_mutating_preference(string state)
    {
        var steam = Installation("steam:42", state);
        var emulator = Installation("emulator:42", GameInstallStates.Installed);
        var title = Title(steam.InstallationId, steam, emulator);

        Assert.Same(emulator, GameTitleLaunchPolicy.ChooseForQuickPlay(title));
        Assert.Equal(steam.InstallationId, title.DefaultInstallationId);
        Assert.False(GameTitleLaunchPolicy.CanStart(steam));
    }

    [Fact]
    public void A_known_installed_copy_beats_an_unknown_default()
    {
        var uncertain = Installation("steam:42", GameInstallStates.Unknown);
        var installed = Installation("manual:42", GameInstallStates.Installed);
        Assert.Same(installed, GameTitleLaunchPolicy.ChooseForQuickPlay(Title(uncertain.InstallationId, uncertain, installed)));
    }

    [Fact]
    public void Legacy_unknown_copy_is_usable_if_nothing_is_known_installed()
    {
        var updating = Installation("steam:42", GameInstallStates.Updating);
        var legacy = Installation("gog:42", GameInstallStates.Unknown);
        Assert.Same(legacy, GameTitleLaunchPolicy.ChooseForQuickPlay(Title(updating.InstallationId, updating, legacy)));
        Assert.True(GameTitleLaunchPolicy.CanStart(legacy));
    }

    [Fact]
    public void All_blocked_copies_remain_visible_but_cannot_start()
    {
        var incomplete = Installation("steam:42", GameInstallStates.Incomplete);
        var updating = Installation("epic:42", GameInstallStates.Updating);
        var chosen = GameTitleLaunchPolicy.ChooseForQuickPlay(Title(updating.InstallationId, incomplete, updating));
        Assert.Same(updating, chosen);
        Assert.False(GameTitleLaunchPolicy.CanStart(chosen));
    }

    [Fact]
    public void Missing_and_stale_defaults_fall_back_to_a_launchable_copy()
    {
        var unavailable = Installation("steam:42", GameInstallStates.Incomplete);
        var usable = Installation("manual:42", GameInstallStates.Installed);
        Assert.Same(usable, GameTitleLaunchPolicy.ChooseForQuickPlay(Title("steam:deleted", unavailable, usable)));
        Assert.Same(usable, GameTitleLaunchPolicy.ChooseForQuickPlay(Title(null, unavailable, usable)));
        Assert.Null(GameTitleLaunchPolicy.ChooseForQuickPlay(Title(null)));
        Assert.Null(GameTitleLaunchPolicy.ChooseForQuickPlay(null));
    }

    [Theory]
    [InlineData("uninstalled")]
    [InlineData("")]
    [InlineData("INCOMPLETE")]
    public void Unrecognized_or_unavailable_states_never_authorize_quick_play(string state) =>
        Assert.False(GameTitleLaunchPolicy.CanStart(Installation("steam:42", state)));
}
