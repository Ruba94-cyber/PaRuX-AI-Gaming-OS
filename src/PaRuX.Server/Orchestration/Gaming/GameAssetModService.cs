using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Game-agnostic AI material/texture editor. Uses an owner-selected existing PNG
/// and preserves its dimensions and alpha. This EXPORTS a clean, standalone
/// overlay ZIP containing the exact user-specified virtual asset path; it NEVER
/// installs into a game, overwrites user-managed files, touches process memory,
/// runs scripts, or claims live-reload without a game-specific adapter.
/// </summary>
public sealed class GameAssetModService
{
    private const int MaxSourceBytes = 2 * 1024 * 1024;
    private const int MaxReferenceBytes = 16 * 1024 * 1024;
    private const int MaxDrafts = 4;
    private static readonly TimeSpan DraftTtl = TimeSpan.FromMinutes(10);
    private sealed record Draft(
        string Id, string GameName, string RelativePath,
        string OriginalHash, string UpdatedHash, byte[] Png, int Width, int Height,
        long Changed, DateTime ExpiresUtc);
    private readonly Func<string, CancellationToken, Task<byte[]>> _generate;
    private readonly Func<bool> _gaming;
    private readonly string _exportRoot;
    private readonly SemaphoreSlim _lease = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);

    public GameAssetModService(MediaGenerationService media, LocalRuntimeService runtime)
        : this(async (prompt, ct) =>
        {
            ImageGenerationResponse output = await media.GenerateImageAsync(
                new ImageGenerationRequest { Prompt = prompt, Width = 1024, Height = 1024 }, ct)
                .ConfigureAwait(false);
            if (output.Base64Data.Length > MaxReferenceBytes * 2)
                throw new InvalidDataException("Generated material reference exceeds the limit.");
            try { return Convert.FromBase64String(output.Base64Data); }
            catch (FormatException ex)
            { throw new InvalidDataException("The local model produced invalid base64 image data.", ex); }
        }, () => runtime.GamingMode,
        Path.Combine(PaRuXDataRoot.Path, "server", "game-asset-mods")) { }

    internal GameAssetModService(
        Func<string, CancellationToken, Task<byte[]>> generate,
        Func<bool> gaming, string exportRoot)
    {
        _generate = generate ?? throw new ArgumentNullException(nameof(generate));
        _gaming = gaming ?? throw new ArgumentNullException(nameof(gaming));
        _exportRoot = Path.GetFullPath(exportRoot);
    }

    /// <summary>
    /// No universal game compatibility is implied. Each adapter has a separately
    /// audited delivery path; unknown games use manual, documented overlay ZIP.
    /// </summary>
    public static IReadOnlyList<GameModAdapterDto> Adapters() =>
    [
        new("generic-png-overlay", "Generic PNG texture overlay", ".png",
            false, false, false, "Manual ZIP package",
            "Only titles or community mod loaders that accept loose PNG replacement paths can use this package. "
            + "The exact virtual path and game-specific pack format must be supplied by the owner; no game is modified automatically."),
        new("zip-drop-in-owner-confirmed", "Opt-in ZIP mod-folder deployment", ".zip (single PNG overlay)",
            false, true, false, "User-selected existing mods/resourcepacks/addons folder",
            "ONLY after the owner independently verifies that THIS game's mod loader directly accepts " +
            "the exact ZIP format. PaRuX places a new uniquely named file without overwriting existing mods, " +
            "tracks it for hash-checked removal, and NEVER guarantees that the game loads or hot-reloads it."),
        new("minecraft-java-skin", "Minecraft Java player skin", ".png (64x64)",
            false, false, false, "Reviewed PNG in official skin selector",
            "Specific skin UV editor; applies only when imported separately using Minecraft's supported interface."),
        new("portable-config-paused", "Managed portable game configuration", ".ini, .cfg, .json, .txt",
            true, true, false, "SHA-checked temporary text edit with rollback",
            "Only offline PaRuX-managed portable games whose exact process is confirmed suspended; games may need restart.")
    ];

    internal static string ValidateAssetPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 ||
            relative.Contains('\\') || relative.StartsWith('/') || relative.Contains(':') ||
            relative.Any(char.IsControl) ||
            !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a safe relative PNG path inside the game's supported asset pack.");
        string[] parts = relative.Split('/');
        if (parts.Length is < 1 or > 12 || parts.Any(p => p.Length is < 1 or > 64 ||
            p is "." or ".." || p.StartsWith('.') || p.EndsWith('.') ||
            p.EndsWith(' ') ||
            p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
            IsReservedWindowsName(p)))
            throw new ArgumentException("Asset path components contain unsupported or unsafe characters.");
        return relative;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL") return true;
        return name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.Ordinal) ||
             name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public async Task<GameAssetModDraftDto> GenerateDraftAsync(
        GameAssetModDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string relative = ValidateAssetPath(request.RelativeAssetPath);
        string title = request.GameName?.Trim() ?? "";
        string description = request.Prompt?.Trim() ?? "";
        if (title.Length is < 1 or > 100 || title.Any(char.IsControl))
            throw new ArgumentException("Choose a game title (1-100 printable characters).");
        if (description.Length is < 1 or > 800 || description.Any(char.IsControl))
            throw new ArgumentException("Describe the desired texture in 1-800 printable characters.");
        if (string.IsNullOrWhiteSpace(request.OriginalPngBase64) ||
            request.OriginalPngBase64.Length > MaxSourceBytes * 2)
            throw new ArgumentException("The original PNG asset is missing or too large.");
        byte[] original;
        try { original = Convert.FromBase64String(request.OriginalPngBase64); }
        catch (FormatException ex) { throw new ArgumentException("Original asset is not base64 PNG.", ex); }
        if (original.Length > MaxSourceBytes)
            throw new ArgumentException("The original PNG asset exceeds 2 MiB.");
        (int width, int height) = PngAssetGuard.ReadDimensions(original);
        if (width is < 16 or > 1024 || height is < 16 or > 1024)
            throw new ArgumentException("Source texture dimensions must be between 16 and 1024 pixels.");
        if (_gaming())
            throw new InvalidOperationException("Gaming Mode must be off to release GPU resources for local image generation.");
        if (!await _lease.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Another local game texture generation is in progress.");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_gaming()) throw new InvalidOperationException("Gaming Mode is active.");
            string prompt = "Create a FLAT seamless full-frame material TEXTURE reference for an existing " +
                "video-game asset. No perspective, environment, text, scene, character portrait, watermark or blank margins. " +
                "This reference will tint an EXISTING UV-aligned PNG while retaining its layout and transparency. " +
                "User request: " + description;
            byte[] generated = await _generate(prompt, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_gaming())
                throw new OperationCanceledException("Gaming started; no asset modification was prepared.", ct);
            (byte[] png, long changed) = Recolour(original, generated, width, height, ct);
            ct.ThrowIfCancellationRequested();
            string before = Hash(original), after = Hash(png);
            if (changed == 0)
                throw new InvalidOperationException("The generated material did not change any visible texture pixels.");
            lock (_gate)
            {
                PruneExpired();
                if (_drafts.Count >= MaxDrafts)
                    throw new InvalidOperationException("Export or discard old drafts before generating more textures.");
                var draft = new Draft(Guid.NewGuid().ToString("N"), title, relative,
                    before, after, png, width, height, changed, DateTime.UtcNow.Add(DraftTtl));
                _drafts.Add(draft.Id, draft);
                return ToDto(draft);
            }
        }
        finally { _lease.Release(); }
    }

    private static (byte[] Png, long ChangedPixels) Recolour(
        byte[] original, byte[] referenceBytes, int width, int height, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PNG texture editing currently requires Windows.");
        if (referenceBytes is not { Length: > 0 and <= MaxReferenceBytes })
            throw new InvalidDataException("Local image material reference is empty or too large.");
        (int rw, int rh) = PngAssetGuard.ReadDimensions(referenceBytes);
        if (rw is < 64 or > 4096 || rh is < 64 or > 4096)
            throw new InvalidDataException("Generated material image dimensions are not supported.");
        try
        {
            using var sourceStream = new MemoryStream(original, writable: false);
            using var materialStream = new MemoryStream(referenceBytes, writable: false);
            using var source = new Bitmap(sourceStream);
            using var material = new Bitmap(materialStream);
            if (source.Width != width || source.Height != height ||
                material.Width != rw || material.Height != rh)
                throw new InvalidDataException("A texture decoder returned unexpected dimensions.");
            using var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            long changed = 0;
            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    Color before = source.GetPixel(x, y);
                    if (before.A == 0) { output.SetPixel(x, y, before); continue; }
                    Color materialPixel = material.GetPixel(
                        Math.Min(rw - 1, (int)((x + 0.5) * rw / width)),
                        Math.Min(rh - 1, (int)((y + 0.5) * rh / height)));
                    static int Limit(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
                    // Avoid changing alpha or inventing opaque pixels. Recolour
                    // only; geometry/UV semantics must be handled by adapters.
                    Color after = Color.FromArgb(before.A,
                        Limit(before.R * 0.3 + materialPixel.R * 0.7),
                        Limit(before.G * 0.3 + materialPixel.G * 0.7),
                        Limit(before.B * 0.3 + materialPixel.B * 0.7));
                    output.SetPixel(x, y, after);
                    if (after.ToArgb() != before.ToArgb()) changed++;
                }
            }
            using var stream = new MemoryStream();
            output.Save(stream, ImageFormat.Png);
            return (stream.ToArray(), changed);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException
            or System.Runtime.InteropServices.ExternalException)
        { throw new InvalidDataException("The game texture or generated material could not be decoded.", ex); }
    }

    public IReadOnlyList<GameAssetModDraftDto> ListDrafts()
    {
        lock (_gate) { PruneExpired(); return _drafts.Values.Select(ToDto).ToArray(); }
    }

    public GameAssetModExportDto Export(GameAssetModExportRequest request)
    {
        if (request is null || !request.Consent)
            throw new ArgumentException("Owner consent is required before exporting a game mod package.");
        if (request.DraftId is not { Length: 32 } || !request.DraftId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid asset draft id.");
        lock (_gate)
        {
            PruneExpired();
            if (!_drafts.TryGetValue(request.DraftId, out Draft? draft))
                throw new InvalidOperationException("Asset preview is expired or already exported.");
            Directory.CreateDirectory(_exportRoot);
            if (!ManualGameLibrary.IsSafeDirectory(_exportRoot))
                throw new InvalidDataException("Asset export folder is linked or unsafe.");
            string path = Path.Combine(_exportRoot, "parux-game-texture-" + draft.Id + ".zip");
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough))
                {
                    using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var texture = zip.CreateEntry(draft.RelativePath, CompressionLevel.Optimal);
                        using (Stream entry = texture.Open()) entry.Write(draft.Png);
                        var meta = zip.CreateEntry(".parux/manifest.json", CompressionLevel.Optimal);
                        var manifest = new
                        {
                            formatVersion = 1, createdBy = "PaRuX", kind = "png-overlay",
                            title = draft.GameName, relativeAssetPath = draft.RelativePath,
                            originalSha256 = draft.OriginalHash, modifiedSha256 = draft.UpdatedHash,
                            width = draft.Width, height = draft.Height, changedPixels = draft.Changed,
                            installation = "manual", hotReloadGuaranteed = false,
                            note = "Only use with a compatible supported game resource/mod loader. Does not alter game data."
                        };
                        using Stream stream = meta.Open();
                        JsonSerializer.Serialize(stream, manifest);
                    }
                    file.Flush(flushToDisk: true);
                }
                _drafts.Remove(draft.Id);
                return new GameAssetModExportDto(path, Hash(File.ReadAllBytes(path)),
                    draft.RelativePath, draft.OriginalHash, draft.UpdatedHash,
                    "ZIP with ONE replacement PNG and a PaRuX manifest, exported to PaRuX-owned storage. " +
                    "Choose a compatible supported game mod/resource loader and install it manually. " +
                    "No executable, running game or user-managed files were modified; live reload is not guaranteed.");
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    private static GameAssetModDraftDto ToDto(Draft draft) => new(
        draft.Id, draft.GameName, draft.RelativePath, draft.OriginalHash,
        draft.UpdatedHash, Convert.ToBase64String(draft.Png), draft.Width,
        draft.Height, draft.Changed, draft.ExpiresUtc,
        "Material-colour edit of the existing PNG. UV positions and alpha are preserved; " +
        "the game-specific mod loader and virtual replacement path must be verified manually. " +
        "No installation or hot reload takes place on export.");

    private void PruneExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach (string key in _drafts.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToArray())
            _drafts.Remove(key);
    }
}
