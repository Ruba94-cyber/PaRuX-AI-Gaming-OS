namespace PaRuX.Contracts;

/// <summary>
/// Fast, read-only summary over an ALREADY fetched game catalogue. No filesystem,
/// provider, credentials or network access; safe to recompute while Gaming is active.
/// Provider claims of installed/unknown are only launch candidates, not a verified
/// executable or DRM/license check.
/// </summary>
public sealed record GameLibraryHealthSummary(
    int VisibleTitles,
    int LaunchCandidates,
    int ExplicitlyInstalledTitles,
    int NeedsAttention,
    int MetadataOnlyTitles,
    int Favorites,
    int PlayedTitles,
    long TotalPlaySeconds,
    IReadOnlyList<GameTitleDto> AttentionTitles);

public static class GameLibraryHealthPolicy
{
    public static GameLibraryHealthSummary Summarize(IEnumerable<GameTitleDto>? source)
    {
        GameTitleDto[] titles = (source ?? Array.Empty<GameTitleDto>())
            .Where(t => t is not null && !t.Hidden)
            .GroupBy(t => t.TitleKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        int candidate = 0, knownInstalled = 0, metadata = 0, favorites = 0, played = 0;
        long totalSeconds = 0;
        var attention = new List<GameTitleDto>();
        foreach (GameTitleDto title in titles)
        {
            IReadOnlyList<GameInstallationDto> installs = title.Installations ?? Array.Empty<GameInstallationDto>();
            bool canStart = installs.Any(GameTitleLaunchPolicy.CanStart);
            bool explicitlyInstalled = installs.Any(i =>
                GameTitleLaunchPolicy.CanStart(i)
                && string.Equals(i.InstallState, GameInstallStates.Installed, StringComparison.OrdinalIgnoreCase));
            bool onlyMetadata = installs.Count > 0 && installs.All(i =>
                string.Equals(i.InstallState, GameInstallStates.MetadataOnly, StringComparison.OrdinalIgnoreCase));
            if (canStart) candidate++;
            else attention.Add(title);
            if (explicitlyInstalled) knownInstalled++;
            if (onlyMetadata) metadata++;
            if (title.Favorite) favorites++;
            if (title.LastPlayedUtc.HasValue || title.PlaySeconds > 0) played++;
            long seconds = Math.Max(0, title.PlaySeconds);
            totalSeconds = totalSeconds > long.MaxValue - seconds ? long.MaxValue : totalSeconds + seconds;
        }

        return new GameLibraryHealthSummary(titles.Length, candidate, knownInstalled,
            attention.Count, metadata, favorites, played, totalSeconds,
            attention.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.TitleKey, StringComparer.Ordinal)
                .ToArray());
    }
}
