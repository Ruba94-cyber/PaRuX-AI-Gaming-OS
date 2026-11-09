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
    public const int MaxCollections = 12;
    public const int MaxCollectionLength = 40;
    public const int MaxTagLength = 40;
    public const int MaxTitles = 20_000;
    /// <summary>One observed session cannot add more than this (a clock jump must not poison the totals).</summary>
    public static readonly TimeSpan MaxSession = TimeSpan.FromDays(2);

    private sealed record FileModel(int Version, Dictionary<string, GameTitleState> Titles);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _leasePath;
    private Dictionary<string, GameTitleState> _titles = new(StringComparer.Ordinal);
    private string? _loadError;

    internal static string DefaultPath => Path.Combine(PaRuXDataRoot.Path, "server", "game-titles.json");

    public GameTitleStore() : this(DefaultPath) { }

    public GameTitleStore(string path)
    {
        _path = Path.GetFullPath(path);
        _leasePath = _path + ".write-lock";
        Load();
    }

    public string? LoadError { get { lock (_gate) return _loadError; } }

    public IReadOnlyDictionary<string, GameTitleState> Snapshot()
    {
        lock (_gate)
        {
            // Refresh a second Core's changes on a read, not just when this Core edits.
            // A corrupt file must not be represented as the previous valid snapshot.
            using var lease = AcquireLease();
            Load();
            if (_loadError is not null) throw new InvalidDataException(_loadError);
            return _titles.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.Ordinal);
        }
    }

    public bool SetFavorite(string titleKey, bool favorite) => Mutate(titleKey, s => s.Favorite = favorite);

    /// <returns>False when the tag list is invalid (nothing is changed).</returns>
    public bool SetTags(string titleKey, IEnumerable<string>? tags)
    {
        List<string>? cleaned = CleanList(tags, MaxTagLength, MaxTags);
        return cleaned is not null && Mutate(titleKey, s => s.Tags = cleaned);
    }

    public bool SetCollections(string titleKey, IEnumerable<string>? collections)
    {
        List<string>? cleaned = CleanList(collections, MaxCollectionLength, MaxCollections);
        return cleaned is not null && Mutate(titleKey, s => s.Collections = cleaned);
    }

    public bool SetHidden(string titleKey, bool hidden) => Mutate(titleKey, s => s.Hidden = hidden);

    /// <returns>False for an unknown status label (nothing is changed).</returns>
    public bool SetStatus(string titleKey, string? status) =>
        GameCompletionStatuses.IsValid(status) &&
        Mutate(titleKey, s => s.Status = GameCompletionStatuses.Normalize(status));

    /// <returns>False when the score is outside 0-10 (nothing is changed).</returns>
    public bool SetScore(string titleKey, int score) =>
        GameCompletionStatuses.IsValidScore(score) && Mutate(titleKey, s => s.Score = score);

    /// <summary>Trim, drop empties, de-duplicate case-insensitively; null when any item or the count is invalid.</summary>
    private static List<string>? CleanList(IEnumerable<string>? items, int maxLength, int maxCount)
    {
        var cleaned = new List<string>();
        foreach (string raw in items ?? Array.Empty<string>())
        {
            string item = (raw ?? string.Empty).Trim();
            if (item.Length == 0) continue;
            if (item.Length > maxLength || item.Any(char.IsControl)) return null;
            if (!cleaned.Contains(item, StringComparer.OrdinalIgnoreCase)) cleaned.Add(item);
        }
        return cleaned.Count > maxCount ? null : cleaned;
    }

    /// <summary>
    /// Applies the same favourite/hidden/tag/collection edit to up to <see cref="BulkGameTitleRequest.MaxKeys"/>
    /// titles in ONE atomic write. Everything is validated first; on any invalid input or failed write nothing changes.
    /// </summary>
    /// <returns>The number of distinct titles updated, or null when the request is invalid or could not be saved.</returns>
    public int? BulkUpdate(IEnumerable<string>? titleKeys, bool? favorite, bool? hidden,
        IEnumerable<string>? tags, IEnumerable<string>? collections)
    {
        if (titleKeys is null) return null;
        var keys = new List<string>();
        foreach (string key in titleKeys)
        {
            if (!GameTitleEndpoints.ValidTitleKey(key)) return null;
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
            if (keys.Count > BulkGameTitleRequest.MaxKeys) return null;
        }
        if (keys.Count == 0) return null;
        List<string>? cleanTags = null, cleanCollections = null;
        if (tags is not null && (cleanTags = CleanList(tags, MaxTagLength, MaxTags)) is null) return null;
        if (collections is not null && (cleanCollections = CleanList(collections, MaxCollectionLength, MaxCollections)) is null) return null;
        if (favorite is null && hidden is null && cleanTags is null && cleanCollections is null) return null;
        lock (_gate)
        {
            if (_loadError is not null) return null;
            try
            {
                using var lease = AcquireLease();
                Load();
                if (_loadError is not null) return null;
                var next = new Dictionary<string, GameTitleState>(_titles, StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    GameTitleState updated;
                    if (next.TryGetValue(key, out GameTitleState? existing)) updated = Clone(existing);
                    else
                    {
                        if (next.Count >= MaxTitles) return null;
                        updated = new GameTitleState { AddedUtc = DateTime.UtcNow };
                    }
                    if (favorite is { } f) updated.Favorite = f;
                    if (hidden is { } h) updated.Hidden = h;
                    if (cleanTags is not null) updated.Tags = new List<string>(cleanTags);
                    if (cleanCollections is not null) updated.Collections = new List<string>(cleanCollections);
                    next[key] = updated;
                }
                if (!Persist(next)) return null;
                _titles = next;
                return keys.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
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
            try
            {
                using var lease = AcquireLease();
                Load();
                if (_loadError is not null) return false; // a different Core may have corrupted the file
                return MutateUnderLease(titleKey, change);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private bool MutateUnderLease(string titleKey, Action<GameTitleState> change)
    {
            if (!_titles.TryGetValue(titleKey, out GameTitleState? state))
            {
                if (_titles.Count >= MaxTitles) return false;
                state = new GameTitleState { AddedUtc = DateTime.UtcNow };
            }
            var updated = Clone(state);
            change(updated);
            var next = new Dictionary<string, GameTitleState>(_titles, StringComparer.Ordinal) { [titleKey] = updated };
            if (!Persist(next)) return false;
            _titles = next;
            return true;
    }

    /// <summary>OS file-sharing lock: distinct Core processes cannot overwrite each other's overlay updates.</summary>
    private FileStream AcquireLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_leasePath)!);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try { return new FileStream(_leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { Thread.Sleep(20); }
        }
        throw new IOException("Could not acquire the game title overlay write lease.");
    }

    private bool Persist(Dictionary<string, GameTitleState> titles)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new FileModel(1, titles), JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _loadError = null;
            _titles = new Dictionary<string, GameTitleState>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length > 16 * 1024 * 1024) { _loadError = "The title state file is too large."; return; }
                FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (model is null || model.Version != 1 || model.Titles is null || model.Titles.Count > MaxTitles)
                { _loadError = "The title state file has an unsupported format."; return; }
                if (model.Titles.Any(p => p.Value is null ||
                    string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 300 || p.Key.Any(char.IsControl) ||
                    p.Value.PlaySeconds < 0 || p.Value.Tags is null || p.Value.Tags.Count > MaxTags ||
                    p.Value.Collections is null || p.Value.Collections.Count > MaxCollections ||
                    p.Value.Tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > MaxTagLength || x.Any(char.IsControl)) ||
                    p.Value.Collections.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > MaxCollectionLength || x.Any(char.IsControl))))
                { _loadError = "The title state file has invalid entries."; return; }
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
        Hidden = s.Hidden,
        AddedUtc = s.AddedUtc,
        Tags = new List<string>(s.Tags ?? new List<string>()),
        Collections = new List<string>(s.Collections ?? new List<string>()),
        PlaySeconds = s.PlaySeconds,
        LastPlayedUtc = s.LastPlayedUtc,
        DefaultInstallationId = s.DefaultInstallationId,
        Status = s.Status ?? string.Empty,
        Score = s.Score,
    };
}
