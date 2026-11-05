using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>
/// Persist ONLY PaRuX-owned native torrent jobs, for explicit owner resume after
/// restarting Core. Raw magnets can contain private tracker passkeys: never put
/// them in plaintext JSON or normal logs. Windows DPAPI protects the entire
/// bounded sidecar for the currently signed-in user. No auto-resume on startup.
/// HTTP signed URLs and external qBittorrent jobs are intentionally excluded.
/// </summary>
internal sealed class NativeTorrentRecoveryStore
{
    internal sealed record Entry(
        string Id, string Kind, string Target, string Source, string FileName,
        string Directory, string InfoHash, string? ExpectedSha256,
        int Priority, DateTime CreatedUtc, long BytesDone, long? TotalBytes);

    private const int MaxItems = 200;
    private const int MaxEncryptedBytes = 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaRuX.NativeTorrentRecovery.v1");
    private readonly string _path;

    internal NativeTorrentRecoveryStore(string path) => _path = Path.GetFullPath(path);

    internal void Save(IReadOnlyList<Entry> entries)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (entries.Count > MaxItems) throw new InvalidOperationException("Too many torrent recovery jobs.");
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!ManualGameLibrary.IsSafeDirectory(directory))
            throw new InvalidDataException("Torrent recovery directory is unsafe.");
        if (entries.Count == 0)
        {
            if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Torrent recovery file cannot be a link.");
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (plain.Length > MaxEncryptedBytes / 2)
            throw new InvalidDataException("Too much torrent recovery metadata.");
        byte[] sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        if (sealedBytes.Length > MaxEncryptedBytes)
            throw new InvalidDataException("Encrypted torrent recovery data is too large.");
        string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Torrent recovery file cannot be a link.");
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                file.Write(sealedBytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
        }
    }

    internal IReadOnlyList<Entry> Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return Array.Empty<Entry>();
        var info = new FileInfo(_path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 or > MaxEncryptedBytes)
            throw new InvalidDataException("Torrent recovery sidecar is linked or too large.");
        byte[] sealedBytes = File.ReadAllBytes(_path);
        byte[] plain;
        try { plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Torrent recovery sidecar cannot be opened by this Windows user.", ex);
        }
        if (plain.Length > MaxEncryptedBytes / 2)
            throw new InvalidDataException("Torrent recovery payload is too large.");
        try
        {
            Entry[] items = JsonSerializer.Deserialize<Entry[]>(plain) ?? Array.Empty<Entry>();
            if (items.Length > MaxItems) throw new InvalidDataException("Too many stored torrent jobs.");
            return items;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Torrent recovery data is malformed.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
