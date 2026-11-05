using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalWishlistPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static LocalWishlistEntry Entry(bool optedIn = true, string currency = "EUR") =>
        new("steam", "42", "Synthetic Puzzle", currency, 2500, optedIn);

    [Fact]
    public void Adding_an_item_does_not_change_the_existing_list_or_auto_opt_in()
    {
        LocalWishlistEntry[] existing = Array.Empty<LocalWishlistEntry>();
        LocalWishlistEntry candidate = Entry(optedIn: false);
        var result = LocalWishlistPolicy.TryAdd(existing, candidate);
        Assert.Null(result.Error);
        Assert.Empty(existing);
        LocalWishlistEntry item = Assert.Single(result.Entries);
        Assert.False(item.PriceWatchOptIn);
        Assert.Equal("steam", item.Store);
    }

    [Fact]
    public void Duplicate_ids_and_invalid_store_or_identifier_are_rejected()
    {
        var items = LocalWishlistPolicy.TryAdd(Array.Empty<LocalWishlistEntry>(), Entry()).Entries;
        Assert.NotNull(LocalWishlistPolicy.TryAdd(items, Entry()).Error);
        Assert.NotNull(LocalWishlistPolicy.TryAdd(items,
            Entry() with { Store = "torrent-provider" }).Error);
        Assert.NotNull(LocalWishlistPolicy.TryAdd(items,
            Entry() with { ProductId = "../escape" }).Error);
    }

    [Fact]
    public void Price_alert_requires_opt_in_and_correct_product_currency_and_store()
    {
        var sample = new PublicStorePriceSample("steam", "42", "EUR", 1999, Now);
        Assert.True(LocalWishlistPolicy.Evaluate(Entry(), sample, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(optedIn: false), sample, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(currency: "USD"), sample, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(), sample with { ProductId = "77" }, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(), sample with { Store = "other" }, Now).NotifyEligible);
    }

    [Fact]
    public void Stale_future_or_above_target_samples_are_not_eligible()
    {
        var sample = new PublicStorePriceSample("steam", "42", "EUR", 2501, Now);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(), sample, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(),
            sample with { PriceMinor = 1000, ObservedAtUtc = Now.AddHours(-25) }, Now).NotifyEligible);
        Assert.False(LocalWishlistPolicy.Evaluate(Entry(),
            sample with { PriceMinor = 1000, ObservedAtUtc = Now.AddMinutes(5) }, Now).NotifyEligible);
    }

    [Fact]
    public void Missing_target_disables_price_watch_without_modifying_entry()
    {
        var before = Entry() with { PriceTargetMinor = null };
        var result = LocalWishlistPolicy.Evaluate(before,
            new PublicStorePriceSample("steam", "42", "EUR", 0, Now), Now);
        Assert.False(result.NotifyEligible);
        Assert.Null(before.PriceTargetMinor);
    }
}
