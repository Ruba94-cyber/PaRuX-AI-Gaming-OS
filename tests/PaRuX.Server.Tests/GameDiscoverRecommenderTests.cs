using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Discover;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameDiscoverRecommenderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = DiscoverFixtures.Dir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static GameCatalogEntryDto Cand(string id, string title, string[] genres, string[]? tags = null) =>
        new("steam", id, title, genres, tags ?? Array.Empty<string>(), null, false, null, SteamStoreCatalogProvider.StorePageUrl(id));

    private static PlayedGame Played(string title, double hours, int daysAgo, string[] genres, string[]? tags = null) =>
        new(title, (long)(hours * 3600), Now.AddDays(-daysAgo), genres, tags ?? Array.Empty<string>());

    private static readonly ISet<string> None = new HashSet<string>();

    [Fact]
    public void ScoresByOverlapAndExplainsWithPlayedGameAndSharedTerms()
    {
        var profile = GameDiscoverRecommender.BuildProfile(new[]
        {
            Played("Hades", 100, 2, new[] { "Action", "Indie" }, new[] { "Single-player" }),
            Played("Stardew Valley", 5, 300, new[] { "Simulation" }),
        }, Now);
        var recs = GameDiscoverRecommender.Recommend(profile, new[]
        {
            Cand("1", "Dead Cells", new[] { "Action", "Indie" }, new[] { "Single-player" }),
            Cand("2", "Farm Sim", new[] { "Simulation" }),
            Cand("3", "Tax Office", new[] { "Strategy" }),
        }, None, None, None);

        Assert.Equal(new[] { "1", "2" }, recs.Select(r => r.Game.StoreId));   // unrelated one dropped
        Assert.True(recs[0].Score > recs[1].Score);
        Assert.Equal("Hades", recs[0].BecauseYouPlay[0]);
        Assert.Contains("Because you play Hades", recs[0].Explanation);
        Assert.Contains("Action", recs[0].SharedTerms);
        Assert.Equal("Stardew Valley", recs[1].BecauseYouPlay[0]);
        Assert.InRange(recs[0].Score, 0.0, 1.0);
    }

    [Fact]
    public void MoreAndMoreRecentPlayWeighsMore()
    {
        var heavy = Played("A", 200, 1, new[] { "Action" });
        var light = Played("B", 1, 400, new[] { "Action" });
        Assert.True(GameDiscoverRecommender.GameWeight(heavy, Now) > 5 * GameDiscoverRecommender.GameWeight(light, Now));
        var never = new PlayedGame("C", 0, null, new[] { "Action" }, Array.Empty<string>());
        Assert.True(GameDiscoverRecommender.GameWeight(never, Now) > 0);
    }

    [Fact]
    public void ScoringIsDeterministic()
    {
        var played = new[] { Played("A", 10, 1, new[] { "Action", "RPG" }) };
        var cands = new[] { Cand("1", "X", new[] { "Action" }), Cand("2", "Y", new[] { "RPG" }) };
        var a = GameDiscoverRecommender.Recommend(GameDiscoverRecommender.BuildProfile(played, Now), cands, None, None, None);
        var b = GameDiscoverRecommender.Recommend(GameDiscoverRecommender.BuildProfile(played, Now), cands.Reverse(), None, None, None);
        Assert.Equal(a.Select(r => r.Game.StoreId), b.Select(r => r.Game.StoreId));
    }

    [Fact]
    public void ExcludesOwnedHiddenAndSameTitleOwnedElsewhere()
    {
        var profile = GameDiscoverRecommender.BuildProfile(new[] { Played("A", 10, 1, new[] { "Action" }) }, Now);
        var cands = new[]
        {
            Cand("1", "Owned By Id", new[] { "Action" }),
            Cand("2", "Hidden One", new[] { "Action" }),
            Cand("3", "Owned On GOG", new[] { "Action" }),
            Cand("4", "Fresh Pick", new[] { "Action" }),
            Cand("4", "Fresh Pick", new[] { "Action" }),          // duplicate
        };
        var recs = GameDiscoverRecommender.Recommend(profile, cands,
            new HashSet<string> { "steam:1" }, new HashSet<string> { GameDiscoverRecommender.NormalizeTitle("Owned on GOG!") },
            new HashSet<string> { "steam:2" });
        Assert.Equal("4", Assert.Single(recs).Game.StoreId);
    }

    [Fact]
    public void DiversityCapLimitsOneGenre()
    {
        var profile = GameDiscoverRecommender.BuildProfile(new[] { Played("A", 10, 1, new[] { "Action", "Puzzle" }) }, Now);
        var cands = Enumerable.Range(1, 6).Select(i => Cand(i.ToString(), $"Action {i}", new[] { "Action" }))
            .Append(Cand("9", "Puzzler", new[] { "Puzzle" })).ToList();
        var recs = GameDiscoverRecommender.Recommend(profile, cands, None, None, None, new RecommendOptions(MaxResults: 10, MaxPerGenre: 2));
        Assert.Equal(2, recs.Count(r => r.Game.Genres[0] == "Action"));
        Assert.Contains(recs, r => r.Game.StoreId == "9");
    }

    [Fact]
    public void LongTagListDoesNotWinByLength()
    {
        var profile = GameDiscoverRecommender.BuildProfile(new[] { Played("A", 10, 1, new[] { "Action" }) }, Now);
        var focused = Cand("1", "Focused", new[] { "Action" });
        var padded = Cand("2", "Padded", new[] { "Action" }, Enumerable.Range(0, 20).Select(i => "tag" + i).ToArray());
        var recs = GameDiscoverRecommender.Recommend(profile, new[] { padded, focused }, None, None, None);
        Assert.Equal("1", recs[0].Game.StoreId);
    }

    [Fact]
    public void EmptyProfileOrNoTermsYieldsNothing()
    {
        Assert.Empty(GameDiscoverRecommender.Recommend(GameDiscoverRecommender.BuildProfile(Array.Empty<PlayedGame>(), Now),
            new[] { Cand("1", "X", new[] { "Action" }) }, None, None, None));
        var profile = GameDiscoverRecommender.BuildProfile(new[] { Played("A", 1, 1, new[] { "Action" }) }, Now);
        Assert.Empty(GameDiscoverRecommender.Recommend(profile, new[] { Cand("1", "X", Array.Empty<string>()) }, None, None, None));
    }

    // ---- deep links ----------------------------------------------------------------------------------

    [Fact]
    public void InstallDeepLinkAcceptsOnlyNumericIds()
    {
        Assert.Equal("steam://install/730", GameDiscoverActions.SteamInstallLink("730"));
        foreach (string bad in new[] { "", "abc", "730/../x", "730 ", "730;calc", "steam://x", "-1", "0730", "12345678901", "7 3", "730\n" })
            Assert.Null(GameDiscoverActions.SteamInstallLink(bad));
        Assert.Null(GameDiscoverActions.SteamInstallLink(null));
    }

    [Fact]
    public void ActionsReturnOfficialStorePageAndRequireConfirmation()
    {
        var a = GameDiscoverActions.For("Steam", "730")!;
        Assert.Equal("https://store.steampowered.com/app/730/", a.StorePageUrl);
        Assert.Equal("steam://install/730", a.InstallDeepLink);
        Assert.True(a.InstallRequiresConfirmation);
        Assert.Null(GameDiscoverActions.For("steam", "x;y"));
        Assert.Null(GameDiscoverActions.For("humble", "1"));
    }

    // ---- service: opt-in, hard-off, end to end --------------------------------------------------------

    private sealed class FakeCatalog : IGameCatalogProvider
    {
        public int Calls;
        public string Id => "steam";
        public string DisplayName => "Steam";
        public Func<bool>? OnCall;
        public Dictionary<string, GameCatalogEntryDto> Details = new();
        public Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> FeaturedAsync(CancellationToken ct)
        {
            Calls++; OnCall?.Invoke();
            return Task.FromResult(CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(Details.Values.ToList()));
        }
        public Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> SearchAsync(string q, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(Array.Empty<GameCatalogEntryDto>()));
        }
        public Task<CatalogResult<GameCatalogEntryDto>> DetailAsync(string id, CancellationToken ct)
        {
            Calls++; OnCall?.Invoke();
            return Task.FromResult(Details.TryGetValue(id, out var e) ? CatalogResult<GameCatalogEntryDto>.Ok(e) : CatalogResult<GameCatalogEntryDto>.Fail(GameDiscoverStatus.Failed));
        }
    }

    private static GameTitleDto Title(string name, string steamId, long secs) =>
        new("k" + steamId, name, false, Array.Empty<string>(), secs, Now.AddDays(-1), null,
            new[] { new GameInstallationDto("i" + steamId, "steam", steamId, "game", name) });

    private (GameDiscoverService Svc, FakeCatalog Cat, GameDiscoverSettingsStore Settings, Func<bool> SetGaming) MakeService(bool enabled)
    {
        bool gaming = false;
        var settings = new GameDiscoverSettingsStore(Path.Combine(_dir, "s.json"));
        if (enabled) settings.SetEnabled(true);
        var cat = new FakeCatalog();
        cat.Details["10"] = Cand("10", "Hades", new[] { "Action", "Indie" });
        cat.Details["20"] = Cand("20", "Dead Cells", new[] { "Action", "Indie" });
        cat.Details["30"] = Cand("30", "Owned Already", new[] { "Action" });
        var gate = new GameDiscoverGate(settings, () => gaming);
        return (new GameDiscoverService(cat, gate, settings, () => Now), cat, settings, () => gaming = true);
    }

    [Fact]
    public async Task ServiceOffByDefaultDoesNothing()
    {
        var (svc, cat, _, _) = MakeService(enabled: false);
        var r = await svc.RecommendAsync(new[] { Title("Hades", "10", 36000) }, default);
        Assert.Equal(GameDiscoverStatus.Disabled, r.Status);
        Assert.Empty(r.Recommendations);
        Assert.Equal(0, cat.Calls);
    }

    [Fact]
    public async Task ServiceHardOffAtStartDoesNotCallOrScore()
    {
        var (svc, cat, _, setGaming) = MakeService(enabled: true);
        setGaming();
        var r = await svc.RecommendAsync(new[] { Title("Hades", "10", 36000) }, default);
        Assert.Equal(GameDiscoverStatus.GamingActive, r.Status);
        Assert.Empty(r.Recommendations);
        Assert.Equal(0, cat.Calls);
    }

    [Fact]
    public async Task ServiceStopsWhenGamingStartsMidRun()
    {
        var (svc, cat, _, setGaming) = MakeService(enabled: true);
        cat.OnCall = () => { setGaming(); return true; };
        var r = await svc.RecommendAsync(new[] { Title("Hades", "10", 36000) }, default);
        // The fake does not gate by itself; the service must still refuse to score once Gaming is active.
        Assert.Equal(GameDiscoverStatus.GamingActive, r.Status);
        Assert.Empty(r.Recommendations);
    }

    [Fact]
    public async Task ServiceRecommendsFromLibraryAndExcludesOwnedAndHidden()
    {
        var (svc, cat, settings, _) = MakeService(enabled: true);
        cat.Details["40"] = Cand("40", "Hidden Gem", new[] { "Action", "Indie" });
        settings.Hide("steam:40");
        var library = new[] { Title("Hades", "10", 36000), Title("Owned Already", "30", 100) };
        var r = await svc.RecommendAsync(library, default);
        Assert.Equal(GameDiscoverStatus.Ok, r.Status);
        Assert.Equal(new[] { "20" }, r.Recommendations.Select(x => x.Game.StoreId)); // 10 and 30 owned, 40 hidden
        Assert.Equal("Hades", r.Recommendations[0].BecauseYouPlay[0]);
    }

    [Fact]
    public async Task ServiceWithoutSteamGamesExplainsInsteadOfGuessing()
    {
        var (svc, cat, _, _) = MakeService(enabled: true);
        var gogOnly = new GameTitleDto("kx", "Witcher", false, Array.Empty<string>(), 100, Now, null,
            new[] { new GameInstallationDto("g1", "gog", "123", "game", "Witcher") });
        var r = await svc.RecommendAsync(new[] { gogOnly }, default);
        Assert.Equal(GameDiscoverStatus.Ok, r.Status);
        Assert.Empty(r.Recommendations);
        Assert.NotNull(r.Message);
        Assert.Equal(0, cat.Calls);
    }
}
