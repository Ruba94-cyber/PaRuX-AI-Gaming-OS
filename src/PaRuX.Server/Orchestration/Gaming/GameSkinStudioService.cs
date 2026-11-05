using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Local AI-assisted Minecraft Java skin concept exporter. Generates an image with
/// PaRuX's EXISTING local media engine, renders a bounded 64x64 PNG and holds the
/// proposed pixels in memory for explicit owner review. Export does not touch a
/// Minecraft installation, launcher, account, executable, resource pack or save.
///
/// Minecraft's UV atlas is semantic, not just a 64x64 bitmap: a diffusion image
/// may need manual UV correction before it renders properly. Never claim that
/// resizing automatically produces a correctly mapped wearable skin.
/// </summary>
public sealed class GameSkinStudioService
{
    private const int MaxSourceBytes = 16 * 1024 * 1024;
    private const int MaxPendingDrafts = 4;
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(10);
    private sealed record Draft(string Id, string ArmStyle, byte[] Png, DateTime ExpiresUtc);
    private readonly Func<string, CancellationToken, Task<byte[]>> _generate;
    private readonly Func<bool> _gamingActive;
    private readonly string _outputRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _generationLease = new(1, 1);
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);

    public GameSkinStudioService(MediaGenerationService media, LocalRuntimeService runtime)
        : this(async (prompt, ct) =>
        {
            ImageGenerationResponse image = await media.GenerateImageAsync(new ImageGenerationRequest
            {
                Prompt = prompt, Width = 1024, Height = 1024
            }, ct).ConfigureAwait(false);
            if (image.Base64Data.Length > MaxSourceBytes * 2)
                throw new InvalidDataException("The generated skin image exceeds the allowed size.");
            try { return Convert.FromBase64String(image.Base64Data); }
            catch (FormatException ex) { throw new InvalidDataException("Local media generation returned invalid image data.", ex); }
        }, () => runtime.GamingMode, Path.Combine(PaRuXDataRoot.Path, "server", "skin-studio")) { }

    internal GameSkinStudioService(
        Func<string, CancellationToken, Task<byte[]>> generate,
        Func<bool> gamingActive, string outputRoot)
    {
        _generate = generate ?? throw new ArgumentNullException(nameof(generate));
        _gamingActive = gamingActive ?? throw new ArgumentNullException(nameof(gamingActive));
        _outputRoot = Path.GetFullPath(outputRoot);
    }

    public async Task<GameSkinDraftDto> GenerateDraftAsync(GameSkinDraftRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        if (_gamingActive())
            throw new InvalidOperationException("Leave Gaming Mode before generating images; game VRAM stays reserved for the game.");
        // Do not start multiple expensive image jobs or leave speculative GPU work running.
        if (!await _generationLease.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A skin generation is already in progress.");
        try
        {
            if (_gamingActive()) throw new InvalidOperationException("Gaming Mode is active.");
            string prompt =
                "Create a precise flat UNWRAPPED Minecraft Java Edition 64x64 character SKIN UV ATLAS. " +
                "The image is a texture sheet, NOT a render of a 3D character; use the official Minecraft " +
                "head/body/arms/legs front/back/top/side UV layout and crisp pixel-art blocks with no lettering, " +
                "no background and no perspective. Arm variant: " + request.ArmStyle + ". User design: " + request.Prompt.Trim();
            byte[] generated = await _generate(prompt, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_gamingActive()) throw new OperationCanceledException("Gaming Mode started during image generation.", ct);
            byte[] skin = NormalizeSkinPng(generated);
            var draft = new Draft(Guid.NewGuid().ToString("N"), request.ArmStyle, skin,
                DateTime.UtcNow.Add(DraftLifetime));
            lock (_gate)
            {
                PruneExpired();
                if (_drafts.Count >= MaxPendingDrafts)
                    throw new InvalidOperationException("Review or discard an existing skin draft before generating more.");
                _drafts.Add(draft.Id, draft);
            }
            return ToDto(draft);
        }
        finally { _generationLease.Release(); }
    }

    public GameSkinExportDto Export(GameSkinExportRequest request)
    {
        if (request is null || !request.Consent)
            throw new ArgumentException("Explicit approval of the reviewed generated image is required.");
        if (request.DraftId is not { Length: 32 } || !request.DraftId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid skin draft identifier.");
        lock (_gate)
        {
            PruneExpired();
            if (!_drafts.TryGetValue(request.DraftId, out Draft? draft))
                throw new InvalidOperationException("This draft has expired or was already exported.");
            // Export writes only PaRuX-owned, newly created, uniquely named files.
            Directory.CreateDirectory(_outputRoot);
            if (!ManualGameLibrary.IsSafeDirectory(_outputRoot))
                throw new InvalidDataException("The skin export directory is linked or unsafe.");
            string path = Path.Combine(_outputRoot, "minecraft-skin-" + draft.Id + ".png");
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    file.Write(draft.Png);
                    file.Flush(flushToDisk: true);
                }
                _drafts.Remove(request.DraftId); // Consume only after the durable export succeeds.
                return new GameSkinExportDto(path,
                    Convert.ToHexString(SHA256.HashData(draft.Png)).ToLowerInvariant(),
                    draft.ArmStyle,
                    "Saved as a local 64x64 PNG. Inspect the UV layout and import it manually using Minecraft's official skin selector. " +
                    "A generative image may have incorrect UV mapping. PaRuX did not change any game or account.");
            }
            catch
            {
                // A failed save never leaves a seemingly successful partial asset.
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
                throw;
            }
        }
    }

    public IReadOnlyList<GameSkinDraftDto> ListDrafts()
    {
        lock (_gate)
        {
            PruneExpired();
            return _drafts.Values.Select(ToDto).ToArray();
        }
    }

    private static void ValidateRequest(GameSkinDraftRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Prompt)
            || request.Prompt.Length > 800 || request.Prompt.Any(char.IsControl))
            throw new ArgumentException("Describe your skin in 1-800 printable characters.");
        if (request.ArmStyle is not ("classic" or "slim"))
            throw new ArgumentException("Arm style must be classic or slim.");
    }

    private static GameSkinDraftDto ToDto(Draft d) => new(
        d.Id, d.ArmStyle, Convert.ToBase64String(d.Png), d.ExpiresUtc,
        "Image generated on your local model. This is a 64x64 candidate texture, not a guaranteed correct UV atlas. " +
        "Review the image before exporting; your Minecraft account or game installation will not be changed.");

    private void PruneExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach (string key in _drafts.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToArray())
            _drafts.Remove(key);
    }

    /// <summary>
    /// Decode untrusted media with the platform image decoder, cap size before
    /// decoding and create a fresh metadata-free RGBA PNG with nearest-neighbor
    /// resampling. This Windows-only API runs in PaRuX's desktop Core.
    /// </summary>
    internal static byte[] NormalizeSkinPng(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Minecraft skin decoder requires Windows.");
        if (data is not { Length: > 0 and <= MaxSourceBytes })
            throw new InvalidDataException("Generated skin image is missing or too large.");
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width is < 64 or > 4096 || image.Height is < 64 or > 4096)
                throw new InvalidDataException("Generated skin dimensions are outside the allowed range.");
            using var resized = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(image, new Rectangle(0, 0, 64, 64),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            using var output = new MemoryStream();
            resized.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            throw new InvalidDataException("The generated data was not a decodable raster image.", ex);
        }
    }
}
