using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Offline, inert images only. Real local generation/GPU inference, Minecraft UV
/// correctness and actual skin import require Windows and user acceptance.
/// </summary>
public sealed class GameSkinStudioServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-skin-tests-" + Guid.NewGuid().ToString("N"));

    public GameSkinStudioServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] InertPng()
    {
        using var bitmap = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.FromArgb(255, 15, 55, 95));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public async Task Draft_is_local_preview_only_and_export_is_explicit_and_single_use()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] fixture = InertPng();
        string? observedPrompt = null;
        var service = new GameSkinStudioService(
            (p, _) => { observedPrompt = p; return Task.FromResult(fixture); },
            () => false, Path.Combine(_root, "exports"));
        var draft = await service.GenerateDraftAsync(
            new GameSkinDraftRequest("green explorer", "slim"), CancellationToken.None);
        Assert.Contains("green explorer", observedPrompt);
        Assert.Contains("UV ATLAS", observedPrompt);
        Assert.False(File.Exists(Path.Combine(_root, "exports", draft.DraftId + ".png")));
        Assert.Single(service.ListDrafts());

        byte[] image = Convert.FromBase64String(draft.PngBase64);
        using (var bitmap = Image.FromStream(new MemoryStream(image)))
        {
            Assert.Equal(64, bitmap.Width);
            Assert.Equal(64, bitmap.Height);
        }
        Assert.Throws<ArgumentException>(() => service.Export(new(draft.DraftId, false)));
        Assert.Single(service.ListDrafts());

        GameSkinExportDto exported = service.Export(new(draft.DraftId, true));
        Assert.Equal("slim", exported.ArmStyle);
        Assert.StartsWith(Path.Combine(_root, "exports"), exported.FilePath);
        Assert.True(File.Exists(exported.FilePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), exported.Sha256);
        Assert.Equal(image, File.ReadAllBytes(exported.FilePath));
        Assert.Empty(service.ListDrafts());
        Assert.Throws<InvalidOperationException>(() => service.Export(new(draft.DraftId, true)));
    }

    [Fact]
    public async Task Invalid_input_and_active_gaming_cannot_generate_or_export()
    {
        if (!OperatingSystem.IsWindows()) return;
        int generated = 0;
        var service = new GameSkinStudioService(
            (_, _) => { generated++; return Task.FromResult(InertPng()); },
            () => true, Path.Combine(_root, "blocked"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GenerateDraftAsync(new GameSkinDraftRequest("", "classic"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GenerateDraftAsync(new GameSkinDraftRequest("player", "arbitrary"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateDraftAsync(new GameSkinDraftRequest("player", "classic"), CancellationToken.None));
        Assert.Equal(0, generated);
        Assert.False(Directory.Exists(Path.Combine(_root, "blocked")));
    }

    [Fact]
    public async Task Invalid_generated_image_cannot_be_saved_or_return_success()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = new GameSkinStudioService(
            (_, _) => Task.FromResult(new byte[] { 1, 2, 3, 4 }),
            () => false, Path.Combine(_root, "bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GenerateDraftAsync(new GameSkinDraftRequest("robot", "classic"),
                CancellationToken.None));
        Assert.Empty(service.ListDrafts());
        Assert.False(Directory.Exists(Path.Combine(_root, "bad")));
    }
}
