using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExternalLibraryPricePoliciesTests
{
    [Theory]
    [InlineData("playnite", "{\"games\":[{\"Name\":\"Example Game\",\"GameId\":\"p-1\",\"ExecutablePath\":\"ignored\"}]}", "p-1")]
    [InlineData("heroic", "{\"library\":[{\"title\":\"Example Game\",\"app_name\":\"h-1\",\"token\":\"ignored\"}]}", "h-1")]
    [InlineData("lutris", "{\"games\":[{\"name\":\"Example Game\",\"slug\":\"l-1\",\"path\":\"ignored\"}]}", "l-1")]
    public void Previews_only_title_provider_and_id(string provider, string json, string id)
    {
        var games = ExternalGameLibraryExportParser.Parse(provider, json);
        var game = Assert.Single(games);
        Assert.Equal(id, game.ExternalId);
        Assert.Equal("Example Game", game.Title);
        Assert.Equal(provider, game.Provider);
    }

    [Fact]
    public void Unrecognized_layout_or_excessive_json_fails_closed()
    {
        Assert.Throws<System.IO.InvalidDataException>(() =>
            ExternalGameLibraryExportParser.Parse("heroic", "{\"games\":[]}"));
        Assert.Throws<System.IO.InvalidDataException>(() =>
            ExternalGameLibraryExportParser.Parse("playnite", new string('x', 512_001)));
    }

    [Fact]
    public void Duplicate_external_id_is_deduplicated_without_mutating_the_library()
    {
        var games = ExternalGameLibraryExportParser.Parse("lutris",
            "{\"games\":[{\"name\":\"First\",\"slug\":\"same\"},{\"name\":\"Second\",\"slug\":\"same\"}]}");
        Assert.Single(games);
        Assert.Equal("First", games[0].Title);
    }

    [Fact]
    public void Price_watch_needs_explicit_opt_in_and_valid_public_store_response()
    {
        const string json = "{\"42\":{\"success\":true,\"data\":{\"price_overview\":{\"currency\":\"EUR\",\"final\":1999}}}}";
        var now = new System.DateTimeOffset(2026, 11, 5, 6, 0, 0, System.TimeSpan.Zero);
        Assert.Null(PublicStorePriceWatchPolicy.ParseSteamAppDetails("42", json, false, now));
        var sample = PublicStorePriceWatchPolicy.ParseSteamAppDetails("42", json, true, now);
        Assert.NotNull(sample);
        Assert.Equal(1999, sample!.PriceMinor);
        var wishlist = new LocalWishlistEntry("steam", "42", "Synthetic Puzzle", "EUR", 2000, false);
        Assert.False(LocalWishlistPolicy.Evaluate(wishlist, sample, now).NotifyEligible);
        Assert.True(LocalWishlistPolicy.Evaluate(
            wishlist with { PriceWatchOptIn = true }, sample, now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(
            wishlist with { PriceWatchOptIn = true, PriceTargetMinor = 1000 }, sample, now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(
            wishlist with { PriceWatchOptIn = true }, sample, now.AddHours(25)).NotifyEligible);
    }

    [Fact]
    public void Malformed_public_response_is_not_a_discount()
    {
        var now = System.DateTimeOffset.UtcNow;
        Assert.Null(PublicStorePriceWatchPolicy.ParseSteamAppDetails("42",
            "{\"42\":{\"success\":true,\"data\":{}}}", true, now));
        Assert.Null(PublicStorePriceWatchPolicy.ParseSteamAppDetails("42",
            "{not-json}", true, now));
    }
}
