using System;
using System.IO;

namespace PaRuX.Services
{
    public enum MediaGalleryPreviewKind
    {
        Unavailable,
        Image,
        Video
    }

    /// <summary>
    /// Pure on-demand preview admission for Media Gallery. Never probes the network,
    /// decodes a thumbnail or launches a file. Paths from Artifact Workspace are not
    /// trusted merely because their metadata says "image" or "video".
    /// </summary>
    public static class MediaGalleryPreviewPolicy
    {
        public const int PreviewDecodeWidth = 960;

        /// <summary>
        /// Normalizes an Artifact ContentRef without probing the filesystem.
        /// Safe to call for every result in a very large library; success says
        /// only that the string is a local-looking supported-media path, NOT
        /// that the file exists, is owned or is safe to open/delete.
        /// </summary>
        public static bool TryGetLocalMediaPath(MediaGalleryItem? item, out string fullPath)
        {
            fullPath = string.Empty;
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return false;
            string path = item.FilePath;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal))
                return false;

            try
            {
                if (!Path.IsPathFullyQualified(path)) return false;
                string full = Path.GetFullPath(path);
                if (!Uri.TryCreate(full, UriKind.Absolute, out Uri? uri)
                    || !uri.IsFile || uri.IsUnc)
                    return false;
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
                    or ".mp4" or ".webm" or ".mov" or ".mkv"))
                    return false;
                fullPath = full;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static MediaGalleryPreviewKind KindFor(MediaGalleryItem? item)
        {
            if (!TryGetLocalMediaPath(item, out string full))
                return MediaGalleryPreviewKind.Unavailable;

            try
            {
                // Only user-visible card/actions do disk admission. Never
                // trust MIME labels or follow a link-file to another target.
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return MediaGalleryPreviewKind.Unavailable;

                return Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => MediaGalleryPreviewKind.Image,
                    ".mp4" or ".webm" or ".mov" or ".mkv" => MediaGalleryPreviewKind.Video,
                    _ => MediaGalleryPreviewKind.Unavailable
                };
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return MediaGalleryPreviewKind.Unavailable;
            }
        }

    }
}
