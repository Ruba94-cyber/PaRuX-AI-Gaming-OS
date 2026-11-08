using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGalleryDeletionPolicyTests
{
    [Fact]
    public void Generated_images_and_videos_directly_inside_owned_roots_may_be_deleted()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string image = Write(images, "generated.png");
            string video = Write(videos, "generated.mp4");
            Assert.True(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(image), images));
            Assert.True(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(video), videos));
            Assert.False(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(image)));
            Assert.False(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(video)));
        });
    }

    [Fact]
    public void Imported_artifact_outside_owned_roots_must_not_delete_user_files()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string personal = Write(outside, "family-photo.png");
            MediaGalleryItem imported = MediaGalleryItemMapper.FromFile(personal, artifactId: "artifact-registered");
            Assert.False(MediaGalleryBrowser.CanDelete(imported, images, videos));
            Assert.True(File.Exists(personal));
        });
    }

    [Fact]
    public void Prefix_sibling_and_nested_subfolders_are_not_equivalent_to_the_output_root()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string sibling = images + "-private";
            string nested = Path.Combine(images, "nested");
            Directory.CreateDirectory(sibling);
            Directory.CreateDirectory(nested);
            string siblingFile = Write(sibling, "do-not-delete.png");
            string nestedFile = Write(nested, "do-not-delete.png");
            Assert.False(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(siblingFile), images));
            Assert.False(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(nestedFile), images));
        });
    }

    [Fact]
    public void Explicit_custom_output_folder_is_allowed_but_never_general_artifact_path()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string customOutput = Path.Combine(outside, "my-media-exports");
            Directory.CreateDirectory(customOutput);
            string exported = Write(customOutput, "export.webp");
            MediaGalleryItem item = MediaGalleryItemMapper.FromFile(exported, artifactId: "custom-output");
            Assert.False(MediaGalleryBrowser.CanDelete(item, images, videos));
            Assert.True(MediaGalleryBrowser.CanDelete(item, images, customOutput));
        });
    }

    [Fact]
    public void Misleading_metadata_does_not_authorize_a_nonmedia_file_or_wrong_kind()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string html = Write(images, "not-media.html");
            string videoInImages = Write(images, "video.mp4");
            MediaGalleryItem disguised = Item(html, "image", "image/png");
            Assert.False(MediaGalleryBrowser.CanDelete(disguised, images));
            Assert.False(MediaGalleryBrowser.CanDelete(
                Item(videoInImages, "image", "image/png"), images));
            Assert.False(MediaGalleryBrowser.CanDelete(
                MediaGalleryItemMapper.FromFile(videoInImages), videos));
        });
    }

    [Fact]
    public void Missing_files_or_relative_unc_and_url_paths_cannot_be_deleted()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            Assert.False(MediaGalleryBrowser.CanDelete(Item(Path.Combine(images, "missing.png")), images));
            Assert.False(MediaGalleryBrowser.CanDelete(Item("media.png"), images));
            Assert.False(MediaGalleryBrowser.CanDelete(Item(@"\\server\share\media.png"), images));
            Assert.False(MediaGalleryBrowser.CanDelete(Item("https://example.org/media.png"), images));
            Assert.False(MediaGalleryBrowser.CanDelete(null, images));
        });
    }

    [Fact]
    public void Symlink_to_an_external_file_is_not_eligible_for_deletion()
    {
        WithOutputFolders((images, videos, outside) =>
        {
            string personal = Write(outside, "personal.png");
            string alias = Path.Combine(images, "generated.png");
            try
            {
                File.CreateSymbolicLink(alias, personal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows developer accounts may not permit test symlink creation.
                return;
            }

            Assert.False(MediaGalleryBrowser.CanDelete(MediaGalleryItemMapper.FromFile(alias), images));
            Assert.True(File.Exists(personal));
        });
    }

    private static MediaGalleryItem Item(string path, string type = "image", string mediaType = "image/png") => new()
    {
        Id = "test-output", Title = "Test", ArtifactType = type,
        MediaType = mediaType, FilePath = path, Prompt = "test",
        Model = "local", Status = "final", CreatedUtc = DateTime.UtcNow
    };

    private static string Write(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static void WithOutputFolders(Action<string, string, string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-delete-" + Guid.NewGuid().ToString("N"));
        string images = Path.Combine(root, "images");
        string videos = Path.Combine(root, "videos");
        string outside = Path.Combine(root, "personal");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(videos);
        Directory.CreateDirectory(outside);
        try { test(images, videos, outside); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
