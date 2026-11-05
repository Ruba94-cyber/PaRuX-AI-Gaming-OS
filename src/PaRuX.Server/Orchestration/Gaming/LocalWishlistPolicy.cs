using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Wishlist data only. Price watching defaults off; storage, networking and notifications are out of scope.</summary>
public sealed record LocalWishlistEntry(
    string Store, string ProductId, string Title, string Currency,
    int? PriceTargetMinor, bool PriceWatchOptIn = false)
{
    /// <summary>Optional two-letter store country; mandatory before a price can qualify.</summary>
    public string? StoreCountryCode { get; init; }
}

public sealed record LocalWishlistEvaluation(bool NotifyEligible, string Reason);

public static class LocalWishlistPolicy
{
    public const int MaximumEntries = 500;

    /// <summary>
    /// Returns a new list after validation. Never mutates the caller's library, contacts a store
    /// or enables monitoring automatically. Only a public Steam numeric app id is supported.
    /// </summary>
    public static (IReadOnlyList<LocalWishlistEntry> Entries, string? Error) TryAdd(
        IReadOnlyList<LocalWishlistEntry> existing, LocalWishlistEntry candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.Store, "steam", StringComparison.OrdinalIgnoreCase)
            || !ValidProductId(candidate.ProductId)
            || string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > 200
            || candidate.Title.Any(char.IsControl)
            || !ValidCurrency(candidate.Currency)
            || (candidate.StoreCountryCode is not null && !ValidCountryCode(candidate.StoreCountryCode))
            || (candidate.PriceWatchOptIn && !ValidCountryCode(candidate.StoreCountryCode))
            || (candidate.PriceWatchOptIn && candidate.PriceTargetMinor is null)
            || candidate.PriceTargetMinor is < 0)
            return (existing.ToArray(), "Choose a supported public store product, currency and optional target.");

        if (existing.Count >= MaximumEntries)
            return (existing.ToArray(), "The local wishlist entry limit has been reached.");
        if (existing.Any(e => string.Equals(e.Store, candidate.Store, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ProductId, candidate.ProductId, StringComparison.Ordinal)))
            return (existing.ToArray(), "This store product is already on the wishlist.");

        var copy = existing.ToList();
        copy.Add(candidate with
        {
            Store = "steam",
            ProductId = candidate.ProductId.Trim(),
            Title = candidate.Title.Trim(),
            Currency = candidate.Currency.ToUpperInvariant(),
            StoreCountryCode = candidate.StoreCountryCode?.ToUpperInvariant()
        });
        return (copy, null);
    }

    /// <summary>
    /// Reports eligibility only. A real notification layer must have separate opt-in, dedupe,
    /// rate limits, country/store-region checks and a user-visible way to turn polling off.
    /// </summary>
    public static LocalWishlistEvaluation Evaluate(
        LocalWishlistEntry entry, PublicStorePriceSample? sample, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.PriceWatchOptIn)
            return new(false, "Price watching is disabled for this item.");
        if (entry.PriceTargetMinor is not int target || target < 0)
            return new(false, "Set a target price before watching.");
        if (!ValidProductId(entry.ProductId) || !ValidCurrency(entry.Currency) || sample is null)
            return new(false, "No valid matching public price sample is available.");
        if (!string.Equals(sample.Store, entry.Store, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sample.ProductId, entry.ProductId, StringComparison.Ordinal)
            || !string.Equals(sample.Currency, entry.Currency, StringComparison.OrdinalIgnoreCase))
            return new(false, "The store, product or currency differs.");
        if (!ValidCountryCode(entry.StoreCountryCode)
            || !ValidCountryCode(sample.StoreCountryCode)
            || !string.Equals(entry.StoreCountryCode, sample.StoreCountryCode, StringComparison.OrdinalIgnoreCase))
            return new(false, "The store country is missing or differs from the selected wishlist country.");
        if (sample.PriceMinor < 0)
            return new(false, "The public price is invalid.");

        TimeSpan age = nowUtc.ToUniversalTime() - sample.ObservedAtUtc.ToUniversalTime();
        if (age < TimeSpan.Zero || age > TimeSpan.FromHours(24))
            return new(false, "The price sample is stale or has a future timestamp.");
        return sample.PriceMinor <= target
            ? new(true, "Price is at or below the target; a separately authorised notifier may report it.")
            : new(false, "Current public price is above the target.");
    }

    private static bool ValidProductId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 12
        && id.All(c => c >= '0' && c <= '9');

    private static bool ValidCurrency(string? code) =>
        code is { Length: 3 }
        && code.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool ValidCountryCode(string? code) =>
        code is { Length: 2 }
        && code.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
}
