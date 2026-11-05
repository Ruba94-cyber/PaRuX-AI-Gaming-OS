namespace PaRuX.Contracts;

/// <summary>
/// Public metadata-only view of a user-managed Steam wishlist item.
/// No account token, executable path or purchase data is ever stored.
/// </summary>
public sealed record LocalWishlistItemDto(
    string Store, string ProductId, string Title, string Currency,
    int? PriceTargetMinor, bool PriceWatchOptIn = false)
{
    public string? StoreCountryCode { get; init; }
}

/// <summary>
/// Explicit owner action. The Core persists only a preference;
/// it does not schedule checks or send notifications.
/// </summary>
public sealed record LocalWishlistWatchUpdate(bool Enabled, bool Consent);


/// <summary>One deliberate user click, not a scheduling or monitoring preference.</summary>
public sealed record LocalWishlistQuoteRequest(bool Consent);

/// <summary>
/// Indicative one-shot price for the requested storefront country only.
/// Never claims the user's actual account region, an alert, or purchase eligibility.
/// </summary>
public sealed record LocalWishlistQuoteDto(
    string Store, string ProductId, string RequestedCountry, string Currency,
    int PriceMinor, int? TargetMinor, bool? AtOrBelowTarget,
    DateTimeOffset ObservedAtUtc, string Disclosure);
