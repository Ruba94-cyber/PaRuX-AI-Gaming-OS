using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Explicit, opt-in delivery adapter for games whose *documented* loader accepts
/// standalone ZIP files in an existing mods/resourcepacks/addons directory.
/// Copies only a verified PaRuX-created inert PNG-overlay ZIP, with a unique
/// PaRuX-owned filename. Never overwrites/deletes pre-existing game files,
/// extracts an archive, executes code, patches a game, edits memory, enables
/// anti-cheat bypass or asserts real-time reload. Removal is SHA-bound.
/// </summary>
public sealed class GameModDropInService
{
    private const int MaxBundleBytes = 16 * 1024 * 1024;
    private const int MaxReceipts = 200;
    private readonly string _exportRoot;
    private readonly string _receiptRoot;
    private readonly Func<bool> _gaming;
    private readonly Func<string, bool> _gameProcessRunning;
    private readonly object _gate = new();

    private sealed record Receipt(
        string Id, string GameName, string ModFolder, string InstalledFile,
        string BundleSha256, string InstalledSha256, DateTime CreatedUtc,
        string? GameExecutablePath = null);

    public GameModDropInService(LocalRuntimeService runtime)
        : this(() => runtime.GamingMode,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-asset-mods"),
            Path.Combine(PaRuXDataRoot.Path, "server", "game-mod-install-receipts")) { }

    internal GameModDropInService(Func<bool> gaming, string exportRoot, string receiptRoot,
        Func<string, bool>? gameProcessRunning = null)
    {
        _gaming = gaming ?? throw new ArgumentNullException(nameof(gaming));
        _gameProcessRunning = gameProcessRunning ?? GameModProcessGuard.IsRunning;
        _exportRoot = Path.GetFullPath(exportRoot);
        _receiptRoot = Path.GetFullPath(receiptRoot);
    }

    private string RequireStoppedGame(string? executable)
    {
        string full = GameModProcessGuard.ValidateExecutable(executable);
        if (_gameProcessRunning(full))
            throw new InvalidOperationException(
                "The selected game is still running or paused. Fully close it before installing or removing a ZIP mod.");
        return full;
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ValidSha(string? sha) =>
        sha is { Length: 64 } && sha.All(Uri.IsHexDigit);

    private static bool ValidId(string? id) =>
        id is { Length: 32 } && id.All(Uri.IsHexDigit);

    private static void RequireOffline(Func<bool> gaming, bool confirmed)
    {
        if (!confirmed)
            throw new ArgumentException("Confirm the offline game is fully closed before changing its mod directory.");
        if (gaming())
            throw new InvalidOperationException("Mod installation/removal is disabled during Gaming Mode.");
    }

    internal static string ValidateModFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 1024 ||
            !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local absolute mod directory; network shares are not supported.");
        string full = Path.GetFullPath(folder);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        if (name is null || !(name.Equals("mods", StringComparison.OrdinalIgnoreCase)
            || name.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase)
            || name.Equals("addons", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose an existing mods, resourcepacks or addons folder documented by the game's loader.");
        if (!ManualGameLibrary.IsSafeDirectory(full))
            throw new InvalidOperationException("Mod directory is missing or contains a symlink/junction.");
        return full;
    }

    private void EnsureReceiptRoot()
    {
        // Before creating an owned child, refuse a linked parent: creation
        // itself must not traverse a pre-existing user-controlled junction.
        string parent = Path.GetDirectoryName(_receiptRoot)!;
        if (!ManualGameLibrary.IsSafeDirectory(parent))
            throw new InvalidDataException("PaRuX mod receipts parent directory is unsafe.");
        Directory.CreateDirectory(_receiptRoot);
        if (!ManualGameLibrary.IsSafeDirectory(_receiptRoot))
            throw new InvalidDataException("PaRuX mod receipts directory is unsafe.");
    }

    private string ReceiptPath(string id) =>
        Path.Combine(_receiptRoot, "parux-mod-" + id + ".json");

    private static void RequireRegularFile(string path, int maxBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length is < 1 || file.Length > maxBytes)
            throw new InvalidDataException("The mod package is missing, linked or over its size limit.");
    }

    private byte[] ReadOwnedBundle(string path, string expected)
    {
        if (!ValidSha(expected) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Select a known, SHA-verified PaRuX-created ZIP bundle.");
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), _exportRoot, StringComparison.OrdinalIgnoreCase) ||
            name.Length != "parux-game-texture-".Length + 32 + 4 ||
            !name.StartsWith("parux-game-texture-", StringComparison.Ordinal) ||
            !name.EndsWith(".zip", StringComparison.Ordinal) ||
            !ValidId(name.Substring("parux-game-texture-".Length, 32)) ||
            !ManualGameLibrary.IsSafeDirectory(_exportRoot))
            throw new InvalidOperationException("Only a PaRuX-owned, directly exported asset ZIP may be installed.");
        RequireRegularFile(full, MaxBundleBytes);
        byte[] bytes = File.ReadAllBytes(full);
        if (!string.Equals(Sha(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exported ZIP changed since review. Export the mod again.");
        return bytes;
    }

    /// <summary>
    /// Accept exactly one PNG and PaRuX manifest from the exporter. No archive
    /// extraction occurs: this validation prevents executable/script payloads
    /// being shipped by PaRuX through the opt-in drop-in bridge.
    /// </summary>
    internal static string ValidateBundle(byte[] bytes)
    {
        if (bytes is not { Length: >= 120 and <= MaxBundleBytes })
            throw new InvalidDataException("Asset mod ZIP is missing or exceeds the safe size limit.");
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != 2)
            throw new InvalidDataException("Mod ZIP must contain exactly one PNG and one PaRuX manifest.");
        var meta = archive.GetEntry(".parux/manifest.json")
            ?? throw new InvalidDataException("Mod ZIP has no PaRuX manifest.");
        if (meta.Length is < 2 or > 8192)
            throw new InvalidDataException("Mod manifest is too large.");
        // Regular files only; do not accept symlink entries in either slot.
        if ((meta.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            throw new InvalidDataException("Mod ZIP contains a linked manifest.");
        using var metadata = meta.Open();
        using var doc = JsonDocument.Parse(metadata,
            new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement json = doc.RootElement;
        string kind = json.GetProperty("kind").GetString() ?? "";
        string relative = GameAssetModService.ValidateAssetPath(
            json.GetProperty("relativeAssetPath").GetString());
        if (kind != "png-overlay" || json.GetProperty("installation").GetString() != "manual" ||
            json.GetProperty("hotReloadGuaranteed").GetBoolean() ||
            json.GetProperty("createdBy").GetString() != "PaRuX" ||
            json.GetProperty("formatVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported or untrusted mod package manifest.");
        var png = archive.Entries.SingleOrDefault(x => x.FullName == relative)
            ?? throw new InvalidDataException("Expected replacement PNG not found.");
        if ((png.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
            png.Length is < 33 or > 8 * 1024 * 1024)
            throw new InvalidDataException("Mod PNG is linked, empty or too large.");
        if (!ValidSha(json.GetProperty("modifiedSha256").GetString()))
            throw new InvalidDataException("Mod PNG digest is missing.");
        byte[] raster;
        using (Stream source = png.Open())
        using (var output = new MemoryStream())
        {
            source.CopyTo(output);
            raster = output.ToArray();
        }
        if (raster.Length != png.Length ||
            !string.Equals(Sha(raster), json.GetProperty("modifiedSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mod replacement texture failed SHA-256 verification.");
        (int width, int height) = PngAssetGuard.ReadDimensions(raster);
        if (width != json.GetProperty("width").GetInt32() ||
            height != json.GetProperty("height").GetInt32())
            throw new InvalidDataException("Mod image dimensions differ from the review manifest.");
        return json.GetProperty("title").GetString() ?? "";
    }

    public GameModDropInReceiptDto Install(GameModDropInInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.CompatibleZipLoaderConfirmed)
            throw new ArgumentException("Confirm that the game's mod loader accepts this exact standalone ZIP format.");
        RequireOffline(_gaming, request.OfflineGameClosedConfirmed);
        string executable = RequireStoppedGame(request.GameExecutablePath);
        lock (_gate)
        {
            string folder = ValidateModFolder(request.TargetModFolder);
            byte[] bundle = ReadOwnedBundle(request.BundlePath, request.BundleSha256);
            string title;
            try { title = ValidateBundle(bundle); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                InvalidOperationException or FormatException or NotSupportedException)
            { throw new InvalidDataException("The PaRuX mod ZIP manifest is invalid.", ex); }
            if (string.IsNullOrWhiteSpace(title) || title.Length > 100 ||
                title.Any(char.IsControl))
                throw new InvalidDataException("Mod ZIP has an invalid game title.");
            EnsureReceiptRoot();
            if (Directory.EnumerateFiles(_receiptRoot, "parux-mod-*.json").Take(MaxReceipts + 1).Count() >= MaxReceipts)
                throw new InvalidOperationException("Too many active PaRuX mod installations.");
            if (ReadReceipts().Any(existing =>
                string.Equals(existing.ModFolder, folder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.BundleSha256, request.BundleSha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(existing.InstalledFile)))
                throw new InvalidOperationException("This ZIP is already recorded in that mod folder. Remove it before reinstalling.");
            string id = Guid.NewGuid().ToString("N");
            string target = Path.Combine(folder, "parux-mod-" + id + ".zip");
            string receiptPath = ReceiptPath(id);
            string partial = Path.Combine(folder, "parux-mod-" + id + ".partial");
            var receipt = new Receipt(id, title, folder, target,
                request.BundleSha256.ToLowerInvariant(), Sha(bundle), DateTime.UtcNow, executable);
            // Durable receipt is written BEFORE touching the mod folder so a
            // crash leaves a visible, recoverable pending entry.
            byte[] journal = JsonSerializer.SerializeToUtf8Bytes(receipt);
            using (var record = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                record.Write(journal);
                record.Flush(flushToDisk: true);
            }
            try
            {
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    output.Write(bundle);
                    output.Flush(flushToDisk: true);
                }
                RequireOffline(_gaming, request.OfflineGameClosedConfirmed);
                RequireStoppedGame(executable);
                ValidateModFolder(folder);
                if (!string.Equals(Sha(File.ReadAllBytes(partial)), receipt.InstalledSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Mod ZIP changed while copying.");
                File.Move(partial, target); // create-only: NEVER overwrites an existing file.
                return ToDto(receipt);
            }
            finally
            {
                // Never delete an unrelated file: only this newly generated
                // GUID partial in the previously verified directory.
                if (ManualGameLibrary.IsSafeDirectory(folder))
                {
                    try { if (File.Exists(partial) && (File.GetAttributes(partial) &
                        FileAttributes.ReparsePoint) == 0) File.Delete(partial); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static GameModDropInReceiptDto ToDto(Receipt item, bool verifyContent = true)
    {
        string state;
        if (string.IsNullOrWhiteSpace(item.GameExecutablePath))
            state = "unbound"; // old receipt: never remove unknown ZIP without process identity
        else if (!ManualGameLibrary.IsSafeDirectory(item.ModFolder))
            state = "unsafe";
        else if (!File.Exists(item.InstalledFile))
            state = "missing";
        else if (!verifyContent)
            state = "unverified"; // manual inventory scan budget exhausted; removal always verifies.
        else
        {
            try
            {
                RequireRegularFile(item.InstalledFile, MaxBundleBytes);
                state = Sha(File.ReadAllBytes(item.InstalledFile)) == item.InstalledSha256
                    ? "installed" : "changed";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { state = "unsafe"; }
        }
        return new GameModDropInReceiptDto(item.Id, item.GameName, item.ModFolder,
            item.InstalledFile, item.BundleSha256, item.InstalledSha256,
            item.CreatedUtc, state,
            "Opt-in standalone ZIP placed in a user-confirmed compatible offline mod directory. " +
            "The game may need restart or may reject this package. PaRuX checks the selected " +
            "executable's process state and deletes only matching ZIP bytes; helper processes are not attested.",
            item.GameExecutablePath ?? "");
    }

    private IEnumerable<Receipt> ReadReceipts()
    {
        if (!Directory.Exists(_receiptRoot)) yield break;
        if (!ManualGameLibrary.IsSafeDirectory(_receiptRoot))
            throw new InvalidDataException("PaRuX mod receipt folder is unsafe.");
        foreach (string file in Directory.EnumerateFiles(_receiptRoot, "parux-mod-*.json",
            SearchOption.TopDirectoryOnly).Take(MaxReceipts + 1))
        {
            string name = Path.GetFileName(file);
            if (name.Length != "parux-mod-".Length + 32 + 5 ||
                !ValidId(name.Substring("parux-mod-".Length, 32)))
                continue;
            Receipt? receipt = null;
            try
            {
                RequireRegularFile(file, 16 * 1024);
                receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException) { }
            if (receipt is null || !ValidId(receipt.Id) ||
                !string.Equals(ReceiptPath(receipt.Id), file, StringComparison.OrdinalIgnoreCase) ||
                !ValidSha(receipt.InstalledSha256) || !ValidSha(receipt.BundleSha256) ||
                receipt.ModFolder is not { Length: > 2 and <= 1024 } ||
                receipt.InstalledFile is not { Length: > 2 and <= 1200 } ||
                !Path.IsPathFullyQualified(receipt.ModFolder) ||
                !Path.IsPathFullyQualified(receipt.InstalledFile))
                continue;
            try
            {
                if (!string.Equals(receipt.InstalledFile,
                    Path.Combine(receipt.ModFolder, "parux-mod-" + receipt.Id + ".zip"),
                    StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { continue; }
            yield return receipt;
        }
    }

    public IReadOnlyList<GameModDropInReceiptDto> List()
    {
        // Inventory scanning is explicit but must never hash gigabytes of ZIPs
        // during foreground gaming or monopolise disk I/O for a large library.
        if (_gaming())
            throw new InvalidOperationException("Mod inventory hashing is disabled during Gaming Mode.");
        lock (_gate)
        {
            const long scanBudget = 64L * 1024 * 1024;
            long remaining = scanBudget;
            var list = new List<GameModDropInReceiptDto>();
            foreach (Receipt item in ReadReceipts().Take(MaxReceipts))
            {
                bool verify = false;
                try
                {
                    var file = new FileInfo(item.InstalledFile);
                    if (file.Exists && file.Length is > 0 and <= MaxBundleBytes &&
                        file.Length <= remaining)
                    {
                        remaining -= file.Length;
                        verify = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                list.Add(ToDto(item, verify));
            }
            return list.OrderByDescending(x => x.CreatedUtc).ToArray();
        }
    }

    public bool Remove(GameModDropInRemoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Consent || !ValidId(request.ReceiptId))
            throw new ArgumentException("Choose a valid mod installation and explicitly approve its removal.");
        RequireOffline(_gaming, request.OfflineGameClosedConfirmed);
        lock (_gate)
        {
            Receipt? receipt = ReadReceipts().SingleOrDefault(x => x.Id == request.ReceiptId);
            if (receipt is null) return false;
            // An interrupted install may have persisted its receipt before
            // the copy, and the user may later have removed the entire folder.
            // In that case clear ONLY our receipt, never create a directory.
            if (!Directory.Exists(receipt.ModFolder) && !File.Exists(receipt.InstalledFile))
            {
                File.Delete(ReceiptPath(receipt.Id));
                return true;
            }
            // Old/unbound receipts are still visible, but must NEVER confer
            // authority to delete a mod ZIP without a known game identity.
            string executable = RequireStoppedGame(receipt.GameExecutablePath);
            string folder = ValidateModFolder(receipt.ModFolder);
            string path = Path.Combine(folder, "parux-mod-" + receipt.Id + ".zip");
            if (!string.Equals(path, receipt.InstalledFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed mod target no longer matches the recovery receipt.");
            if (File.Exists(path))
            {
                RequireRegularFile(path, MaxBundleBytes);
                byte[] current = File.ReadAllBytes(path);
                if (!string.Equals(Sha(current), receipt.InstalledSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Mod ZIP changed since PaRuX installed it; retained for manual review.");
                RequireOffline(_gaming, request.OfflineGameClosedConfirmed);
                RequireStoppedGame(executable);
                ValidateModFolder(folder);
                // Re-check immediately before deletion; external concurrent
                // writers can still race on Windows and need hardware QA.
                RequireRegularFile(path, MaxBundleBytes);
                if (Sha(File.ReadAllBytes(path)) != receipt.InstalledSha256)
                    throw new InvalidOperationException("Mod ZIP changed; refusing to delete it.");
                File.Delete(path);
            }
            File.Delete(ReceiptPath(receipt.Id));
            return true;
        }
    }
}
