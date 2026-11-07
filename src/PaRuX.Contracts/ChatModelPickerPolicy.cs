using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Contracts;

/// <summary>
/// Pure policy for the default Chat's model chooser. The quick flyout stays
/// small and the full catalogue stays reachable through a searchable,
/// paged picker. No model is downloaded, loaded or switched by this policy.
/// </summary>
public static class ChatModelPickerPolicy
{
    public const int QuickChoices = 12;
    public const int BrowsePageSize = 24;

    public sealed record Page(IReadOnlyList<ManagedModelInfo> Visible, int TotalMatches,
        int PageIndex, int TotalPages);

    public static Page Browse(IEnumerable<ManagedModelInfo>? catalogue, string? query, int requestedPage)
    {
        string search = (query ?? string.Empty).Trim();
        List<ManagedModelInfo> matching = (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.FileName))
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(m => search.Length == 0 ||
                m.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.Alias?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Quantization?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(m => string.IsNullOrWhiteSpace(m.Alias) ? m.FileName : m.Alias,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int pages = Math.Max(1, (matching.Count + BrowsePageSize - 1) / BrowsePageSize);
        int page = Math.Clamp(requestedPage, 0, pages - 1);
        return new Page(matching.Skip(page * BrowsePageSize).Take(BrowsePageSize).ToList(),
            matching.Count, page, pages);
    }

    /// <summary>Quick comparison is allowed only between two distinct,
    /// still-installed local files. Aliases are display-only and must never
    /// be used as a stable model identity.</summary>
    public static bool CanCompare(
        IEnumerable<ManagedModelInfo>? catalogue, string? firstFile, string? secondFile)
    {
        if (string.IsNullOrWhiteSpace(firstFile) || string.IsNullOrWhiteSpace(secondFile)
            || string.Equals(firstFile, secondFile, StringComparison.OrdinalIgnoreCase))
            return false;
        var installed = new HashSet<string>(
            (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.FileName))
                .Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        return installed.Contains(firstFile) && installed.Contains(secondFile);
    }

    public static IReadOnlyList<ManagedModelInfo> Quick(
        IEnumerable<ManagedModelInfo>? catalogue, string? currentManualFile)
    {
        var first = Browse(catalogue, null, 0).Visible.Take(QuickChoices).ToList();
        if (string.IsNullOrWhiteSpace(currentManualFile)) return first;
        ManagedModelInfo? selected = (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
            .FirstOrDefault(m => string.Equals(m.FileName, currentManualFile, StringComparison.OrdinalIgnoreCase));
        if (selected == null || first.Any(m =>
            string.Equals(m.FileName, selected.FileName, StringComparison.OrdinalIgnoreCase)))
            return first;
        if (first.Count >= QuickChoices) first.RemoveAt(first.Count - 1);
        first.Insert(0, selected);
        return first;
    }
}
