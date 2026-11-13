using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Logic behind the virtualized Gaming library (GAM-004): list diff, key index, grid columns, the bounded cover cache and the
/// stamped index cache, plus filter/sort/build over 2,000 synthetic titles. Everything here is in memory (no file I/O),
/// which is itself part of the contract: refreshing a 2,000-title library must not touch the disk per title.
/// </summary>
public sealed class GameTileListModelTests
{
    // Generous on purpose (shared CI machines): the real cost is a few milliseconds; the budget only catches an accidental O(n^2).
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(750);

    private static GameTitleDto Title(int n, bool favorite = false, long seconds = 0, int score = 0, string state = GameInstallStates.Installed)
    {
        string name = "Synthetic Game " + n.ToString("D4");
        string key = GameLibraryQuery.NormalizeName(name);
        return new GameTitleDto(key, name, favorite, Array.Empty<string>(), seconds,
            seconds > 0 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n) : null, null,
            new[] { new GameInstallationDto("manual:" + n, GameInstallationSources.Manual, n.ToString(), ManualEntryKinds.Game, name)
                { InstallState = state, Platform = "PC" } })
        { Score = score };
    }

    private static List<GameTitleDto> Library(int count) => Enumerable.Range(0, count).Select(i => Title(i)).ToList();

    [Fact]
    public void Identical_list_changes_nothing()
    {
        List<GameTitleDto> shown = Library(2000);
        // A refresh produces new DTO instances with the same content.
        GameTileListDelta delta = GameTileListModel.Diff(shown, Library(2000));
        Assert.Equal(GameTileListChange.None, delta.Change);
        Assert.Empty(delta.ChangedIndexes);
    }

    [Fact]
    public void Same_titles_with_one_edited_face_is_content_only_and_names_the_tile()
    {
        List<GameTitleDto> shown = Library(500);
        List<GameTitleDto> next = Library(500);
        next[123] = Title(123, favorite: true);
        next[400] = Title(400, score: 8);
        GameTileListDelta delta = GameTileListModel.Diff(shown, next);
        Assert.Equal(GameTileListChange.ContentOnly, delta.Change);
        Assert.Equal(new[] { 123, 400 }, delta.ChangedIndexes);
    }

    [Fact]
    public void Search_filter_sort_and_import_reset_the_presenter()
    {
        List<GameTitleDto> shown = Library(300);
        Assert.Equal(GameTileListChange.Reset, GameTileListModel.Diff(shown, shown.Take(10).ToList()).Change);
        Assert.Equal(GameTileListChange.Reset, GameTileListModel.Diff(shown, Library(301)).Change);
        List<GameTitleDto> reversed = Enumerable.Reverse(shown).ToList();
        Assert.Equal(GameTileListChange.Reset, GameTileListModel.Diff(shown, reversed).Change);
        Assert.Equal(GameTileListChange.Reset, GameTileListModel.Diff(Array.Empty<GameTitleDto>(), shown).Change);
        Assert.Equal(GameTileListChange.None, GameTileListModel.Diff(Array.Empty<GameTitleDto>(), Array.Empty<GameTitleDto>()).Change);
    }

    [Fact]
    public void Face_comparison_ignores_what_a_tile_does_not_show_and_notices_what_it_does()
    {
        GameTitleDto a = Title(1, seconds: 120);
        Assert.True(GameTileListModel.SameFace(a, a with { Tags = new[] { "invisible-on-tile" }, Collections = new[] { "x" } }));
        Assert.False(GameTileListModel.SameFace(a, a with { Status = GameCompletionStatuses.Playing }));
        Assert.False(GameTileListModel.SameFace(a, a with { PlaySeconds = 121 }));
        Assert.False(GameTileListModel.SameFace(a, Title(1, seconds: 120, state: GameInstallStates.Incomplete)));
        Assert.False(GameTileListModel.SameFace(a, a with { Hidden = true }));
    }

    [Fact]
    public void Diff_and_key_index_over_2000_titles_stay_inside_the_budget()
    {
        List<GameTitleDto> shown = Library(2000);
        List<GameTitleDto> next = Library(2000);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) GameTileListModel.Diff(shown, next);
        Dictionary<string, int> index = GameTileListModel.IndexByKey(next);
        clock.Stop();
        Assert.True(clock.Elapsed < Budget, "diff x20 + index took " + clock.Elapsed);
        Assert.Equal(2000, index.Count);
        Assert.Equal(1999, index[next[1999].TitleKey]);
    }

    [Fact]
    public void Key_index_keeps_the_first_of_duplicate_keys()
    {
        GameTitleDto first = Title(1), duplicate = Title(1) with { Name = "Other" };
        Assert.Equal(0, GameTileListModel.IndexByKey(new[] { first, Title(2), duplicate })[first.TitleKey]);
    }

    [Theory]
    [InlineData(1000, 4)]   // floor((1000 + 12) / 214)
    [InlineData(1264, 5)]   // 6 tiles would need 6 * 202 + 5 * 12 = 1272
    [InlineData(1272, 6)]
    [InlineData(202, 1)]
    [InlineData(100, 1)]    // narrower than one tile: still one column
    [InlineData(0, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void Columns_never_drop_below_one_and_match_the_uniform_grid(double width, int expected) =>
        Assert.Equal(expected, GameTileListModel.Columns(width, 202, 12));

    [Fact]
    public void Lru_cache_is_bounded_and_evicts_the_least_recently_used()
    {
        var cache = new BoundedLruCache<string, int>(3);
        cache.Set("a", 1); cache.Set("b", 2); cache.Set("c", 3);
        Assert.True(cache.TryGet("a", out _));      // a is now the freshest
        cache.Set("d", 4);                          // evicts b
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out int a) && a == 1);
        Assert.Equal(3, cache.Count);
        Assert.True(cache.Remove("c"));
        Assert.False(cache.Remove("c"));
        for (int i = 0; i < 5000; i++) cache.Set("k" + i, i);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Stamped_cache_reads_the_file_once_for_2000_tile_lookups_and_again_only_after_a_change()
    {
        int loads = 0, stamps = 0;
        long stamp = 1, now = 0;
        var cache = new StampedCache<int>(() => { stamps++; return stamp; }, () => ++loads, () => now, recheckMilliseconds: 1500);
        for (int i = 0; i < 2000; i++) cache.Get();
        Assert.Equal(1, loads);
        Assert.Equal(1, stamps);

        // Within the recheck window a changed file is not even looked at; afterwards it is reloaded exactly once.
        stamp = 2;
        for (int i = 0; i < 100; i++) cache.Get();
        Assert.Equal(1, loads);
        now = 2000;
        for (int i = 0; i < 100; i++) cache.Get();
        Assert.Equal(2, loads);
        Assert.Equal(2, stamps);

        cache.Invalidate();
        Assert.Equal(3, cache.Get());
    }

    [Fact]
    public void Server_filter_and_sort_over_2000_titles_are_in_memory_and_stay_inside_the_budget()
    {
        List<GameTitleDto> library = Enumerable.Range(0, 2000)
            .Select(i => Title(i, favorite: i % 7 == 0, seconds: i % 5 == 0 ? 600 + i : 0, score: i % 11)).ToList();
        var clock = Stopwatch.StartNew();
        IReadOnlyList<GameTitleDto> search = GameTitleCatalog.Query(library, new GameTitleQuery("game 19"));
        IReadOnlyList<GameTitleDto> byScore = GameTitleCatalog.Query(library, new GameTitleQuery { Sort = GameTitleSorts.Score });
        IReadOnlyList<GameTitleDto> byPlayTime = GameTitleCatalog.Query(library, new GameTitleQuery(FavoritesOnly: true) { Sort = GameTitleSorts.PlayTime });
        IReadOnlyList<GameTitleDto> home = GameLibraryHome.Apply(library, new GameHomeFilter { Installed = true }, GameTitleSorts.LastPlayed,
            _ => Array.Empty<string>(), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        GameHomeFacets facets = GameLibraryHome.Facets(library, _ => Array.Empty<string>(), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        clock.Stop();

        Assert.True(clock.Elapsed < Budget, "filter/sort/facets took " + clock.Elapsed);
        Assert.Equal(100, search.Count);                      // "game 19xx" = 100 titles
        Assert.Equal(10, byScore[0].Score);
        Assert.True(byScore.Zip(byScore.Skip(1)).All(p => p.First.Score >= p.Second.Score), "score sort is descending");
        Assert.All(byPlayTime, t => Assert.True(t.Favorite));
        Assert.Equal(2000, home.Count);
        Assert.Equal(2000, facets.Total);
    }

    [Fact]
    public void Building_the_unified_catalogue_from_2000_manual_games_is_linear_and_touches_no_files()
    {
        var manual = Enumerable.Range(0, 2000)
            .Select(i => new ManualGameDto("id" + i, "Synthetic Game " + i.ToString("D4"), @"C:\synthetic\never-launched\game" + i + ".exe"))
            .ToList();
        var clock = Stopwatch.StartNew();
        IReadOnlyList<GameTitleDto> titles = GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(), manual,
            new Dictionary<string, GameTitleState>());
        clock.Stop();
        Assert.True(clock.Elapsed < Budget, "build took " + clock.Elapsed);
        Assert.Equal(2000, titles.Count);
        Assert.Equal(2000, titles.Select(t => t.TitleKey).Distinct().Count());
    }

    private static string Source(params string[] parts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Library_grid_is_virtualized_recycles_tiles_and_decodes_covers_at_display_size()
    {
        string code = Source("src", "PaRuX.App", "Views", "GameTileGrid.cs");
        Assert.Contains("new ItemsRepeater", code);
        Assert.Contains("new UniformGridLayout", code);
        Assert.Contains("IElementFactory", code);
        Assert.Contains("RecycleElement", code);
        Assert.DoesNotContain("VariableSizedWrapGrid", code);
        Assert.DoesNotContain("_items.Children.Add", code);
        Assert.Contains("DecodePixelWidth = pixels", code);
        Assert.Contains("BoundedLruCache<string, CoverEntry>", code);
        Assert.Contains("entry.Bitmap.UriSource = null", code);   // an unfinished decode is cancelled on recycle
        // Handlers are attached once, when a tile is created, never when it is bound to a title.
        string bind = code[code.IndexOf("private void Bind(", StringComparison.Ordinal)..code.IndexOf("private void Unbind(", StringComparison.Ordinal)];
        Assert.DoesNotContain("+=", bind);
        Assert.DoesNotContain("File.", bind);
        // A refresh with the same titles must not swap the items source.
        Assert.Contains("GameTileListModel.Diff(_source, titles)", code);
    }

    [Fact]
    public void Search_sort_and_filter_reloads_do_not_download_the_unfiltered_library_again()
    {
        string code = Source("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        Assert.Contains("ReloadAsync(queryOnly: true)", code);
        Assert.Contains("queryOnly && _continueLoaded && !refresh", code);
        string client = Source("src", "PaRuX.App", "Services", "GamingLibraryClient.cs");
        Assert.Contains("StampedCache<Dictionary<string, string>> IndexCache", client);
        Assert.Contains("IndexCache.Get().TryGetValue", client);
    }
}
