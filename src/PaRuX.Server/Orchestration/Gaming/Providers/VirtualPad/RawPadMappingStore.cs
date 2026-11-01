using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>
/// First-party local-only mapping profiles for raw physical gamepads. The connected
/// device's NonRoamableId is never stored in plaintext; only its SHA-256 is retained.
/// Corrupt existing profiles are preserved for recovery, not silently overwritten.
/// </summary>
public sealed class RawPadMappingStore
{
    private const int Version = 1;
    private const long MaxProfileBytes = 16 * 1024;
    private readonly string _directory;
    private sealed record Stored(int Version, string DeviceHash, PadMapping Mapping);

    public RawPadMappingStore() : this(Path.Combine(PaRuXDataRoot.Path, "gaming", "raw-pad-mappings")) { }

    internal RawPadMappingStore(string directory) => _directory = directory;

    private static string? DeviceHash(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 300 || deviceId.Any(char.IsControl))
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
    }

    private string FileFor(string hash) => Path.Combine(_directory, hash + ".json");

    public PadMapping? Load(string deviceId, out string? error)
    {
        error = null;
        string? hash = DeviceHash(deviceId);
        if (hash is null) { error = "Select a valid physical gamepad."; return null; }
        string path = FileFor(hash);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxProfileBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            { error = "The saved mapping is not a valid regular profile file."; return null; }
            Stored? data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (data is null || data.Version != Version ||
                !string.Equals(data.DeviceHash, hash, StringComparison.Ordinal) ||
                data.Mapping is null || data.Mapping.Validate() is not null)
            { error = "The saved mapping is invalid; it was preserved without changes."; return null; }
            return data.Mapping;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { error = "The saved mapping cannot be read; it was preserved without changes."; return null; }
    }

    public bool Save(string deviceId, PadMapping mapping, out string? error)
    {
        error = null;
        string? hash = DeviceHash(deviceId);
        if (hash is null) { error = "Select a valid physical gamepad."; return false; }
        if (mapping is null || mapping.Validate() is { } problem)
        { error = problem ?? "Choose a mapping."; return false; }
        string target = FileFor(hash);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_directory);
            if (File.Exists(target))
            {
                _ = Load(deviceId, out string? existingError);
                if (existingError is not null)
                { error = existingError; return false; }
            }
            temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Stored(Version, hash, mapping));
            if (json.Length > MaxProfileBytes)
            { error = "The mapping is too large."; return false; }
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { error = "The mapping could not be saved."; return false; }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
