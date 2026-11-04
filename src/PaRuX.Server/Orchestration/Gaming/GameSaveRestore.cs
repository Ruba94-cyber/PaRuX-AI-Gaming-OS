using System.IO.Compression;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// User-initiated, confirmed restore of a PaRuX-owned save backup. Order of operations:
/// verify the archive (SHA-256 of the archive and of every file) -> dry-run plan -> automatic
/// pre-restore backup of the target -> extract to a staging folder inside the target (same volume),
/// re-hashing while writing -> move into place -> re-hash the result. Any name that could escape the
/// target (rooted, drive/stream colon, backslash, dot segments, trailing dot/space, links) is refused.
/// Disabled while Gaming is active. Never deletes files that are not in the backup.
/// </summary>
public sealed partial class GameSaveBackupService
{
    private readonly SemaphoreSlim _restoreGate = new(1, 1);

    /// <summary>True when <paramref name="name"/> is a plain forward-slash relative path that resolves strictly inside <paramref name="targetFull"/>.</summary>
    internal static bool TryResolveInside(string targetFull, string name, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Any(char.IsControl) ||
            name.Contains('\\') || name.Contains(':') || name.Contains('*') || name.Contains('?') ||
            name.Contains('"') || name.Contains('<') || name.Contains('>') || name.Contains('|') ||
            Path.IsPathRooted(name))
            return false;
        foreach (string part in name.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
        }
        try
        {
            string prefix = targetFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(prefix, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            full = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private string ValidateRestoreTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 1024 || target.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(target))
            throw new InvalidDataException("Choose a fully qualified save folder.");
        string full = Path.GetFullPath(target);
        string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string targetPrefix = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            _root.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A restore target must not be or contain the managed backup directory.");
        CheckLocalDirectory(full);
        return full;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
    }

    /// <summary>Dry run. Verifies the backup and lists, per file, whether a restore would create, overwrite or leave it unchanged. Writes nothing.</summary>
    public async Task<GameSaveRestorePlanDto> PlanRestoreAsync(string id, string targetFolder, CancellationToken ct)
    {
        if (_gaming()) return new(id, false, "Restore is unavailable during Gaming.", Array.Empty<GameSaveRestoreItemDto>());
        string target;
        try { target = ValidateRestoreTarget(targetFolder); }
        catch (InvalidDataException ex) { return new(id, false, ex.Message, Array.Empty<GameSaveRestoreItemDto>()); }

        GameSaveVerificationDto verification = await VerifyAsync(id, ct);
        if (!verification.Valid)
            return new(id, false, "Backup does not verify: " + verification.Message, Array.Empty<GameSaveRestoreItemDto>());
        Manifest? manifest = ReadManifest(id);
        if (manifest is null) return new(id, false, "Backup or manifest is missing or unreadable.", Array.Empty<GameSaveRestoreItemDto>());

        var items = new List<GameSaveRestoreItemDto>();
        foreach (Item item in manifest.Items.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryResolveInside(target, item.Name, out string dest))
                return new(id, false, "The backup contains a file name that cannot be restored safely; nothing was changed.", Array.Empty<GameSaveRestoreItemDto>());
            string? blocked = FirstLinkBelow(target, dest);
            if (blocked is not null)
                return new(id, false, "A linked folder or file lies inside the target; nothing was changed.", Array.Empty<GameSaveRestoreItemDto>());
            string action;
            if (!File.Exists(dest)) action = "create";
            else if (new FileInfo(dest).Length == item.Bytes &&
                     string.Equals(await HashFileAsync(dest, ct), item.Sha256, StringComparison.OrdinalIgnoreCase))
                action = "unchanged";
            else action = "overwrite";
            items.Add(new GameSaveRestoreItemDto(item.Name, item.Bytes, action));
        }
        int changes = items.Count(i => i.Action != "unchanged");
        return new(id, true, changes == 0 ? "The target already matches this backup." :
            changes + " file(s) would be written; a backup of the target is made first.", items);
    }

    /// <summary>The first existing link (reparse point) at or below <paramref name="target"/> on the way to <paramref name="dest"/>, or null.</summary>
    private static string? FirstLinkBelow(string target, string dest)
    {
        for (string? current = dest; current is not null &&
             current.Length > target.Length; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return current;
        }
        return null;
    }

    public async Task<GameSaveRestoreResultDto> RestoreAsync(string id, GameSaveRestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_gaming()) return new(id, false, "Restore is unavailable during Gaming.", 0, null);
        if (!request.Confirm) return new(id, false, "Restore needs explicit confirmation; nothing was changed.", 0, null);
        await _restoreGate.WaitAsync(ct);
        try
        {
            GameSaveRestorePlanDto plan = await PlanRestoreAsync(id, request.TargetFolder, ct);
            if (!plan.Ok) return new(id, false, plan.Message, 0, null);
            string target = Path.GetFullPath(request.TargetFolder);
            Manifest manifest = ReadManifest(id)!;
            var changed = plan.Items.Where(i => i.Action != "unchanged").Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            if (changed.Count == 0) return new(id, true, plan.Message, 0, null);

            // Safety copy of what is there now. Without it nothing is overwritten.
            string? preId = null;
            try
            {
                var pre = await CreateAsync(new GameSaveBackupRequest(target,
                    ("Pre-restore " + manifest.Backup.Label).Length > 100 ? ("Pre-restore " + manifest.Backup.Label)[..100]
                        : "Pre-restore " + manifest.Backup.Label), ct);
                preId = pre.Id;
            }
            catch (InvalidDataException ex) when (ex.Message == NoFilesMessage) { /* empty target: nothing to protect */ }
            catch (InvalidDataException ex)
            {
                return new(id, false, "The current files could not be backed up first (" + ex.Message + "); nothing was changed.", 0, null);
            }

            if (_gaming()) return new(id, false, "Restore interrupted by Gaming; nothing was changed.", 0, preId);
            string staging = Path.Combine(target, ".parux-restore-" + Guid.NewGuid().ToString("N"));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingToken());
            try
            {
                string zipPath = Path.Combine(_root, "save-" + id + ".zip");
                Directory.CreateDirectory(staging);
                var staged = new List<(string Staged, string Dest, Item Item)>();
                await using (var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
                {
                    foreach (Item item in manifest.Items.Where(i => changed.Contains(i.Name)))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        ZipArchiveEntry? entry = zip.GetEntry(item.Name);
                        if (entry is null || entry.Length != item.Bytes || !TryResolveInside(target, item.Name, out string dest))
                            return new(id, false, "The archive does not match its manifest; nothing was changed.", 0, preId);
                        string temp = Path.Combine(staging, staged.Count.ToString("D4") + ".tmp");
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using (var input = entry.Open())
                        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            long total = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer.AsMemory(), linked.Token)) != 0)
                            {
                                if (_gaming()) throw new OperationCanceledException("Gaming started during restore.");
                                total += read;
                                if (total > item.Bytes) return new(id, false, "A file exceeds its recorded size; nothing was changed.", 0, preId);
                                hash.AppendData(buffer, 0, read);
                                await output.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                            }
                            if (total != item.Bytes) return new(id, false, "A file is shorter than recorded; nothing was changed.", 0, preId);
                        }
                        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                            return new(id, false, "A file failed the SHA-256 check; nothing was changed.", 0, preId);
                        staged.Add((temp, dest, item));
                    }
                }

                int written = 0;
                foreach (var (stagedPath, dest, item) in staged)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (_gaming()) throw new OperationCanceledException("Gaming started during restore.");
                    string? dir = Path.GetDirectoryName(dest);
                    if (FirstLinkBelow(target, dest) is not null)
                        return new(id, false, "A link appeared inside the target; restore stopped (" + written + " file(s) written). The pre-restore backup keeps the earlier state.", written, preId);
                    Directory.CreateDirectory(dir!);
                    File.Move(stagedPath, dest, overwrite: true);
                    written++;
                    if (!string.Equals(await HashFileAsync(dest, linked.Token), item.Sha256, StringComparison.OrdinalIgnoreCase))
                        return new(id, false, "A restored file failed its post-restore SHA-256 check; the pre-restore backup keeps the earlier state.", written, preId);
                }
                return new(id, true, written + " file(s) restored and verified.", written, preId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(id, false, "Gaming interrupted the restore; the pre-restore backup keeps the earlier state.", 0, preId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new(id, false, "The restore could not be completed; the pre-restore backup keeps the earlier state.", 0, preId);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { _restoreGate.Release(); }
    }
}
