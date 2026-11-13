namespace PaRuX.Contracts;

/// <summary>What a new title list means for the tile presenter that already shows the previous one.</summary>
public enum GameTileListChange
{
    /// <summary>Same titles, same order, nothing a tile shows has changed: leave the presenter alone.</summary>
    None,
    /// <summary>Same titles in the same order; only the listed tiles show something different. Rebind them in place.</summary>
    ContentOnly,
    /// <summary>The set or the order changed (search, filter, sort, import, remove): swap the items source.</summary>
    Reset,
}

/// <summary>Result of <see cref="GameTileListModel.Diff"/>.</summary>
public sealed record GameTileListDelta(GameTileListChange Change, IReadOnlyList<int> ChangedIndexes);

/// <summary>
/// Pure, UI-free rules behind the virtualized Gaming library grid: how a refreshed list differs from the one on screen,
/// the key to index map used for focus/scroll, and the column count of the uniform grid. No file or network access;
/// everything is O(n) over lightweight DTOs, so a 2,000-title library never rebuilds visuals just to find out nothing changed.
/// </summary>
public static class GameTileListModel
{
    /// <summary>
    /// True when two DTOs of the same title look identical on a tile (name, state badges, status, score, play history, the
    /// installation that Play would use and the source line). Anything not shown on a tile is ignored on purpose.
    /// </summary>
    public static bool SameFace(GameTitleDto a, GameTitleDto b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.TitleKey != b.TitleKey || a.Name != b.Name || a.Favorite != b.Favorite || a.Hidden != b.Hidden
            || a.Status != b.Status || a.Score != b.Score || a.PlaySeconds != b.PlaySeconds
            || a.LastPlayedUtc != b.LastPlayedUtc || a.DefaultInstallationId != b.DefaultInstallationId
            || a.Installations.Count != b.Installations.Count) return false;
        for (int i = 0; i < a.Installations.Count; i++)
        {
            GameInstallationDto x = a.Installations[i], y = b.Installations[i];
            if (x.InstallationId != y.InstallationId || x.Source != y.Source || x.Kind != y.Kind || x.Platform != y.Platform
                || x.InstallState != y.InstallState || x.EmulatorName != y.EmulatorName) return false;
        }
        return true;
    }

    /// <summary>Compares the list on screen with a freshly loaded one.</summary>
    public static GameTileListDelta Diff(IReadOnlyList<GameTitleDto> shown, IReadOnlyList<GameTitleDto> next)
    {
        if (shown.Count != next.Count) return new(GameTileListChange.Reset, Array.Empty<int>());
        List<int>? changed = null;
        for (int i = 0; i < next.Count; i++)
        {
            if (!string.Equals(shown[i].TitleKey, next[i].TitleKey, StringComparison.Ordinal))
                return new(GameTileListChange.Reset, Array.Empty<int>());
            if (!SameFace(shown[i], next[i])) (changed ??= new List<int>()).Add(i);
        }
        return changed is null
            ? new(GameTileListChange.None, Array.Empty<int>())
            : new(GameTileListChange.ContentOnly, changed);
    }

    /// <summary>Title key to position map (first occurrence wins), used to focus or scroll to a tile that may not be realized.</summary>
    public static Dictionary<string, int> IndexByKey(IReadOnlyList<GameTitleDto> titles)
    {
        var map = new Dictionary<string, int>(titles.Count, StringComparer.Ordinal);
        for (int i = 0; i < titles.Count; i++) map.TryAdd(titles[i].TitleKey, i);
        return map;
    }

    /// <summary>
    /// Items per row of a uniform grid that wraps at <paramref name="availableWidth"/>. Always at least one, so keyboard and
    /// controller movement never divides by zero before the first layout pass.
    /// </summary>
    public static int Columns(double availableWidth, double itemWidth, double spacing)
    {
        if (itemWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth)) return 1;
        double pitch = itemWidth + Math.Max(0, spacing);
        return Math.Max(1, (int)Math.Floor((Math.Max(availableWidth, itemWidth) + Math.Max(0, spacing)) / pitch));
    }
}

/// <summary>
/// Small least-recently-used cache with a hard capacity. Holds decoded cover bitmaps (or any other per-tile resource) so scrolling
/// back to a tile does not decode it again, while memory stays bounded no matter how large the library is.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public BoundedLruCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
    }

    public int Count => _map.Count;
    public int Capacity => _capacity;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _map.Remove(key);
        }
        var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
        _order.AddFirst(node);
        _map[key] = node;
        while (_map.Count > _capacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    public bool Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return false;
        _order.Remove(node);
        return true;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}

/// <summary>
/// Holds a value that is expensive to build from disk (for example the cover index) and rebuilds it only when its on-disk stamp
/// changes. The stamp itself is checked at most once per <c>recheckMilliseconds</c>, so asking for the value once per tile of
/// a 2,000-title library costs one stamp check, not 2,000 file reads.
/// </summary>
public sealed class StampedCache<T>
{
    private readonly Func<long> _stamp;
    private readonly Func<T> _load;
    private readonly Func<long> _nowMilliseconds;
    private readonly long _recheckMilliseconds;
    private readonly object _gate = new();
    private T _value = default!;
    private bool _loaded;
    private long _stampValue;
    private long _checkedAt;

    public StampedCache(Func<long> stamp, Func<T> load, Func<long> nowMilliseconds, long recheckMilliseconds = 1500)
    {
        _stamp = stamp;
        _load = load;
        _nowMilliseconds = nowMilliseconds;
        _recheckMilliseconds = recheckMilliseconds;
    }

    public T Get()
    {
        lock (_gate)
        {
            long now = _nowMilliseconds();
            if (_loaded && now - _checkedAt < _recheckMilliseconds) return _value;
            long stamp = _stamp();
            _checkedAt = now;
            if (!_loaded || stamp != _stampValue)
            {
                _value = _load();
                _stampValue = stamp;
                _loaded = true;
            }
            return _value;
        }
    }

    /// <summary>Forces the next <see cref="Get"/> to reload (call after this process wrote the file).</summary>
    public void Invalidate()
    {
        lock (_gate) _loaded = false;
    }
}
