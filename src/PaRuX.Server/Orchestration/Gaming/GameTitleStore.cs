using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Per-title user state (favorite, tags, play time, last played, preferred installation) as a small overlay
/// file beside the library. Atomic replace; a corrupt file is left untouched and the store starts empty in
/// memory (nothing is overwritten until the user changes something and the file parses again).
/// </summary>
public sealed class GameTitleStore
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 40;
    public const int MaxTitles = 20_000;
    /// <summary>One observed session cannot add more than this (a clock jump must not poison the totals).</summary>
    public static readonly TimeSpan MaxSession = TimeSpan.FromDays(2);

    private sealed record FileModel(int Version, Dictionary<string, GameTitleState> Titles);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, GameTitleState> _titles = new(StringComparer.Ordinal);
    private string? _loadError;

    internal static string DefaultPath => Path.Combine(PaRuXDataRoot.Path, "server", "game-titles.json");

    public GameTitleStore() : this(DefaultPath) { }

    public GameTitleStore(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string? LoadError { get { lock (_gate) return _loadError; } }

    public IReadOnlyDictionary<string, GameTitleState> Snapshot()
    {
        lock (_gate)
            return _titles.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.Ordinal);
    }

    public bool SetFavorite(string titleKey, bool favorite) => Mutate(titleKey, s => s.Favorite = favorite);

    /// <returns>False when the tag list is invalid (nothing is changed).</returns>
    public bool SetTags(string titleKey, IEnumerable<string>? tags)
    {
        var cleaned = new List<string>();
        foreach (string raw in tags ?? Array.Empty<string>())
        {
            string tag = (raw ?? string.Empty).Trim();
            if (tag.Length == 0) continue;
            if (tag.Length > MaxTagLength || tag.Any(char.IsControl)) return false;
            if (!cleaned.Contains(tag, StringComparer.OrdinalIgnoreCase)) cleaned.Add(tag);
        }
        if (cleaned.Count > MaxTags) return false;
        return Mutate(titleKey, s => s.Tags = cleaned);
    }

    public bool SetDefaultInstallation(string titleKey, string? installationId)
    {
        if (installationId is { Length: > 300 } || installationId?.Any(char.IsControl) == true) return false;
        return Mutate(titleKey, s => s.DefaultInstallationId = string.IsNullOrWhiteSpace(installationId) ? null : installationId);
    }

    /// <summary>Adds an observed session. Sessions of zero or negative length are ignored; very long ones are capped.</summary>
    public bool AddSession(string titleKey, DateTime startedUtc, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return false;
        if (duration > MaxSession) duration = MaxSession;
        DateTime end = startedUtc.ToUniversalTime() + duration;
        return Mutate(titleKey, s =>
        {
            s.PlaySeconds = checked(s.PlaySeconds + (long)duration.TotalSeconds);
            if (s.LastPlayedUtc is null || end > s.LastPlayedUtc) s.LastPlayedUtc = end;
        });
    }

    private bool Mutate(string titleKey, Action<GameTitleState> change)
    {
        if (string.IsNullOrWhiteSpace(titleKey) || titleKey.Length > 300 || titleKey.Any(char.IsControl)) return false;
        lock (_gate)
        {
            if (_loadError is not null) return false; // never overwrite a file we could not read
            if (!_titles.TryGetValue(titleKey, out GameTitleState? state))
            {
                if (_titles.Count >= MaxTitles) return false;
                state = new GameTitleState();
            }
            var updated = Clone(state);
            change(updated);
            var next = new Dictionary<string, GameTitleState>(_titles, StringComparer.Ordinal) { [titleKey] = updated };
            if (!Persist(next)) return false;
            _titles = next;
            return true;
        }
    }

    private bool Persist(Dictionary<string, GameTitleState> titles)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel(1, titles), JsonOptions));
            File.Move(temp, _path, overwrite: true);
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
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length > 16 * 1024 * 1024) { _loadError = "The title state file is too large."; return; }
                FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (model is null || model.Version != 1 || model.Titles is null || model.Titles.Count > MaxTitles)
                { _loadError = "The title state file has an unsupported format."; return; }
                _titles = new Dictionary<string, GameTitleState>(model.Titles, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _loadError = "The title state file could not be read.";
            }
        }
    }

    private static GameTitleState Clone(GameTitleState s) => new()
    {
        Favorite = s.Favorite,
        Tags = new List<string>(s.Tags ?? new List<string>()),
        PlaySeconds = s.PlaySeconds,
        LastPlayedUtc = s.LastPlayedUtc,
        DefaultInstallationId = s.DefaultInstallationId,
    };
}
