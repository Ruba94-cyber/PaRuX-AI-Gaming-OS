using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Stores a checked copy under PaRuX AppData. Never extracts or executes it.
/// Only the owner with an independent trusted SHA-256 can request staging.
/// </summary>
public sealed class GamePackageStageService
{
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxStagedBytes = 16L * 1024 * 1024 * 1024;
    private static readonly TimeSpan InterruptedPartialRetention = TimeSpan.FromHours(24);
    private readonly long _maxTotalStagedBytes;
    private readonly Func<bool> _gamingOrUnready;
    private readonly Func<CancellationToken> _gamingCancellation;
    private readonly string _stagingRoot;

    public GamePackageStageService(
        WorkloadOrchestrator orchestrator, LocalAiProcessManager process, LocalRuntimeService runtime)
        : this(() => orchestrator.CurrentState != "AiReady" || process.GamingActive,
            () => runtime.GamingCancellationToken,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-package-staging")) { }

    internal GamePackageStageService(
        Func<bool> gamingOrUnready, Func<CancellationToken> gamingCancellation,
        string stagingRoot, long maxTotalStagedBytes = MaxStagedBytes)
    {
        _gamingOrUnready = gamingOrUnready ?? throw new ArgumentNullException(nameof(gamingOrUnready));
        _gamingCancellation = gamingCancellation ?? throw new ArgumentNullException(nameof(gamingCancellation));
        _stagingRoot = Path.GetFullPath(stagingRoot);
        if (maxTotalStagedBytes <= 0 || maxTotalStagedBytes > MaxStagedBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTotalStagedBytes));
        _maxTotalStagedBytes = maxTotalStagedBytes;
    }

    internal static bool IsPublisherHash(string? expected)
    {
        if (expected is null || expected.Length != 64) return false;
        return expected.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private static void EnsureLocalUnlinkedDirectory(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidDataException("Staging needs a regular local volume.");

        for (string? dir = path; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) &&
                (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staging path contains a linked directory.");
        }
    }

    internal const string LockFileName = ".parux-staging.lock";

    private async Task<FileStream> AcquireStagingLeaseAsync(CancellationToken ct)
    {
        // Keep a stable lock file: deleting it could let another Core start
        // a second writer on a newly-created lock inode.
        string lockPath = Path.Combine(_stagingRoot, LockFileName);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(lockPath) &&
                (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staging lock is not a regular file.");
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50, ct);
            }
        }
    }

    /// <summary>
    /// Reclaim stale PaRuX transaction partials only under the existing staging
    /// lease, during an explicit AI-ready Stage action. Never sweep in Gaming.
    /// Completed files, recent/in-use partials, links and foreign names survive.
    /// </summary>
    private void PruneInterruptedPartials(CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow - InterruptedPartialRetention;
        int inspected = 0;
        foreach (string path in Directory.EnumerateFiles(_stagingRoot, ".parux-game-*.partial",
                     SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (_gamingOrUnready())
                throw new InvalidOperationException("Gaming interrupted package recovery.");
            if (++inspected > 256)
                throw new InvalidDataException("Too many partial packages to recover safely.");

            string name = Path.GetFileName(path);
            const string prefix = ".parux-game-";
            const string suffix = ".partial";
            if (name.Length != prefix.Length + 32 + suffix.Length ||
                !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal) ||
                !ValidStagedId(name.Substring(prefix.Length, 32)))
                continue;

            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.LastWriteTimeUtc > cutoff)
                continue;

            // DeleteOnClose acts on the already opened file, avoiding a second
            // path lookup after the safety check. Never force-delete a file
            // currently held by another process.
            try
            {
                using var retired = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) { /* In use, disappeared, or unavailable: retain it. */ }
        }
    }

    private void EnsureCapacity(long sourceBytes, CancellationToken ct)
    {
        long used = 0;
        int entries = 0;
        foreach (string path in Directory.EnumerateFiles(_stagingRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (_gamingOrUnready())
                throw new InvalidOperationException("Gaming interrupted staging capacity checks.");
            // The persistent lease file is infrastructure: it neither consumes quota nor entry budget.
            if (string.Equals(Path.GetFileName(path), LockFileName, StringComparison.Ordinal)) continue;
            if (++entries > 256 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staging directory exceeds its safe entry limit.");
            long size = new FileInfo(path).Length;
            if (size > _maxTotalStagedBytes - used)
                throw new InvalidDataException("Staging directory exceeds its allowed capacity.");
            used += size;
        }
        if (sourceBytes > _maxTotalStagedBytes - used)
            throw new InvalidDataException("Not enough staging quota for this package.");

        string root = Path.GetPathRoot(_stagingRoot)!;
        long available = new DriveInfo(root).AvailableFreeSpace;
        if (available < sourceBytes + 64L * 1024 * 1024)
            throw new InvalidDataException("Not enough free space to stage the package.");
    }

    internal static bool ValidStagedId(string? id) =>
        id is { Length: 32 } && id.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string[] EnumerateManagedPackageFiles()
    {
        if (!Directory.Exists(_stagingRoot)) return Array.Empty<string>();
        EnsureLocalUnlinkedDirectory(_stagingRoot);
        var results = new List<string>();
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(_stagingRoot, "game-*"))
        {
            if (++count > 256)
                throw new InvalidDataException("The staging catalog exceeds its safe entry limit.");
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("game-", StringComparison.OrdinalIgnoreCase) ||
                !ValidStagedId(name[5..]) ||
                !GameInstallerInspectionService.ValidLocalPackagePath(file))
                continue;
            results.Add(file);
        }
        return results.ToArray();
    }

    public IReadOnlyList<StagedGamePackageDto> ListStaged() =>
        EnumerateManagedPackageFiles()
            .Select(file => new FileInfo(file))
            .Select(info => new StagedGamePackageDto(
                Path.GetFileNameWithoutExtension(info.Name)[5..],
                info.Name, info.Length, info.LastWriteTimeUtc))
            .OrderByDescending(item => item.LastModifiedUtc)
            .ToArray();

    public async Task<bool> DeleteStagedAsync(string id, CancellationToken ct)
    {
        if (!ValidStagedId(id)) return false;
        ct.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Staging management unavailable while Gaming.");
        if (!Directory.Exists(_stagingRoot)) return false;
        EnsureLocalUnlinkedDirectory(_stagingRoot);
        // Cancel even while a different PaRuX instance holds the persistent
        // staging lease. A Gaming transition must not queue a delayed deletion.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingCancellation());
        using var lease = await AcquireStagingLeaseAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Gaming interrupted staged-package deletion.");
        string[] matches = EnumerateManagedPackageFiles()
            .Where(p => string.Equals(Path.GetFileNameWithoutExtension(p),
                "game-" + id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return false;
        linked.Token.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Gaming interrupted staged-package deletion.");
        File.Delete(matches[0]);
        return true;
    }

    public async Task<GamePackageStageResult> StageAsync(GamePackageStageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Package staging is disabled while Gaming or recovering AI.");
        if (!IsPublisherHash(request.ExpectedPublisherSha256) ||
            !GameInstallerInspectionService.ValidLocalPackagePath(request.FilePath))
            throw new ArgumentException("Choose a regular local package and a trusted publisher SHA-256.");
        byte[] expected = Convert.FromHexString(request.ExpectedPublisherSha256);
        var file = new FileInfo(request.FilePath);
        long bytes = file.Length;
        DateTime written = file.LastWriteTimeUtc;
        if (bytes is <= 0 or > MaxBytes) throw new InvalidDataException("Package size is not supported.");

        EnsureLocalUnlinkedDirectory(_stagingRoot);
        Directory.CreateDirectory(_stagingRoot);
        EnsureLocalUnlinkedDirectory(_stagingRoot);
        // Admission and lease acquisition share the Gaming cancellation token:
        // staging must not remain queued behind another writer after hard-off.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingCancellation());
        using var stagingLease = await AcquireStagingLeaseAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Gaming interrupted package staging.");
        PruneInterruptedPartials(linked.Token);
        EnsureCapacity(bytes, linked.Token);
        string id = Guid.NewGuid().ToString("N");
        string extension = Path.GetExtension(file.Name);
        string temp = Path.Combine(_stagingRoot, ".parux-game-" + id + ".partial");
        string final = Path.Combine(_stagingRoot, "game-" + id + extension);
        try
        {
            await using var input = new FileStream(file.FullName, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), linked.Token)) > 0)
                {
                    if (count > MaxBytes - copied || count > bytes - copied)
                        throw new InvalidDataException("Source changed or exceeded package limit.");
                    sha.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                    copied += count;
                }
                linked.Token.ThrowIfCancellationRequested();
                file.Refresh();
                if (copied != bytes || input.Length != bytes ||
                    file.Length != bytes || file.LastWriteTimeUtc != written ||
                    !GameInstallerInspectionService.ValidLocalPackagePath(file.FullName))
                    throw new InvalidDataException("Source package changed during staging.");
                byte[] actual = sha.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new InvalidDataException("Package SHA-256 differs from the publisher checksum.");
                await output.FlushAsync(linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            if (_gamingOrUnready())
                throw new InvalidOperationException("Gaming began during staging.");
            EnsureLocalUnlinkedDirectory(_stagingRoot);
            File.Move(temp, final); // No overwrite, no install or executable dispatch.
            return new GamePackageStageResult(file.Name, final, bytes,
                request.ExpectedPublisherSha256.ToLowerInvariant(),
                "Verified byte-for-byte against the supplied SHA-256, not a publisher or malware verdict. File only staged; PaRuX has not installed, unpacked or executed it.");
        }
        finally
        {
            // A failed or cancelled transaction must never leave a named package.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
