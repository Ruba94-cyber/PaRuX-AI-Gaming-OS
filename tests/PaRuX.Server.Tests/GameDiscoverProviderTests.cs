using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Discover;
using Xunit;

namespace PaRuX.Server.Tests;

internal static class DiscoverFixtures
{
    // Shapes recorded from the public Steam Store endpoints (trimmed).
    public const string AppDetailsRoguelike = """
    {"1001":{"success":true,"data":{"type":"game","name":"Dungeon Delver","steam_appid":1001,"is_free":false,
      "short_description":"A roguelike.","header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1001/header.jpg?t=1",
      "price_overview":{"currency":"EUR","initial":1999,"final":1499,"final_formatted":"14,99€"},
      "categories":[{"id":2,"description":"Single-player"},{"id":1,"description":"Multi-player"}],
      "genres":[{"id":"1","description":"Action"},{"id":"25","description":"Adventure"}]}}}
    """;
    public const string AppDetailsFree = """
    {"1002":{"success":true,"data":{"type":"game","name":"Free Arena","steam_appid":1002,"is_free":true,
      "header_image":"https://evil.example.com/x.jpg",
      "categories":[{"description":"Multi-player"}],"genres":[{"description":"Action"},{"description":"Free To Play"}]}}}
    """;
    public const string AppDetailsDlc = """{"1003":{"success":true,"data":{"type":"dlc","name":"Extra","steam_appid":1003}}}""";
    public const string AppDetailsMissing = """{"1004":{"success":false}}""";
    public const string AppDetailsWrongId = """{"1005":{"success":true,"data":{"type":"game","name":"Liar","steam_appid":9999,"genres":[{"description":"Action"}]}}}""";
    public const string Featured = """
    {"top_sellers":{"id":"cat_topsellers","items":[
        {"id":1001,"type":0,"name":"Dungeon Delver","final_price":1499,"currency":"EUR","header_image":"https://shared.akamai.steamstatic.com/a.jpg"},
        {"id":1002,"name":"Free Arena","final_price":0,"currency":"EUR"},
        {"id":"bad","name":"No id"},{"id":-5,"name":"neg"},{"name":"missing id"}]},
     "specials":{"items":[{"id":1001,"name":"Dungeon Delver"},{"id":1006,"name":"Space Farmer","final_price":999,"currency":"USD"}]},
     "new_releases":"not-an-object"}
    """;
    public const string Search = """{"total":2,"items":[{"type":"app","name":"Dungeon Delver","id":1001,"price":{"currency":"EUR","initial":1999,"final":1499}},{"type":"bundle","name":"Pack","id":55}]}""";

    public static string Dir() => Path.Combine(Path.GetTempPath(), "parux-discover-" + Guid.NewGuid().ToString("N"));
}

internal sealed class FakeSteamHandler : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class UnknownLengthContent : HttpContent
{
    private readonly int _bytes;
    public UnknownLengthContent(int bytes) { _bytes = bytes; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(new byte[_bytes]).AsTask();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}

public sealed class GameDiscoverProviderTests : IDisposable
{
    private readonly string _dir = DiscoverFixtures.Dir();
    private readonly FakeSteamHandler _handler = new();
    private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private bool _gaming;
    private readonly List<TimeSpan> _delays = new();

    private (SteamStoreCatalogProvider Provider, GameDiscoverSettingsStore Settings) Make(bool enabled, SteamStoreOptions? options = null, Action? onDelay = null)
    {
        var settings = new GameDiscoverSettingsStore(Path.Combine(_dir, "settings.json"));
        if (enabled) Assert.True(settings.SetEnabled(true));
        var gate = new GameDiscoverGate(settings, () => _gaming);
        var provider = new SteamStoreCatalogProvider(new HttpClient(_handler), gate, Path.Combine(_dir, "cache"),
            options ?? new SteamStoreOptions(), () => _now, (t, _) => { _delays.Add(t); onDelay?.Invoke(); return Task.CompletedTask; });
        return (provider, settings);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task OptInIsOffByDefaultAndNothingIsSent()
    {
        var (p, settings) = Make(enabled: false);
        Assert.False(settings.Enabled);
        Assert.Equal(GameDiscoverStatus.Disabled, (await p.DetailAsync("1001", default)).Status);
        Assert.Equal(GameDiscoverStatus.Disabled, (await p.FeaturedAsync(default)).Status);
        Assert.Equal(GameDiscoverStatus.Disabled, (await p.SearchAsync("dungeon", default)).Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void ConsentTextNamesWhatIsSent()
    {
        Assert.Contains("app ids", GameDiscoverSettingsStore.ConsentText);
        Assert.Contains("store.steampowered.com", GameDiscoverSettingsStore.ConsentText);
    }

    [Fact]
    public void SettingsPersistAndCorruptFileMeansOff()
    {
        string path = Path.Combine(_dir, "s2.json");
        var s = new GameDiscoverSettingsStore(path);
        Assert.True(s.SetEnabled(true));
        Assert.True(new GameDiscoverSettingsStore(path).Enabled);
        File.WriteAllText(path, "{ not json");
        var bad = new GameDiscoverSettingsStore(path);
        Assert.False(bad.Enabled);
        Assert.NotNull(bad.LoadError);
        Assert.False(bad.SetEnabled(true));
        Assert.Equal("{ not json", File.ReadAllText(path)); // never overwritten
    }

    [Fact]
    public async Task ParsesAppDetailsAndSendsOnlyTheAppId()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (p, _) = Make(true);
        var r = await p.DetailAsync("1001", default);
        Assert.True(r.IsOk);
        var e = r.Value!;
        Assert.Equal("Dungeon Delver", e.Title);
        Assert.Equal("steam", e.Store);
        Assert.Equal(new[] { "Action", "Adventure" }, e.Genres);
        Assert.Contains("Single-player", e.Tags);
        Assert.Equal("14,99€", e.PriceText);
        Assert.False(e.IsFreeToPlay);
        Assert.StartsWith("https://shared.akamai.steamstatic.com/", e.HeaderImageUrl);
        Assert.Equal("https://store.steampowered.com/app/1001/", e.StorePageUrl);

        var req = Assert.Single(_handler.Requests);
        Assert.Equal("store.steampowered.com", req.RequestUri!.Host);
        Assert.Equal(Uri.UriSchemeHttps, req.RequestUri.Scheme);
        Assert.Contains("appids=1001", req.RequestUri.Query);
        Assert.Null(req.Headers.Authorization);
        Assert.False(req.Headers.Contains("Cookie"));
        Assert.Null(req.Content);
    }

    [Fact]
    public async Task OneShotPriceQuoteUsesOnlySteamHostAppIdAndRequestedCountry()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (provider, _) = Make(true);
        var price = await provider.QuotePriceAsync("1001", "IT", default);

        Assert.True(price.IsOk);
        Assert.Equal("EUR", price.Value!.Currency);
        Assert.Equal(1499, price.Value.PriceMinor);
        Assert.Equal("IT", price.Value.StoreCountryCode);
        Assert.Equal(_now, price.Value.ObservedAtUtc);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("store.steampowered.com", request.RequestUri.Host);
        Assert.Contains("appids=1001", request.RequestUri.Query);
        Assert.Contains("cc=it", request.RequestUri.Query);
        Assert.Contains("filters=price_overview", request.RequestUri.Query);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData("1001", "i&t")]
    [InlineData("1001", "../")]
    [InlineData("1001", "IT&evil=1")]
    [InlineData("1001", "")]
    [InlineData("1001", "1T")]
    [InlineData("../1001", "IT")]
    [InlineData("1001&other=5", "IT")]
    [InlineData("001", "IT")]
    public async Task OneShotPriceQuoteRejectsMalformedIdentifiersWithoutNetwork(string id, string country)
    {
        var (provider, _) = Make(true);
        Assert.False((await provider.QuotePriceAsync(id, country, default)).IsOk);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task OneShotPriceQuoteRefusesWhenDiscoverIsOffOrGamingStarts()
    {
        var (provider, settings) = Make(false);
        Assert.Equal(GameDiscoverStatus.Disabled,
            (await provider.QuotePriceAsync("1001", "IT", default)).Status);
        Assert.Empty(_handler.Requests);

        Assert.True(settings.SetEnabled(true));
        _gaming = true;
        Assert.Equal(GameDiscoverStatus.GamingActive,
            (await provider.QuotePriceAsync("1001", "IT", default)).Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task OneShotQuoteDiscardsPriceIfGamingStartsWhileWaitingForRateLimiter()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (provider, _) = Make(true, onDelay: () => _gaming = true);
        Assert.True((await provider.DetailAsync("1001", default)).IsOk);
        var price = await provider.QuotePriceAsync("1001", "IT", default);
        Assert.Equal(GameDiscoverStatus.GamingActive, price.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task OneShotQuoteDoesNotInventPricesFromInvalidOrMissingJson()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsMissing);
        var (provider, _) = Make(true);
        Assert.False((await provider.QuotePriceAsync("1001", "IT", default)).IsOk);
        _handler.Respond = _ => FakeSteamHandler.Json("{not-json");
        Assert.False((await provider.QuotePriceAsync("1001", "IT", default)).IsOk);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task FreeToPlayAndUntrustedImageHostFallsBackToSteamCdn()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsFree);
        var (p, _) = Make(true);
        var e = (await p.DetailAsync("1002", default)).Value!;
        Assert.True(e.IsFreeToPlay);
        Assert.Equal("Free to Play", e.PriceText);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/1002/header.jpg", e.HeaderImageUrl);
    }

    [Theory]
    [InlineData(DiscoverFixtures.AppDetailsDlc, "1003")]
    [InlineData(DiscoverFixtures.AppDetailsMissing, "1004")]
    [InlineData(DiscoverFixtures.AppDetailsWrongId, "1005")]
    [InlineData("{ truncated", "1006")]
    [InlineData("[]", "1007")]
    [InlineData("null", "1008")]
    public async Task RejectsNonGamesMissingMismatchedAndMalformed(string body, string id)
    {
        _handler.Respond = _ => FakeSteamHandler.Json(body);
        var (p, _) = Make(true);
        var r = await p.DetailAsync(id, default);
        Assert.False(r.IsOk);
        Assert.Equal(GameDiscoverStatus.Failed, r.Status);
    }

    [Fact]
    public async Task MissingAppIsNegativeCachedSoSteamIsNotHammered()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsMissing);
        var (p, _) = Make(true);
        await p.DetailAsync("1004", default);
        await p.DetailAsync("1004", default);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("12 34")] [InlineData("0123")] [InlineData("12345678901")]
    [InlineData("1001&evil=1")] [InlineData("1001,1002")] [InlineData("../1")]
    public async Task InvalidAppIdNeverReachesTheNetwork(string id)
    {
        var (p, _) = Make(true);
        Assert.False((await p.DetailAsync(id, default)).IsOk);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("https://store.steampowered.com/api/appdetails?appids=1", true)]
    [InlineData("http://store.steampowered.com/api/appdetails?appids=1", false)]
    [InlineData("https://steampowered.com/api/appdetails", false)]
    [InlineData("https://store.steampowered.com.evil.com/x", false)]
    [InlineData("https://evil.com/store.steampowered.com", false)]
    [InlineData("https://api.steampowered.com/x", false)]
    [InlineData("https://store.steampowered.com:8443/x", false)]
    [InlineData("https://user@store.steampowered.com/x", false)]
    public void HostAllowListIsStrict(string url, bool allowed) =>
        Assert.Equal(allowed, SteamStoreCatalogProvider.IsAllowedUri(new Uri(url)));

    [Fact]
    public async Task RedirectsAreNeverFollowedOrTrusted()
    {
        _handler.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/x");
            return r;
        };
        var (p, _) = Make(true);
        var res = await p.DetailAsync("1001", default);
        Assert.Equal(GameDiscoverStatus.Failed, res.Status);
        Assert.Single(_handler.Requests); // no second request to the redirect target
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        var (p, _) = Make(true, new SteamStoreOptions { MaxResponseBytes = 2048 });
        _handler.Respond = _ => FakeSteamHandler.Json(new string(' ', 4096) + DiscoverFixtures.AppDetailsRoguelike); // declared length
        Assert.Equal(GameDiscoverStatus.Failed, (await p.DetailAsync("1001", default)).Status);
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(8192) }; // undeclared length
        Assert.Equal(GameDiscoverStatus.Failed, (await p.DetailAsync("1002", default)).Status);
    }

    [Fact]
    public async Task CacheHonoursTtl()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (p, _) = Make(true, new SteamStoreOptions { DetailTtl = TimeSpan.FromHours(1) });
        await p.DetailAsync("1001", default);
        _now += TimeSpan.FromMinutes(59);
        var cached = await p.DetailAsync("1001", default);
        Assert.True(cached.IsOk);
        Assert.Single(_handler.Requests);
        _now += TimeSpan.FromMinutes(2);
        await p.DetailAsync("1001", default);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task CacheSurvivesAProviderRestart()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (p1, _) = Make(true);
        await p1.DetailAsync("1001", default);
        var (p2, _) = Make(true);
        Assert.True((await p2.DetailAsync("1001", default)).IsOk);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task RequestsAreRateLimited()
    {
        _handler.Respond = r => FakeSteamHandler.Json(r.RequestUri!.Query.Contains("1001") ? DiscoverFixtures.AppDetailsRoguelike : DiscoverFixtures.AppDetailsFree);
        var (p, _) = Make(true, new SteamStoreOptions { MinRequestInterval = TimeSpan.FromSeconds(2) });
        await p.DetailAsync("1001", default);
        Assert.Empty(_delays);                          // first request is immediate
        await p.DetailAsync("1002", default);           // clock did not advance -> must wait the full interval
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(_delays));
    }

    [Fact]
    public async Task GamingHardOffSendsNothing()
    {
        _gaming = true;
        var (p, _) = Make(true);
        Assert.Equal(GameDiscoverStatus.GamingActive, (await p.DetailAsync("1001", default)).Status);
        Assert.Equal(GameDiscoverStatus.GamingActive, (await p.FeaturedAsync(default)).Status);
        Assert.Equal(GameDiscoverStatus.GamingActive, (await p.SearchAsync("x", default)).Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GamingStartingWhileQueuedBehindRateLimitCancelsTheRequest()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.AppDetailsRoguelike);
        var (p, _) = Make(true, onDelay: () => _gaming = true);
        await p.DetailAsync("1001", default);           // first call: no delay needed
        var r = await p.DetailAsync("1002", default);   // delayed; a game starts meanwhile
        Assert.Equal(GameDiscoverStatus.GamingActive, r.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ParsesFeaturedDedupesAndSkipsBadItems()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.Featured);
        var (p, _) = Make(true);
        var list = (await p.FeaturedAsync(default)).Value!;
        Assert.Equal(new[] { "1001", "1002", "1006" }, list.Select(e => e.StoreId));
        Assert.Equal("14.99 EUR", list[0].PriceText);
        Assert.Null(list[1].PriceText);
        Assert.All(list, e => Assert.StartsWith("https://store.steampowered.com/app/", e.StorePageUrl));
    }

    [Fact]
    public async Task FeaturedIsCappedAndCached()
    {
        var items = string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"name\":\"G{i}\"}}"));
        _handler.Respond = _ => FakeSteamHandler.Json("{\"top_sellers\":{\"items\":[" + items + "]}}");
        var (p, _) = Make(true, new SteamStoreOptions { MaxFeatured = 10 });
        Assert.Equal(10, (await p.FeaturedAsync(default)).Value!.Count);
        await p.FeaturedAsync(default);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task SearchKeepsOnlyApps()
    {
        _handler.Respond = _ => FakeSteamHandler.Json(DiscoverFixtures.Search);
        var (p, _) = Make(true);
        var list = (await p.SearchAsync("dungeon delver", default)).Value!;
        Assert.Equal("1001", Assert.Single(list).StoreId);
        Assert.Contains("term=dungeon%20delver", _handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task EpicAndGogAreHonestStubsWithoutNetwork()
    {
        foreach (IGameCatalogProvider stub in new IGameCatalogProvider[] { new EpicCatalogProviderStub(), new GogCatalogProviderStub() })
        {
            Assert.Equal(GameDiscoverStatus.NotAvailable, (await stub.FeaturedAsync(default)).Status);
            Assert.Equal(GameDiscoverStatus.NotAvailable, (await stub.SearchAsync("x", default)).Status);
            Assert.Equal(GameDiscoverStatus.NotAvailable, (await stub.DetailAsync("1", default)).Status);
        }
        Assert.Null(GameDiscoverActions.For("epic", "123"));
        Assert.Null(GameDiscoverActions.For("gog", "123"));
    }
}
