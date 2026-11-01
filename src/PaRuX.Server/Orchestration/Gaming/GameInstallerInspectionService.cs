using System.Security;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Checks an explicitly selected local installer, without downloading, unpacking,
/// installing, modifying or executing anything. A checksum is NOT a malware verdict.
/// Never hash during Gaming/unknown recovery to protect gameplay resources.
/// </summary>
public sealed class GameInstallerInspectionService
{
    private const long MaxInspectionBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".exe", ".msi", ".msix", ".zip", ".7z", ".rar", ".iso" },
        StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _gamingOrUnready;

    public GameInstallerInspectionService(WorkloadOrchestrator orchestrator, LocalAiProcessManager process)
        : this(() => orchestrator.CurrentState != "AiReady" || process.GamingActive) { }

    internal GameInstallerInspectionService(Func<bool> gamingOrUnready) =>
        _gamingOrUnready = gamingOrUnready ?? throw new ArgumentNullException(nameof(gamingOrUnready));

    internal static bool ValidLocalPackagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !SupportedExtensions.Contains(Path.GetExtension(path)))
            return false;

        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path &&
                File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    public async Task<GameInstallerInspectionResult> InspectAsync(
        GameInstallerInspectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Installer inspection is disabled during Gaming or AI recovery.");
        if (!ValidLocalPackagePath(request.FilePath))
            throw new ArgumentException("Choose an existing regular local installer file with a supported extension.");

        var original = new FileInfo(request.FilePath);
        long bytes = original.Length;
        DateTime written = original.LastWriteTimeUtc;
        if (bytes <= 0 || bytes > MaxInspectionBytes)
            throw new InvalidDataException("The installer is empty or exceeds the 8 GiB inspection limit.");

        // Exclusive of writers: hashing a package while it is still being
        // downloaded would create a misleading checksum.
        await using var source = new FileStream(original.FullName, FileMode.Open,
            FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_gamingOrUnready())
            throw new InvalidOperationException("Gaming began while inspecting; no result is accepted.");

        original.Refresh();
        if (source.Length != bytes || original.Length != bytes || original.LastWriteTimeUtc != written)
            throw new InvalidDataException("The installer changed during inspection; select the completed file again.");

        return new GameInstallerInspectionResult(
            original.Name, bytes, Convert.ToHexString(hash).ToLowerInvariant(),
            "SHA-256 identifies bytes only. Verify the hash against a trusted independent publisher source; PaRuX did not check malware, software licensing or signatures and did not execute or install this file.");
    }
}
