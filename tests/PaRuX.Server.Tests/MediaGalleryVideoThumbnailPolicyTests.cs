using System;
using System.IO;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGalleryVideoThumbnailPolicyTests
{
    [Fact]
    public void Cache_key_is_deterministic_private_and_changes_for_replaced_video()
    {
        string path = Path.Combine(Path.GetTempPath(), "private-media-" + Guid.NewGuid().ToString("N"), "holiday.mp4");
        MediaGalleryItem item = Video(path);
        string? key = MediaGalleryVideoThumbnailPolicy.CacheKey(item, 123, 638990000000000000);
        Assert.NotNull(key);
        Assert.Equal(64, key!.Length);
        Assert.All(key, c => Assert.True(char.IsAsciiHexDigit(c)));
        Assert.Equal(key, MediaGalleryVideoThumbnailPolicy.CacheKey(item, 123, 638990000000000000));
        Assert.NotEqual(key, MediaGalleryVideoThumbnailPolicy.CacheKey(item, 124, 638990000000000000));
        Assert.NotEqual(key, MediaGalleryVideoThumbnailPolicy.CacheKey(item, 123, 638990000000000001));
        Assert.DoesNotContain("holiday", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cache_key_rejects_nonvideo_untrusted_paths_and_invalid_identity()
    {
        string path = Path.Combine(Path.GetTempPath(), "test.mp4");
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Video(path), 0, 123));
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Video(path), 2, 0));
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Video(@"\\server\share\clip.mp4"), 12, 100));
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Video("https://example.org/clip.mp4"), 12, 100));
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Video("clip.mp4"), 12, 100));
        Assert.Null(MediaGalleryVideoThumbnailPolicy.CacheKey(Image(Path.Combine(Path.GetTempPath(), "test.png")), 12, 100));
    }

    [Fact]
    public void Accepts_only_bounded_real_png_or_jpeg_bytes_not_generic_icon_payload()
    {
        byte[] png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0 };
        byte[] jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0 };
        Assert.True(MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(png, out string pngExt));
        Assert.Equal(".png", pngExt);
        Assert.True(MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(jpeg, out string jpgExt));
        Assert.Equal(".jpg", jpgExt);
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(new byte[8], out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(new byte[7], out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetImageExtension(
            new byte[MediaGalleryVideoThumbnailPolicy.MaxEncodedBytes + 1], out _));
        Assert.InRange(MediaGalleryVideoThumbnailPolicy.ThumbnailWidth, 1u, 512u);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public void Cache_filename_accepts_only_sha256_hex_and_safe_image_extension(string extension)
    {
        string hash = new string('a', 64);
        Assert.True(MediaGalleryVideoThumbnailPolicy.IsCacheFileName(hash + extension));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName("user-" + hash + extension));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName(hash + ".exe"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName(hash[..63] + "g" + extension));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName(hash[..63] + "A" + extension));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName("../" + hash + extension));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsCacheFileName(null));
        Assert.InRange(MediaGalleryVideoThumbnailPolicy.MaxCacheEntries, 1, 512);
        Assert.InRange(MediaGalleryVideoThumbnailPolicy.MaxCacheBytes, 1024L, 64L * 1024 * 1024);
    }

    [Fact]
    public void Stale_staging_cleanup_recognizes_only_owned_hash_and_guid_names()
    {
        string hash = new string('a', 64);
        string guid = new string('b', 32);
        string owned = hash + "-" + guid + ".tmp";
        Assert.True(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(owned));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(hash + ".tmp"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName("user-media.tmp"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(hash + "-" + guid + ".png"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(
            hash + "-" + guid[..31] + "g.tmp"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(
            hash + "-" + guid[..31] + ".tmp"));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(
            "../" + owned));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStagingFileName(null));
    }

    [Fact]
    public void Staging_cleanup_waits_for_24_hours_and_never_reaps_future_files()
    {
        DateTime now = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(24, MediaGalleryVideoThumbnailPolicy.StagingLifetimeHours);
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStaleStaging(
            now.AddHours(-23), now));
        Assert.True(MediaGalleryVideoThumbnailPolicy.IsStaleStaging(
            now.AddHours(-24), now));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStaleStaging(
            now.AddHours(1), now));
        Assert.False(MediaGalleryVideoThumbnailPolicy.IsStaleStaging(
            DateTime.SpecifyKind(now.AddDays(-2), DateTimeKind.Local), now));
    }

    [Fact]
    public void Bad_image_invalidation_is_confined_to_direct_managed_cache_files()
    {
        string cache = Path.Combine(Path.GetTempPath(), "parux-cache-policy", "thumbnail-cache", "video");
        string key = new string('c', 64);
        string owned = Path.Combine(cache, key + ".jpg");

        Assert.True(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            owned, cache, out string canonical));
        Assert.Equal(Path.GetFullPath(owned), canonical);

        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            Path.Combine(cache, key + ".exe"), cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            Path.Combine(cache, "nested", key + ".jpg"), cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            Path.Combine(cache + "-private", key + ".jpg"), cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            Path.Combine(Path.GetTempPath(), "user-photo.jpg"), cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            "https://example.org/" + key + ".png", cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            null, cache, out _));
        Assert.False(MediaGalleryVideoThumbnailPolicy.TryGetOwnedCacheFile(
            owned, null, out _));
    }

    private static MediaGalleryItem Video(string path) => new()
    {
        Id = "clip", Title = "Clip", FilePath = path, ArtifactType = "video",
        MediaType = "video/mp4", Prompt = "test", Model = "local",
        CreatedUtc = DateTime.UtcNow, Status = "final"
    };

    private static MediaGalleryItem Image(string path) => new()
    {
        Id = "image", Title = "Image", FilePath = path, ArtifactType = "image",
        MediaType = "image/png", Prompt = "test", Model = "local",
        CreatedUtc = DateTime.UtcNow, Status = "final"
    };
}
