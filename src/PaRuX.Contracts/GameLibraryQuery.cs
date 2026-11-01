using System.Globalization;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>
/// Pure identity, duplicate and filter rules for the unified Steam / Epic / GOG list. Identity is a UI
/// grouping hint only: every source keeps its own official launch action and nothing is merged or
/// launched on the basis of a name match.
/// </summary>
public static class GameLibraryQuery
{
    public const string AllStores = "";
    public const string AllStates = "";

    /// <summary>Lower-case letters and digits only, so "The Witcher® 3: Wild Hunt" equals "the witcher 3 wild hunt".</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        name = name.Replace("™", string.Empty).Replace("℠", string.Empty);
        // Compatibility decomposition turns the trademark sign into the letters "TM": drop marks first.
        foreach (char c in name.Normalize(NormalizationForm.FormKD))
            if (c is not ('™' or '℠' or '®' or '©') && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }

    /// <summary>True when both entries describe the same title from two different stores.</summary>
    public static bool IsSameTitleInAnotherStore(DiscoveredGameDto a, DiscoveredGameDto b)
    {
        if (string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase)) return false;
        string dirA = NormalizeDirectory(a.InstallDirectory), dirB = NormalizeDirectory(b.InstallDirectory);
        if (dirA.Length > 0 && dirA == dirB) return true;
        string nameA = NormalizeName(a.Name);
        return nameA.Length >= 3 && nameA == NormalizeName(b.Name);
    }

    /// <summary>Other stores that also list the same title (distinct, upper-case, sorted).</summary>
    public static IReadOnlyList<string> OtherStores(DiscoveredGameDto game, IEnumerable<DiscoveredGameDto> all) =>
        all.Where(other => IsSameTitleInAnotherStore(game, other))
           .Select(other => other.ProviderId.ToUpperInvariant())
           .Distinct(StringComparer.Ordinal)
           .OrderBy(id => id, StringComparer.Ordinal)
           .ToArray();

    public static IReadOnlyList<DiscoveredGameDto> Filter(
        IEnumerable<DiscoveredGameDto> games, string? text, string? store, string? state)
    {
        string needle = (text ?? string.Empty).Trim();
        return games
            .Where(g => needle.Length == 0 || g.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .Where(g => string.IsNullOrEmpty(store) || string.Equals(g.ProviderId, store, StringComparison.OrdinalIgnoreCase))
            .Where(g => string.IsNullOrEmpty(state) || string.Equals(g.InstallState, state, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
