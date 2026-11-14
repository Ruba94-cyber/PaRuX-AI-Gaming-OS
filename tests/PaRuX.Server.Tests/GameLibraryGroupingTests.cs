using System.Diagnostics;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Cover size, grouping and cross-group keyboard movement of the Gaming library (GAM-004 remainder).</summary>
public sealed class GameLibraryGroupingTests
{
    private static GameTitleDto Title(int n, string source = GameInstallationSources.Manual, string platform = "PC",
        string status = "", string state = GameInstallStates.Installed)
    {
        string name = "Synthetic Game " + n.ToString("D4");
        return new GameTitleDto(GameLibraryQuery.NormalizeName(name), name, false, Array.Empty<string>(), 0, null, null,
            new[] { new GameInstallationDto("i:" + n, source, n.ToString(), ManualEntryKinds.Game, name)
                { InstallState = state, Platform = platform } })
        { Status = status };
    }

    [Theory]
    [InlineData(null, GameCoverSizes.Medium)]
    [InlineData("", GameCoverSizes.Medium)]
    [InlineData("huge", GameCoverSizes.Medium)]
    [InlineData("SMALL", GameCoverSizes.Small)]
    [InlineData(" large ", GameCoverSizes.Large)]
    public void Cover_size_normalizes_unknown_values_to_medium(string? raw, string expected) =>
        Assert.Equal(expected, GameCoverSizes.Normalize(raw));

    [Fact]
    public void Cover_scale_is_ordered_and_medium_is_unchanged()
    {
        Assert.Equal(1.0, GameCoverSizes.Scale(GameCoverSizes.Medium));
        Assert.True(GameCoverSizes.Scale(GameCoverSizes.Small) < 1.0);
        Assert.True(GameCoverSizes.Scale(GameCoverSizes.Large) > 1.0);
        Assert.Equal(1.0, GameCoverSizes.Scale("garbage"));
    }

    [Theory]
    [InlineData(null, GameGroupModes.None)]
    [InlineData("platform", GameGroupModes.Platform)]
    [InlineData("SOURCE", GameGroupModes.Source)]
    [InlineData("status", GameGroupModes.Status)]
    [InlineData("genre", GameGroupModes.None)]
    public void Group_mode_normalizes_unknown_values_to_none(string? raw, string expected) =>
        Assert.Equal(expected, GameGroupModes.Normalize(raw));

    [Fact]
    public void No_grouping_is_one_group_with_everything_in_order()
    {
        List<GameTitleDto> titles = Enumerable.Range(0, 5).Select(i => Title(i)).ToList();
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(titles, GameGroupModes.None);
        GameTileGroup only = Assert.Single(groups);
        Assert.Equal(titles.Select(t => t.TitleKey), only.Titles.Select(t => t.TitleKey));
        Assert.Empty(GameLibraryGrouping.Build(Array.Empty<GameTitleDto>(), GameGroupModes.None));
    }

    [Fact]
    public void Status_groups_follow_progress_order_with_unset_last_and_keep_input_order()
    {
        var titles = new List<GameTitleDto>
        {
            Title(1, status: GameCompletionStatuses.Completed), Title(2), Title(3, status: GameCompletionStatuses.Playing),
            Title(4, status: GameCompletionStatuses.Backlog), Title(5, status: GameCompletionStatuses.Playing),
            Title(6, status: GameCompletionStatuses.Dropped), Title(7, status: GameCompletionStatuses.OnHold),
        };
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(titles, GameGroupModes.Status);
        Assert.Equal(new[] { "playing", "backlog", "onhold", "completed", "dropped", "" }, groups.Select(g => g.Key));
        Assert.Equal(new[] { titles[2].TitleKey, titles[4].TitleKey }, groups[0].Titles.Select(t => t.TitleKey));
    }

    [Fact]
    public void Source_groups_follow_store_order_and_unknown_sources_come_after_known_ones()
    {
        var titles = new List<GameTitleDto>
        {
            Title(1, GameInstallationSources.Gog), Title(2, GameInstallationSources.Steam), Title(3, "futurestore"),
            Title(4, GameInstallationSources.Emulator), Title(5, GameInstallationSources.Steam),
        };
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(titles, GameGroupModes.Source);
        Assert.Equal(new[] { GameInstallationSources.Steam, GameInstallationSources.Gog, GameInstallationSources.Emulator, "futurestore" },
            groups.Select(g => g.Key));
        Assert.Equal(2, groups[0].Titles.Count);
    }

    [Fact]
    public void Platform_groups_are_alphabetical_with_the_unknown_bucket_last()
    {
        var titles = new List<GameTitleDto>
        {
            Title(1, platform: "PC"), Title(2, platform: ""), Title(3, platform: "Nintendo Switch"), Title(4, platform: "pc"),
        };
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(titles, GameGroupModes.Platform);
        Assert.Equal(3, groups.Count);
        Assert.Equal("Nintendo Switch", groups[0].Key);
        Assert.Equal(2, groups[1].Titles.Count); // "PC" and "pc" are one platform
        Assert.Equal(string.Empty, groups[2].Key);
    }

    [Fact]
    public void A_title_with_two_installations_lands_in_exactly_one_group_by_default_then_installed_then_first()
    {
        GameTitleDto both = Title(1) with
        {
            Installations = new[]
            {
                new GameInstallationDto("a", GameInstallationSources.Steam, "1", ManualEntryKinds.Game, "x") { InstallState = GameInstallStates.MetadataOnly },
                new GameInstallationDto("b", GameInstallationSources.Gog, "2", ManualEntryKinds.Game, "x") { InstallState = GameInstallStates.Installed },
            }
        };
        Assert.Equal(GameInstallationSources.Gog, GameLibraryGrouping.KeyOf(both, GameGroupModes.Source));
        Assert.Equal(GameInstallationSources.Steam,
            GameLibraryGrouping.KeyOf(both with { DefaultInstallationId = "a" }, GameGroupModes.Source));
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(new[] { both }, GameGroupModes.Source);
        Assert.Equal(1, groups.Sum(g => g.Titles.Count));
    }

    [Fact]
    public void Two_thousand_titles_group_into_complete_disjoint_groups_within_budget()
    {
        string[] sources = { GameInstallationSources.Steam, GameInstallationSources.Epic, GameInstallationSources.Gog, GameInstallationSources.Manual };
        string[] statuses = { "", "playing", "backlog", "completed" };
        List<GameTitleDto> titles = Enumerable.Range(0, 2000)
            .Select(i => Title(i, sources[i % 4], platform: i % 7 == 0 ? "Switch" : "PC", status: statuses[i % 4])).ToList();
        var timer = Stopwatch.StartNew();
        foreach (string mode in new[] { GameGroupModes.Source, GameGroupModes.Platform, GameGroupModes.Status })
        {
            IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(titles, mode);
            Assert.Equal(2000, groups.Sum(g => g.Titles.Count));
            Assert.Equal(2000, groups.SelectMany(g => g.Titles).Select(t => t.TitleKey).Distinct().Count());
            Assert.DoesNotContain(groups, g => g.Titles.Count == 0);
        }
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(750), "grouping must stay linear: " + timer.Elapsed);
    }

    // Keyboard movement across groups with 3 columns and group sizes 4, 2 and 5.
    private static readonly int[] Sizes = { 4, 2, 5 };

    private static (int, int) Step(int g, int i, GameLibraryHome.Direction d) => GameLibraryGrouping.Move(Sizes, 3, g, i, d);

    [Fact]
    public void Left_and_right_continue_into_the_neighbouring_group_and_stop_at_the_ends()
    {
        Assert.Equal((1, 0), Step(0, 3, GameLibraryHome.Direction.Right));
        Assert.Equal((2, 0), Step(1, 1, GameLibraryHome.Direction.Right));
        Assert.Equal((2, 4), Step(2, 4, GameLibraryHome.Direction.Right));
        Assert.Equal((0, 3), Step(1, 0, GameLibraryHome.Direction.Left));
        Assert.Equal((1, 1), Step(2, 0, GameLibraryHome.Direction.Left));
        Assert.Equal((0, 0), Step(0, 0, GameLibraryHome.Direction.Left));
    }

    [Fact]
    public void Up_and_down_keep_the_column_across_groups_and_clamp_to_the_last_tile()
    {
        Assert.Equal((0, 3), Step(0, 0, GameLibraryHome.Direction.Down)); // within the group
        Assert.Equal((1, 0), Step(0, 3, GameLibraryHome.Direction.Down)); // last row of group 0 -> group 1, column 0
        Assert.Equal((0, 3), Step(0, 1, GameLibraryHome.Direction.Down)); // short last row has no column 1: last tile
        Assert.Equal((0, 3), Step(0, 2, GameLibraryHome.Direction.Down)); // index 2 has no tile below inside the group: last tile
        Assert.Equal((2, 1), Step(1, 1, GameLibraryHome.Direction.Down));
        Assert.Equal((1, 0), Step(2, 0, GameLibraryHome.Direction.Up));
        Assert.Equal((0, 3), Step(1, 0, GameLibraryHome.Direction.Up)); // group 0 last row starts at index 3
    }

    [Fact]
    public void Down_from_a_row_above_a_short_last_row_lands_on_the_last_tile_instead_of_sticking()
    {
        Assert.Equal((2, 4), Step(2, 2, GameLibraryHome.Direction.Down)); // 5 tiles, 3 columns: index 2 has no tile below
    }

    [Fact]
    public void Home_end_and_page_keys_cross_groups()
    {
        Assert.Equal((0, 0), Step(2, 4, GameLibraryHome.Direction.First));
        Assert.Equal((2, 4), Step(0, 0, GameLibraryHome.Direction.Last));
        Assert.Equal((2, 0), Step(0, 0, GameLibraryHome.Direction.PageDown));
        Assert.Equal((0, 0), Step(1, 0, GameLibraryHome.Direction.PageUp));
    }

    [Fact]
    public void Movement_skips_empty_groups_and_reports_nothing_to_focus_for_an_empty_library()
    {
        int[] sizes = { 2, 0, 2 };
        Assert.Equal((2, 0), GameLibraryGrouping.Move(sizes, 3, 0, 1, GameLibraryHome.Direction.Right));
        Assert.Equal((0, 1), GameLibraryGrouping.Move(sizes, 3, 2, 0, GameLibraryHome.Direction.Left));
        Assert.Equal((-1, -1), GameLibraryGrouping.Move(new[] { 0, 0 }, 3, 0, 0, GameLibraryHome.Direction.Right));
        Assert.Equal((-1, -1), GameLibraryGrouping.Move(Array.Empty<int>(), 3, 0, 0, GameLibraryHome.Direction.First));
    }

    [Fact]
    public void Walking_right_visits_every_tile_exactly_once()
    {
        var visited = new List<(int, int)> { (0, 0) };
        (int g, int i) = (0, 0);
        for (int guard = 0; guard < 100; guard++)
        {
            (int ng, int ni) = Step(g, i, GameLibraryHome.Direction.Right);
            if ((ng, ni) == (g, i)) break;
            (g, i) = (ng, ni);
            visited.Add((g, i));
        }
        Assert.Equal(Sizes.Sum(), visited.Count);
        Assert.Equal(visited.Count, visited.Distinct().Count());
    }
}
