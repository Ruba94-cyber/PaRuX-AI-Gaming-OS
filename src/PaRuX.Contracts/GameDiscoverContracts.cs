namespace PaRuX.Contracts;

/// <summary>
/// Gaming "Discover": recommendations from OFFICIAL public store catalogues only. PaRuX never downloads a
/// game; installation is handed to the official client through a validated deep link after the user confirms.
/// </summary>
public static class GameDiscoverStatus
{
    public const string Ok = "ok";
    /// <summary>Opt-in is off (the default): nothing was sent anywhere.</summary>
    public const string Disabled = "disabled";
    /// <summary>Gaming owns the machine: no network, no scoring.</summary>
    public const string GamingActive = "gaming-active";
    /// <summary>Provider has no official public catalogue API (Epic, GOG).</summary>
    public const string NotAvailable = "not-available";
    public const string Failed = "failed";
}

/// <summary>One catalogue entry as the official store shows it. Prices are display text from the store.</summary>
public sealed record GameCatalogEntryDto(
    string Store,
    string StoreId,
    string Title,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    string? PriceText,
    bool IsFreeToPlay,
    string? HeaderImageUrl,
    string StorePageUrl,
    string? ShortDescription = null);

public sealed record GameRecommendationDto(
    GameCatalogEntryDto Game,
    double Score,
    IReadOnlyList<string> BecauseYouPlay,
    IReadOnlyList<string> SharedTerms,
    string Explanation);

public sealed record GameDiscoverResultDto(
    string Status,
    string? Message,
    IReadOnlyList<GameRecommendationDto> Recommendations);

public sealed record GameCatalogListDto(
    string Status,
    string? Message,
    IReadOnlyList<GameCatalogEntryDto> Games);

public sealed record GameDiscoverSettingsDto(bool Enabled, string ConsentText, IReadOnlyList<string> HiddenStoreIds);

public sealed record SetGameDiscoverSettingsRequest(bool Enabled);

/// <summary>
/// What the UI may do with a recommendation. The UI opens <see cref="StorePageUrl"/> in the browser and,
/// only after an explicit confirmation, launches <see cref="InstallDeepLink"/> (the official client).
/// </summary>
public sealed record GameDiscoverActionsDto(
    string Store,
    string StoreId,
    string StorePageUrl,
    string? InstallDeepLink,
    bool InstallRequiresConfirmation,
    string? InstallNote);
