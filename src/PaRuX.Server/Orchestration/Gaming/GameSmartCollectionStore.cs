using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Saved smart collections in one small atomic JSON file. A damaged file means none; reading never overwrites it.</summary>
public sealed class GameSmartCollectionStore
{
    internal const int MaximumCollections = 30;
    private const long MaxFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<GameSmartCollection> _items = new();

    public GameSmartCollectionStore() : this(PaRuXDataRoot.Combine("server", "game-smart-collections.json")) { }

    public GameSmartCollectionStore(string path)
    {
        _path = path;
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes)
                    _items = (JsonSerializer.Deserialize<List<GameSmartCollection>>(File.ReadAllText(_path), JsonOptions) ?? new())
                        .Where(c => GameSmartCollectionRules.Validate(c) is null && !string.IsNullOrWhiteSpace(c.Id))
                        .Take(MaximumCollections).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    public IReadOnlyList<GameSmartCollection> List() { lock (_gate) return _items.Select(Copy).ToList(); }

    /// <summary>Creates (empty Id) or replaces (known Id) a collection. Returns the saved copy, or the reason.</summary>
    public (GameSmartCollection? Saved, string? Error) Save(GameSmartCollection input)
    {
        string? error = GameSmartCollectionRules.Validate(input);
        if (error != null) return (null, error);
        lock (_gate)
        {
            string name = input.Name.Trim();
            bool isNew = string.IsNullOrWhiteSpace(input.Id) || _items.All(c => c.Id != input.Id);
            if (isNew && _items.Count >= MaximumCollections) return (null, "At most " + MaximumCollections + " smart collections.");
            if (_items.Any(c => c.Id != input.Id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, "A smart collection with this name already exists.");
            GameSmartCollection saved = Copy(input);
            saved.Name = name;
            saved.Id = isNew ? Guid.NewGuid().ToString("N")[..12] : input.Id;
            var next = _items.Where(c => c.Id != saved.Id).Append(saved).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Persist(next) ? (Copy(saved), null) : (null, "The smart collection could not be saved.");
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_items.All(c => c.Id != id)) return false;
            return Persist(_items.Where(c => c.Id != id).ToList());
        }
    }

    private bool Persist(List<GameSmartCollection> next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _items = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static GameSmartCollection Copy(GameSmartCollection c) => new()
    {
        Id = c.Id, Name = c.Name, MatchAll = c.MatchAll,
        Rules = c.Rules.Select(r => new GameSmartRule { Field = r.Field, Op = r.Op, Value = r.Value }).ToList(),
    };
}
