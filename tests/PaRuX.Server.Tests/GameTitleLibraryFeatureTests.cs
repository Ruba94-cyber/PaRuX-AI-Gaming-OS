using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameTitleSortFilterTests
{
    private static GameTitleDto T(string name, Action<GameTitleState>? s = null, params GameInstallationDto[] installs)
    {
        var state = new GameTitleState();
        s?.Invoke(state);
        if (installs.Length == 0)
            installs = new[] { new GameInstallationDto("steam:" + name, "steam", name, "game", name)
                { Platform = "PC", InstallState = GameInstallStates.Installed } };
        return new GameTitleDto(GameTitleCatalog.KeyFor(name, name), name, state.Favorite, state.Tags,
            state.PlaySeconds, state.LastPlayedUtc, installs[0].InstallationId, installs)
        { Collections = state.Collections, Hidden = state.Hidden, AddedUtc = state.AddedUtc };
    }

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static GameTitleDto[] Sample() => new[]
    {
        T("Alpha", s => { s.PlaySeconds = 10; s.LastPlayedUtc = Now.AddDays(-3); s.AddedUtc = Now.AddDays(-30); }),
        T("Bravo", s => { s.PlaySeconds = 500; s.LastPlayedUtc = Now.AddDays(-10); s.AddedUtc = Now.AddDays(-1); s.Tags = new() { "rpg" }; }),
        T("Charlie", s => { s.PlaySeconds = 100; s.LastPlayedUtc = Now.AddDays(-1); s.Hidden = true; }),
        T("Delta"),
    };

    [Fact]
    public void Default_sort_is_name_order_and_hidden_titles_are_excluded()
    {
        var all = GameTitleCatalog.Query(Sample(), new GameTitleQuery());
        Assert.Equal(new[] { "Alpha", "Bravo", "Delta" }, all.Select(t => t.Name));
        // The legacy Filter signature behaves identically.
        Assert.Equal(3, GameTitleCatalog.Filter(Sample(), null, null, false, null).Count);
    }

    [Theory]
    [InlineData("lastplayed", "Alpha,Bravo,Delta")]
    [InlineData("LASTPLAYED", "Alpha,Bravo,Delta")]
    [InlineData("playtime", "Bravo,Alpha,Delta")]
    [InlineData("recentlyadded", "Bravo,Alpha,Delta")]
    [InlineData("name", "Alpha,Bravo,Delta")]
    [InlineData("", "Alpha,Bravo,Delta")]
    public void Sort_orders_by_allowlisted_key_with_unplayed_last(string sort, string expected)
    {
        var result = GameTitleCatalog.Query(Sample(), new GameTitleQuery { Sort = sort });
        Assert.Equal(expected.Split(','), result.Select(t => t.Name));
    }

    [Fact]
    public void Sort_allowlist_rejects_unknown_values()
    {
        Assert.True(GameTitleSorts.IsValid(null));
        Assert.True(GameTitleSorts.IsValid("PlayTime"));
        Assert.False(GameTitleSorts.IsValid("name; drop"));
        Assert.False(GameTitleSorts.IsValid("size"));
    }

    [Fact]
    public void Hidden_modes_exclude_by_default_include_or_show_only_hidden()
    {
        Assert.DoesNotContain(GameTitleCatalog.Query(Sample(), new GameTitleQuery { Hidden = "false" }), t => t.Hidden);
        Assert.Equal(4, GameTitleCatalog.Query(Sample(), new GameTitleQuery { Hidden = "true" }).Count);
        Assert.Equal("Charlie", Assert.Single(GameTitleCatalog.Query(Sample(), new GameTitleQuery { Hidden = "only" })).Name);
        Assert.Null(GameTitleCatalog.HiddenModes.Parse("maybe"));
    }

    [Fact]
    public void Platform_installed_tag_and_source_filters_combine_with_search()
    {
        var emu = T("Zelda", null, new GameInstallationDto("emulator:z", "emulator", "z", "game", "Zelda")
            { Platform = "Nintendo Switch", InstallState = GameInstallStates.Installed });
        var store = T("Store only", null, new GameInstallationDto("epic:s", "epic", "s", "game", "Store only")
            { Platform = "PC", InstallState = GameInstallStates.Incomplete });
        var all = Sample().Concat(new[] { emu, store }).ToArray();

        Assert.Equal("Zelda", Assert.Single(GameTitleCatalog.Query(all, new GameTitleQuery { Platform = "nintendo switch" })).Name);
        Assert.Equal("Store only", Assert.Single(GameTitleCatalog.Query(all, new GameTitleQuery { Installed = false })).Name);
        Assert.DoesNotContain(GameTitleCatalog.Query(all, new GameTitleQuery { Installed = true }), t => t.Name == "Store only");
        Assert.Equal("Bravo", Assert.Single(GameTitleCatalog.Query(all, new GameTitleQuery(Tag: "rpg"))).Name);
        Assert.Equal("Zelda", Assert.Single(GameTitleCatalog.Query(all, new GameTitleQuery(Source: "emulator"))).Name);
        Assert.Empty(GameTitleCatalog.Query(all, new GameTitleQuery(Text: "zel") { Platform = "PC" }));
    }

    [Fact]
    public void Build_carries_platform_install_state_hidden_and_added_onto_dtos()
    {
        var discovered = new[] { new DiscoveredGameDto("steam", "1", "Portal", @"C:\x", GameInstallStates.Updating) };
        var manual = new[] { new ManualGameDto("m1", "Hades", @"C:\g\h.exe") };
        var added = Now;
        var state = new Dictionary<string, GameTitleState>
        {
            [GameTitleCatalog.KeyFor("Hades", "x")] = new() { Hidden = true, AddedUtc = added }
        };
        var titles = GameTitleCatalog.Build(discovered, manual, state);
        var portal = titles.Single(t => t.Name == "Portal");
        Assert.Equal("PC", portal.Installations[0].Platform);
        Assert.Equal(GameInstallStates.Updating, portal.Installations[0].InstallState);
        var hades = titles.Single(t => t.Name == "Hades");
        Assert.True(hades.Hidden);
        Assert.Equal(added, hades.AddedUtc);
        Assert.Equal(GameInstallStates.Installed, hades.Installations[0].InstallState);
    }

    [Fact]
    public void Old_dto_json_without_new_fields_still_deserializes_with_defaults()
    {
        const string json = "{\"InstallationId\":\"steam:1\",\"Source\":\"steam\",\"SourceGameId\":\"1\",\"Kind\":\"game\",\"Name\":\"X\"}";
        var dto = System.Text.Json.JsonSerializer.Deserialize<GameInstallationDto>(json)!;
        Assert.Equal(string.Empty, dto.Platform);
        Assert.Equal(GameInstallStates.Unknown, dto.InstallState);
    }
}

public sealed class GameTitleRecentTests
{
    private static GameTitleDto T(string name, long play, DateTime? last, bool hidden = false) =>
        new(GameTitleCatalog.KeyFor(name, name), name, false, Array.Empty<string>(), play, last, null,
            Array.Empty<GameInstallationDto>()) { Hidden = hidden };

    [Fact]
    public void Recent_lists_most_recently_played_then_most_played_and_skips_unplayed_and_hidden()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var titles = new[]
        {
            T("Old", 9000, now.AddDays(-9)),
            T("Latest", 1, now),
            T("Mid", 50, now.AddDays(-2)),
            T("Hidden", 5, now.AddDays(-1), hidden: true),
            T("Never", 0, null),
            T("Imported", 700, null),
        };
        var recent = GameTitleCatalog.Recent(titles, 10);
        Assert.Equal(new[] { "Latest", "Mid", "Old", "Imported" }, recent.Select(t => t.Name));
        Assert.Equal(2, GameTitleCatalog.Recent(titles, 2).Count);
        Assert.Empty(GameTitleCatalog.Recent(titles, -5));
        Assert.Empty(GameTitleCatalog.Recent(Array.Empty<GameTitleDto>()));
    }
}

public sealed class GameTitleStoreHiddenBulkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-titles-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "titles.json");
    public GameTitleStoreHiddenBulkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Old_state_file_without_hidden_or_added_fields_loads_as_visible()
    {
        File.WriteAllText(File_, "{\"Version\":1,\"Titles\":{\"hades\":{\"Favorite\":true,\"Tags\":[\"a\"],\"Collections\":[],\"PlaySeconds\":5}}}");
        var store = new GameTitleStore(File_);
        Assert.Null(store.LoadError);
        var s = store.Snapshot()["hades"];
        Assert.False(s.Hidden);
        Assert.Null(s.AddedUtc);
        Assert.True(s.Favorite);
    }

    [Fact]
    public void Hidden_persists_across_instances_and_can_be_cleared()
    {
        Assert.True(new GameTitleStore(File_).SetHidden("hades", true));
        var again = new GameTitleStore(File_);
        Assert.True(again.Snapshot()["hades"].Hidden);
        Assert.NotNull(again.Snapshot()["hades"].AddedUtc);
        Assert.True(again.SetHidden("hades", false));
        Assert.False(new GameTitleStore(File_).Snapshot()["hades"].Hidden);
    }

    [Fact]
    public void Bulk_update_changes_all_keys_in_one_write()
    {
        var store = new GameTitleStore(File_);
        store.SetFavorite("a", true);
        int? n = store.BulkUpdate(new[] { "a", "b", "b", "c" }, null, true, new[] { "x", "X", "y" }, new[] { "Shelf" });
        Assert.Equal(3, n);
        var snap = new GameTitleStore(File_).Snapshot();
        Assert.All(new[] { "a", "b", "c" }, k =>
        {
            Assert.True(snap[k].Hidden);
            Assert.Equal(new[] { "x", "y" }, snap[k].Tags);
            Assert.Equal(new[] { "Shelf" }, snap[k].Collections);
        });
        Assert.True(snap["a"].Favorite); // untouched field preserved
        Assert.False(snap["b"].Favorite);
    }

    [Fact]
    public void Bulk_update_is_atomic_and_rejects_malformed_or_oversized_input()
    {
        var store = new GameTitleStore(File_);
        Assert.Equal(1, store.BulkUpdate(new[] { "keep" }, true, null, null, null));
        string before = File.ReadAllText(File_);

        Assert.Null(store.BulkUpdate(new[] { "keep", "bad key!" }, false, null, null, null));
        Assert.Null(store.BulkUpdate(new[] { "keep", "" }, false, null, null, null));
        Assert.Null(store.BulkUpdate(Enumerable.Range(0, 101).Select(i => "k" + i), true, null, null, null));
        Assert.Null(store.BulkUpdate(Array.Empty<string>(), true, null, null, null));
        Assert.Null(store.BulkUpdate(null, true, null, null, null));
        Assert.Null(store.BulkUpdate(new[] { "keep" }, null, null, null, null));
        Assert.Null(store.BulkUpdate(new[] { "keep" }, false, null, new[] { new string('t', 41) }, null));
        Assert.Null(store.BulkUpdate(new[] { "keep" }, false, null, new[] { "bad\u0001" }, null));
        Assert.Null(store.BulkUpdate(new[] { "keep" }, false, null, null, Enumerable.Range(0, 13).Select(i => "c" + i)));
        Assert.Null(store.BulkUpdate(new[] { "keep", "new" }, false, null,
            Enumerable.Range(0, 21).Select(i => "t" + i), null));

        Assert.Equal(before, File.ReadAllText(File_));
        Assert.True(store.Snapshot()["keep"].Favorite);
        Assert.DoesNotContain("new", store.Snapshot().Keys);
        // Exactly 100 keys is accepted.
        Assert.Equal(100, store.BulkUpdate(Enumerable.Range(0, 100).Select(i => "k" + i), true, null, null, null));
    }

    [Fact]
    public void Bulk_update_never_overwrites_a_corrupt_file()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new GameTitleStore(File_);
        Assert.Null(store.BulkUpdate(new[] { "a" }, true, null, null, null));
        Assert.Equal("{ broken", File.ReadAllText(File_));
    }
}

public sealed class GameTitleEndpointsHttpTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-titles-http-" + Guid.NewGuid().ToString("N"));
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GameTitleEndpointsHttpTests(ApiSecurityTests.PaRuXFactory factory) { _factory = factory; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private HttpClient Client(out GameTitleStore store, out ManualGameLibrary library)
    {
        var s = new GameTitleStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-t.json"));
        var l = new ManualGameLibrary(Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-l.json"));
        store = s; library = l;
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<GameTitleStore>();
            services.RemoveAll<ManualGameLibrary>();
            services.AddSingleton(s);
            services.AddSingleton(l);
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Fact]
    public async Task New_routes_reject_missing_credentials()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/v1/gaming/titles/bulk", new BulkGameTitleRequest(new() { "a" }) { Favorite = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync("/api/v1/gaming/titles/a/hidden?hidden=true", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/v1/gaming/library/x", new UpdateManualGameRequest("n", "C:\\a.exe"))).StatusCode);
    }

    [Fact]
    public async Task Bulk_endpoint_updates_validates_and_hidden_endpoint_persists()
    {
        using var client = Client(out var store, out _);
        var ok = await client.PutAsJsonAsync("/api/v1/gaming/titles/bulk",
            new BulkGameTitleRequest(new() { "alpha", "beta" }) { Favorite = true, Tags = new() { "co-op" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await ok.Content.ReadFromJsonAsync<BulkGameTitleResult>())!.Updated);
        Assert.True(store.Snapshot()["beta"].Favorite);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/gaming/titles/bulk",
            new BulkGameTitleRequest(new() { "alpha", "../evil" }) { Favorite = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/gaming/titles/bulk",
            new BulkGameTitleRequest(Enumerable.Range(0, 101).Select(i => "k" + i).ToList()) { Favorite = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/gaming/titles/bulk",
            new BulkGameTitleRequest(new() { "alpha" }))).StatusCode);
        Assert.True(store.Snapshot()["alpha"].Favorite);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/v1/gaming/titles/alpha/hidden?hidden=true", null)).StatusCode);
        Assert.True(store.Snapshot()["alpha"].Hidden);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/v1/gaming/titles/bad%20key/hidden?hidden=true", null)).StatusCode);
    }

    [Fact]
    public async Task Titles_list_rejects_unknown_sort_and_hidden_values()
    {
        using var client = Client(out _, out _);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/gaming/titles?sort=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/gaming/titles?hidden=maybe")).StatusCode);
    }

    [Fact]
    public async Task Put_updates_a_manual_entry_and_rejects_unsafe_input()
    {
        using var client = Client(out _, out var library);
        string exe = Path.Combine(_dir, "g.exe");
        var added = library.Add(new("Old", exe));
        var ok = await client.PutAsJsonAsync("/api/v1/gaming/library/" + added.Id,
            new UpdateManualGameRequest("New", exe, "-windowed"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("New", library.List().Single().Name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/gaming/library/nope",
            new UpdateManualGameRequest("N", exe))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/gaming/library/" + added.Id,
            new UpdateManualGameRequest("N", "relative.exe"))).StatusCode);
        Assert.Equal("New", library.List().Single().Name);
    }
}

public sealed class ManualGameLibraryUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-manual-upd-" + Guid.NewGuid().ToString("N"));
    public ManualGameLibraryUpdateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ManualGameLibrary Lib() => new(Path.Combine(_dir, "lib.json"));
    private string Exe(string n) => Path.Combine(_dir, n + ".exe");

    [Fact]
    public void Update_changes_editable_fields_and_keeps_identity_and_kind()
    {
        var lib = Lib();
        var added = lib.Add(new("Game", Exe("a"), Kind: ManualEntryKinds.Application));
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var u = lib.Update(added.Id, new("  Renamed ", Exe("b"), " -x ", work, Platform: "Handheld"))!;
        Assert.Equal(added.Id, u.Id);
        Assert.Equal("Renamed", u.Name);
        Assert.Equal(Exe("b"), u.ExecutablePath);
        Assert.Equal("-x", u.Arguments);
        Assert.Equal(work, u.WorkingDirectory);
        Assert.Equal(ManualEntryKinds.Application, u.Kind);
        Assert.Equal(u, new ManualGameLibrary(Path.Combine(_dir, "lib.json")).List().Single());
    }

    [Fact]
    public void Update_returns_null_for_unknown_id_and_rejects_every_invalid_field_without_changes()
    {
        var lib = Lib();
        var added = lib.Add(new("Game", Exe("a")));
        string before = File.ReadAllText(Path.Combine(_dir, "lib.json"));
        Assert.Null(lib.Update("missing", new("n", Exe("b"))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("", Exe("b"))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new(new string('n', 201), Exe("b"))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", "relative.exe")));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", @"\\server\share\g.exe")));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Path.Combine(_dir, "g.cmd"))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b") + new string('x', 1100))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), new string('a', 1025))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), "a\u0001b")));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), "", Path.Combine(_dir, "no-such-dir"))));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), "", "relative")));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), EmulatorId: "not-an-emulator")));
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), Platform: new string('p', 81))));
        Assert.Throws<ArgumentException>(() => lib.Update(new string('i', 101), new("n", Exe("b"))));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "lib.json")));
    }

    [Fact]
    public void Update_rejects_a_working_directory_that_is_a_junction()
    {
        var lib = Lib();
        var added = lib.Add(new("Game", Exe("a")));
        string target = Path.Combine(_dir, "target");
        string link = Path.Combine(_dir, "link");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // no privilege to create links
        Assert.Throws<ArgumentException>(() => lib.Update(added.Id, new("n", Exe("b"), "", link)));
        Assert.Throws<ArgumentException>(() => lib.Add(new("n", Exe("c"), WorkingDirectory: link)));
    }

    [Fact]
    public void Update_refuses_a_duplicate_launch_configuration_but_allows_resaving_itself()
    {
        var lib = Lib();
        var a = lib.Add(new("A", Exe("a")));
        lib.Add(new("B", Exe("b")));
        Assert.Throws<InvalidOperationException>(() => lib.Update(a.Id, new("A", Exe("b"))));
        Assert.NotNull(lib.Update(a.Id, new("A2", Exe("a"))));
    }

    [Fact]
    public void Remove_deletes_only_the_entry_and_never_the_game_files()
    {
        var lib = Lib();
        string exe = Exe("keep");
        File.WriteAllText(exe, "binary");
        string save = Path.Combine(_dir, "save.dat");
        File.WriteAllText(save, "data");
        var added = lib.Add(new("Game", exe));
        Assert.True(lib.Remove(added.Id));
        Assert.False(lib.Remove(added.Id));
        Assert.Empty(lib.List());
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(save));
    }
}
