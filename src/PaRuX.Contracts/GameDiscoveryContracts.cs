namespace PaRuX.Contracts;

/// <summary>Install states a store provider can honestly report.</summary>
public static class GameInstallStates
{
    public const string Unknown = "unknown";
    public const string Installed = "installed";
    public const string Updating = "updating";
    public const string Incomplete = "incomplete";
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
    string InstallState = GameInstallStates.Unknown);
