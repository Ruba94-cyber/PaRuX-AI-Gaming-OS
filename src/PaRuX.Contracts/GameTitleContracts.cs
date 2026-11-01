namespace PaRuX.Contracts;

/// <summary>Where one installation of a title comes from.</summary>
public static class GameInstallationSources
{
    public const string Manual = "manual";
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Emulator = "emulator";
}

/// <summary>
/// One concrete way to run a title: a store install, a local or portable executable, or an emulated copy.
/// <paramref name="InstallationId"/> is "{source}:{sourceGameId}" and is stable across restarts.
/// </summary>
public sealed record GameInstallationDto(string InstallationId, string Source, string SourceGameId, string Kind, string Name)
{
    /// <summary>Optional public emulator display label, never an executable/ROM path.
    /// An older Core or stored title payload may omit it without breaking desktop/TV clients.</summary>
    public string EmulatorName { get; init; } = string.Empty;
}

/// <summary>
/// A game as the user thinks of it. Identity (<paramref name="TitleKey"/>) is separate from installations,
/// so the same title found in Steam and as a portable copy is one card with two ways to start it.
/// </summary>
public sealed record GameTitleDto(
    string TitleKey,
    string Name,
    bool Favorite,
    IReadOnlyList<string> Tags,
    long PlaySeconds,
    DateTime? LastPlayedUtc,
    string? DefaultInstallationId,
    IReadOnlyList<GameInstallationDto> Installations)
{
    // Optional additive property; older clients can omit it without invalidating titles.
    public IReadOnlyList<string> Collections { get; init; } = Array.Empty<string>();
}

/// <summary>User state kept per title (the persisted overlay). Installations are never persisted: they are re-derived.</summary>
public sealed class GameTitleState
{
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Collections { get; set; } = new();
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public string? DefaultInstallationId { get; set; }
}

public sealed record SetGameTagsRequest(List<string> Tags);
public sealed record SetDefaultInstallationRequest(string? InstallationId);

/// <summary>Up to 12 named local user collections for one title; no cloud account or scraping.</summary>
public sealed record SetGameCollectionsRequest(List<string> Collections);

/// <summary>
/// On a user-initiated return to the foreground, refresh potentially changed game
/// statistics only when a previously observed Gaming session is confirmed finished.
/// Unreachable Core, an in-progress transition or a persisted Gaming hard-off
/// cannot be interpreted as success. This helper performs no background polling.
/// </summary>
public static class GameLibraryForegroundReturnPolicy
{
    public static bool CanRefreshTitles(bool previouslyGaming, GamingStatusDto? status) =>
        previouslyGaming && status is { State: "AiReady", IsGamingActive: false };
}
