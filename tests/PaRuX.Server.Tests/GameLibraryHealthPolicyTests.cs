using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure, instantaneous local-library view; no installed providers or WinUI required.</summary>
public sealed class GameLibraryHealthPolicyTests
{
    private static GameInstallationDto Install(string provider, string state) =>
        new(provider + ":1", provider, "1", "game", "Example") { InstallState = state };

    private static GameTitleDto Title(string key, string name, GameInstallationDto[] installs,
        bool favorite = false, long seconds = 0, bool hidden = false) =>
        new(key, name, favorite, Array.Empty<string>(), seconds,
            seconds > 0 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            null, installs) { Hidden = hidden };

    [Fact]
    public void A_cached_mixed_library_distinguishes_launch_candidates_and_metadata_only_imports()
    {
        GameTitleDto steam = Title("a", "Owned store", new[] { Install("steam", GameInstallStates.Installed) },
            favorite: true, seconds: 3600);
        GameTitleDto imported = Title("b", "Imported title", new[] { Install("playnite", GameInstallStates.MetadataOnly) });
        GameTitleDto missing = Title("c", "Needs installation", new[] { Install("epic", GameInstallStates.Incomplete) });
        GameTitleDto legacy = Title("d", "Legacy unknown", new[] { Install("gog", GameInstallStates.Unknown) });
        GameLibraryHealthSummary health = GameLibraryHealthPolicy.Summarize(new[] { steam, imported, missing, legacy });

        Assert.Equal(4, health.VisibleTitles);
        Assert.Equal(2, health.LaunchCandidates);
        Assert.Equal(1, health.ExplicitlyInstalledTitles);
        Assert.Equal(1, health.MetadataOnlyTitles);
        Assert.Equal(2, health.NeedsAttention);
        Assert.Equal(new[] { "Imported title", "Needs installation" }, health.AttentionTitles.Select(t => t.Name));
        Assert.Equal(1, health.Favorites);
        Assert.Equal(1, health.PlayedTitles);
        Assert.Equal(3600, health.TotalPlaySeconds);
    }

    [Fact]
    public void Known_provider_state_is_not_verified_executable_or_license_claim()
    {
        GameTitleDto metadataOnly = Title("x", "Only metadata", new[]
        {
            Install("heroic", GameInstallStates.Installed) // imported third-party JSON must NOT grant execution
        });
        var health = GameLibraryHealthPolicy.Summarize(new[] { metadataOnly });
        Assert.Equal(0, health.LaunchCandidates);
        Assert.Equal(0, health.ExplicitlyInstalledTitles);
        Assert.Equal(1, health.NeedsAttention);
    }

    [Fact]
    public void Hidden_and_duplicate_titles_do_not_inflate_visible_statistics()
    {
        GameTitleDto title = Title("x", "One", new[] { Install("steam", GameInstallStates.Installed) });
        GameTitleDto hidden = Title("secret", "Hidden", Array.Empty<GameInstallationDto>(), hidden: true);
        GameLibraryHealthSummary result = GameLibraryHealthPolicy.Summarize(new[] { title, title, hidden });
        Assert.Equal(1, result.VisibleTitles);
        Assert.Equal(1, result.LaunchCandidates);
        Assert.Equal(0, result.NeedsAttention);
    }

    [Fact]
    public void Zero_titles_and_corrupt_negative_playtime_cannot_report_fake_hours()
    {
        Assert.Equal(0, GameLibraryHealthPolicy.Summarize(null).VisibleTitles);
        var result = GameLibraryHealthPolicy.Summarize(new[]
        {
            Title("bad", "Negative duration", Array.Empty<GameInstallationDto>(), seconds: -300)
        });
        Assert.Equal(0, result.TotalPlaySeconds);
        Assert.Equal(1, result.NeedsAttention);
    }
}
