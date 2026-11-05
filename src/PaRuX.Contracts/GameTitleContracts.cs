namespace PaRuX.Contracts;

/// <summary>Where one installation of a title comes from.</summary>
public static class GameInstallationSources
{
    public const string Manual = "manual";
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Ea = "ea";
    public const string BattleNet = "battlenet";
    public const string Ubisoft = "ubisoft";
    public const string Xbox = "xbox";
    public const string Emulator = "emulator";
    public const string Playnite = "playnite";
    public const string Heroic = "heroic";
    public const string Lutris = "lutris";
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

    /// <summary>Additive: public platform label ("PC" for store/local installs, the emulator platform otherwise).
    /// Older payloads omit it.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Additive: one of <see cref="GameInstallStates"/>; defaults to unknown when a source cannot tell.</summary>
    public string InstallState { get; init; } = GameInstallStates.Unknown;
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

    // Optional additive property: a hidden title is excluded from the library unless explicitly requested.
    public bool Hidden { get; init; }

    // Optional additive property: when the title first received user state (null for older state).
    public DateTime? AddedUtc { get; init; }
}

/// <summary>
/// Pure, side-effect-free choice for one-click Play in both the Desktop and Console home.
/// A saved default is a preference, not a request to start an updating/incomplete copy:
/// prefer an installed copy, then a known-unknown legacy copy. Never silently change
/// the saved preference; the detail panel still offers every installation.
/// </summary>
public static class GameTitleLaunchPolicy
{
    /// <summary>
    /// Unknown means the source cannot determine install state; it remains launchable
    /// for older store clients, but unrecognized future states fail closed.
    /// The actual launch is still validated and user-confirmed by the host.
    /// </summary>
    public static bool CanStart(GameInstallationDto? installation) =>
        installation is not null &&
        // Third-party JSON imports never grant launch authority, even if
        // legacy/forged payloads say the install state is unknown or installed.
        installation.Source is not (GameInstallationSources.Playnite or
            GameInstallationSources.Heroic or GameInstallationSources.Lutris) &&
        (string.Equals(installation.InstallState, GameInstallStates.Installed, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(installation.InstallState, GameInstallStates.Unknown, StringComparison.OrdinalIgnoreCase));

    public static GameInstallationDto? ChooseForQuickPlay(GameTitleDto? title)
    {
        IReadOnlyList<GameInstallationDto>? installations = title?.Installations;
        if (installations is null || installations.Count == 0) return null;

        GameInstallationDto? preferred = string.IsNullOrWhiteSpace(title!.DefaultInstallationId)
            ? null : installations.FirstOrDefault(i =>
                string.Equals(i.InstallationId, title.DefaultInstallationId, StringComparison.Ordinal));

        static bool Installed(GameInstallationDto? item) =>
            item is not null &&
            string.Equals(item.InstallState, GameInstallStates.Installed, StringComparison.OrdinalIgnoreCase);

        if (Installed(preferred)) return preferred;
        GameInstallationDto? installed = installations.FirstOrDefault(Installed);
        if (installed is not null) return installed;

        if (CanStart(preferred)) return preferred;
        GameInstallationDto? legacy = installations.FirstOrDefault(CanStart);
        return legacy ?? preferred ?? installations[0];
    }
}

/// <summary>Allowlisted sort orders of the title list; anything else is rejected.</summary>
public static class GameTitleSorts
{
    public const string Name = "name";
    public const string LastPlayed = "lastplayed";
    public const string PlayTime = "playtime";
    public const string RecentlyAdded = "recentlyadded";

    public static bool IsValid(string? sort) =>
        string.IsNullOrEmpty(sort) || sort.Trim().ToLowerInvariant() is Name or LastPlayed or PlayTime or RecentlyAdded;
}

/// <summary>User state kept per title (the persisted overlay). Installations are never persisted: they are re-derived.</summary>
public sealed class GameTitleState
{
    public bool Favorite { get; set; }
    /// <summary>Absent in files written before this field existed, which load as not hidden.</summary>
    public bool Hidden { get; set; }
    /// <summary>When the title first got user state; drives "recently added". Absent in older files.</summary>
    public DateTime? AddedUtc { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Collections { get; set; } = new();
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public string? DefaultInstallationId { get; set; }
}

public sealed record SetGameTagsRequest(List<string> Tags);
public sealed record SetDefaultInstallationRequest(string? InstallationId);

/// <summary>
/// One atomic edit of up to 100 titles. Only the fields that are set are changed; Tags and Collections
/// replace the full list for every key. All keys and values are validated before anything is written.
/// </summary>
public sealed record BulkGameTitleRequest(List<string> TitleKeys)
{
    public const int MaxKeys = 100;
    public bool? Favorite { get; init; }
    public bool? Hidden { get; init; }
    public List<string>? Tags { get; init; }
    public List<string>? Collections { get; init; }
}

public sealed record BulkGameTitleResult(int Updated);

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

    /// <summary>A successful exit of a verified, directly owned process; this is
    /// never inferred from window focus, game titles, child process scans or
    /// absence of GPU usage. Older Core versions omit the optional fields.</summary>
    public static bool HasConfirmedOwnedExit(GamingStatusDto? status) =>
        status is { State: "AiReady", IsGamingActive: false,
            LastVerifiedOwnedGameExitUtc: not null, LastVerifiedOwnedGameExitRestoredAi: true };
}
