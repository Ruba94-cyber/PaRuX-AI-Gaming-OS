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

    private async Task<FileStream> AcquireStagingLeaseAsync(CancellationToken ct)
    {
        // Keep a stable lock file: deleting it could let another Core start
        // a second writer on a newly-created lock inode.
        string lockPath = Path.Combine(_stagingRoot, ".parux-staging.lock");
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

    private void EnsureCapacity(long sourceBytes)
    {
        long used = 0;
        int entries = 0;
        foreach (string path in Directory.EnumerateFiles(_stagingRoot))
        {
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
        using var stagingLease = await AcquireStagingLeaseAsync(ct);
        EnsureCapacity(bytes);
        string id = Guid.NewGuid().ToString("N");
        string extension = Path.GetExtension(file.Name);
        string temp = Path.Combine(_stagingRoot, ".parux-game-" + id + ".partial");
        string final = Path.Combine(_stagingRoot, "game-" + id + extension);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingCancellation());
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
