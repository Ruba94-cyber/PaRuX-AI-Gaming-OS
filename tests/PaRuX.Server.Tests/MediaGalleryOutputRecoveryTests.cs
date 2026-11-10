using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGalleryOutputRecoveryTests
{
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("processing")]
    [InlineData("cancelling")]
    [InlineData("downloading")]
    public void Active_jobs_do_not_decode_a_partial_file(string status)
    {
        WithMediaFile(".mp4", new byte[] { 1, 2, 3 }, file =>
        {
            MediaGalleryItem item = Make(file, status, "video", "video/mp4");
            Assert.Equal(MediaGalleryOutputState.Generating, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(item));
            Assert.False(MediaGalleryBrowser.CanOpen(item));
            Assert.Contains("Create or Chat", MediaGalleryOutputRecovery.Guidance(
                MediaGalleryOutputRecovery.Inspect(item), italian: false));
        });
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public void Failed_or_cancelled_jobs_preserve_the_partial_file_without_preview(string status)
    {
        WithMediaFile(".png", new byte[] { 1, 2 }, file =>
        {
            MediaGalleryItem item = Make(file, status);
            Assert.Equal(MediaGalleryOutputState.GenerationStopped, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(item));
            Assert.True(File.Exists(file));
            Assert.Contains("partial file is left untouched",
                MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.GenerationStopped, italian: false));
        });
    }

    [Fact]
    public void Missing_file_is_recoverable_by_manual_recheck_after_it_appears()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "generated.png");
            MediaGalleryItem item = Make(file, "final");
            Assert.Equal(MediaGalleryOutputState.Missing, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(item));
            File.WriteAllBytes(file, new byte[] { 3, 4, 5 });
            Assert.Equal(MediaGalleryOutputState.Ready, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Image, MediaGalleryPreviewPolicy.KindFor(item));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Empty_files_wait_for_a_nonempty_completed_output()
    {
        WithMediaFile(".mp4", Array.Empty<byte>(), file =>
        {
            MediaGalleryItem item = Make(file, "final", "video", "video/mp4");
            Assert.Equal(MediaGalleryOutputState.Empty, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Unavailable, MediaGalleryPreviewPolicy.KindFor(item));
            Assert.False(MediaGalleryBrowser.CanOpen(item));
            File.WriteAllBytes(file, new byte[] { 0, 1, 2 });
            Assert.Equal(MediaGalleryOutputState.Ready, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Video, MediaGalleryPreviewPolicy.KindFor(item));
        });
    }

    [Fact]
    public void Draft_with_a_nonempty_file_remains_previewable_as_before()
    {
        WithMediaFile(".png", new byte[] { 1 }, file =>
        {
            MediaGalleryItem item = Make(file, "draft");
            Assert.Equal(MediaGalleryOutputState.Ready, MediaGalleryOutputRecovery.Inspect(item));
            Assert.Equal(MediaGalleryPreviewKind.Image, MediaGalleryPreviewPolicy.KindFor(item));
        });
    }

    [Fact]
    public void Unsupported_paths_and_metadata_never_trigger_network_or_shell_preview()
    {
        Assert.Equal(MediaGalleryOutputState.Unsupported,
            MediaGalleryOutputRecovery.Inspect(Make(@"\\server\share\generated.mp4", "final", "video", "video/mp4")));
        Assert.Equal(MediaGalleryOutputState.Unsupported,
            MediaGalleryOutputRecovery.Inspect(Make("https://example.net/generated.png", "final")));
        Assert.Equal(MediaGalleryOutputState.Unsupported, MediaGalleryOutputRecovery.Inspect(null));
        Assert.Contains("non tenterà di aprirlo",
            MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.Unsupported, italian: true));
    }

    [Fact]
    public void Guidance_is_localized_without_claiming_a_successful_generation()
    {
        Assert.Contains("vuoto", MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.Empty, true));
        Assert.Contains("Create o Chat", MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.Generating, true));
        Assert.Contains("codec", MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.Ready, true));
        Assert.Contains("not been verified", MediaGalleryOutputRecovery.Guidance(MediaGalleryOutputState.Ready, italian: false));
    }

    private static MediaGalleryItem Make(
        string file, string status, string type = "image", string mediaType = "image/png") =>
        new()
        {
            Id = "media-recovery-test", Title = "Generated output", ArtifactType = type,
            MediaType = mediaType, FilePath = file, Prompt = "test", Model = "local",
            Status = status, CreatedUtc = DateTime.UtcNow
        };

    private static void WithMediaFile(string extension, byte[] data, Action<string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "generated" + extension);
            File.WriteAllBytes(file, data);
            action(file);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
