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


/// <summary>
/// Pure window-placement decisions for an opt-in TV Console Home. No OS calls:
/// moving the app or switching presenters is the sole responsibility of WinUI.
/// A configured target must first pass ConsoleDisplaySelectionPolicy.SelectTarget.
/// </summary>
public static class ConsoleHomeRepositionPolicy
{
    public readonly record struct Bounds(int X, int Y, int Width, int Height);

    /// <summary>A Windows fullscreen window is on the requested TV if its
    /// center lies inside that TV's actual virtual-desktop rectangle.</summary>
    public static bool IsOnTarget(GamingDisplayModeDto target, Bounds current)
    {
        if (target.Width <= 0 || target.Height <= 0 ||
            current.Width <= 0 || current.Height <= 0) return false;
        long right = (long)target.X + target.Width;
        long bottom = (long)target.Y + target.Height;
        long cx2 = 2L * current.X + current.Width;
        long cy2 = 2L * current.Y + current.Height;
        return cx2 >= 2L * target.X && cx2 < 2L * right &&
               cy2 >= 2L * target.Y && cy2 < 2L * bottom;
    }

    /// <summary>A safely bounded, windowed anchor which Windows can then make
    /// fullscreen on the chosen display. Supports negative monitor coordinates
    /// without overflow; never requests changes to OS display configuration.</summary>
    public static Bounds? CenteredAnchor(GamingDisplayModeDto target)
    {
        if (target.Width < 2 || target.Height < 2) return null;
        int width = Math.Max(1, target.Width / 2);
        int height = Math.Max(1, target.Height / 2);
        long x = (long)target.X + (target.Width - width) / 2;
        long y = (long)target.Y + (target.Height - height) / 2;
        if (x < int.MinValue || x > int.MaxValue ||
            y < int.MinValue || y > int.MaxValue) return null;
        return new Bounds((int)x, (int)y, width, height);
    }
}
