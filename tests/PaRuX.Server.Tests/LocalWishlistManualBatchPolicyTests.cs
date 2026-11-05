using System;
using System.Linq;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalWishlistManualBatchPolicyTests
{
    private static LocalWishlistItemDto Item(int n, bool optedIn = true) =>
        new("steam", (10000 + n).ToString(), "Synthetic game " + n, "EUR",
            1299, optedIn) { StoreCountryCode = "IT" };

    [Fact]
    public void Batch_is_opt_in_only_bounded_and_reports_unchecked_items()
    {
        var input = Enumerable.Range(1, 13).Select(i => Item(i)).ToArray();
        LocalWishlistManualBatchPlan plan = LocalWishlistManualBatchPolicy.Build(input);
        Assert.Equal(8, plan.Items.Count);
        Assert.Equal(13, plan.EligibleCount);
        Assert.Equal(5, plan.DeferredCount);
        Assert.Equal(input.Take(8), plan.Items);
        Assert.Equal(13, input.Length);
    }

    [Fact]
    public void Disabled_preferences_and_invalid_storefront_metadata_never_start_a_quote()
    {
        LocalWishlistItemDto[] input =
        {
            Item(1, false),
            Item(2) with { Store = "torrent" },
            Item(3) with { StoreCountryCode = null },
            Item(4) with { Currency = "EUR\n" },
            Item(5) with { ProductId = "../etc/passwd" },
            Item(6) with { ProductId = "0004" },
            Item(7) with { PriceTargetMinor = null },
            Item(8) with { PriceTargetMinor = -1 },
            Item(9)
        };
        var plan = LocalWishlistManualBatchPolicy.Build(input);
        Assert.Equal("10009", Assert.Single(plan.Items).ProductId);
        Assert.Equal(1, plan.EligibleCount);
        Assert.Equal(0, plan.DeferredCount);
    }

    [Fact]
    public void Duplicate_ids_and_null_input_are_rejected_without_mutating_original()
    {
        var item = Item(1);
        LocalWishlistItemDto[] original = { item, item with { Title = "Another title" }, Item(2) };
        var plan = LocalWishlistManualBatchPolicy.Build(original);
        Assert.Equal(2, plan.EligibleCount);
        Assert.Equal(new[] { "10001", "10002" }, plan.Items.Select(x => x.ProductId));
        Assert.Equal(3, original.Length);
        Assert.Empty(LocalWishlistManualBatchPolicy.Build(null).Items);
        Assert.Empty(LocalWishlistManualBatchPolicy.Build(new[] { Item(42, optedIn: false) }).Items);
    }
}
