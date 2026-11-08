using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace PaRuX.Services
{
    /// <summary>
    /// Optional Windows shell video-frame thumbnails for visible Gallery cards.
    /// Decodes at most one at a time, only outside Gaming, and keeps a bounded,
    /// disposable cache under PaRuX's own data root. No FFmpeg installation,
    /// background library scan or writes to source video files.
    /// </summary>
    public sealed class MediaGalleryVideoThumbnailService
    {
        public static MediaGalleryVideoThumbnailService Current { get; } = new();
        private readonly SemaphoreSlim _decoderGate = new(1, 1);
        private DateTime _lastCacheMaintenanceUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheMaintenanceInterval = TimeSpan.FromMinutes(15);
        private static string CacheDirectory =>
            Path.Combine(AppPaths.PaRuXRoot, "thumbnail-cache", "video");

        private MediaGalleryVideoThumbnailService() { }

        /// <summary>Returns a previously cached or newly extracted PNG/JPEG
        /// thumbnail, or null if Windows cannot supply a genuine image frame.
        /// The caller must keep old placeholder UI when the result is null.</summary>
        public async Task<string?> GetOrCreateAsync(
            MediaGalleryItem item, Func<bool> gamingActive, CancellationToken cancellationToken)
        {
            if (item == null || gamingActive == null || gamingActive()
                || MediaGalleryPreviewPolicy.KindFor(item) != MediaGalleryPreviewKind.Video)
                return null;

            await _decoderGate.WaitAsync(cancellationToken);
            string? staging = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive() || !MediaGalleryPreviewPolicy.TryGetLocalMediaPath(item, out string source))
                    return null;

                var fileInfo = new FileInfo(source);
                fileInfo.Refresh();
                long size = fileInfo.Length;
                long ticks = fileInfo.LastWriteTimeUtc.Ticks;
                string? key = MediaGalleryVideoThumbnailPolicy.CacheKey(item, size, ticks);
                if (key == null) return null;

                foreach (string ext in new[] { ".jpg", ".png" })
                {
                    string hit = Path.Combine(CacheDirectory, key + ext);
                    if (IsUsableCachedThumbnail(hit))
                    {
                        // A restart can leave stale staging files even when
                        // every card is served from a cache hit. Maintain
                        // occasionally; never rescan the directory per card.
                        await MaintainCacheIfDueAsync(gamingActive, cancellationToken);
                        return !gamingActive() && !cancellationToken.IsCancellationRequested
                            && IsUsableCachedThumbnail(hit) ? hit : null;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive()) return null;
                StorageFile file = await StorageFile.GetFileFromPathAsync(source);
                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive()) return null;

                // Windows uses its own video thumbnail provider to extract a
                // frame. Skip generic icons and broken/unsupported formats.
                using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.VideosView,
                    MediaGalleryVideoThumbnailPolicy.ThumbnailWidth,
                    ThumbnailOptions.UseCurrentScale);
                if (thumbnail == null || thumbnail.Type != ThumbnailType.Image
                    || thumbnail.Size < 8
                    || thumbnail.Size > MediaGalleryVideoThumbnailPolicy.MaxEncodedBytes)
                    return null;
                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive()) return null;

                uint byteCount = checked((uint)thumbnail.Size);
                using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
                uint loaded = await reader.LoadAsync(byteCount);
                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive() || loaded != byteCount) return null;
                byte[] encoded = new byte[checked((int)byteCount)];
                reader.ReadBytes(encoded);
                if (!MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(encoded, out string extension))
                    return null;

                fileInfo.Refresh();
                if (fileInfo.Length != size || fileInfo.LastWriteTimeUtc.Ticks != ticks
                    || MediaGalleryPreviewPolicy.KindFor(item) != MediaGalleryPreviewKind.Video)
                    return null;

                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive()) return null;

                Directory.CreateDirectory(CacheDirectory);
                string destination = Path.Combine(CacheDirectory, key + extension);
                staging = Path.Combine(CacheDirectory, key + "-" + Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllBytesAsync(staging, encoded, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (gamingActive()) return null;
                File.Move(staging, destination, overwrite: true);
                staging = null;

                // Cache trimming is best-effort; failure never affects the
                // source output, and never claims a missing thumbnail exists.
                await MaintainCacheIfDueAsync(gamingActive, cancellationToken);
                return !gamingActive() && !cancellationToken.IsCancellationRequested
                    && IsUsableCachedThumbnail(destination) ? destination : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // No shell fallback that could open/execute a file. Codec or
                // disk errors leave the existing video placeholder visible.
                Log.Write("Gallery video thumbnail unavailable: " + ex.GetType().Name);
                return null;
            }
            finally
            {
                if (staging != null)
                {
                    try { File.Delete(staging); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                _decoderGate.Release();
            }
        }

        private async Task MaintainCacheIfDueAsync(
            Func<bool> gamingActive, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || gamingActive()
                || DateTime.UtcNow - _lastCacheMaintenanceUtc < CacheMaintenanceInterval)
                return;
            try
            {
                await Task.Run(() => TrimCache(gamingActive, ct), ct);
                if (!ct.IsCancellationRequested && !gamingActive())
                    _lastCacheMaintenanceUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Write("Gallery thumbnail cache trim: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Called only when WinUI reports that a returned cached bitmap could
        /// not be decoded asynchronously. Removing the cache copy permits a
        /// fresh Windows thumbnail on a later visit; the original video is
        /// never touched. UI handlers must not block on filesystem operations.
        /// </summary>
        public async Task InvalidateDecodeFailureAsync(
            string? cachePath, Func<bool> gamingActive)
        {
            if (gamingActive == null || gamingActive()
                || !MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
                    cachePath, CacheDirectory, out string owned))
                return;
            await _decoderGate.WaitAsync();
            try
            {
                if (gamingActive()) return;
                await Task.Run(() =>
                {
                    try
                    {
                        if (gamingActive() || !File.Exists(owned)
                            || (new DirectoryInfo(CacheDirectory).Attributes & FileAttributes.ReparsePoint) != 0
                            || (File.GetAttributes(owned) & FileAttributes.ReparsePoint) != 0)
                            return;
                        File.Delete(owned);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or ArgumentException or NotSupportedException)
                    {
                        Log.Write("Gallery rejected cache invalidation: " + ex.GetType().Name);
                    }
                });
            }
            finally
            {
                _decoderGate.Release();
            }
        }

        private static bool IsUsableCachedThumbnail(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    || info.Length < 8
                    || info.Length > MediaGalleryVideoThumbnailPolicy.MaxEncodedBytes)
                    return false;
                // A corrupted or partially-written cache must not become
                // a permanent broken image. Check the encoded signature on
                // cache hit and regenerate from the video if it is invalid.
                byte[] header = new byte[8];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Read(header, 0, header.Length) != header.Length)
                        return false;
                }
                return MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(header, out string extension)
                    && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static void TrimCache(Func<bool> gamingActive, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || gamingActive() || !Directory.Exists(CacheDirectory))
                return;
            var directory = new DirectoryInfo(CacheDirectory);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;

            FileInfo[] all = directory.EnumerateFiles().ToArray();
            DateTime now = DateTime.UtcNow;

            // Interrupted writes can leave .tmp files that the ordinary
            // PNG/JPEG cap does not count. Reap only our exact staging names,
            // and only after a generous 24-hour grace period.
            foreach (FileInfo item in all)
            {
                if (ct.IsCancellationRequested || gamingActive()) return;
                if (!MediaGalleryVideoThumbnailPolicy.IsStagingFileName(item.Name)
                    || !MediaGalleryVideoThumbnailPolicy.IsStaleStaging(item.LastWriteTimeUtc, now)
                    || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                try { item.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            FileInfo[] entries = all
                .Where(f => MediaGalleryVideoThumbnailPolicy.IsCacheFileName(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
            long bytes = 0;
            int kept = 0;
            foreach (FileInfo file in entries)
            {
                if (ct.IsCancellationRequested || gamingActive()) return;
                // Never dereference unexpected linked entries during trim.
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (kept < MediaGalleryVideoThumbnailPolicy.MaxCacheEntries
                    && bytes + file.Length <= MediaGalleryVideoThumbnailPolicy.MaxCacheBytes)
                {
                    kept++;
                    bytes += file.Length;
                    continue;
                }
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
