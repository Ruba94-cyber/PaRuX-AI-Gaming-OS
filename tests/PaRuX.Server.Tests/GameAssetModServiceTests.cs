using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Synthetic local PNG textures only. Never starts a game, model, torrent,
/// mod loader or executable. Runtime image decode needs Windows GDI+.
/// </summary>
public sealed class GameAssetModServiceTests
{
    private static byte[] Png(int width, int height, bool original)
    {
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            image.SetPixel(x, y, original && (x + y) % 5 == 0
                ? Color.Transparent
                : original
                    ? Color.FromArgb(180, 40, 100, 160)
                    : Color.FromArgb(255, 200, 30, 20));
        using var output = new MemoryStream();
        image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("game.exe")]
    [InlineData("/full/path.png")]
    [InlineData(@"C:\\windows\\temp.png")]
    [InlineData("a/../b.png")]
    [InlineData("a//b.png")]
    [InlineData(".parux/secrets.png")]
    [InlineData("readme.png:ads")]
    [InlineData("foo/config.json")]
    [InlineData("assets/symbol?.png")]
    [InlineData("assets/leading .png")]
    [InlineData("assets/con.png")]
    [InlineData("assets/LPT1.png")]
    public void Unsafe_virtual_asset_paths_are_rejected(string path)
        => Assert.Throws<ArgumentException>(() => GameAssetModService.ValidateAssetPath(path));

    [Fact]
    public void Explicit_adapter_catalog_never_claims_universal_live_patching()
    {
        var list = GameAssetModService.Adapters();
        Assert.Contains(list, x => x.Id == "generic-png-overlay");
        Assert.Contains(list, x => x.Id == "portable-config-paused");
        Assert.Contains(list, x => x.Id == "minecraft-java-skin");
        var generic = Assert.Single(list.Where(x => x.Id == "generic-png-overlay"));
        Assert.False(generic.CanDirectApply);
        Assert.False(generic.CanHotReload);
        Assert.False(generic.RequiresPausedGame);
        Assert.Equal("Manual ZIP package", generic.Delivery);
    }

    [Fact]
    public async Task AI_texture_preview_exports_single_safe_zip_after_consent_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "parux-multi-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? lastPrompt = null;
            var service = new GameAssetModService(
                (prompt, _) => { lastPrompt = prompt; return Task.FromResult(Png(256, 256, false)); },
                () => false, Path.Combine(root, "bundle"));
            byte[] original = Png(64, 32, true);
            string originalPath = Path.Combine(root, "do-not-touch.png");
            File.WriteAllBytes(originalPath, original);
            var request = new GameAssetModDraftRequest("My Offline RPG",
                "assets/characters/hero_cloak.png", Convert.ToBase64String(original),
                "Green cloth with gold trim");
            GameAssetModDraftDto draft = await service.GenerateDraftAsync(request, CancellationToken.None);
            Assert.Contains("Green cloth", lastPrompt);
            Assert.Equal(64, draft.Width);
            Assert.Equal(32, draft.Height);
            Assert.True(draft.ChangedPixels > 0);
            Assert.False(Directory.Exists(Path.Combine(root, "bundle")));
            Assert.Equal(original, File.ReadAllBytes(originalPath));
            using (var before = new Bitmap(new MemoryStream(original)))
            using (var after = new Bitmap(new MemoryStream(Convert.FromBase64String(draft.PreviewPngBase64))))
            {
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 64; x++)
                    Assert.Equal(before.GetPixel(x, y).A, after.GetPixel(x, y).A);
            }

            Assert.Throws<ArgumentException>(() => service.Export(new(draft.DraftId, false)));
            Assert.Single(service.ListDrafts());
            GameAssetModExportDto exported = service.Export(new(draft.DraftId, true));
            Assert.True(File.Exists(exported.BundlePath));
            Assert.Equal(original, File.ReadAllBytes(originalPath)); // no in-game edit
            Assert.Empty(service.ListDrafts());
            Assert.Throws<InvalidOperationException>(() => service.Export(new(draft.DraftId, true)));
            using var archive = ZipFile.OpenRead(exported.BundlePath);
            Assert.Equal(2, archive.Entries.Count);
            var texture = Assert.Single(archive.Entries.Where(x =>
                x.FullName == "assets/characters/hero_cloak.png"));
            Assert.Single(archive.Entries.Where(x => x.FullName == ".parux/manifest.json"));
            using (var entry = texture.Open())
            using (var image = Image.FromStream(entry))
            {
                Assert.Equal(64, image.Width);
                Assert.Equal(32, image.Height);
            }
            using (var stream = archive.GetEntry(".parux/manifest.json")!.Open())
            using (var document = JsonDocument.Parse(stream))
            {
                Assert.Equal("manual", document.RootElement.GetProperty("installation").GetString());
                Assert.False(document.RootElement.GetProperty("hotReloadGuaranteed").GetBoolean());
                Assert.Equal("My Offline RPG", document.RootElement.GetProperty("title").GetString());
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Gaming_mode_or_bad_source_does_not_start_local_ai_or_write_package()
    {
        if (!OperatingSystem.IsWindows()) return;
        int calls = 0;
        var service = new GameAssetModService(
            (_, _) => { ++calls; return Task.FromResult(Png(64, 64, false)); },
            () => true, Path.Combine(Path.GetTempPath(), "parux-no-mod-" + Guid.NewGuid().ToString("N")));
        var input = new GameAssetModDraftRequest("Sample Game", "textures/armor.png",
            Convert.ToBase64String(Png(64, 64, true)), "blue armor");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateDraftAsync(input, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.Empty(service.ListDrafts());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GenerateDraftAsync(input with { OriginalPngBase64 = "bad!" }, CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
