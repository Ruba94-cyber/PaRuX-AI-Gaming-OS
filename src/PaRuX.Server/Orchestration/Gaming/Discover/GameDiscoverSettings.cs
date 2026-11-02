using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

/// <summary>
/// Persistent Discover opt-in plus the titles the user hid. Discover is OFF by default; a missing, unreadable
/// or corrupt file always means OFF (a corrupt file is never overwritten until the user changes a value).
/// </summary>
public sealed class GameDiscoverSettingsStore
{
    public const int MaxHidden = 2000;

    /// <summary>Shown next to the switch. Enabling the switch is the user's consent to exactly this.</summary>
    public const string ConsentText =
        "Game Discover contacts the public Steam store (store.steampowered.com) to show recommended games. " +
        "The Steam app ids of games you play (for example 730) are sent to Steam so PaRuX can read their genres and tags, " +
        "and your search text is sent if you search. No account, login, API key, play time or personal data is sent. " +
        "Nothing is contacted while this is off or while a game is running. PaRuX never downloads games: " +
        "installing opens the official store client after you confirm.";

    private sealed class FileModel
    {
        public bool Enabled { get; set; }
        public List<string> Hidden { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private FileModel _model = new();
    private string? _loadError;

    public GameDiscoverSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public static string DefaultPath => Path.Combine(PaRuXDataRoot.Path, "server", "game-discover.json");

    public string? LoadError { get { lock (_gate) return _loadError; } }
    public bool Enabled { get { lock (_gate) return _model.Enabled && _loadError is null; } }

    public IReadOnlyCollection<string> Hidden()
    {
        lock (_gate) return _model.Hidden.ToArray();
    }

    public bool SetEnabled(bool enabled) => Mutate(m => m.Enabled = enabled);

    public bool Hide(string key) => Mutate(m =>
    {
        if (!m.Hidden.Contains(key, StringComparer.Ordinal) && m.Hidden.Count < MaxHidden) m.Hidden.Add(key);
    });

    public bool Unhide(string key) => Mutate(m => m.Hidden.RemoveAll(h => string.Equals(h, key, StringComparison.Ordinal)));

    private bool Mutate(Action<FileModel> change)
    {
        lock (_gate)
        {
            if (_loadError is not null) return false;
            var next = new FileModel { Enabled = _model.Enabled, Hidden = _model.Hidden.ToList() };
            change(next);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
                    File.Move(temp, _path, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            _model = next;
            return true;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _loadError = null;
            if (!File.Exists(_path)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (loaded is null) throw new InvalidDataException();
                loaded.Hidden = (loaded.Hidden ?? new()).Where(h => !string.IsNullOrWhiteSpace(h) && h.Length <= 64).Take(MaxHidden).ToList();
                _model = loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _model = new FileModel();
                _loadError = "Discover settings could not be read; Discover stays off.";
            }
        }
    }
}

/// <summary>
/// The single decision point before any network call or scoring: opt-in must be on and Gaming must not own
/// the machine. Evaluated again before every individual request, not once per feature call.
/// </summary>
public sealed class GameDiscoverGate
{
    private readonly GameDiscoverSettingsStore _settings;
    private readonly Func<bool> _gamingActive;

    public GameDiscoverGate(GameDiscoverSettingsStore settings, Func<bool> gamingActive)
    {
        _settings = settings;
        _gamingActive = gamingActive;
    }

    public bool GamingActive() => _gamingActive();

    /// <summary>Null when work is allowed, else the <see cref="GameDiscoverStatus"/> that explains why not.</summary>
    public string? Check()
    {
        // Hard-off wins over everything, including a missing opt-in answer.
        if (_gamingActive()) return GameDiscoverStatus.GamingActive;
        if (!_settings.Enabled) return GameDiscoverStatus.Disabled;
        return null;
    }

    public static string MessageFor(string status) => status switch
    {
        GameDiscoverStatus.Disabled => "Game Discover is off. Turn it on in Gaming settings to contact the Steam store.",
        GameDiscoverStatus.GamingActive => "Discover is paused while a game is running.",
        GameDiscoverStatus.NotAvailable => "This store has no official public catalogue API, so it is not available.",
        _ => "Discover could not reach the store."
    };
}
