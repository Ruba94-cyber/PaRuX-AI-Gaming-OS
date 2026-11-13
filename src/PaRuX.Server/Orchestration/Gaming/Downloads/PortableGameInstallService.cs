using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>
/// Explicitly consented after-download deployment of a HASH-VERIFIED portable ZIP.
/// "Install" means safe extraction + adding a manual library entry, NEVER invoking
/// an EXE, MSI, batch script, archive-provided command, shell or installer.
/// A torrent info hash is not publisher provenance; the independent SHA-256 must
/// have been entered by the owner before enqueue.
/// </summary>
public sealed class PortableGameInstallService
{
    private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 12L * 1024 * 1024 * 1024;
    private const int MaxEntries = 200_000;
    private readonly ManualGameLibrary _library;
    private readonly Func<bool> _gaming;
    private readonly Func<CancellationToken> _gamingCancellation;
    private readonly string _root;

    public PortableGameInstallService(ManualGameLibrary library, LocalRuntimeService runtime)
        : this(library, () => runtime.GamingMode || GamingHardOffWatch.MarkerActive(),
            () => runtime.GamingCancellationToken,
            Path.Combine(PaRuXDataRoot.Path, "server", "portable-games")) { }

    internal PortableGameInstallService(ManualGameLibrary library, Func<bool> gaming,
        Func<CancellationToken> gamingCancellation, string root)
    {
        _library = library;
        _gaming = gaming;
        _gamingCancellation = gamingCancellation;
        _root = Path.GetFullPath(root);
    }

    /// <summary>Validates only data the user entered, never untrusted archive content.</summary>
    public static bool ValidPortableEntryPoint(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240
            || relative.Any(char.IsControl) || relative.Contains(':')
            || Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
            return false;
        string normalized = relative.Replace('\\', '/');
        if (!string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase)
            || !SafeArchiveExtractor.IsSafePortableEntryName(normalized))
            return false;
        string[] parts = normalized.Split('/');
        if (parts.Length is < 1 or > 12 || parts.Any(x => x.Length == 0 || x is "." or ".."))
            return false;
        string file = parts[^1];
        // Never mistake an installer/setup helper inside an archive for a portable
        // game's direct executable. Nothing is executed automatically regardless.
        string stem = Path.GetFileNameWithoutExtension(file);
        return !new[] { "setup", "install", "installer", "uninstall", "uninstaller",
            "patch", "keygen", "crack" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ManualGameDto> InstallAsync(string downloadedArchive, string independentSha256,
        string relativeExecutable, string gameTitle, CancellationToken ct)
    {
        if (!GamePackageStageService.IsPublisherHash(independentSha256))
            throw new ArgumentException("An independently supplied SHA-256 is required.");
        if (!ValidPortableEntryPoint(relativeExecutable))
            throw new ArgumentException("Choose a relative portable game executable, not a setup program.");
        string title = gameTitle?.Trim() ?? "";
        if (title.Length is < 1 or > 200 || title.Any(char.IsControl))
            throw new ArgumentException("Enter a short game title.");
        if (!string.Equals(Path.GetExtension(downloadedArchive), ".zip", StringComparison.OrdinalIgnoreCase)
            || !GameInstallerInspectionService.ValidLocalPackagePath(downloadedArchive))
            throw new InvalidDataException("Automatic portable installation supports regular local ZIP packages only.");

        // Hold a read-only share lock for the entire verify/extract transaction.
        // On Windows this prevents a separate writer from changing the ZIP after
        // its independent SHA-256 was checked but before extraction completes.
        using var packageLease = new FileStream(downloadedArchive, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var package = new FileInfo(downloadedArchive);
        long bytes = package.Length;
        DateTime modified = package.LastWriteTimeUtc;
        if (bytes is < 1 or > MaxArchiveBytes)
            throw new InvalidDataException("This ZIP is outside the supported size budget.");
        ct.ThrowIfCancellationRequested();
        if (_gaming()) throw new OperationCanceledException("Gaming suspended portable installation.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingCancellation());
        CancellationToken token = linked.Token;

        EnsureLocalRegularDirectory(_root, create: true);
        using FileStream lease = await AcquireLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (_gaming()) throw new OperationCanceledException(token);

        // Do not trust the torrent's info hash as publisher authorization. Rehash
        // the already completed file against the owner's pre-supplied SHA-256.
        byte[] actual = await HashFileAsync(downloadedArchive, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(independentSha256)))
            throw new InvalidDataException("Package differs from the independent SHA-256.");
        package.Refresh();
        if (package.Length != bytes || package.LastWriteTimeUtc != modified
            || !GameInstallerInspectionService.ValidLocalPackagePath(downloadedArchive))
            throw new InvalidDataException("Package changed during pre-install verification.");

        string normalized = relativeExecutable.Replace('\\', '/');
        long expandedBytes = ValidateZipContents(downloadedArchive, normalized);
        string volume = Path.GetPathRoot(_root)!;
        if (new DriveInfo(volume).AvailableFreeSpace < expandedBytes + 256L * 1024 * 1024)
            throw new InvalidDataException("Not enough local free space for verified ZIP extraction.");

        string id = Guid.NewGuid().ToString("N");
        string partial = Path.Combine(_root, ".parux-portable-" + id + ".partial");
        string installed = Path.Combine(_root, "game-" + id);
        bool committed = false;
        ManualGameDto? registration = null;
        try
        {
            Directory.CreateDirectory(partial);
            EnsureLocalRegularDirectory(partial);
            // A user-chosen, hash-verified game ZIP is judged by its real expanded size against MaxExpandedBytes (and
            // the free-space check above), not by a compression ratio that large game assets can legitimately exceed.
            await SafeArchiveExtractor.ExtractAsync(downloadedArchive, partial, token,
                maxExtractedBytes: MaxExpandedBytes, maxEntries: MaxEntries, enforceCompressionRatio: false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException(token);
            EnsureLocalRegularDirectory(partial);

            string exeWithinPartial = Path.GetFullPath(Path.Combine(partial,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            EnsureExactChild(partial, exeWithinPartial);
            if (!RegularExecutable(exeWithinPartial))
                throw new InvalidDataException("The selected game executable is missing or linked.");

            // Commit is an atomic rename on the same local volume. The library
            // registration is separate; rollback the exact managed directory if
            // registration fails, never touch the downloaded archive or user data.
            token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException(token);
            Directory.Move(partial, installed);
            committed = true;

            string installedExe = Path.Combine(installed,
                normalized.Replace('/', Path.DirectorySeparatorChar));
            EnsureExactChild(installed, installedExe);
            if (!RegularExecutable(installedExe))
                throw new InvalidDataException("The installed executable is no longer a regular file.");
            token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException(token);
            registration = _library.Add(new AddManualGameRequest(
                title, installedExe, WorkingDirectory: Path.GetDirectoryName(installedExe)!,
                Kind: ManualEntryKinds.Game, Platform: "PC"));
            token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException(token);
            return registration;
        }
        catch
        {
            if (registration is not null)
            {
                try { _library.Remove(registration.Id); }
                catch (Exception) { /* Preserve the folder if the entry could not be rolled back. */ }
            }
            throw;
        }
        finally
        {
            SafeDeleteOwnedTree(partial);
            // Never remove game files while a surviving library entry can refer to
            // them (e.g. transient library IO failure during rollback).
            if (committed)
            {
                bool referenced;
                try { referenced = _library.List().Any(x =>
                    x.ExecutablePath.StartsWith(installed + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)); }
                catch (Exception) { referenced = true; }
                if (!referenced)
                    SafeDeleteOwnedTree(installed);
            }
        }
    }

    private static long ValidateZipContents(string archive, string requiredExecutable)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is < 1 or > MaxEntries)
            throw new InvalidDataException("This ZIP contains no entries or more than " + MaxEntries
                + " files, so PaRuX will not extract it automatically. Extract it yourself and add the game's folder manually.");
        long total = 0;
        bool found = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string key = entry.FullName.Replace('\\', '/');
            if (!SafeArchiveExtractor.IsSafePortableEntryName(key)
                || key.StartsWith('/') || key.Contains(':') || key.StartsWith("./", StringComparison.Ordinal)
                || key.Length > 1024)
                throw new InvalidDataException("Archive entry has an unsafe or ambiguous path.");
            if (!seen.Add(key.TrimEnd('/')))
                throw new InvalidDataException("Archive contains duplicate or case-conflicting paths.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Archive contains symbolic links.");
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - total)
                throw new InvalidDataException("The ZIP would expand beyond the " + MaxExpandedBytes / (1024 * 1024 * 1024)
                    + " GB budget for automatic installs. Extract it yourself and add the game's folder manually.");
            total += entry.Length;
            if (key.Equals(requiredExecutable, StringComparison.OrdinalIgnoreCase))
                found = true;
        }
        if (!found) throw new InvalidDataException("The selected portable game executable is not in the ZIP.");
        return total;
    }

    private static bool RegularExecutable(string path)
    {
        try
        {
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe",
                StringComparison.OrdinalIgnoreCase)) return false;
            FileInfo info = new(path);
            return info.Length > 0 && info.LinkTarget is null
                && (info.Attributes & FileAttributes.ReparsePoint) == 0
                && ManualGameLibrary.IsSafeDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void EnsureExactChild(string root, string candidate)
    {
        if (!candidate.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive file would leave the managed game directory.");
    }

    private static void EnsureLocalRegularDirectory(string path, bool create = false)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType is
                DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidDataException("Portable installation requires a local volume.");
        if (create) Directory.CreateDirectory(path);
        if (!ManualGameLibrary.IsSafeDirectory(path))
            throw new InvalidDataException("Portable installation folder contains a link or is missing.");
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        string lockPath = Path.Combine(_root, ".parux-portable.lock");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked install lease is not permitted.");
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(40, ct).ConfigureAwait(false); }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 18, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Delete only newly created PaRuX-named paths if no link exists.</summary>
    private static void SafeDeleteOwnedTree(string path)
    {
        try
        {
            if (!Directory.Exists(path) || !ManualGameLibrary.IsSafeDirectory(path)) return;
            var pending = new Stack<string>();
            pending.Push(path);
            int count = 0;
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                if (++count > MaxEntries * 2) return;
                foreach (string subdir in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0) return;
                    pending.Push(subdir);
                }
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                }
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { /* Never follow a link or delete anything outside a managed transaction. */ }
    }
}
