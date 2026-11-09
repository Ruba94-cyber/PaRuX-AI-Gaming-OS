namespace PaRuX.Contracts;

/// <summary>The side-panel narrowing of the Games home. All fields optional; they combine with AND.</summary>
public sealed record GameHomeFilter
{
    /// <summary>Empty (all) or a <see cref="GameInstallationSources"/> value.</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>Empty (all) or a genre label (case-insensitive).</summary>
    public string Genre { get; init; } = string.Empty;
    /// <summary>True: installed only. False: not installed only. Null: both.</summary>
    public bool? Installed { get; init; }
    public bool FavoritesOnly { get; init; }
    public bool RecentlyPlayedOnly { get; init; }
    public bool IsEmpty => Source.Length == 0 && Genre.Length == 0 && Installed is null && !FavoritesOnly && !RecentlyPlayedOnly;
}

/// <summary>Counts shown next to each side-filter entry. Counted over the visible (non-hidden) library, ignoring the filter itself.</summary>
public sealed record GameHomeFacets(
    int Total,
    IReadOnlyList<KeyValuePair<string, int>> Stores,
    IReadOnlyList<KeyValuePair<string, int>> Genres,
    int Installed,
    int NotInstalled,
    int Favorites,
    int RecentlyPlayed);

/// <summary>
/// Pure rules of the Playnite / Hydra style home: which title the hero shows, the side-filter facets and counts,
/// client-side filtering and sorting, and the grid arrow-key / controller movement. No UI types, so it is unit-tested.
/// </summary>
public static class GameLibraryHome
{
    /// <summary>"Recently played" means played within this many days.</summary>
    public const int RecentDays = 30;
    public const int MaxGenres = 12;

    private static readonly string[] StoreOrder =
    {
        GameInstallationSources.Steam, GameInstallationSources.Epic, GameInstallationSources.Gog, GameInstallationSources.Ea,
        GameInstallationSources.BattleNet, GameInstallationSources.Ubisoft, GameInstallationSources.Xbox,
        GameInstallationSources.Manual, GameInstallationSources.Emulator,
        GameInstallationSources.Playnite, GameInstallationSources.Heroic, GameInstallationSources.Lutris,
    };

    public static bool IsInstalled(GameTitleDto title) => title.Installations.Any(i => i.InstallState == GameInstallStates.Installed);

    public static bool IsRecent(GameTitleDto title, DateTime nowUtc) =>
        title.LastPlayedUtc is { } last && nowUtc - last.ToUniversalTime() <= TimeSpan.FromDays(RecentDays) && last.ToUniversalTime() <= nowUtc.AddMinutes(5);

    /// <summary>
    /// The hero: the most recently played visible title that can start now. Never a hidden title. Null when nothing has been
    /// played, so the home shows no hero rather than an invented one.
    /// </summary>
    public static GameTitleDto? PickHero(IEnumerable<GameTitleDto> titles) =>
        titles.Where(t => !t.Hidden && t.LastPlayedUtc.HasValue
                          && GameTitleLaunchPolicy.ChooseForQuickPlay(t) is { } c && GameTitleLaunchPolicy.CanStart(c))
              .OrderByDescending(t => t.LastPlayedUtc!.Value)
              .ThenByDescending(t => t.PlaySeconds)
              .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
              .FirstOrDefault();

    public static GameHomeFacets Facets(IEnumerable<GameTitleDto> titles, Func<string, IReadOnlyList<string>> genresOf, DateTime nowUtc)
    {
        var visible = titles.Where(t => !t.Hidden).ToList();
        var stores = new List<KeyValuePair<string, int>>();
        foreach (string source in StoreOrder)
        {
            int n = visible.Count(t => t.Installations.Any(i => i.Source == source));
            if (n > 0) stores.Add(new(source, n));
        }
        var genreCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (GameTitleDto t in visible)
            foreach (string g in genresOf(t.TitleKey).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase))
                genreCounts[g] = genreCounts.GetValueOrDefault(g) + 1;
        var genres = genreCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxGenres).Select(p => new KeyValuePair<string, int>(p.Key, p.Value)).ToList();
        int installed = visible.Count(IsInstalled);
        return new GameHomeFacets(visible.Count, stores, genres, installed, visible.Count - installed,
            visible.Count(t => t.Favorite), visible.Count(t => IsRecent(t, nowUtc)));
    }

    public static IReadOnlyList<GameTitleDto> Apply(IEnumerable<GameTitleDto> titles, GameHomeFilter filter, string? sort,
        Func<string, IReadOnlyList<string>> genresOf, DateTime nowUtc)
    {
        IEnumerable<GameTitleDto> q = titles;
        if (filter.Source.Length > 0) q = q.Where(t => t.Installations.Any(i => i.Source == filter.Source));
        if (filter.Genre.Length > 0)
            q = q.Where(t => genresOf(t.TitleKey).Any(g => string.Equals(g.Trim(), filter.Genre, StringComparison.CurrentCultureIgnoreCase)));
        if (filter.Installed is { } installed) q = q.Where(t => IsInstalled(t) == installed);
        if (filter.FavoritesOnly) q = q.Where(t => t.Favorite);
        if (filter.RecentlyPlayedOnly) q = q.Where(t => IsRecent(t, nowUtc));
        IOrderedEnumerable<GameTitleDto> ordered = sort switch
        {
            GameTitleSorts.LastPlayed => q.OrderByDescending(t => t.LastPlayedUtc.HasValue).ThenByDescending(t => t.LastPlayedUtc),
            GameTitleSorts.PlayTime => q.OrderByDescending(t => t.PlaySeconds),
            GameTitleSorts.RecentlyAdded => q.OrderByDescending(t => t.AddedUtc.HasValue).ThenByDescending(t => t.AddedUtc),
            GameTitleSorts.Score => q.OrderByDescending(t => t.Score),
            _ => q.OrderBy(t => 0),
        };
        return ordered.ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TitleKey, StringComparer.Ordinal).ToList();
    }

    public enum Direction { Left, Right, Up, Down, First, Last, PageUp, PageDown }

    /// <summary>
    /// Next focused tile index for arrow keys / D-pad in a grid of <paramref name="columns"/> columns. Up/Down stop at the
    /// edge (a controller must not jump to the far side by accident). -1 when the grid is empty.
    /// </summary>
    public static int Move(int current, int count, int columns, Direction direction)
    {
        if (count <= 0) return -1;
        columns = Math.Max(1, columns);
        int i = Math.Clamp(current, 0, count - 1);
        return direction switch
        {
            Direction.Left => Math.Max(0, i - 1),
            Direction.Right => Math.Min(count - 1, i + 1),
            Direction.Up => i - columns >= 0 ? i - columns : i,
            Direction.Down => i + columns <= count - 1 ? i + columns : i,
            Direction.First => 0,
            Direction.Last => count - 1,
            Direction.PageUp => Math.Max(0, i - columns * 3),
            Direction.PageDown => Math.Min(count - 1, i + columns * 3),
            _ => i,
        };
    }

    /// <summary>
    /// Stores whose discovery and launch layout is implemented from documentation and knowledge and has NOT been checked on a real
    /// install. The UI says so next to those titles instead of implying a verified integration.
    /// </summary>
    public static bool IsUnverifiedStore(string? source) =>
        source is GameInstallationSources.Ea or GameInstallationSources.BattleNet or GameInstallationSources.Ubisoft or GameInstallationSources.Xbox;

    public static string UnverifiedStoreNote(string? source, bool italian) => IsUnverifiedStore(source)
        ? italian
            ? "Il rilevamento di questo store è in sola lettura e non è ancora verificato su un'installazione reale: se manca un gioco, aggiungilo come copia locale. L'avvio passa dal protocollo ufficiale dello store."
            : "Discovery for this store is read-only and has not been verified on a real install yet: if a game is missing, add it as a local copy. Launch goes through the store's official protocol."
        : string.Empty;

    /// <summary>One-line, user-readable summary of the Quick Resume state. Never claims a resume that did not happen.</summary>
    public static string QuickResumeSummary(QuickResumeStatusDto status, bool italian)
    {
        int paused = status.Apps.Count(a => a.Suspended);
        int resumed = status.Recovery?.Count(r => r.Outcome == "resumed") ?? 0;
        int failed = status.Recovery?.Count(r => r.Outcome == "failed") ?? 0;
        var parts = new List<string>
        {
            paused == 0 ? (italian ? "Nessuna app in pausa" : "Nothing paused")
                        : (italian ? $"{paused} in pausa" : $"{paused} paused")
        };
        if (resumed > 0) parts.Add(italian ? $"{resumed} ripresi automaticamente all'avvio" : $"{resumed} resumed automatically at start-up");
        if (failed > 0) parts.Add(italian ? $"{failed} NON ripresi: riprendili da qui sotto" : $"{failed} could NOT be resumed: resume them below");
        return string.Join(" · ", parts);
    }
}
