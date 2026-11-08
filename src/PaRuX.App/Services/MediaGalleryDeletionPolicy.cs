using System;
using System.Collections.Generic;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// A gallery record is NOT evidence that PaRuX owns its ContentRef.
    /// Direct file deletion is available only for existing media placed directly
    /// in an app-generated output directory (or a folder explicitly configured
    /// by the owner for generated media). External/imported artifacts stay visible
    /// and may still be opened, but cannot be deleted by the gallery.
    /// </summary>
    public static class MediaGalleryDeletionPolicy
    {
        public static bool CanDelete(MediaGalleryItem? item, params string?[] outputDirectories)
        {
            if (item == null || outputDirectories == null || outputDirectories.Length == 0)
                return false;

            MediaGalleryPreviewKind kind = MediaGalleryPreviewPolicy.KindFor(item);
            if (kind == MediaGalleryPreviewKind.Unavailable
                || (item.IsVideo && kind != MediaGalleryPreviewKind.Video)
                || (item.IsImage && kind != MediaGalleryPreviewKind.Image)
                || (!item.IsVideo && !item.IsImage))
                return false;

            try
            {
                string fullPath = Path.GetFullPath(item.FilePath);
                string? parent = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(parent))
                    return false;

                // Symlinks/junctions, including a configured output directory
                // which resolves outside its apparent location, are not an
                // appropriate target for destructive actions. Fail closed.
                if (HasReparsePointInAncestry(fullPath, parent))
                    return false;

                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                foreach (string? configured in outputDirectories)
                {
                    if (string.IsNullOrWhiteSpace(configured))
                        continue;
                    string root = Path.GetFullPath(configured);
                    if (!Path.IsPathFullyQualified(configured) || !Directory.Exists(root))
                        continue;
                    if (string.Equals(Path.TrimEndingDirectorySeparator(root),
                            Path.TrimEndingDirectorySeparator(parent), comparison))
                        return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                // Unknown/hostile paths or access-denied output roots are never
                // treated as deletable.
            }
            return false;
        }

        private static bool HasReparsePointInAncestry(string file, string parent)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                return true;

            string? current = parent;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
                string? next = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
                if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.Ordinal))
                    break;
                current = next;
            }
            return false;
        }
    }
}
