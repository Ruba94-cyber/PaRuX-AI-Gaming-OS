using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameLibraryHomeTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static GameTitleDto Title(string name, string source = GameInstallationSources.Steam, string state = GameInstallStates.Installed,
        DateTime? last = null, long seconds = 0, bool favorite = false, bool hidden = false, DateTime? added = null) =>
        new(name.ToLowerInvariant().Replace(" ", ""), name, favorite, Array.Empty<string>(), seconds, last, null,
            new[] { new GameInstallationDto(source + ":" + name, source, name, "game", name) { InstallState = state, Platform = "PC" } })
        { Hidden = hidden, AddedUtc = added };

    private static IReadOnlyList<string> NoGenres(string key) => Array.Empty<string>();

    private static IReadOnlyList<string> Genres(string key) => key switch
    {
        "alpha" => new[] { "Racing", "Arcade" },
        "bravo" => new[] { "racing" },
        "charlie" => new[] { "Puzzle" },
        _ => Array.Empty<string>(),
    };

    [Fact]
    public void Hero_is_the_most_recently_played_visible_startable_title()
    {
        var titles = new[]
        {
            Title("Alpha", last: Now.AddDays(-5), seconds: 100),
            Title("Bravo", last: Now.AddDays(-1), seconds: 50),
            Title("Hidden Gem", last: Now.AddHours(-1), hidden: true),
            Title("Broken", state: GameInstallStates.Incomplete, last: Now.AddMinutes(-5)),
            Title("Never", last: null),
        };

        Assert.Equal("Bravo", GameLibraryHome.PickHero(titles)!.Name);
    }

    [Fact]
    public void Hero_is_absent_without_play_history_instead_of_inventing_one()
    {
        Assert.Null(GameLibraryHome.PickHero(new[] { Title("Alpha"), Title("Bravo") }));
        Assert.Null(GameLibraryHome.PickHero(Array.Empty<GameTitleDto>()));
    }

    [Fact]
    public void Hero_tie_on_time_prefers_more_play_time_then_name()
    {
        var t = Now.AddDays(-2);
        var titles = new[] { Title("Zulu", last: t, seconds: 10), Title("Alpha", last: t, seconds: 10), Title("Mike", last: t, seconds: 99) };

        Assert.Equal("Mike", GameLibraryHome.PickHero(titles)!.Name);
        Assert.Equal("Alpha", GameLibraryHome.PickHero(titles.Where(x => x.Name != "Mike"))!.Name);
    }

    [Fact]
    public void Facets_count_visible_titles_only_and_in_a_fixed_store_order()
    {
        var titles = new[]
        {
            Title("Alpha", GameInstallationSources.Epic, favorite: true, last: Now.AddDays(-3)),
            Title("Bravo", GameInstallationSources.Steam, state: GameInstallStates.Unknown, last: Now.AddDays(-90)),
            Title("Charlie", GameInstallationSources.Xbox),
            Title("Ghost", GameInstallationSources.Steam, hidden: true, favorite: true, last: Now),
        };

        var facets = GameLibraryHome.Facets(titles, Genres, Now);

        Assert.Equal(3, facets.Total);
        Assert.Equal(new[] { "steam", "epic", "xbox" }, facets.Stores.Select(s => s.Key));
        Assert.All(facets.Stores, s => Assert.Equal(1, s.Value));
        Assert.Equal(2, facets.Installed);
        Assert.Equal(1, facets.NotInstalled);   // Unknown install state is not "installed"
        Assert.Equal(1, facets.Favorites);
        Assert.Equal(1, facets.RecentlyPlayed); // 90 days ago is outside the window; hidden Ghost is not counted
    }

    [Fact]
    public void Facet_genres_merge_case_insensitively_and_sort_by_count()
    {
        var facets = GameLibraryHome.Facets(new[] { Title("Alpha"), Title("Bravo"), Title("Charlie") }, Genres, Now);

        Assert.Equal("Racing", facets.Genres[0].Key);
        Assert.Equal(2, facets.Genres[0].Value);
        Assert.Contains(facets.Genres, g => g.Key == "Puzzle" && g.Value == 1);
    }

    [Fact]
    public void Apply_combines_filters_with_and()
    {
        var titles = new[]
        {
            Title("Alpha", GameInstallationSources.Steam, favorite: true, last: Now.AddDays(-2)),
            Title("Bravo", GameInstallationSources.Steam, favorite: false, last: Now.AddDays(-2)),
            Title("Charlie", GameInstallationSources.Epic, favorite: true, state: GameInstallStates.Incomplete),
        };

        var result = GameLibraryHome.Apply(titles, new GameHomeFilter { Source = "steam", FavoritesOnly = true, Installed = true, RecentlyPlayedOnly = true },
            null, NoGenres, Now);

        Assert.Equal(new[] { "Alpha" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Apply_genre_filter_is_case_insensitive()
    {
        var titles = new[] { Title("Alpha"), Title("Bravo"), Title("Charlie") };

        var result = GameLibraryHome.Apply(titles, new GameHomeFilter { Genre = "RACING" }, null, Genres, Now);

        Assert.Equal(new[] { "Alpha", "Bravo" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Apply_sorts_stably()
    {
        var titles = new[]
        {
            Title("Bravo", last: Now.AddDays(-1), seconds: 10, added: Now.AddDays(-10)),
            Title("Alpha", last: Now.AddDays(-3), seconds: 500, added: Now.AddDays(-1)),
            Title("Charlie", last: null, seconds: 10, added: null),
        };
        string[] Names(string? sort) => GameLibraryHome.Apply(titles, new GameHomeFilter(), sort, NoGenres, Now).Select(t => t.Name).ToArray();

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, Names(GameTitleSorts.Name));
        Assert.Equal(new[] { "Bravo", "Alpha", "Charlie" }, Names(GameTitleSorts.LastPlayed));
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, Names(GameTitleSorts.PlayTime));
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, Names(GameTitleSorts.RecentlyAdded));
    }

    [Theory]
    [InlineData(0, 10, 4, GameLibraryHome.Direction.Right, 1)]
    [InlineData(9, 10, 4, GameLibraryHome.Direction.Right, 9)]
    [InlineData(0, 10, 4, GameLibraryHome.Direction.Left, 0)]
    [InlineData(5, 10, 4, GameLibraryHome.Direction.Up, 1)]
    [InlineData(1, 10, 4, GameLibraryHome.Direction.Up, 1)]      // top row: stay, never jump to the far side
    [InlineData(5, 10, 4, GameLibraryHome.Direction.Down, 9)]
    [InlineData(7, 10, 4, GameLibraryHome.Direction.Down, 7)]    // no tile below in a ragged last row: stay
    [InlineData(5, 10, 4, GameLibraryHome.Direction.First, 0)]
    [InlineData(5, 10, 4, GameLibraryHome.Direction.Last, 9)]
    [InlineData(1, 40, 4, GameLibraryHome.Direction.PageDown, 13)]
    [InlineData(20, 40, 4, GameLibraryHome.Direction.PageUp, 8)]
    [InlineData(3, 10, 0, GameLibraryHome.Direction.Down, 4)]    // bad column count is treated as a single column
    public void Move_follows_grid_geometry(int current, int count, int columns, GameLibraryHome.Direction direction, int expected) =>
        Assert.Equal(expected, GameLibraryHome.Move(current, count, columns, direction));

    [Fact]
    public void Move_on_an_empty_grid_returns_minus_one() =>
        Assert.Equal(-1, GameLibraryHome.Move(0, 0, 4, GameLibraryHome.Direction.Right));

    [Theory]
    [InlineData(GameInstallationSources.Ea, true)]
    [InlineData(GameInstallationSources.BattleNet, true)]
    [InlineData(GameInstallationSources.Ubisoft, true)]
    [InlineData(GameInstallationSources.Xbox, true)]
    [InlineData(GameInstallationSources.Steam, false)]
    [InlineData(GameInstallationSources.Epic, false)]
    [InlineData(GameInstallationSources.Gog, false)]
    [InlineData(GameInstallationSources.Manual, false)]
    [InlineData(null, false)]
    public void Only_the_stores_not_yet_checked_on_a_real_install_carry_the_unverified_note(string? source, bool unverified)
    {
        Assert.Equal(unverified, GameLibraryHome.IsUnverifiedStore(source));
        Assert.Equal(unverified, GameLibraryHome.UnverifiedStoreNote(source, false).Length > 0);
        if (unverified) Assert.Contains("not been verified", GameLibraryHome.UnverifiedStoreNote(source, false));
    }

    [Fact]
    public void Quick_resume_summary_reports_pauses_and_never_hides_a_failed_recovery()
    {
        var app = new QuickResumeAppDto(1, "app", "w", Now, true, true, null);
        var idle = new QuickResumeStatusDto(Array.Empty<QuickResumeAppDto>());
        Assert.Equal("Nothing paused", GameLibraryHome.QuickResumeSummary(idle, false));

        var recovered = new QuickResumeStatusDto(new[] { app }, new[]
        {
            new QuickResumeRecoveryDto(2, "a", "resumed", "ok", Now, "startup"),
            new QuickResumeRecoveryDto(3, "b", "failed", "denied", Now, "startup"),
            new QuickResumeRecoveryDto(4, "c", "already-closed", "gone", Now, "startup"),
        });
        string text = GameLibraryHome.QuickResumeSummary(recovered, false);
        Assert.Contains("1 paused", text);
        Assert.Contains("1 resumed automatically", text);
        Assert.Contains("1 could NOT be resumed", text);
        Assert.Contains("NON ripresi", GameLibraryHome.QuickResumeSummary(recovered, true));
    }
}
