using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Deterministic offline 64x64 synthetic PNG skin and AI texture fixtures.
/// Requires Windows System.Drawing; no model, game install or network involved.
/// </summary>
public sealed class MinecraftSkinUvRestylerTests
{
    private static byte[] Png(int width, int height, bool skin)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // Skin: opaque base, mostly transparent second layers and unused cells.
            bool opaque = !skin || y is >= 8 and < 32 || (x is >= 8 and < 16 && y < 8);
            bitmap.SetPixel(x, y, skin && x == 21 && y == 37
                ? Color.FromArgb(120, 160, 100, 80) // translucent jacket pixel
                : opaque
                    ? Color.FromArgb(255, 80 + x % 10, 100 + y % 10, 130)
                    : Color.FromArgb(0, 0, 0, 0));
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    [Theory]
    [InlineData("classic", 44, 20, 4)]
    [InlineData("slim", 44, 20, 3)]
    public void Arm_front_faces_match_vanilla_uv_coordinates(string arm, int x, int y, int width)
    {
        var faces = MinecraftSkinUvRestyler.Faces("arms", arm);
        Assert.Contains(faces, f => f.Rect.X == x && f.Rect.Y == y
            && f.Rect.Width == width && f.Rect.Height == 12);
        Assert.Contains(faces, f => f.Rect.X == 36 && f.Rect.Y == 52
            && f.Rect.Width == width && f.Rect.Height == 12);
        Assert.All(faces, f => Assert.True(f.Rect.X >= 0 && f.Rect.Right <= 64
            && f.Rect.Y >= 0 && f.Rect.Bottom <= 64));
    }

    [Theory]
    [InlineData("head", 8, 8, 8, 8)]
    [InlineData("torso", 20, 20, 8, 12)]
    [InlineData("legs", 4, 20, 4, 12)]
    public void Selected_front_face_coordinates_are_fixed(string part, int x, int y, int w, int h)
    {
        Assert.Contains(MinecraftSkinUvRestyler.Faces(part, "classic"),
            f => f.Rect == new Rectangle(x, y, w, h));
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("slim")]
    public void Restyle_only_changes_selected_existing_uv_pixels_and_preserves_alpha(string style)
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] original = Png(64, 64, true);
        byte[] reference = Png(128, 128, false);
        byte[] result = MinecraftSkinUvRestyler.Recolour(original, reference, "torso", style);
        using var before = new Bitmap(new MemoryStream(original));
        using var after = new Bitmap(new MemoryStream(result));
        var pixels = MinecraftSkinUvRestyler.Faces("torso", style)
            .SelectMany(f => Enumerable.Range(f.Rect.Y, f.Rect.Height)
                .SelectMany(y => Enumerable.Range(f.Rect.X, f.Rect.Width).Select(x => (x, y))))
            .ToHashSet();
        int changes = 0;
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
            Assert.Equal(a.A, b.A);
            if (!pixels.Contains((x, y)) || a.A == 0)
                Assert.Equal(a.ToArgb(), b.ToArgb());
            else if (a.ToArgb() != b.ToArgb())
                changes++;
        }
        Assert.True(changes > 0);
    }

    [Fact]
    public void Bad_dimensions_non_png_and_invalid_part_fail_without_silent_resizing()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] material = Png(128, 128, false);
        Assert.Throws<InvalidDataException>(() => MinecraftSkinUvRestyler.Recolour(
            Png(128, 128, true), material, "head", "classic"));
        Assert.Throws<InvalidDataException>(() => MinecraftSkinUvRestyler.Recolour(
            new byte[256], material, "head", "classic"));
        Assert.Throws<ArgumentException>(() => MinecraftSkinUvRestyler.Recolour(
            Png(64, 64, true), material, "body-exe", "classic"));
    }

    [Fact]
    public void Oversized_advertised_PNG_dimensions_fail_before_native_decoding()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] badSkin = Png(64, 64, true);
        // Replace ONLY IHDR dimensions. A native decoder should never see
        // this image (CRC deliberately left wrong).
        badSkin[16] = 0x7f;
        badSkin[17] = 0xff;
        Assert.Throws<InvalidDataException>(() =>
            MinecraftSkinUvRestyler.ValidateSkinPng(badSkin));

        byte[] badReference = Png(128, 128, false);
        badReference[20] = 0x7f;
        badReference[21] = 0xff;
        Assert.Throws<InvalidDataException>(() =>
            MinecraftSkinUvRestyler.Recolour(Png(64, 64, true), badReference,
                "head", "classic"));
    }

    [Fact]
    public async Task Imported_skin_local_ai_restylng_requires_separate_consent_to_export()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(),
            "parux-skin-uv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            int calls = 0;
            var service = new GameSkinStudioService(
                (_, _) => { calls++; return Task.FromResult(Png(128, 128, false)); },
                () => false, Path.Combine(folder, "export"));
            byte[] original = Png(64, 64, true);
            var request = new GameSkinRestyleRequest(Convert.ToBase64String(original),
                "Red armor", "torso", "classic");
            GameSkinDraftDto preview = await service.RestyleDraftAsync(request, CancellationToken.None);
            Assert.Equal(1, calls);
            Assert.Equal(64, new Bitmap(new MemoryStream(
                Convert.FromBase64String(preview.PngBase64))).Width);
            Assert.False(Directory.Exists(Path.Combine(folder, "export")));
            Assert.Throws<ArgumentException>(() => service.Export(new(preview.DraftId, false)));
            var saved = service.Export(new(preview.DraftId, true));
            Assert.True(File.Exists(saved.FilePath));
            Assert.False(File.Exists(Path.Combine(folder, "minecraft.jar")));
            Assert.Equal(0, service.ListDrafts().Count);
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }
}
