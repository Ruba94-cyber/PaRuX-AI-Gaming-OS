using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// User-initiated, read-only save-folder capture to a PaRuX-owned ZIP with a
/// per-file SHA-256 manifest. Never restores, deletes, discovers or executes game data.
/// Source, stored archive and verification are disabled when Gaming is active.
/// </summary>
public sealed class GameSaveBackupService
{
    private const int MaxFiles = 512;
    private const int MaxDepth = 8;
    private const long MaxBytes = 512L * 1024 * 1024;
    private const int MaxArchives = 100;
    private readonly string _root;
    private readonly Func<bool> _gaming;
    private readonly Func<CancellationToken> _gamingToken;
    private sealed record Item(string Name, long Bytes, string Sha256);
    private sealed record Manifest(int Version, GameSaveBackupDto Backup, List<Item> Items);

    public GameSaveBackupService(LocalRuntimeService runtime)
        : this(Path.Combine(PaRuXDataRoot.Path, "server", "game-save-backups"),
            () => runtime.GamingMode, () => runtime.GamingCancellationToken) { }

    internal GameSaveBackupService(string root, Func<bool> gaming, Func<CancellationToken> gamingToken)
    {
        _root = Path.GetFullPath(root);
        _gaming = gaming;
        _gamingToken = gamingToken;
    }

    private static bool ValidId(string? id) => id is { Length: 32 } &&
        id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void CheckLocalDirectory(string path)
    {
        if (!Directory.Exists(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Choose an existing local folder.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked folders are not permitted in save backups.");
        }
        var volume = new DriveInfo(Path.GetPathRoot(path)!);
        if (volume.DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new InvalidDataException("A local volume is required for save backup.");
    }

    private static List<(string Absolute, string Relative, long Bytes)> Inspect(string directory)
    {
        var files = new List<(string, string, long)>();
        var queue = new Queue<(string Folder, int Depth)>();
        queue.Enqueue((directory, 0));
        long total = 0;
        int visitedEntries = 0;
        while (queue.Count != 0)
        {
            var (folder, depth) = queue.Dequeue();
            if (depth > MaxDepth) throw new InvalidDataException("Save folder nesting is too deep.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++visitedEntries > 1024)
                    throw new InvalidDataException("Save folder has too many nested entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked save files or directories are not permitted.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth == MaxDepth) throw new InvalidDataException("Save folder nesting is too deep.");
                    queue.Enqueue((entry, depth + 1));
                    continue;
                }
                string relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) ||
                    relative.StartsWith("/", StringComparison.Ordinal) ||
                    relative.Length > 512 || relative.Any(char.IsControl) || relative == "." ||
                    relative.Split('/').Any(part => part is ".." or "." or ""))
                    throw new InvalidDataException("An unsafe save file path was found.");
                long size = new FileInfo(entry).Length;
                if (size < 0 || size > MaxBytes - total || files.Count >= MaxFiles)
                    throw new InvalidDataException("Save backup exceeds the 512-file / 512 MiB limit.");
                total += size;
                files.Add((entry, relative, size));
            }
        }
        if (files.Count == 0) throw new InvalidDataException("The selected save folder contains no files.");
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Relative, b.Relative));
        return files;
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        string path = Path.Combine(_root, ".backup-lock");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The backup lock path is not a regular file.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { await Task.Delay(30, ct); }
        }
        throw new IOException("The save backup is busy.");
    }

    public async Task<GameSaveBackupDto> CreateAsync(GameSaveBackupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 100 ||
            request.Label.Any(char.IsControl))
            throw new InvalidDataException("Enter a label (1–100 characters, no control characters).");
        if (string.IsNullOrWhiteSpace(request.SourceFolder) || request.SourceFolder.Length > 1024 ||
            !Path.IsPathFullyQualified(request.SourceFolder))
            throw new InvalidDataException("Choose a fully qualified save folder.");

        string source = Path.GetFullPath(request.SourceFolder);
        CheckLocalDirectory(source);
        CheckLocalDirectory(Path.GetDirectoryName(_root)!);
        Directory.CreateDirectory(_root);
        CheckLocalDirectory(_root);
        string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (source.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            _root.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backing up the backup directory itself is not allowed.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingToken());
        using var lease = await AcquireLeaseAsync(linked.Token);
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        if (Directory.GetFiles(_root, "save-*.json").Length >= MaxArchives)
            throw new InvalidDataException("The local backup capacity of 100 archives was reached.");
        var files = Inspect(source);
        string id = Guid.NewGuid().ToString("N");
        string temp = Path.Combine(_root, "." + id + ".partial");
        string zipPath = Path.Combine(_root, "save-" + id + ".zip");
        string manifestPath = Path.Combine(_root, "save-" + id + ".json");
        string? manifestTemp = null;
        bool zipPublished = false;
        bool published = false;
        try
        {
            var items = new List<Item>();
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in files)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (_gaming()) throw new OperationCanceledException("Gaming started during save backup.");
                        if ((File.GetAttributes(file.Absolute) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("The source file was replaced by a link.");
                        var metadata = new FileInfo(file.Absolute);
                        if (metadata.Length != file.Bytes) throw new InvalidDataException("A save file changed during backup.");
                        var entry = archive.CreateEntry(file.Relative, CompressionLevel.Optimal);
                        await using var input = new FileStream(file.Absolute, FileMode.Open,
                            FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using var destination = entry.Open();
                        byte[] buffer = new byte[64 * 1024];
                        long copied = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(), linked.Token)) != 0)
                        {
                            if (_gaming()) throw new OperationCanceledException("Gaming started during save backup.");
                            if (read > file.Bytes - copied) throw new InvalidDataException("A save file grew during backup.");
                            hash.AppendData(buffer, 0, read);
                            await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                            copied += read;
                        }
                        if (copied != file.Bytes) throw new InvalidDataException("A save file shrank during backup.");
                        items.Add(new Item(file.Relative, copied, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                    }
                }
                await output.FlushAsync(linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException("Gaming started before save backup publication.");
            string zipHash;
            await using (var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                zipHash = Convert.ToHexString(await SHA256.HashDataAsync(input, linked.Token)).ToLowerInvariant();
            var dto = new GameSaveBackupDto(id, request.Label.Trim(), items.Count, items.Sum(i => i.Bytes),
                DateTime.UtcNow, zipHash);
            manifestTemp = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, dto, items));
            if (manifest.Length > 256 * 1024) throw new InvalidDataException("Save manifest exceeds its safe limit.");
            await File.WriteAllBytesAsync(manifestTemp, manifest, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_gaming()) throw new OperationCanceledException("Gaming started before save backup publication.");
            File.Move(temp, zipPath);
            zipPublished = true;
            File.Move(manifestTemp, manifestPath);
            manifestTemp = null;
            published = true;
            return dto;
        }
        finally
        {
            if (zipPublished && !published)
                try { File.Delete(zipPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (manifestTemp is not null)
                try { File.Delete(manifestTemp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private Manifest? ReadManifest(string id)
    {
        if (!ValidId(id)) return null;
        string path = Path.Combine(_root, "save-" + id + ".json");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length is <= 0 or > 256 * 1024)
            return null;
        try
        {
            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            if (manifest is null || manifest.Version != 1 || manifest.Backup is null ||
                manifest.Backup.Id != id || manifest.Items is null ||
                manifest.Items.Count is <= 0 or > MaxFiles ||
                manifest.Backup.Files != manifest.Items.Count ||
                manifest.Backup.Bytes < 0 || manifest.Backup.Bytes > MaxBytes ||
                manifest.Backup.ArchiveSha256 is not { Length: 64 } ||
                !manifest.Backup.ArchiveSha256.All(Uri.IsHexDigit) ||
                manifest.Backup.Label is null || manifest.Backup.Label.Length > 100 ||
                manifest.Items.Any(i => i is null || i.Name is null || i.Name.Length > 512 ||
                    i.Name.StartsWith("/", StringComparison.Ordinal) ||
                    i.Name.Split('/').Any(part => part is "" or "." or "..") ||
                    i.Bytes < 0 || i.Bytes > MaxBytes || i.Sha256 is not { Length: 64 } ||
                    !i.Sha256.All(Uri.IsHexDigit)) ||
                manifest.Items.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Items.Count ||
                manifest.Items.Sum(i => i.Bytes) != manifest.Backup.Bytes)
                return null;
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<GameSaveBackupDto> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<GameSaveBackupDto>();
        CheckLocalDirectory(_root);
        return Directory.EnumerateFiles(_root, "save-*.json").Take(MaxArchives + 1)
            .Select(x => Path.GetFileNameWithoutExtension(x))
            .Where(x => x.Length == 37)
            .Select(x => x[5..])
            .Select(ReadManifest)
            .Where(m => m is not null && File.Exists(Path.Combine(_root, "save-" + m.Backup.Id + ".zip")))
            .Select(m => m!.Backup)
            .OrderByDescending(x => x.CreatedUtc).Take(MaxArchives).ToArray();
    }

    public async Task<GameSaveVerificationDto> VerifyAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) return new(id, false, "Invalid local backup ID.", 0);
        if (_gaming()) return new(id, false, "Verification is disabled while Gaming.", 0);
        if (!Directory.Exists(_root)) return new(id, false, "No backup folder was found.", 0);
        CheckLocalDirectory(_root);
        Manifest? manifest = ReadManifest(id);
        string path = Path.Combine(_root, "save-" + id + ".zip");
        if (manifest is null || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > MaxBytes + 16L * 1024 * 1024)
            return new(id, false, "Backup or manifest is missing or unreadable.", 0);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingToken());
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous);
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(file, linked.Token)).ToLowerInvariant();
            if (!string.Equals(digest, manifest.Backup.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                return new(id, false, "Archive SHA-256 differs from its saved manifest.", 0);
            file.Position = 0;
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count != manifest.Items.Count)
                return new(id, false, "Archive file count differs from its manifest.", 0);
            int verified = 0;
            var expected = manifest.Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (_gaming()) return new(id, false, "Verification interrupted by Gaming.", verified);
                if (!seen.Add(entry.FullName) ||
                    !expected.TryGetValue(entry.FullName, out var item) || entry.Length != item.Bytes ||
                    item.Bytes < 0 || item.Bytes > MaxBytes)
                    return new(id, false, "The archive contains unexpected file metadata.", verified);
                await using var input = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                long readTotal = 0;
                int size;
                while ((size = await input.ReadAsync(buffer.AsMemory(), linked.Token)) != 0)
                {
                    if (_gaming()) return new(id, false, "Verification interrupted by Gaming.", verified);
                    if (size > item.Bytes - readTotal)
                        return new(id, false, "A file exceeds the recorded size.", verified);
                    hash.AppendData(buffer, 0, size);
                    readTotal += size;
                }
                if (readTotal != item.Bytes ||
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(id, false, "A save file failed the SHA-256 check.", verified);
                verified++;
            }
            if (seen.Count != expected.Count)
                return new(id, false, "The archive is missing a declared file.", verified);
            return new(id, true, "Archive, manifest and every file hash matched.", verified);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(id, false, "Verification was cancelled by Gaming.", 0); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return new(id, false, "The archive could not be verified.", 0); }
    }
}
