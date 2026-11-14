using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;

namespace PaRuX.Views;

/// <summary>
/// The Games library grouped by platform, source or completion status (Playnite style): a heading per group, then that group's own
/// virtualized <see cref="GameTileGrid"/>. Every grid only realizes the tiles near the viewport (they all scroll in the page's
/// ScrollViewer), so a 2,000-title library costs the same grouped or not. Titles keep the order of the sorted list inside a group.
/// Arrow / D-pad / Home / End / Page keys move across groups through <see cref="GameLibraryGrouping.Move"/> (pure, unit-tested).
/// Sections are reused by group key when the list is refreshed (search, filters), so tiles are rebound, not rebuilt.
/// </summary>
internal sealed class GroupedGameLibrary : StackPanel, IGameTileSurface
{
    private sealed class Section
    {
        public required string Key { get; init; }
        public required TextBlock Header { get; init; }
        public required GameTileGrid Grid { get; init; }
    }

    private readonly GameTileGrid.Presentation _presentation;
    private readonly string _mode;
    private readonly Dictionary<string, Section> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _groupOfTitle = new(StringComparer.Ordinal);
    private List<Section> _ordered = new();
    private List<GameTitleDto> _titles = new();
    private ISet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
    private string? _openKey;
    private bool _console;
    private bool _selectMode;
    private string _coverSize = GameCoverSizes.Medium;

    public event Action<GameTitleDto>? OpenRequested;
    public event Action<GameTitleDto>? PlayRequested;
    public event Action<GameTitleDto>? SelectionToggled;

    internal GroupedGameLibrary(GameTileGrid.Presentation presentation, string mode)
    {
        _presentation = presentation;
        _mode = GameGroupModes.Normalize(mode);
        Spacing = 14;
        ActualThemeChanged += (_, _) => { foreach (Section section in _sections.Values) PaintHeader(section.Header); };
        KeyDown += OnKeyDown;
    }

    public FrameworkElement View => this;
    public int Count => _titles.Count;

    public void SetConsole(bool enabled)
    {
        _console = enabled;
        foreach (Section section in _sections.Values)
        {
            section.Grid.SetConsole(enabled);
            section.Header.FontSize = enabled ? 24 : 17;
        }
    }

    public void SetSelectMode(bool enabled)
    {
        _selectMode = enabled;
        foreach (Section section in _sections.Values) section.Grid.SetSelectMode(enabled);
    }

    public void SetCoverSize(string size)
    {
        _coverSize = GameCoverSizes.Normalize(size);
        foreach (Section section in _sections.Values) section.Grid.SetCoverSize(_coverSize);
    }

    public void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        _titles = new List<GameTitleDto>(titles);
        IReadOnlyList<GameTileGroup> groups = GameLibraryGrouping.Build(_titles, _mode);

        _groupOfTitle.Clear();
        var ordered = new List<Section>(groups.Count);
        foreach (GameTileGroup group in groups)
        {
            Section section = _sections.TryGetValue(group.Key, out Section? existing) ? existing : CreateSection(group.Key);
            string label = LabelOf(group);
            section.Header.Text = label + " · " + group.Titles.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);
            AutomationProperties.SetName(section.Header, section.Header.Text);
            section.Grid.Render(group.Titles, selected, openKey);
            foreach (GameTitleDto title in group.Titles) _groupOfTitle[title.TitleKey] = group.Key;
            ordered.Add(section);
        }

        foreach (string stale in _sections.Keys.Where(k => !groups.Any(g => string.Equals(g.Key, k, StringComparison.OrdinalIgnoreCase))).ToList())
            _sections.Remove(stale);

        // Only touch the visual tree when the sequence of sections changed; a refresh of the same groups keeps every tile and the scroll offset.
        bool same = ordered.Count == _ordered.Count && ordered.Zip(_ordered).All(pair => ReferenceEquals(pair.First, pair.Second));
        _ordered = ordered;
        if (same && Children.Count == ordered.Count * 2) return;
        Children.Clear();
        foreach (Section section in ordered)
        {
            Children.Add(section.Header);
            Children.Add(section.Grid);
        }
    }

    public void RefreshState(ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        foreach (Section section in _ordered) section.Grid.RefreshState(selected, openKey);
    }

    public void UpdateTitle(GameTitleDto title)
    {
        if (!_groupOfTitle.TryGetValue(title.TitleKey, out string? oldKey)) return;
        int index = _titles.FindIndex(t => string.Equals(t.TitleKey, title.TitleKey, StringComparison.Ordinal));
        if (index >= 0) _titles[index] = title;
        // A title that moved to another group (its status or source changed) needs the groups rebuilt; otherwise rebind in place.
        if (!string.Equals(GameLibraryGrouping.KeyOf(title, _mode), oldKey, StringComparison.OrdinalIgnoreCase))
        {
            Render(_titles, _selected, _openKey);
            return;
        }
        if (_sections.TryGetValue(oldKey, out Section? section)) section.Grid.UpdateTitle(title);
    }

    public bool FocusTile(string key) =>
        _groupOfTitle.TryGetValue(key, out string? group) && _sections.TryGetValue(group, out Section? section) && section.Grid.FocusTile(key);

    public bool FocusFirst() => _ordered.Count > 0 && _ordered[0].Grid.FocusIndex(0);

    private Section CreateSection(string key)
    {
        var header = new TextBlock
        {
            FontSize = _console ? 24 : 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            IsTextScaleFactorEnabled = true
        };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(header, "GameHome_Group_" + IdOf(key));
        PaintHeader(header);

        var grid = new GameTileGrid(_presentation) { HandlesNavigationKeys = false };
        AutomationProperties.SetAutomationId(grid, "GameHome_Grid_" + IdOf(key));
        grid.SetConsole(_console);
        grid.SetSelectMode(_selectMode);
        grid.SetCoverSize(_coverSize);
        grid.OpenRequested += t => OpenRequested?.Invoke(t);
        grid.PlayRequested += t => PlayRequested?.Invoke(t);
        grid.SelectionToggled += t => SelectionToggled?.Invoke(t);

        var section = new Section { Key = key, Header = header, Grid = grid };
        _sections[key] = section;
        return section;
    }

    private void PaintHeader(TextBlock header) => header.Foreground = GameUi.ThemeBrush(header, "PaRuXTextPrimaryBrush");

    private static string IdOf(string key)
    {
        if (key.Length == 0) return "unknown";
        var chars = key.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars);
    }

    private string LabelOf(GameTileGroup group)
    {
        switch (_mode)
        {
            case GameGroupModes.Status:
                return GameUi.StatusLabel(group.Key);
            case GameGroupModes.Platform:
                return group.Key.Length == 0 ? GameUi.T("Other platform", "Altra piattaforma") : group.Key;
            case GameGroupModes.Source:
                if (group.Key.Length == 0 || group.Titles.Count == 0 || GameLibraryGrouping.PrimaryInstallation(group.Titles[0]) is not { } installation)
                    return GameUi.T("Unknown source", "Origine sconosciuta");
                return group.Key switch
                {
                    GameInstallationSources.Manual => GameUi.T("On this PC", "Su questo PC"),
                    GameInstallationSources.Emulator => GameUi.T("Emulator", "Emulatore"),
                    _ => GameUi.SourceLabel(installation),
                };
            default:
                return string.Empty;
        }
    }

    /// <summary>Keyboard / D-pad movement across the groups. Only keys pressed on a tile's title button are handled.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_selectMode || e.OriginalSource is not ButtonBase source) return;
        if (GameTileGrid.DirectionOf(e.Key) is not { } direction) return;
        int group = -1, index = -1;
        for (int i = 0; i < _ordered.Count && index < 0; i++)
        {
            index = _ordered[i].Grid.IndexOfSource(source);
            if (index >= 0) group = i;
        }
        if (group < 0) return;
        (int nextGroup, int nextIndex) = GameLibraryGrouping.Move(
            _ordered.Select(s => s.Grid.Count).ToList(), _ordered[group].Grid.ColumnCount, group, index, direction);
        if (nextGroup < 0 || (nextGroup == group && nextIndex == index)) return;
        if (_ordered[nextGroup].Grid.FocusIndex(nextIndex)) e.Handled = true;
    }
}
