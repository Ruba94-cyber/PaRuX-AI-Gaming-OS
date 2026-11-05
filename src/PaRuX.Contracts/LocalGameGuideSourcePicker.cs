using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Contracts;

/// <summary>A visible indexed document folder in the existing local-game Chat handoff.</summary>
public sealed record LocalGameGuideSourceOption(string Id, string FolderPath);

public sealed record LocalGameGuideSourcePage(
    IReadOnlyList<LocalGameGuideSourceOption> Visible, int TotalMatches);

/// <summary>
/// Search a potentially large set of enabled RAG folders without silently discarding
/// everything after the first 64. Selection lives independently of the visible page.
/// Server-side membership, indexed content and Gaming admission remain authoritative.
/// </summary>
public static class LocalGameGuideSourcePicker
{
    public const int MaximumSelected = 64;
    public const int MaximumVisible = 64;

    public static LocalGameGuideSourcePage Filter(
        IEnumerable<LocalGameGuideSourceOption>? available, string? search)
    {
        string query = (search ?? string.Empty).Trim();
        var visible = new List<LocalGameGuideSourceOption>(MaximumVisible);
        int total = 0;
        foreach (LocalGameGuideSourceOption option in available
                     ?? Enumerable.Empty<LocalGameGuideSourceOption>())
        {
            if (option is null || string.IsNullOrWhiteSpace(option.Id) ||
                string.IsNullOrWhiteSpace(option.FolderPath))
                continue;
            if (query.Length > 0 &&
                !option.FolderPath.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !option.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            total++;
            if (visible.Count < MaximumVisible)
                visible.Add(option);
        }
        return new(visible, total);
    }

    /// <summary>
    /// Update a selection even if the item is currently filtered out.
    /// Invalid IDs and selection beyond the request limit are refused, not truncated.
    /// </summary>
    public static bool TrySetSelected(ISet<string> selected, string? id, bool checkedState)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl))
            return false;
        if (!checkedState)
        {
            selected.Remove(id);
            return true;
        }
        return selected.Contains(id) || selected.Count < MaximumSelected && selected.Add(id);
    }
}
