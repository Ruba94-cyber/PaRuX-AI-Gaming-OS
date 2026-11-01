namespace PaRuX.Contracts;

/// <summary>A user-defined role for one Windows desktop display. Device names from
/// the OS are not serial numbers: reconnecting or rearranging ports may change IDs.</summary>
public sealed record ConsoleDisplayAssignment(string DisplayId, string Role);

/// <summary>
/// Local routing policy for the SINGLE existing PaRuX window. PaRuX never assumes
/// that a 4K panel is a TV, and never silently redirects to a different monitor
/// when the configured target is disconnected or reidentified by Windows.
/// </summary>
public static class ConsoleDisplaySelectionPolicy
{
    public const string TvRole = "tv";
    public const string MonitorRole = "monitor";

    public static bool ValidDisplayId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        !id.Any(char.IsControl);

    public static bool ValidRole(string? role) =>
        role is TvRole or MonitorRole;

    public static GamingDisplayModeDto? SelectTarget(
        GamingDisplaySnapshotDto? topology,
        IEnumerable<ConsoleDisplayAssignment>? roles,
        string? targetDisplayId)
    {
        if (topology is not { Supported: true } || !ValidDisplayId(targetDisplayId) ||
            roles is null) return null;
        // User settings can be edited or corrupted outside PaRuX. Reject
        // conflicting duplicate assignments instead of choosing whichever
        // happens to be enumerated first.
        var matches = roles.Where(a => a is not null &&
            string.Equals(a.DisplayId, targetDisplayId, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Role != TvRole)
            return null;
        return topology.Displays.FirstOrDefault(d =>
            string.Equals(d.DisplayId, targetDisplayId, StringComparison.OrdinalIgnoreCase) &&
            d.Width > 0 && d.Height > 0);
    }
}
