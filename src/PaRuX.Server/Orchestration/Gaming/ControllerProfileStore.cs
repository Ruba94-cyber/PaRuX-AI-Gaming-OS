using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Persists the single controller profile atomically. A corrupt or invalid file is never
/// overwritten by defaults on load; defaults are used in memory and the reason is exposed.</summary>
public sealed class ControllerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const long MaxFileBytes = 64 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private GamingControllerProfile _profile = GamingControllerProfile.Default;
    private string? _loadError;

    public ControllerProfileStore()
        : this(PaRuX.Contracts.PaRuXDataRoot.Combine("server", "controller-profile.json")) { }

    public ControllerProfileStore(string path)
    {
        _path = path;
        Load();
    }

    public string? LoadError { get { lock (_gate) return _loadError; } }

    public GamingControllerProfile Get() { lock (_gate) return _profile; }

    /// <returns>Null on success, otherwise a validation/IO reason. Nothing is persisted on failure.</returns>
    public string? Save(GamingControllerProfile profile)
    {
        string? error = profile.Validate();
        if (error != null) return error;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(profile, JsonOptions));
                File.Move(temp, _path, overwrite: true);
                _profile = profile;
                _loadError = null;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "The controller profile could not be saved.";
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _profile = GamingControllerProfile.Default;
            _loadError = null;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length > MaxFileBytes) { _loadError = "The controller profile file is too large."; return; }
                var loaded = JsonSerializer.Deserialize<GamingControllerProfile>(File.ReadAllText(_path), JsonOptions);
                if (loaded is null) { _loadError = "The controller profile file is empty."; return; }
                string? error = loaded.Validate();
                if (error == null) _profile = loaded;
                else _loadError = error;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _loadError = "The controller profile file could not be read.";
            }
        }
    }
}
