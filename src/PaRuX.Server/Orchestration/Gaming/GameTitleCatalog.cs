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
        GameInstallationSources.Gog, GameInstallationSources.Emulator,
    };

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
                    ManualEntryKinds.Game, g.Name)));
        }
        foreach (ManualGameDto m in manual)
        {
            // A library entry started through an emulator is an emulated installation of the title, not a local copy.
            string entrySource = string.IsNullOrEmpty(m.EmulatorId) ? GameInstallationSources.Manual : GameInstallationSources.Emulator;
            installations.Add((KeyFor(m.Name, InstallationId(entrySource, m.Id)),
                new GameInstallationDto(InstallationId(entrySource, m.Id), entrySource, m.Id,
                    string.IsNullOrWhiteSpace(m.Kind) ? ManualEntryKinds.Game : m.Kind, m.Name)));
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
                s?.PlaySeconds ?? 0, s?.LastPlayedUtc, chosen, ordered));
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
        IEnumerable<GameTitleDto> titles, string? text, string? source, bool favoritesOnly, string? tag)
    {
        string needle = (text ?? string.Empty).Trim();
        return titles
            .Where(t => needle.Length == 0 || t.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .Where(t => string.IsNullOrEmpty(source) || t.Installations.Any(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)))
            .Where(t => !favoritesOnly || t.Favorite)
            .Where(t => string.IsNullOrWhiteSpace(tag) || t.Tags.Any(x => string.Equals(x, tag.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string DisplayName(GameInstallationDto[] ordered) =>
        ordered.Select(i => i.Name).OrderByDescending(n => n.Length).First();

    private static int PriorityOf(string source)
    {
        int index = Array.IndexOf(SourcePriority, source);
        return index < 0 ? SourcePriority.Length : index;
    }
}
