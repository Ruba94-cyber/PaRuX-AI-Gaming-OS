using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>Where a resolved mapping came from.</summary>
public enum PadMappingSource { None, GameProfile, DeviceMapping }

public sealed record ResolvedPadMapping(PadMapping? Mapping, PadMappingSource Source, string? Error);

/// <summary>
/// Per-game controller mappings keyed by (game id, physical-device hash). Both ids are stored only as SHA-256, the same
/// privacy rule as <see cref="RawPadMappingStore"/>. A corrupt file is preserved, never overwritten. Selection falls back
/// to the per-device mapping when a game has no profile (<see cref="Resolve"/>).
/// </summary>
public sealed class GamePadProfileStore
{
    private const int Version = 1;
    private const long MaxProfileBytes = 16 * 1024;
    private readonly string _directory;
    private readonly RawPadMappingStore _deviceStore;
    private sealed record Stored(int Version, string GameHash, string DeviceHash, PadMapping Mapping);

    public GamePadProfileStore(RawPadMappingStore deviceStore)
        : this(Path.Combine(PaRuXDataRoot.Path, "gaming", "raw-pad-game-profiles"), deviceStore) { }

    internal GamePadProfileStore(string directory, RawPadMappingStore deviceStore)
    {
        _directory = directory;
        _deviceStore = deviceStore;
    }

    private static string? Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(char.IsControl)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private string FileFor(string gameHash, string deviceHash) => Path.Combine(_directory, gameHash + "_" + deviceHash + ".json");

    public PadMapping? Load(string gameId, string deviceId, out string? error)
    {
        error = null;
        string? g = Hash(gameId), d = Hash(deviceId);
        if (g is null || d is null) { error = "Select a valid game and physical gamepad."; return null; }
        string path = FileFor(g, d);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxProfileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            { error = "The saved game profile is not a valid regular file."; return null; }
            Stored? data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (data is null || data.Version != Version || data.GameHash != g || data.DeviceHash != d ||
                data.Mapping is null || data.Mapping.Validate() is not null)
            { error = "The saved game profile is invalid; it was preserved without changes."; return null; }
            return data.Mapping;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { error = "The saved game profile cannot be read; it was preserved without changes."; return null; }
    }

    public bool Save(string gameId, string deviceId, PadMapping mapping, out string? error)
    {
        error = null;
        string? g = Hash(gameId), d = Hash(deviceId);
        if (g is null || d is null) { error = "Select a valid game and physical gamepad."; return false; }
        if (mapping is null) { error = "Choose a mapping."; return false; }
        if (mapping.Validate() is { } problem) { error = problem; return false; }
        string target = FileFor(g, d);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_directory);
            if (File.Exists(target))
            {
                _ = Load(gameId, deviceId, out string? existing);
                if (existing is not null) { error = existing; return false; }
            }
            temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Stored(Version, g, d, mapping));
            if (json.Length > MaxProfileBytes) { error = "The mapping is too large."; return false; }
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
        { error = "The game profile could not be saved."; return false; }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public bool Delete(string gameId, string deviceId)
    {
        string? g = Hash(gameId), d = Hash(deviceId);
        if (g is null || d is null) return false;
        try { File.Delete(FileFor(g, d)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Game profile first (when a game is known), otherwise the per-device mapping. A damaged game profile is
    /// reported and the device mapping is used instead, so a broken file can never leave the pad unmapped.</summary>
    public ResolvedPadMapping Resolve(string deviceId, string? gameId)
    {
        string? error = null;
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            PadMapping? game = Load(gameId, deviceId, out error);
            if (game is not null) return new(game, PadMappingSource.GameProfile, null);
        }
        PadMapping? device = _deviceStore.Load(deviceId, out string? deviceError);
        if (device is not null) return new(device, PadMappingSource.DeviceMapping, error);
        return new(null, PadMappingSource.None, error ?? deviceError);
    }
}
