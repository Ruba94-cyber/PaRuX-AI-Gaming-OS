using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    public enum MediaGalleryFilter
    {
        All,
        ImagesOnly,
        VideosOnly,
        FinalOnly
    }

    public enum MediaGallerySort
    {
        NewestFirst,
        OldestFirst,
        TitleAscending,
        SizeDescending
    }

    /// <summary>
    /// Pure query engine for filtering, searching, and sorting media gallery items,
    /// and validating allowed user actions.
    /// </summary>
    public static class MediaGalleryBrowser
    {
        public const int PageSize = 24;

        /// <summary>Only this page should instantiate WinUI cards and image
        /// thumbnails. All results remain searchable/sortable and deletion
        /// clamps the page index to the last existing page.</summary>
        public sealed record Page(
            IReadOnlyList<MediaGalleryItem> Visible, int TotalMatches, int PageIndex, int PageCount);

        public static Page Paginate(IReadOnlyList<MediaGalleryItem>? filtered, int requestedPage)
        {
            int total = filtered?.Count ?? 0;
            int pageCount = Math.Max(1, (total + PageSize - 1) / PageSize);
            int pageIndex = Math.Clamp(requestedPage, 0, pageCount - 1);
            IReadOnlyList<MediaGalleryItem> visible = filtered == null
                ? Array.Empty<MediaGalleryItem>()
                : filtered.Skip(pageIndex * PageSize).Take(PageSize).ToArray();
            return new Page(visible, total, pageIndex, pageCount);
        }

        public static IReadOnlyList<MediaGalleryItem> FilterAndSort(
            IEnumerable<MediaGalleryItem> items,
            MediaGalleryFilter filter,
            MediaGallerySort sort,
            string? searchQuery = null)
        {
            if (items == null) return Array.Empty<MediaGalleryItem>();

            IEnumerable<MediaGalleryItem> query = items;

            // Apply media kind / status filter
            query = filter switch
            {
                MediaGalleryFilter.ImagesOnly => query.Where(i => i.IsImage),
                MediaGalleryFilter.VideosOnly => query.Where(i => i.IsVideo),
                MediaGalleryFilter.FinalOnly => query.Where(i => string.Equals(i.Status, "final", StringComparison.OrdinalIgnoreCase)),
                _ => query
            };

            // Apply search filter (prompt, title, model)
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string term = searchQuery.Trim();
                query = query.Where(i =>
                    i.Prompt.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Model.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(i.FilePath).Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            // Apply sort
            query = sort switch
            {
                MediaGallerySort.OldestFirst => query.OrderBy(i => i.CreatedUtc),
                MediaGallerySort.TitleAscending => query.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
                MediaGallerySort.SizeDescending => query.OrderByDescending(i => i.SizeBytes ?? 0L),
                _ => query.OrderByDescending(i => i.CreatedUtc)
            };

            return query.ToList();
        }

        public static bool CanOpen(MediaGalleryItem? item) =>
            MediaGalleryPreviewPolicy.KindFor(item) != MediaGalleryPreviewKind.Unavailable;

        public static bool CanReveal(MediaGalleryItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return false;
            string path = item.FilePath;
            // The Explorer action is explicit, but may still trigger an
            // unexpected network connection if Artifact metadata supplies UNC
            // or a relative path. Inspect local outputs only.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal))
                return false;
            try
            {
                if (!Path.IsPathFullyQualified(path)) return false;
                string full = Path.GetFullPath(path);
                if (!Uri.TryCreate(full, UriKind.Absolute, out Uri? uri) || !uri.IsFile || uri.IsUnc)
                    return false;

                if (MediaGalleryPreviewPolicy.KindFor(item) != MediaGalleryPreviewKind.Unavailable)
                    return true;

                // An interrupted/missing output can still be located in its
                // existing local parent directory, without opening the file.
                string? dir = Path.GetDirectoryName(full);
                return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool CanReuseAsEditSource(MediaGalleryItem? item) =>
            item != null
            && item.IsImage
            && !item.IsVideo
            && MediaGalleryPreviewPolicy.KindFor(item) == MediaGalleryPreviewKind.Image;

        /// <summary>The gallery must pass explicit output roots; a populated
        /// ContentRef or an Artifact ID never establishes file ownership.</summary>
        public static bool CanDelete(MediaGalleryItem? item, params string?[] generatedOutputFolders) =>
            MediaGalleryDeletionPolicy.CanDelete(item, generatedOutputFolders);
    }
}
