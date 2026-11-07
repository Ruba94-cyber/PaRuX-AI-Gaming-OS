using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGalleryPreviewPolicyTests
{
    [Fact]
    public void Preview_accepts_existing_local_media_without_decoding_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string image = Path.Combine(folder, "sample.png");
        string video = Path.Combine(folder, "sample.mp4");
        try
        {
            // Preview admission only checks identity and local extension; it does
            // not decode bytes or launch a player during a background gallery scan.
            File.WriteAllBytes(image, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(video, new byte[] { 4, 5, 6 });
            Assert.Equal(MediaGalleryPreviewKind.Image,
                MediaGalleryPreviewPolicy.KindFor(MediaGalleryItemMapper.FromFile(image)));
            Assert.Equal(MediaGalleryPreviewKind.Video,
                MediaGalleryPreviewPolicy.KindFor(MediaGalleryItemMapper.FromFile(video)));
            Assert.InRange(MediaGalleryPreviewPolicy.PreviewDecodeWidth, 1, 1280);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Preview_rejects_executables_even_if_metadata_claims_image()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "not-an-image.exe");
        try
        {
            File.WriteAllBytes(executable, new byte[] { 1, 2 });
            MediaGalleryItem untrusted = Item(executable, "image", "image/png");
            Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(untrusted));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Preview_rejects_missing_relative_and_remote_references()
    {
        Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(null));
        Assert.Equal(MediaGalleryPreviewKind.Unavailable,
            MediaGalleryPreviewPolicy.KindFor(Item("media.png")));
        Assert.Equal(MediaGalleryPreviewKind.Unavailable,
            MediaGalleryPreviewPolicy.KindFor(Item("invalid" + '\0' + ".png")));
        Assert.Equal(MediaGalleryPreviewKind.Unavailable,
            MediaGalleryPreviewPolicy.KindFor(Item("https://example.org/clip.mp4", "video", "video/mp4")));
        Assert.Equal(MediaGalleryPreviewKind.Unavailable,
            MediaGalleryPreviewPolicy.KindFor(Item(@"\\server\share\clip.mp4", "video", "video/mp4")));
        Assert.Equal(MediaGalleryPreviewKind.Unavailable,
            MediaGalleryPreviewPolicy.KindFor(Item(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"),
                "video", "video/mp4")));
    }

    [Fact]
    public void Preview_rejects_incomplete_or_unsupported_media_without_changing_file()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string unsupported = Path.Combine(folder, "document.html");
        try
        {
            File.WriteAllText(unsupported, "<test>local content</test>");
            long sizeBefore = new FileInfo(unsupported).Length;
            Assert.Equal(MediaGalleryPreviewKind.Unavailable,
                MediaGalleryPreviewPolicy.KindFor(Item(unsupported, "image", "image/png")));
            Assert.Equal(sizeBefore, new FileInfo(unsupported).Length);
            Assert.True(File.Exists(unsupported));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static MediaGalleryItem Item(string path, string type = "image", string mediaType = "image/png") => new()
    {
        Id = "preview-test", Title = "Test output", ArtifactType = type,
        MediaType = mediaType, FilePath = path, Prompt = "test",
        Model = "Local", CreatedUtc = DateTime.UtcNow, Status = "final"
    };
}
