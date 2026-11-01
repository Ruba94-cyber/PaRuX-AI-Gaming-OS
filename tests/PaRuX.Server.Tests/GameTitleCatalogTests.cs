using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameTitleCatalogTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "parux-titles-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() { try { File.Delete(_file); } catch { } }

    private static DiscoveredGameDto Steam(string id, string name) => new("steam", id, name, @"C:\Steam\" + name);
    private static ManualGameDto Manual(string id, string name, string kind = ManualEntryKinds.Game) =>
        new(id, name, @"C:\Games\" + name + ".exe", Kind: kind);

    private static readonly IReadOnlyDictionary<string, GameTitleState> NoState = new Dictionary<string, GameTitleState>();

    [Fact]
    public void Same_game_from_store_and_portable_copy_is_one_title_with_two_installations()
    {
        var titles = GameTitleCatalog.Build(
            new[] { Steam("292030", "The Witcher® 3: Wild Hunt") },
            new[] { Manual("m1", "The Witcher 3 Wild Hunt") }, NoState);
        var title = Assert.Single(titles);
        Assert.Equal(2, title.Installations.Count);
        Assert.Contains(title.Installations, i => i.InstallationId == "steam:292030");
        Assert.Contains(title.Installations, i => i.InstallationId == "manual:m1");
        Assert.Equal("manual:m1", title.DefaultInstallationId); // own entries first when nothing is chosen
    }

    [Fact]
    public void Different_emulator_installations_identify_the_actual_emulator_instead_of_generic_labels()
    {
        var entries = new[]
        {
            Manual("eden-game", "Demo") with { EmulatorId = "eden" },
            Manual("dolphin-game", "Demo") with { EmulatorId = "dolphin" },
            Manual("manual-game", "Demo")
        };
        var title = Assert.Single(GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(), entries, NoState));
        Assert.Equal(3, title.Installations.Count);
        Assert.Equal("Eden", Assert.Single(title.Installations.Where(i => i.SourceGameId == "eden-game")).EmulatorName);
        Assert.Equal("Dolphin", Assert.Single(title.Installations.Where(i => i.SourceGameId == "dolphin-game")).EmulatorName);
        Assert.Empty(Assert.Single(title.Installations.Where(i => i.SourceGameId == "manual-game")).EmulatorName);
        Assert.All(title.Installations, i => Assert.DoesNotContain(@"C:\Games", i.EmulatorName));
    }

    [Fact]
    public void Short_named_emulated_game_session_key_matches_the_catalog_fallback()
    {
        var entry = Manual("short", "X") with { EmulatorId = "eden" };
        var title = Assert.Single(GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(), new[] { entry }, NoState));
        Assert.Equal("id:emulator:short", title.TitleKey);
        Assert.Equal(title.TitleKey, ManualGameSessionService.SessionTitleKey(entry));
        var manual = entry with { EmulatorId = string.Empty };
        Assert.Equal("id:manual:short", ManualGameSessionService.SessionTitleKey(manual));
        Assert.Equal(GameTitleCatalog.KeyFor("Long Title", "ignored"),
            ManualGameSessionService.SessionTitleKey(entry with { Name = "Long Title" }));
    }

    [Fact]
    public void Unrelated_titles_stay_separate_and_sorted_by_name()
    {
        var titles = GameTitleCatalog.Build(new[] { Steam("1", "Portal"), Steam("2", "Hades") }, Array.Empty<ManualGameDto>(), NoState);
        Assert.Equal(new[] { "Hades", "Portal" }, titles.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void Very_short_or_symbol_only_names_never_merge_with_each_other()
    {
        var titles = GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(),
            new[] { Manual("a", "™"), Manual("b", "®"), Manual("c", "X"), Manual("d", "X") }, NoState);
        Assert.Equal(4, titles.Count);
    }

    [Fact]
    public void A_saved_default_is_honoured_and_a_stale_one_falls_back()
    {
        var state = new Dictionary<string, GameTitleState>
        {
            [GameTitleCatalog.KeyFor("Portal", "x")] = new() { DefaultInstallationId = "steam:1" }
        };
        var good = GameTitleCatalog.Build(new[] { Steam("1", "Portal") }, new[] { Manual("m", "Portal") }, state);
        Assert.Equal("steam:1", good[0].DefaultInstallationId);

        state[GameTitleCatalog.KeyFor("Portal", "x")] = new() { DefaultInstallationId = "steam:uninstalled" };
        var stale = GameTitleCatalog.Build(new[] { Steam("1", "Portal") }, new[] { Manual("m", "Portal") }, state);
        Assert.Equal("manual:m", stale[0].DefaultInstallationId);
    }

    [Fact]
    public void User_state_is_applied_to_the_title()
    {
        string key = GameTitleCatalog.KeyFor("Hades", "x");
        var state = new Dictionary<string, GameTitleState>
        {
            [key] = new() { Favorite = true, Tags = new() { "roguelike" }, PlaySeconds = 3600, LastPlayedUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) }
        };
        var title = Assert.Single(GameTitleCatalog.Build(new[] { Steam("1", "Hades") }, Array.Empty<ManualGameDto>(), state));
        Assert.True(title.Favorite);
        Assert.Equal(new[] { "roguelike" }, title.Tags);
        Assert.Equal(3600, title.PlaySeconds);
    }

    [Fact]
    public void Filters_combine_text_source_favorites_and_tag()
    {
        var state = new Dictionary<string, GameTitleState>
        {
            [GameTitleCatalog.KeyFor("Hades", "x")] = new() { Favorite = true, Tags = new() { "Indie" } }
        };
        var titles = GameTitleCatalog.Build(new[] { Steam("1", "Hades"), Steam("2", "Portal") }, new[] { Manual("m", "Tool", ManualEntryKinds.Application) }, state);
        Assert.Equal(3, GameTitleCatalog.Filter(titles, null, null, false, null).Count);
        Assert.Single(GameTitleCatalog.Filter(titles, "had", null, false, null));
        Assert.Single(GameTitleCatalog.Filter(titles, null, "manual", false, null));
        Assert.Single(GameTitleCatalog.Filter(titles, null, null, true, null));
        Assert.Single(GameTitleCatalog.Filter(titles, null, null, false, "indie"));
        Assert.Empty(GameTitleCatalog.Filter(titles, "portal", null, true, null));
    }

    [Fact]
    public void Store_persists_favorite_tags_default_and_sessions()
    {
        var store = new GameTitleStore(_file);
        Assert.True(store.SetFavorite("hades", true));
        Assert.True(store.SetTags("hades", new[] { " Indie ", "indie", "co-op", "" }));
        Assert.True(store.SetDefaultInstallation("hades", "steam:1"));
        var start = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(store.AddSession("hades", start, TimeSpan.FromMinutes(30)));
        Assert.True(store.AddSession("hades", start.AddHours(2), TimeSpan.FromMinutes(15)));

        var reloaded = new GameTitleStore(_file).Snapshot()["hades"];
        Assert.True(reloaded.Favorite);
        Assert.Equal(new[] { "Indie", "co-op" }, reloaded.Tags);
        Assert.Equal("steam:1", reloaded.DefaultInstallationId);
        Assert.Equal(45 * 60, reloaded.PlaySeconds);
        Assert.Equal(start.AddHours(2).AddMinutes(15), reloaded.LastPlayedUtc);
    }

    [Fact]
    public void Named_collections_are_independent_of_tags_and_survive_a_restart()
    {
        var store = new GameTitleStore(_file);
        Assert.True(store.SetTags("hades", new[] { "roguelike" }));
        Assert.True(store.SetCollections("hades", new[] { " Couch Co-op ", "couch co-op", "Family", "" }));
        var reloaded = new GameTitleStore(_file);
        var state = reloaded.Snapshot()["hades"];
        Assert.Equal(new[] { "Couch Co-op", "Family" }, state.Collections);
        Assert.Equal(new[] { "roguelike" }, state.Tags);

        var titles = GameTitleCatalog.Build(new[] { Steam("1", "Hades") }, Array.Empty<ManualGameDto>(),
            reloaded.Snapshot());
        Assert.Equal(1, GameTitleCatalog.Filter(titles, null, null, false, null, "family").Count);
        Assert.Empty(GameTitleCatalog.Filter(titles, null, null, false, null, "Other"));
    }

    [Fact]
    public void Collections_reject_invalid_values_without_overwriting_existing_state()
    {
        var store = new GameTitleStore(_file);
        Assert.True(store.SetCollections("hades", new[] { "Family" }));
        Assert.False(store.SetCollections("hades", new[] { "bad\nname" }));
        Assert.False(store.SetCollections("hades", new[] { new string('x', GameTitleStore.MaxCollectionLength + 1) }));
        Assert.False(store.SetCollections("hades", Enumerable.Range(0, GameTitleStore.MaxCollections + 1).Select(x => "Group" + x)));
        Assert.Equal(new[] { "Family" }, new GameTitleStore(_file).Snapshot()["hades"].Collections);
    }

    [Fact]
    public void Legacy_title_json_without_collections_stays_compatible()
    {
        File.WriteAllText(_file, "{\"Version\":1,\"Titles\":{\"hades\":{\"Favorite\":true,\"Tags\":[\"retro\"],\"PlaySeconds\":10}}}");
        var store = new GameTitleStore(_file);
        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot()["hades"].Collections);
        Assert.True(store.SetCollections("hades", new[] { "Old favorites" }));
        Assert.True(new GameTitleStore(_file).Snapshot()["hades"].Favorite);
    }

    [Fact]
    public void Invalid_tags_sessions_and_keys_are_rejected_without_changing_anything()
    {
        var store = new GameTitleStore(_file);
        Assert.False(store.SetTags("k", new[] { new string('x', GameTitleStore.MaxTagLength + 1) }));
        Assert.False(store.SetTags("k", new[] { "bad\ttag" }));
        Assert.False(store.SetTags("k", Enumerable.Range(0, GameTitleStore.MaxTags + 1).Select(i => "t" + i)));
        Assert.False(store.AddSession("k", DateTime.UtcNow, TimeSpan.Zero));
        Assert.False(store.AddSession("k", DateTime.UtcNow, TimeSpan.FromSeconds(-5)));
        Assert.False(store.SetFavorite("", true));
        Assert.False(store.SetFavorite("bad\nkey", true));
        Assert.False(store.SetDefaultInstallation("k", "x\ny"));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void One_absurd_session_is_capped_so_totals_cannot_be_poisoned()
    {
        var store = new GameTitleStore(_file);
        store.AddSession("k", DateTime.UtcNow, TimeSpan.FromDays(3650));
        Assert.Equal((long)GameTitleStore.MaxSession.TotalSeconds, store.Snapshot()["k"].PlaySeconds);
    }

    [Fact]
    public void A_corrupt_file_is_never_overwritten_and_changes_are_refused()
    {
        File.WriteAllText(_file, "{ not json");
        var store = new GameTitleStore(_file);
        Assert.NotNull(store.LoadError);
        Assert.False(store.SetFavorite("k", true));
        Assert.Equal("{ not json", File.ReadAllText(_file));
    }

    [Fact]
    public void Two_cold_Core_instances_merge_updates_instead_of_losing_collections()
    {
        var first = new GameTitleStore(_file);
        var second = new GameTitleStore(_file);
        Assert.True(first.SetFavorite("hades", true));
        Assert.True(second.SetCollections("hades", new[] { "Family" }));
        Assert.True(first.SetTags("hades", new[] { "Indie" }));
        Assert.True(second.AddSession("hades", DateTime.UtcNow, TimeSpan.FromSeconds(90)));

        var state = new GameTitleStore(_file).Snapshot()["hades"];
        Assert.True(state.Favorite);
        Assert.Equal(new[] { "Indie" }, state.Tags);
        Assert.Equal(new[] { "Family" }, state.Collections);
        Assert.Equal(90, state.PlaySeconds);
        Assert.Equal(state.PlaySeconds, first.Snapshot()["hades"].PlaySeconds); // refresh on read
    }

    [Fact]
    public void External_corruption_between_mutations_is_not_overwritten()
    {
        var store = new GameTitleStore(_file);
        Assert.True(store.SetFavorite("hades", true));
        File.WriteAllText(_file, "{invalid-json");
        Assert.False(store.SetCollections("hades", new[] { "Family" }));
        Assert.Equal("{invalid-json", File.ReadAllText(_file));
        Assert.Throws<InvalidDataException>(() => store.Snapshot());
    }

    [Fact]
    public void Concurrent_sessions_do_not_lose_play_time()
    {
        var store = new GameTitleStore(_file);
        Parallel.For(0, 50, _ => store.AddSession("k", DateTime.UtcNow, TimeSpan.FromSeconds(10)));
        Assert.Equal(500, store.Snapshot()["k"].PlaySeconds);
        Assert.Equal(500, new GameTitleStore(_file).Snapshot()["k"].PlaySeconds);
    }

    [Theory]
    [InlineData("hades", true)]
    [InlineData("thewitcher3wildhunt", true)]
    [InlineData("id:steam:292030", true)]
    [InlineData("", false)]
    [InlineData("id:", false)]
    [InlineData("has space", false)]
    [InlineData("../etc", false)]
    [InlineData("id:a/b", false)]
    public void Title_keys_in_routes_are_validated(string key, bool expected) =>
        Assert.Equal(expected, GameTitleEndpoints.ValidTitleKey(key));
}
