namespace PaRuX.Contracts;

/// <summary>
/// Discovery-only game metadata contract v1. Never an executable launch or install grant.
/// Providers cannot ask PaRuX to run anything through these DTOs.
/// </summary>
public sealed record DiscoveredGameDto(
    string ProviderId,
    string ProviderGameId,
    string Name,
    string InstallDirectory);
