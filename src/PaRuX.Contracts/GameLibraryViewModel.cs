namespace PaRuX.Contracts;

/// <summary>Cover size choices of the Gaming library grid (remembered per user). Unknown values mean medium.</summary>
public static class GameCoverSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";

    public static readonly string[] All = { Small, Medium, Large };

    public static string Normalize(string? value)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is Small or Large ? v : Medium;
    }

    /// <summary>Factor applied to the cover width/height of a grid tile (the tile text keeps its size).</summary>
    public static double Scale(string? value) => Normalize(value) switch
    {
        Small => 0.75,
        Large => 1.3,
        _ => 1.0,
    };
}

/// <summary>How the Gaming library grid is grouped (remembered per user). Unknown values mean no grouping.</summary>
public static class GameGroupModes
{
    public const string None = "none";
    public const string Platform = "platform";
    public const string Source = "source";
    public const string Status = "status";

    public static readonly string[] All = { None, Platform, Source, Status };

    public static string Normalize(string? value)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is Platform or Source or Status ? v : None;
    }
}

/// <summary>One section of a grouped library. <see cref="Key"/> is empty for the "unknown" bucket (no platform / no status).</summary>
public sealed record GameTileGroup(string Key, IReadOnlyList<GameTitleDto> Titles);

/// <summary>
/// Pure grouping and keyboard rules of the grouped Gaming library: every title lands in exactly one group, groups are ordered
/// deterministically, titles keep the order of the sorted list they came from, and arrow / Home / End / Page movement crosses
/// group boundaries. Each group is shown by its own virtualized grid, so grouping never realizes more tiles than the viewport needs.
/// </summary>
public static class GameLibraryGrouping
{
    private static readonly string[] SourceOrder =
    {
        GameInstallationSources.Steam, GameInstallationSources.Epic, GameInstallationSources.Gog, GameInstallationSources.Ea,
        GameInstallationSources.BattleNet, GameInstallationSources.Ubisoft, GameInstallationSources.Xbox,
        GameInstallationSources.Manual, GameInstallationSources.Emulator,
        GameInstallationSources.Playnite, GameInstallationSources.Heroic, GameInstallationSources.Lutris,
    };

    private static readonly string[] StatusOrder =
    {
        GameCompletionStatuses.Playing, GameCompletionStatuses.Backlog, GameCompletionStatuses.OnHold,
        GameCompletionStatuses.Completed, GameCompletionStatuses.Dropped,
    };

    /// <summary>The installation that represents a title in a group: the default one, else an installed one, else the first.</summary>
    public static GameInstallationDto? PrimaryInstallation(GameTitleDto title)
    {
        if (title.Installations.Count == 0) return null;
        if (!string.IsNullOrEmpty(title.DefaultInstallationId)
            && title.Installations.FirstOrDefault(i => i.InstallationId == title.DefaultInstallationId) is { } preferred)
            return preferred;
        return title.Installations.FirstOrDefault(i => i.InstallState == GameInstallStates.Installed) ?? title.Installations[0];
    }

    public static string KeyOf(GameTitleDto title, string mode)
    {
        switch (GameGroupModes.Normalize(mode))
        {
            case GameGroupModes.Platform:
                return (PrimaryInstallation(title)?.Platform ?? string.Empty).Trim();
            case GameGroupModes.Source:
                return PrimaryInstallation(title)?.Source ?? string.Empty;
            case GameGroupModes.Status:
                string status = GameCompletionStatuses.Normalize(title.Status);
                return GameCompletionStatuses.IsValid(status) ? status : string.Empty;
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Splits <paramref name="titles"/> into groups. With no grouping there is one group holding everything. Empty groups are never
    /// returned. Order: sources by store order, statuses by progress (playing first), platforms alphabetically; the unknown bucket is last.
    /// </summary>
    public static IReadOnlyList<GameTileGroup> Build(IReadOnlyList<GameTitleDto> titles, string mode)
    {
        mode = GameGroupModes.Normalize(mode);
        if (mode == GameGroupModes.None)
            return titles.Count == 0 ? Array.Empty<GameTileGroup>() : new[] { new GameTileGroup(string.Empty, titles) };

        var buckets = new Dictionary<string, List<GameTitleDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (GameTitleDto title in titles)
        {
            string key = KeyOf(title, mode);
            if (!buckets.TryGetValue(key, out List<GameTitleDto>? list)) buckets[key] = list = new List<GameTitleDto>();
            list.Add(title);
        }

        IEnumerable<string> ordered = mode switch
        {
            GameGroupModes.Source => buckets.Keys.OrderBy(k => RankOf(k, SourceOrder)).ThenBy(k => k, StringComparer.OrdinalIgnoreCase),
            GameGroupModes.Status => buckets.Keys.OrderBy(k => RankOf(k, StatusOrder)).ThenBy(k => k, StringComparer.OrdinalIgnoreCase),
            _ => buckets.Keys.OrderBy(k => k.Length == 0 ? 1 : 0).ThenBy(k => k, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.Select(k => new GameTileGroup(k, buckets[k])).ToList();
    }

    private static int RankOf(string key, string[] order)
    {
        if (key.Length == 0) return int.MaxValue;
        int index = Array.IndexOf(order, key);
        return index < 0 ? order.Length : index;
    }

    /// <summary>
    /// Next focused tile as (group, index in group) for keyboard / D-pad movement across groups that all use <paramref name="columns"/>
    /// columns. Left/Right continue into the neighbouring group; Up/Down enter the neighbouring group in the same column (clamped to
    /// its last tile); Down from a short last row lands on the group's last tile; Home/End go to the first/last tile of the library;
    /// Page keys move three rows. Movement stops at the first and last tile. (-1, -1) when there is nothing to focus.
    /// </summary>
    public static (int Group, int Index) Move(IReadOnlyList<int> sizes, int columns, int group, int index, GameLibraryHome.Direction direction)
    {
        int last = sizes.Count - 1;
        while (last >= 0 && sizes[last] <= 0) last--;
        if (last < 0) return (-1, -1);
        columns = Math.Max(1, columns);
        group = Math.Clamp(group, 0, sizes.Count - 1);
        if (sizes[group] <= 0) return (-1, -1);
        index = Math.Clamp(index, 0, sizes[group] - 1);

        switch (direction)
        {
            case GameLibraryHome.Direction.First:
                return (NextNonEmpty(sizes, -1, 1), 0);
            case GameLibraryHome.Direction.Last:
                return (last, sizes[last] - 1);
            case GameLibraryHome.Direction.Left:
                if (index > 0) return (group, index - 1);
                return NextNonEmpty(sizes, group, -1) is var prev && prev >= 0 ? (prev, sizes[prev] - 1) : (group, index);
            case GameLibraryHome.Direction.Right:
                if (index < sizes[group] - 1) return (group, index + 1);
                return NextNonEmpty(sizes, group, 1) is var next && next >= 0 ? (next, 0) : (group, index);
            case GameLibraryHome.Direction.Up:
            {
                if (index - columns >= 0) return (group, index - columns);
                if (NextNonEmpty(sizes, group, -1) is var above && above >= 0)
                {
                    int lastRowStart = (sizes[above] - 1) / columns * columns;
                    return (above, Math.Min(lastRowStart + index % columns, sizes[above] - 1));
                }
                return (group, index);
            }
            case GameLibraryHome.Direction.Down:
            {
                if (index + columns <= sizes[group] - 1) return (group, index + columns);
                bool onLastRow = index / columns == (sizes[group] - 1) / columns;
                if (!onLastRow) return (group, sizes[group] - 1);
                if (NextNonEmpty(sizes, group, 1) is var below && below >= 0)
                    return (below, Math.Min(index % columns, sizes[below] - 1));
                return (group, index);
            }
            case GameLibraryHome.Direction.PageUp:
            case GameLibraryHome.Direction.PageDown:
            {
                GameLibraryHome.Direction step = direction == GameLibraryHome.Direction.PageUp
                    ? GameLibraryHome.Direction.Up : GameLibraryHome.Direction.Down;
                (int g, int i) = (group, index);
                for (int row = 0; row < 3; row++) (g, i) = Move(sizes, columns, g, i, step);
                return (g, i);
            }
            default:
                return (group, index);
        }
    }

    private static int NextNonEmpty(IReadOnlyList<int> sizes, int from, int step)
    {
        for (int g = from + step; g >= 0 && g < sizes.Count; g += step)
            if (sizes[g] > 0) return g;
        return -1;
    }
}
