using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure policy shared by Windows thumbnail caching and source-linked tests.
    /// Cache entries are disposable PaRuX-owned previews, never original video files.
    /// </summary>
    public static class MediaGalleryVideoThumbnailPolicy
    {
        public const uint ThumbnailWidth = 320;
        public const int MaxEncodedBytes = 1024 * 1024;
        public const int MaxCacheEntries = 256;
        public const long MaxCacheBytes = 32L * 1024 * 1024;

        public static string? CacheKey(MediaGalleryItem? item, long length, long lastWriteTicks)
        {
            if (item == null || !item.IsVideo || length <= 0 || lastWriteTicks <= 0
                || !MediaGalleryPreviewPolicy.TryGetLocalMediaPath(item, out string full))
                return null;

            // Path alone cannot identify replacement media. Incorporate size
            // and write time, and keep private local paths out of cache names.
            string normalized = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
            string identity = normalized + "|" + length + "|" + lastWriteTicks;
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        public static bool TryGetImageExtension(ReadOnlySpan<byte> bytes, out string extension)
        {
            extension = string.Empty;
            if (bytes.Length < 8 || bytes.Length > MaxEncodedBytes) return false;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E
                && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A
                && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                extension = ".png";
                return true;
            }
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                extension = ".jpg";
                return true;
            }
            return false;
        }

        // Staging files from an interrupted write are not counted by the
        // ordinary JPEG/PNG cache eviction. Recognize only our own exact
        // SHA256-GUID naming convention, never unrelated user files.
        public const int StagingLifetimeHours = 24;

        public static bool IsStagingFileName(string? name)
        {
            if (name == null || name.Length != 101
                || !name.EndsWith(".tmp", StringComparison.Ordinal))
                return false;
            for (int i = 0; i < 97; i++)
            {
                if (i == 64)
                {
                    if (name[i] != '-') return false;
                    continue;
                }
                char c = name[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            // 64 hex + separator + 32 hex + ".tmp" = 101 chars.
            return name[97] == '.' && name[98] == 't'
                && name[99] == 'm' && name[100] == 'p';
        }

        public static bool IsStaleStaging(DateTime writtenUtc, DateTime nowUtc) =>
            writtenUtc.Kind == DateTimeKind.Utc && nowUtc.Kind == DateTimeKind.Utc
            && writtenUtc <= nowUtc.AddHours(-StagingLifetimeHours);

        /// <summary>Only a direct, named JPEG/PNG file in the managed cache can
        /// be invalidated after an asynchronous WinUI decode failure.</summary>
        public static bool TryGetOwnedCacheFile(
            string? candidate, string? cacheDirectory, out string file)
        {
            file = string.Empty;
            if (string.IsNullOrWhiteSpace(candidate)
                || string.IsNullOrWhiteSpace(cacheDirectory))
                return false;
            try
            {
                if (!Path.IsPathFullyQualified(candidate)
                    || !Path.IsPathFullyQualified(cacheDirectory)
                    || !IsCacheFileName(Path.GetFileName(candidate)))
                    return false;
                string full = Path.GetFullPath(candidate);
                string root = Path.GetFullPath(cacheDirectory);
                string? parent = Path.GetDirectoryName(full);
                if (parent == null || !string.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    Path.TrimEndingDirectorySeparator(root),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return false;
                file = full;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        public static bool IsCacheFileName(string? name)
        {
            if (name == null || name.Length != 68) return false; // 64 hex + 4 extension
            if (!(name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                return false;
            for (int i = 0; i < 64; i++)
            {
                char c = name[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            return true;
        }
    }
}
