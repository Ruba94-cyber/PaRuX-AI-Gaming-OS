namespace PaRuX.Contracts;

/// <summary>
/// A named, saved combination of the Gaming library's search, sort and filters. Purely local UI state that is
/// persisted in the desktop settings; it carries no path, key or account data. All fields are optional strings so a
/// preset written by a newer build still loads.
/// </summary>
public sealed class GameFilterPreset
{
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>One of <see cref="GameTitleSorts"/> (empty = name order).</summary>
    public string Sort { get; set; } = string.Empty;
    /// <summary>Empty (all) or a <see cref="GameInstallationSources"/> value.</summary>
    public string Source { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    /// <summary>Empty (all), "installed" or "notinstalled".</summary>
    public string Installed { get; set; } = string.Empty;
    public bool FavoritesOnly { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public bool ShowHidden { get; set; }
}

/// <summary>Pure rules for the saved filter list: bounded, de-duplicated by name, every field sanitised.</summary>
public static class GameFilterPresets
{
    public const int MaxPresets = 12;
    public const int MaxNameLength = 40;

    private static readonly string[] Sources =
    {
        "", GameInstallationSources.Manual, GameInstallationSources.Steam, GameInstallationSources.Epic,
        GameInstallationSources.Gog, GameInstallationSources.Ea, GameInstallationSources.BattleNet,
        GameInstallationSources.Ubisoft, GameInstallationSources.Xbox, GameInstallationSources.Emulator,
    };

    private static string Clean(string? value, int max)
    {
        string s = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    /// <summary>Returns a sanitised copy, or null when the name is empty. Unknown sort/source/installed values fall back to "all".</summary>
    public static GameFilterPreset? Normalize(GameFilterPreset? preset)
    {
        if (preset is null) return null;
        string name = Clean(preset.Name, MaxNameLength);
        if (name.Length == 0) return null;
        string sort = (preset.Sort ?? string.Empty).Trim().ToLowerInvariant();
        string source = (preset.Source ?? string.Empty).Trim().ToLowerInvariant();
        string installed = (preset.Installed ?? string.Empty).Trim().ToLowerInvariant();
        return new GameFilterPreset
        {
            Name = name,
            Text = Clean(preset.Text, 200),
            Sort = GameTitleSorts.IsValid(sort) ? sort : string.Empty,
            Source = Sources.Contains(source) ? source : string.Empty,
            Platform = Clean(preset.Platform, 80),
            Installed = installed is "installed" or "notinstalled" ? installed : string.Empty,
            FavoritesOnly = preset.FavoritesOnly,
            Tag = Clean(preset.Tag, 80),
            Collection = Clean(preset.Collection, 80),
            ShowHidden = preset.ShowHidden,
        };
    }

    /// <summary>
    /// Adds or replaces (case-insensitive name) a preset. Returns the new list, or null when the preset is invalid or the
    /// list would exceed <see cref="MaxPresets"/>. The input list is never modified.
    /// </summary>
    public static List<GameFilterPreset>? Upsert(IEnumerable<GameFilterPreset>? existing, GameFilterPreset? preset)
    {
        GameFilterPreset? clean = Normalize(preset);
        if (clean is null) return null;
        var list = Sanitize(existing);
        int index = list.FindIndex(p => string.Equals(p.Name, clean.Name, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0) list[index] = clean;
        else if (list.Count >= MaxPresets) return null;
        else list.Add(clean);
        return list;
    }

    public static List<GameFilterPreset> Remove(IEnumerable<GameFilterPreset>? existing, string? name) =>
        Sanitize(existing).Where(p => !string.Equals(p.Name, (name ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();

    /// <summary>Drops invalid entries and duplicate names (first wins) and enforces the cap; used when loading old or hand-edited settings.</summary>
    public static List<GameFilterPreset> Sanitize(IEnumerable<GameFilterPreset>? presets)
    {
        var result = new List<GameFilterPreset>();
        foreach (GameFilterPreset? raw in presets ?? Array.Empty<GameFilterPreset>())
        {
            GameFilterPreset? clean = Normalize(raw);
            if (clean is null || result.Any(p => string.Equals(p.Name, clean.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
            result.Add(clean);
            if (result.Count >= MaxPresets) break;
        }
        return result;
    }
}
