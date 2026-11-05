using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Contracts;

/// <summary>
/// A bounded, user-triggered snapshot of explicitly watched Steam entries.
/// Building the plan never fetches prices, schedules work or changes consent.
/// The caller must request each quote with its own owner-only, one-shot consent
/// and stop on cancellation or Gaming. No alert eligibility is implied.
/// </summary>
public static class LocalWishlistManualBatchPolicy
{
    public const int MaximumPerClick = 8;

    public static LocalWishlistManualBatchPlan Build(
        IEnumerable<LocalWishlistItemDto>? entries)
    {
        var choices = new List<LocalWishlistItemDto>(MaximumPerClick);
        int eligible = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (LocalWishlistItemDto? item in entries ?? Enumerable.Empty<LocalWishlistItemDto>())
        {
            if (item is null || !item.PriceWatchOptIn
                || !string.Equals(item.Store, "steam", StringComparison.OrdinalIgnoreCase)
                || !ValidAppId(item.ProductId)
                || item.PriceTargetMinor is not >= 0
                || !ValidCode(item.Currency, 3)
                || !ValidCode(item.StoreCountryCode, 2)
                || !seen.Add(item.ProductId))
                continue;

            eligible++;
            if (choices.Count < MaximumPerClick)
                choices.Add(item);
        }

        return new LocalWishlistManualBatchPlan(choices, eligible,
            Math.Max(0, eligible - choices.Count));
    }

    private static bool ValidAppId(string? appId) =>
        appId is { Length: >= 1 and <= 10 }
        && appId[0] != '0' && appId.All(c => c is >= '0' and <= '9');

    private static bool ValidCode(string? code, int length) =>
        code is not null && code.Length == length &&
        code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
}

public sealed record LocalWishlistManualBatchPlan(
    IReadOnlyList<LocalWishlistItemDto> Items, int EligibleCount, int DeferredCount);
