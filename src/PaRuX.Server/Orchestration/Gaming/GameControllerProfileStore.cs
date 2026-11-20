using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Controller profiles that belong to one game (dead zones, inversion, A/B swap, slot), the per-game profiles Steam Input and
/// Playnite's fullscreen mode offer. A game without its own profile uses the global one. Stored atomically in one small file;
/// a damaged file means "no per-game profiles", never a crash, and is never overwritten by a failed read.
/// </summary>
public sealed class GameControllerProfileStore
{
    internal const int MaximumProfiles = 500;
    internal const int MaximumKeyLength = 128;
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, GamingControllerProfile> _profiles = new(StringComparer.Ordinal);

    public GameControllerProfileStore() : this(PaRuXDataRoot.Combine("server", "game-controller-profiles.json")) { }

    public GameControllerProfileStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Null when the key is acceptable, otherwise the reason. Keys are the library's title keys, so they are short printable text.</summary>
    public static string? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "A game key is required.";
        if (key.Length > MaximumKeyLength) return "The game key is too long.";
        if (key.Any(char.IsControl)) return "The game key contains control characters.";
        return null;
    }

    public GamingControllerProfile? Get(string titleKey)
    {
        lock (_gate) return _profiles.TryGetValue(titleKey, out GamingControllerProfile? p) ? p : null;
    }

    public int Count { get { lock (_gate) return _profiles.Count; } }

    /// <returns>Null on success, otherwise the reason. Nothing is stored on failure.</returns>
    public string? Save(string titleKey, GamingControllerProfile profile)
    {
        string? keyError = ValidateKey(titleKey);
        if (keyError != null) return keyError;
        string? error = profile.Validate();
        if (error != null) return error;
        lock (_gate)
        {
            if (!_profiles.ContainsKey(titleKey) && _profiles.Count >= MaximumProfiles)
                return "At most " + MaximumProfiles + " games can have their own controller profile.";
            var next = new Dictionary<string, GamingControllerProfile>(_profiles, StringComparer.Ordinal) { [titleKey] = profile };
            return Persist(next) ? null : "The controller profile could not be saved.";
        }
    }

    public bool Remove(string titleKey)
    {
        lock (_gate)
        {
            if (!_profiles.ContainsKey(titleKey)) return false;
            var next = new Dictionary<string, GamingControllerProfile>(_profiles, StringComparer.Ordinal);
            next.Remove(titleKey);
            return Persist(next);
        }
    }

    private bool Persist(Dictionary<string, GamingControllerProfile> next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _profiles = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GamingControllerProfile>>(File.ReadAllText(_path), JsonOptions);
                if (loaded is null) return;
                // Keep only entries that are still valid, so one bad entry cannot hide the rest.
                _profiles = loaded.Where(kv => ValidateKey(kv.Key) is null && kv.Value is not null && kv.Value.Validate() is null)
                    .Take(MaximumProfiles).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
}
