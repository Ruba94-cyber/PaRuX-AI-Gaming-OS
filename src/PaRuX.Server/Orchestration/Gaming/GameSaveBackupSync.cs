using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Mirrors PaRuX save backups to a folder the owner picks (a synced cloud folder, a NAS share or a USB drive) and brings back the
/// backups that folder has and this PC lacks. PaRuX never talks to a cloud service itself: the folder's own sync client does that.
/// Every copied archive is hashed against its manifest before it is published, nothing is ever overwritten or deleted on either
/// side, and nothing runs while Gaming is active.
/// </summary>
public sealed partial class GameSaveBackupService
{
    private const string SyncSubfolder = "PaRuX-save-backups";
    private string SyncConfigPath => Path.Combine(Path.GetDirectoryName(_root)!, "game-save-sync.json");

    private sealed record SyncConfig(string? Folder);

    /// <summary>The folder chosen for mirroring, or empty when none is set.</summary>
    public GameSaveSyncStatusDto SyncStatus()
    {
        string? folder = ReadSyncConfig();
        return new GameSaveSyncStatusDto(folder ?? string.Empty, folder is not null);
    }

    private string? ReadSyncConfig()
    {
        try
        {
            if (!File.Exists(SyncConfigPath)) return null;
            var config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(SyncConfigPath));
            return string.IsNullOrWhiteSpace(config?.Folder) ? null : config!.Folder;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Choose (or clear, with an empty path) the mirror folder. The folder must exist and must not overlap the managed backup directory.</summary>
    public GameSaveSyncStatusDto SetSyncFolder(string? folder)
    {
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        if (string.IsNullOrWhiteSpace(folder))
        {
            try { File.Delete(SyncConfigPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new GameSaveSyncStatusDto(string.Empty, false);
        }
        string full = ValidateSyncFolder(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(SyncConfigPath)!);
        File.WriteAllText(SyncConfigPath, JsonSerializer.Serialize(new SyncConfig(full)));
        return new GameSaveSyncStatusDto(full, true);
    }

    private string ValidateSyncFolder(string folder)
    {
        if (folder.Length > 1024 || !Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl))
            throw new InvalidDataException("Choose a fully qualified folder.");
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new InvalidDataException("That folder does not exist.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A linked folder cannot be used for save sync.");
        string root = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sync folder must be separate from PaRuX's own backup directory.");
        return full;
    }

    /// <summary>Copy new local backups to the mirror folder and new mirror backups here. Both directions are additive and hash-checked.</summary>
    public async Task<GameSaveSyncResultDto> SyncAsync(CancellationToken ct)
    {
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        string? configured = ReadSyncConfig();
        if (configured is null) throw new InvalidDataException("Choose a sync folder first.");
        string target = Path.Combine(ValidateSyncFolder(configured), SyncSubfolder);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_root);
        CheckLocalDirectory(_root);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingToken());
        int pushed = 0, pulled = 0, already = 0;
        var problems = new List<string>();

        // Push: local backups the mirror lacks.
        foreach (GameSaveBackupDto backup in List())
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_gaming()) throw new InvalidOperationException("Gaming interrupted the sync.");
            string zipName = "save-" + backup.Id + ".zip", jsonName = "save-" + backup.Id + ".json";
            if (File.Exists(Path.Combine(target, jsonName)) && File.Exists(Path.Combine(target, zipName))) { already++; continue; }
            try
            {
                await CopyVerifiedAsync(Path.Combine(_root, zipName), Path.Combine(target, zipName), backup.ArchiveSha256, linked.Token);
                CopyPlain(Path.Combine(_root, jsonName), Path.Combine(target, jsonName));   // the manifest last: its presence marks a complete backup
                pushed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { problems.Add("Could not copy " + Shorten(backup.Id) + " to the sync folder."); }
        }

        // Pull: mirror backups this PC lacks.
        var local = new HashSet<string>(List().Select(b => b.Id), StringComparer.Ordinal);
        foreach (string manifestPath in Directory.EnumerateFiles(target, "save-*.json").Take(MaxArchives * 2))
        {
            linked.Token.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(manifestPath);
            if (name.Length != 37) continue;
            string id = name[5..];
            if (!ValidId(id) || local.Contains(id)) continue;
            string remoteZip = Path.Combine(target, "save-" + id + ".zip");
            if (!File.Exists(remoteZip)) continue;
            if (local.Count >= MaxArchives) { problems.Add("The local capacity of 100 backups was reached."); break; }
            string localZip = Path.Combine(_root, "save-" + id + ".zip"), localJson = Path.Combine(_root, "save-" + id + ".json");
            try
            {
                CopyPlain(remoteZip, localZip);
                CopyPlain(manifestPath, localJson);
                GameSaveVerificationDto verdict = await VerifyAsync(id, linked.Token);
                if (!verdict.Valid)
                {
                    TryDelete(localZip); TryDelete(localJson);
                    problems.Add("Skipped " + Shorten(id) + ": " + verdict.Message);
                    continue;
                }
                local.Add(id);
                pulled++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TryDelete(localZip); TryDelete(localJson);
                problems.Add("Could not bring " + Shorten(id) + " from the sync folder.");
            }
        }
        return new GameSaveSyncResultDto(pushed, pulled, already, problems);
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyPlain(string from, string to)
    {
        if (File.Exists(to)) return;   // never overwrite
        string temp = to + ".partial";
        File.Copy(from, temp, overwrite: true);
        File.Move(temp, to);
    }

    private static async Task CopyVerifiedAsync(string from, string to, string expectedSha256, CancellationToken ct)
    {
        if (File.Exists(to)) return;
        string temp = to + ".partial";
        try
        {
            File.Copy(from, temp, overwrite: true);
            await using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The copied archive does not match its checksum.");
        }
        catch { TryDelete(temp); throw; }
        File.Move(temp, to);
    }
}
