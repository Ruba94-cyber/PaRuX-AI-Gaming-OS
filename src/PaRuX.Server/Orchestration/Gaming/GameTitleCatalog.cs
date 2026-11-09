using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Builds the unified list: every discovered or manual entry becomes one installation, installations with
/// the same normalized name become one title, and the persisted user state is applied. Pure and deterministic.
/// </summary>
public static class GameTitleCatalog
{
    // When no default is chosen the title starts the way the user is most likely to expect:
    // their own entries first, then the stores, then emulation.
    private static readonly string[] SourcePriority =
    {
        GameInstallationSources.Manual, GameInstallationSources.Steam, GameInstallationSources.Epic,
        GameInstallationSources.Gog, GameInstallationSources.Ea, GameInstallationSources.BattleNet,
        GameInstallationSources.Ubisoft, GameInstallationSources.Xbox, GameInstallationSources.Emulator,
        GameInstallationSources.Playnite, GameInstallationSources.Heroic, GameInstallationSources.Lutris,
    };

    public const string PcPlatform = "PC";

    public static string InstallationId(string source, string sourceGameId) => source + ":" + sourceGameId;

    public static IReadOnlyList<GameTitleDto> Build(
        IEnumerable<DiscoveredGameDto> discovered,
        IEnumerable<ManualGameDto> manual,
        IReadOnlyDictionary<string, GameTitleState> state)
    {
        var installations = new List<(string Key, GameInstallationDto Installation)>();
        foreach (DiscoveredGameDto g in discovered)
        {
            string source = g.ProviderId.ToLowerInvariant();
            installations.Add((KeyFor(g.Name, InstallationId(source, g.ProviderGameId)),
                new GameInstallationDto(InstallationId(source, g.ProviderGameId), source, g.ProviderGameId,
                    ManualEntryKinds.Game, g.Name)
                {
                    Platform = PcPlatform,
                    InstallState = string.IsNullOrWhiteSpace(g.InstallState) ? GameInstallStates.Unknown : g.InstallState,
                }));
        }
        foreach (ManualGameDto m in manual)
        {
            // A library entry started through an emulator is an emulated installation of the title, not a local copy.
            string entrySource = string.IsNullOrEmpty(m.EmulatorId) ? GameInstallationSources.Manual : GameInstallationSources.Emulator;
            installations.Add((KeyFor(m.Name, InstallationId(entrySource, m.Id)),
                new GameInstallationDto(InstallationId(entrySource, m.Id), entrySource, m.Id,
                    string.IsNullOrWhiteSpace(m.Kind) ? ManualEntryKinds.Game : m.Kind, m.Name)
                {
                    EmulatorName = string.IsNullOrEmpty(m.EmulatorId)
                        ? string.Empty
                        : Emulators.EmulatorRegistry.Find(m.EmulatorId)?.Name ?? string.Empty,
                    Platform = !string.IsNullOrWhiteSpace(m.Platform) ? m.Platform
                        : string.IsNullOrEmpty(m.EmulatorId) ? PcPlatform
                        : Emulators.EmulatorRegistry.Find(m.EmulatorId)?.Platform ?? string.Empty,
                    // The owner registered this executable themselves; it is not probed here.
                    InstallState = GameInstallStates.Installed,
                }));
        }

        var titles = new List<GameTitleDto>();
        foreach (var group in installations.GroupBy(i => i.Key, StringComparer.Ordinal))
        {
            GameInstallationDto[] ordered = group.Select(g => g.Installation)
                .OrderBy(i => PriorityOf(i.Source)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.InstallationId, StringComparer.Ordinal).ToArray();
            state.TryGetValue(group.Key, out GameTitleState? s);
            // A saved default that no longer exists (uninstalled, removed) must not hide the title: fall back.
            string? chosen = s?.DefaultInstallationId is { } wanted && ordered.Any(i => i.InstallationId == wanted)
                ? wanted : ordered[0].InstallationId;
            titles.Add(new GameTitleDto(
                group.Key, DisplayName(ordered), s?.Favorite ?? false,
                (IReadOnlyList<string>?)s?.Tags ?? Array.Empty<string>(),
                s?.PlaySeconds ?? 0, s?.LastPlayedUtc, chosen, ordered)
                {
                    Collections = (IReadOnlyList<string>?)s?.Collections ?? Array.Empty<string>(),
                    Hidden = s?.Hidden ?? false,
                    AddedUtc = s?.AddedUtc,
                    Status = s?.Status ?? string.Empty,
                    Score = s?.Score ?? 0,
                });
        }
        return titles.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TitleKey, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Title key for a game name; an unnameable entry keeps its own identity instead of merging with others.</summary>
    public static string KeyFor(string? name, string fallbackInstallationId)
    {
        string key = GameLibraryQuery.NormalizeName(name);
        return key.Length >= 2 ? key : "id:" + fallbackInstallationId;
    }

    public static IReadOnlyList<GameTitleDto> Filter(
        IEnumerable<GameTitleDto> titles, string? text, string? source, bool favoritesOnly, string? tag, string? collection = null) =>
        Query(titles, new GameTitleQuery(text, source, favoritesOnly, tag, collection));

    /// <summary>Hidden titles: default excludes them, <c>include</c> returns hidden and visible, <c>only</c> just the hidden.</summary>
    public static class HiddenModes
    {
        public const string Exclude = "exclude";
        public const string Include = "include";
        public const string Only = "only";

        /// <summary>Accepts nothing/false (exclude), true/include (include) and only; null for anything else.</summary>
        public static string? Parse(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "false" or "exclude" => Exclude,
            "true" or "include" => Include,
            "only" => Only,
            _ => null,
        };
    }

    /// <summary>Filters then sorts. Pure; the default sort keeps the catalogue's own name order.</summary>
    public static IReadOnlyList<GameTitleDto> Query(IEnumerable<GameTitleDto> titles, GameTitleQuery query)
    {
        string needle = (query.Text ?? string.Empty).Trim();
        string? source = query.Source, tag = query.Tag, collection = query.Collection, platform = query.Platform;
        string hidden = HiddenModes.Parse(query.Hidden) ?? HiddenModes.Exclude;
        bool favoritesOnly = query.FavoritesOnly;
        IEnumerable<GameTitleDto> filtered = titles
            .Where(t => hidden == HiddenModes.Include || (hidden == HiddenModes.Only ? t.Hidden : !t.Hidden))
            .Where(t => string.IsNullOrWhiteSpace(platform) ||
                        t.Installations.Any(i => string.Equals(i.Platform, platform.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(t => query.Installed is null ||
                        t.Installations.Any(i => i.InstallState == GameInstallStates.Installed) == query.Installed)
            .Where(t => needle.Length == 0 || t.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .Where(t => string.IsNullOrEmpty(source) || t.Installations.Any(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)))
            .Where(t => !favoritesOnly || t.Favorite)
            .Where(t => string.IsNullOrWhiteSpace(tag) || t.Tags.Any(x => string.Equals(x, tag.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(t => string.IsNullOrWhiteSpace(collection) || t.Collections.Any(x => string.Equals(x, collection.Trim(), StringComparison.OrdinalIgnoreCase)));
        return Sort(filtered, query.Sort);
    }

    /// <summary>Stable sort over the allowlist in <see cref="GameTitleSorts"/>; unknown or empty keeps name order.</summary>
    public static IReadOnlyList<GameTitleDto> Sort(IEnumerable<GameTitleDto> titles, string? sort)
    {
        static IOrderedEnumerable<GameTitleDto> ByName(IOrderedEnumerable<GameTitleDto> o) =>
            o.ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TitleKey, StringComparer.Ordinal);
        switch ((sort ?? string.Empty).Trim().ToLowerInvariant())
        {
            case GameTitleSorts.LastPlayed:
                return ByName(titles.OrderByDescending(t => t.LastPlayedUtc.HasValue).ThenByDescending(t => t.LastPlayedUtc)).ToArray();
            case GameTitleSorts.PlayTime:
                return ByName(titles.OrderByDescending(t => t.PlaySeconds)).ToArray();
            case GameTitleSorts.RecentlyAdded:
                return ByName(titles.OrderByDescending(t => t.AddedUtc.HasValue).ThenByDescending(t => t.AddedUtc)).ToArray();
            case GameTitleSorts.Score:
                return ByName(titles.OrderByDescending(t => t.Score)).ToArray();
            default:
                return titles.ToList();
        }
    }

    /// <summary>
    /// Continue/Recent shelf for a Gaming home: titles with a play history by most recently played, then
    /// by most played; titles with no history are never listed. Hidden titles are skipped. Pure.
    /// </summary>
    public static IReadOnlyList<GameTitleDto> Recent(IEnumerable<GameTitleDto> titles, int count = 10)
    {
        count = Math.Clamp(count, 0, 50);
        return titles
            .Where(t => !t.Hidden && (t.LastPlayedUtc.HasValue || t.PlaySeconds > 0))
            .OrderByDescending(t => t.LastPlayedUtc.HasValue)
            .ThenByDescending(t => t.LastPlayedUtc)
            .ThenByDescending(t => t.PlaySeconds)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.TitleKey, StringComparer.Ordinal)
            .Take(count).ToArray();
    }

    private static string DisplayName(GameInstallationDto[] ordered) =>
        ordered.Select(i => i.Name).OrderByDescending(n => n.Length).First();

    private static int PriorityOf(string source)
    {
        int index = Array.IndexOf(SourcePriority, source);
        return index < 0 ? SourcePriority.Length : index;
    }
}

/// <summary>Everything the title list can be narrowed or ordered by. All members are optional.</summary>
public sealed record GameTitleQuery(
    string? Text = null, string? Source = null, bool FavoritesOnly = false, string? Tag = null, string? Collection = null)
{
    public string? Platform { get; init; }
    public bool? Installed { get; init; }
    public string? Hidden { get; init; }
    public string? Sort { get; init; }
}
