using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed class GamingProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storePath;
    private readonly object _gate = new();
    private List<GamingProfileDto> _profiles = new();
    private string? _loadError;

    /// <summary>A protected/corrupt user store is never replaced with defaults.</summary>
    public string? LoadError { get { lock (_gate) return _loadError; } }

    public GamingProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "server", "gaming-profiles.json"))
    {
    }

    public GamingProfileStore(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    public IReadOnlyList<GamingProfileDto> List()
    {
        lock (_gate)
        {
            return _profiles.ToList();
        }
    }

    public GamingProfileDto? Get(string idOrName)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public GamingProfileDto GetActiveOrDefault(string? requestedName = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                GamingProfileDto? requested = _profiles.FirstOrDefault(p => string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                if (requested != null) return requested;
            }

            GamingProfileDto? enabled = _profiles.FirstOrDefault(p => p.Enabled);
            return enabled ?? BuildDefaultLocalPreset();
        }
    }

    public GamingProfileDto Save(GamingProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            ThrowIfWriteBlocked();
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            // Only publish to memory after a durable, atomic on-disk replacement.
            // Do not return a successful save if disk, antivirus or permissions deny it.
            var next = _profiles.ToList();
            int index = next.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) next[index] = profile;
            else next.Add(profile);
            Persist(next);
            _profiles = next;
            return profile;
        }
    }

    public bool Delete(string idOrName)
    {
        lock (_gate)
        {
            ThrowIfWriteBlocked();
            var next = _profiles.ToList();
            int index = next.FindIndex(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            next.RemoveAt(index);
            Persist(next);
            _profiles = next;
            return true;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _loadError = null;
            if (File.Exists(_storePath))
            {
                try
                {
                    string json = File.ReadAllText(_storePath);
                    List<GamingProfileDto>? loaded = JsonSerializer.Deserialize<List<GamingProfileDto>>(json, JsonOptions);
                    if (loaded is null || loaded.Count > 1024 ||
                        loaded.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) ||
                            string.IsNullOrWhiteSpace(p.Name)))
                        throw new InvalidDataException("Gaming profile JSON is invalid.");
                    _profiles = loaded;
                    if (MigrateUnavailableBuiltInProviders()) Persist(_profiles);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or ArgumentException)
                {
                    // Preserve the original corrupt/inaccessible file byte-for-byte.
                    // Safe defaults are in-memory only; no save/delete until the user
                    // manually recovers the original file.
                    _loadError = "Gaming profiles could not be loaded safely. Original file preserved; editing is disabled.";
                    _profiles = BuildDefaultPresets();
                    return;
                }
            }

            _profiles = BuildDefaultPresets();
            try { Persist(_profiles); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadError = "Gaming profiles cannot be written. Editing is disabled until storage is repaired.";
            }
        }
    }

    private void ThrowIfWriteBlocked()
    {
        if (_loadError != null) throw new InvalidDataException(_loadError);
    }

    private void Persist(IReadOnlyList<GamingProfileDto> profiles)
    {
        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".gaming-profiles-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, profiles, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// The older local stock preset selected XOutput, which is not installed on many PCs. Keep
    /// that built-in profile usable after upgrades while leaving user-created profiles alone.
    /// </summary>
    private bool MigrateUnavailableBuiltInProviders()
    {
        bool changed = false;
        foreach (GamingProfileDto profile in _profiles)
        {
            if (string.Equals(profile.Id, "preset-local-console", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(profile.InputProvider, "xoutput", StringComparison.OrdinalIgnoreCase))
                {
                    profile.InputProvider = "native";
                    changed = true;
                }
            }
        }

        return changed;
    }

    public static List<GamingProfileDto> BuildDefaultPresets() => new()
    {
        BuildDefaultLocalPreset(),
        BuildDefaultRemotePreset()
    };

    public static GamingProfileDto BuildDefaultLocalPreset() => new()
    {
        Id = "preset-local-console",
        Name = "Local Console",
        Enabled = true,
        SessionType = "local",
        // A TV or second monitor is not authorization to launch a game or disable AI.
        // Automatic display triggers are opt-in in the profile editor.
        TriggerProvider = "manual",
        TriggerSettings = new Dictionary<string, string> { { "require_controller", "false" } },
        LauncherProvider = "none",
        LauncherSettings = new Dictionary<string, string>(),
        InputProvider = "native",
        InputSettings = new Dictionary<string, string>(),
        SuspendProvider = "nyrna",
        SuspendSettings = new Dictionary<string, string>(),
        AiResourcePolicy = "release_gpu",
        QuickResumePolicy = "auto",
        GracePeriodSeconds = 15,
        StartupActions = new List<string>(),
        ShutdownActions = new List<string>()
    };

    public static GamingProfileDto BuildDefaultRemotePreset() => new()
    {
        Id = "preset-remote-sunshine",
        Name = "Remote Moonlight/Sunshine",
        // Do not start an unrequested remote session; user must enable the preset.
        Enabled = false,
        SessionType = "remote",
        TriggerProvider = "sunshine",
        TriggerSettings = new Dictionary<string, string> { { "sunshine_url", "http://127.0.0.1:47989/api/config" } },
        LauncherProvider = "playnite",
        LauncherSettings = new Dictionary<string, string>(),
        InputProvider = "sunshine",
        InputSettings = new Dictionary<string, string>(),
        SuspendProvider = "nyrna",
        SuspendSettings = new Dictionary<string, string>(),
        AiResourcePolicy = "release_gpu",
        QuickResumePolicy = "auto",
        GracePeriodSeconds = 30,
        StartupActions = new List<string>(),
        ShutdownActions = new List<string>()
    };
}
