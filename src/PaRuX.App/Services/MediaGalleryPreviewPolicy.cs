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

        public static MediaGalleryPreviewKind KindFor(MediaGalleryItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return MediaGalleryPreviewKind.Unavailable;
            string path = item.FilePath;

            // Reject URLs, UNC shares, extended-device paths, and relative paths:
            // the preview must not accidentally fetch remote content or resolve
            // relative to the current working directory.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal))
                return MediaGalleryPreviewKind.Unavailable;

            try
            {
                // Untrusted artifact paths may contain invalid characters:
                // both qualification and canonicalization must be guarded.
                if (!Path.IsPathFullyQualified(path))
                    return MediaGalleryPreviewKind.Unavailable;
                string full = Path.GetFullPath(path);
                if (!Uri.TryCreate(full, UriKind.Absolute, out Uri? uri)
                    || !uri.IsFile || uri.IsUnc)
                    return MediaGalleryPreviewKind.Unavailable;

                // Reject link-file aliases before a decoder or shell opener can
                // follow an unexpected target. File.GetAttributes also rejects
                // missing/inaccessible and non-file paths.
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return MediaGalleryPreviewKind.Unavailable;

                // Use the actual file extension, not untrusted Artifact metadata.
                // Do not preview executable, script, HTML, or unknown types.
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
