using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Contracts;

/// <summary>
/// A bounded, searchable page of already authorised local document folders.
/// Selection is held separately in ChatDocumentScopeState and is never
/// modified just because a row is filtered or paged out of view.
/// </summary>
public sealed record DocumentSourcePage(
    IReadOnlyList<RagSourceDto> Visible,
    int TotalMatches,
    int PageIndex,
    int PageCount);

public static class DocumentSourcePicker
{
    public const int PageSize = 40;

    public static DocumentSourcePage Filter(
        IEnumerable<RagSourceDto>? sources, string? search, int requestedPage,
        bool includeDisabled = false)
    {
        string query = (search ?? string.Empty).Trim();
        // A lexical path search is fast even for many thousands of folders.
        // Chat excludes disabled sources; the document management view must
        // also show them so the owner can inspect or remove those entries.
        var matches = (sources ?? Enumerable.Empty<RagSourceDto>())
            .Where(source => source != null && (includeDisabled || source.Enabled)
                && !string.IsNullOrWhiteSpace(source.Id)
                && !string.IsNullOrWhiteSpace(source.FolderPath))
            .Where(source => query.Length == 0
                || source.FolderPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || source.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .ToList();

        int pages = Math.Max(1, (int)(((long)matches.Count + PageSize - 1) / PageSize));
        int current = Math.Clamp(requestedPage, 0, pages - 1);
        return new DocumentSourcePage(
            matches.Skip(current * PageSize).Take(PageSize).ToList(),
            matches.Count, current, pages);
    }
}
