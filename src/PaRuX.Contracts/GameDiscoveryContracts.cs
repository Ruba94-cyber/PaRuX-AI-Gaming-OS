namespace PaRuX.Contracts;

/// <summary>Install states a store provider can honestly report.</summary>
public static class GameInstallStates
{
    public const string Unknown = "unknown";
    public const string Installed = "installed";
    public const string Updating = "updating";
    public const string Incomplete = "incomplete";
    /// <summary>Imported third-party title-only metadata; NEVER an executable or a launch grant.</summary>
    public const string MetadataOnly = "metadata-only";
}

/// <summary>
/// Discovery-only game metadata contract v1. Never an executable launch or install grant.
/// Providers cannot ask PaRuX to run anything through these DTOs.
/// <paramref name="InstallState"/> is "unknown" unless the store's own manifest says otherwise.
/// </summary>
public sealed record DiscoveredGameDto(
    string ProviderId,
    string ProviderGameId,
    string Name,
    string InstallDirectory,
    string InstallState = GameInstallStates.Unknown)
{
    /// <summary>Additive (GAM-006): the store's own manifest says a newer build is waiting while this one is still playable.
    /// Read from the local manifest only; PaRuX never contacts a store to ask. Older payloads omit it.</summary>
    public bool UpdatePending { get; init; }
}
