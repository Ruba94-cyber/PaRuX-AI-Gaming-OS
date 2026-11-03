using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>Shared text and theme helpers of the Gaming home views (IT/EN strings, semantic brushes, title helpers).</summary>
internal static class GameUi
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    internal static string T(string en, string it) => Italian ? it : en;

    /// <summary>
    /// Semantic PaRuX brush for the element's actual theme (light, dark or Windows high contrast), looked up in the
    /// application's theme dictionaries so code-built views follow the same palette as the XAML views.
    /// </summary>
    internal static Brush ThemeBrush(FrameworkElement? owner, string key)
    {
        string theme;
        try { theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast" : ThemeName(owner); }
        catch (Exception) { theme = ThemeName(owner); }
        if (TryFind(Application.Current.Resources, key, theme, 0, out object? value) && value is Brush brush) return brush;
        return new SolidColorBrush(Colors.Gray);
    }

    private static string ThemeName(FrameworkElement? owner) =>
        (owner?.ActualTheme ?? (Application.Current.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark))
        == ElementTheme.Light ? "Light" : "Dark";

    private static bool TryFind(ResourceDictionary dictionary, string key, string theme, int depth, out object? value)
    {
        value = null;
        if (depth > 6) return false;
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out object? themed) && themed is ResourceDictionary themedDictionary
            && TryFind(themedDictionary, key, theme, depth + 1, out value)) return true;
        if (dictionary.TryGetValue(key, out value)) return true;
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            if (TryFind(merged, key, theme, depth + 1, out value)) return true;
        value = null;
        return false;
    }

    internal static Style? AccentButtonStyle =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) ? style as Style : null;

    /// <summary>Up to two letters for the cover fallback tile.</summary>
    internal static string Initials(string name)
    {
        var words = (name ?? string.Empty)
            .Split(new[] { ' ', '-', '_', ':', '.', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != default)
            .Take(2).ToArray();
        return words.Length == 0 ? "?" : new string(words).ToUpper(CultureInfo.CurrentCulture);
    }

    // The same safe selection is shared by tile Play and the detail panel.
    // It never rewrites the user's saved preferred installation.
    internal static GameInstallationDto? ChooseInstallation(GameTitleDto title) =>
        GameTitleLaunchPolicy.ChooseForQuickPlay(title);

    internal static bool CanStart(GameInstallationDto? installation) =>
        GameTitleLaunchPolicy.CanStart(installation);

    internal static string PlayLabel(GameInstallationDto? installation) =>
        installation?.Kind == ManualEntryKinds.Application ? T("Start", "Avvia") : T("Play", "Gioca");

    internal static string SourceLabel(GameInstallationDto i) => i.Source switch
    {
        GameInstallationSources.Steam => "Steam",
        GameInstallationSources.Epic => "Epic Games",
        GameInstallationSources.Gog => "GOG",
        GameInstallationSources.Emulator => string.IsNullOrWhiteSpace(i.EmulatorName)
            ? T("Emulator", "Emulatore")
            : T("Emulator · ", "Emulatore · ") + i.EmulatorName,
        _ => i.Kind == ManualEntryKinds.Application ? T("Application on this PC", "Applicazione su questo PC") : T("Copy on this PC", "Copia su questo PC"),
    };

    internal static string StateLabel(string state) => state switch
    {
        GameInstallStates.Installed => T("Installed", "Installato"),
        GameInstallStates.Updating => T("Updating", "In aggiornamento"),
        GameInstallStates.Incomplete => T("Incomplete", "Incompleto"),
        _ => T("State unknown", "Stato sconosciuto"),
    };

    internal static string ShortPlayTime(long seconds) =>
        seconds < 60 ? T("Not played yet", "Mai giocato")
        : seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60} min"
        : $"{seconds / 60} min";

    internal static string PlaySummary(GameTitleDto title)
    {
        string played = title.PlaySeconds < 60
            ? T("Not played yet", "Mai giocato")
            : T("Played ", "Giocato ") + ShortPlayTime(title.PlaySeconds);
        return title.LastPlayedUtc is { } last
            ? played + " · " + T("last ", "ultima volta ") + last.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)
            : played;
    }

    /// <summary>"Steam · PC", used as the quiet second line of a tile.</summary>
    internal static string SourceSummary(GameTitleDto title)
    {
        var sources = title.Installations.Select(SourceLabel).Distinct().Take(2).ToList();
        string platform = title.Installations.Select(i => i.Platform).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? string.Empty;
        string text = string.Join(", ", sources);
        return platform.Length == 0 ? text : text + " · " + platform;
    }

    internal static GameTitleDto WithState(GameTitleDto title, bool? favorite = null, bool? hidden = null,
        IReadOnlyList<string>? tags = null, IReadOnlyList<string>? collections = null) =>
        title with
        {
            Favorite = favorite ?? title.Favorite,
            Hidden = hidden ?? title.Hidden,
            Tags = tags ?? title.Tags,
            Collections = collections ?? title.Collections,
        };
}

/// <summary>
/// The tiles of the Gaming library: a wrapping cover grid, a compact list, or a horizontal shelf of large tiles
/// (the Continue row). Tiles show a local cover image when the user chose one and a lettered fallback otherwise.
/// A tile has three purposeful focus stops (details, Play, Configure); their order remains predictable
/// for keyboard and controller users. In multi-select mode only the title toggle stays focusable.
/// Play never launches by itself: it only
/// raises <see cref="PlayRequested"/>, and the host runs the existing launch-consent flow.
/// </summary>
internal sealed class GameTileGrid : StackPanel
{
    internal enum Presentation { Grid, List, Shelf }

    private const int PageStep = 60;

    private sealed class TileParts
    {
        public required GameTitleDto Title { get; set; }
        public required Border Card { get; init; }
        public required ButtonBase Main { get; init; }
        public required UIElement SelectedBadge { get; init; }
        public required Button Play { get; init; }
        public bool Hovering { get; set; }
    }

    private readonly Presentation _presentation;
    private readonly string _openPrefix;
    private readonly string _playPrefix;
    private readonly string _configurePrefix;
    private readonly Dictionary<string, TileParts> _tiles = new(StringComparer.Ordinal);
    private Panel _items = new StackPanel();
    private readonly Button _showMore = new();
    private IReadOnlyList<GameTitleDto> _titles = Array.Empty<GameTitleDto>();
    private ISet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
    private string? _openKey;
    private int _shown;
    private bool _console;
    private bool _selectMode;

    internal event Action<GameTitleDto>? OpenRequested;
    internal event Action<GameTitleDto>? PlayRequested;
    internal event Action<GameTitleDto>? SelectionToggled;

    internal GameTileGrid(Presentation presentation)
    {
        _presentation = presentation;
        bool shelf = presentation == Presentation.Shelf;
        _openPrefix = shelf ? "GameContinue_" : "GameTile_";
        _playPrefix = shelf ? "GameContinuePlay_" : "GameTilePlay_";
        _configurePrefix = shelf ? "GameContinueConfigure_" : "GameTileConfigure_";
        Spacing = 8;
        _showMore.HorizontalAlignment = HorizontalAlignment.Left;
        _showMore.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_showMore, "GameHome_ShowMore");
        _showMore.Click += (_, _) => ShowMore();
        ActualThemeChanged += (_, _) => { if (_titles.Count > 0) Render(_titles, _selected, _openKey); };
    }

    internal int Count => _titles.Count;

    internal void SetConsole(bool enabled)
    {
        if (_console == enabled) return;
        _console = enabled;
        Render(_titles, _selected, _openKey);
    }

    internal void SetSelectMode(bool enabled)
    {
        if (_selectMode == enabled) return;
        _selectMode = enabled;
        Render(_titles, _selected, _openKey);
    }

    private double ScaleF => _console ? 1.3 : 1.0;
    private bool Large => _presentation == Presentation.Shelf;
    private double CoverWidth => (Large ? 280 : 176) * ScaleF;
    private double CoverHeight => (Large ? 158 : 220) * ScaleF;
    private double NameSize => _console ? 20 : 15;
    private double SubSize => _console ? 16 : 12;
    private double PlayHeight => _console ? 56 : 44;
    private double NameHeight => NameSize * 2.7;
    private double SubHeight => SubSize * 1.7;
    private double CardWidth => CoverWidth + 26;
    private double CardHeight => 26 + CoverHeight + 12 + NameHeight + SubHeight + 6 + PlayHeight;
    private double RowHeight => _console ? 108 : 88;

    internal void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey)
    {
        _titles = titles;
        _selected = selected;
        _openKey = openKey;
        _tiles.Clear();
        Children.Clear();
        _shown = 0;

        if (_presentation == Presentation.Shelf)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _items = strip;
            Children.Add(new ScrollViewer
            {
                Content = strip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                TabNavigation = KeyboardNavigationMode.Local
            });
        }
        else if (_presentation == Presentation.Grid)
        {
            _items = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = CardWidth + 12,
                ItemHeight = CardHeight + 12,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Children.Add(_items);
        }
        else
        {
            _items = new StackPanel { Spacing = 8 };
            Children.Add(_items);
        }

        _showMore.FontSize = _console ? 20 : 14;
        _showMore.MinHeight = _console ? 56 : 44;
        Children.Add(_showMore);
        AddTiles(_presentation == Presentation.Shelf ? titles.Count : PageStep);
    }

    private void AddTiles(int count)
    {
        int target = Math.Min(_titles.Count, _shown + count);
        for (; _shown < target; _shown++)
        {
            GameTitleDto title = _titles[_shown];
            if (_tiles.ContainsKey(title.TitleKey)) continue;
            TileParts parts = _presentation == Presentation.List ? BuildRow(title) : BuildTile(title);
            _tiles[title.TitleKey] = parts;
            _items.Children.Add(parts.Card);
            ApplyState(parts);
        }
        int left = _titles.Count - _shown;
        _showMore.Visibility = left > 0 && _presentation != Presentation.Shelf ? Visibility.Visible : Visibility.Collapsed;
        _showMore.Content = GameUi.T($"Show {Math.Min(left, PageStep)} more ({left} left)",
                                     $"Mostra altri {Math.Min(left, PageStep)} ({left} rimasti)");
        AutomationProperties.SetName(_showMore, _showMore.Content as string ?? string.Empty);
    }

    private void ShowMore()
    {
        int first = _shown;
        AddTiles(PageStep);
        if (first < _titles.Count && _tiles.TryGetValue(_titles[first].TitleKey, out TileParts? next))
            next.Main.Focus(FocusState.Programmatic);
    }

    /// <summary>Updates selection and the open title in place (no rebuild, focus is kept).</summary>
    internal void RefreshState(ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        foreach (TileParts parts in _tiles.Values) ApplyState(parts);
    }

    /// <summary>Replaces one title's displayed state after a local edit; the tile itself is kept.</summary>
    internal void UpdateTitle(GameTitleDto title)
    {
        for (int i = 0; i < _titles.Count; i++)
            if (_titles[i].TitleKey == title.TitleKey && _titles is IList<GameTitleDto> { IsReadOnly: false } list) list[i] = title;
        if (_tiles.TryGetValue(title.TitleKey, out TileParts? parts)) { parts.Title = title; ApplyState(parts); }
    }

    internal bool FocusTile(string key)
    {
        if (_tiles.TryGetValue(key, out TileParts? parts)) return parts.Main.Focus(FocusState.Programmatic);
        return false;
    }

    internal bool FocusFirst()
    {
        TileParts? first = _titles.Count > 0 && _tiles.TryGetValue(_titles[0].TitleKey, out TileParts? parts) ? parts : null;
        return first is not null && first.Main.Focus(FocusState.Programmatic);
    }

    private void ApplyState(TileParts parts)
    {
        string key = parts.Title.TitleKey;
        bool selected = _selected.Contains(key);
        bool opened = string.Equals(_openKey, key, StringComparison.Ordinal);
        parts.Card.BorderThickness = new Thickness(selected || opened ? 2 : 1);
        parts.Card.BorderBrush = GameUi.ThemeBrush(this, selected || opened ? "PaRuXBrandBrush"
            : parts.Hovering ? "PaRuXStrokeStrongBrush" : "PaRuXStrokeBrush");
        parts.Card.Background = GameUi.ThemeBrush(this, selected || opened ? "PaRuXGrapheneSelectedBrush"
            : parts.Hovering ? "PaRuXSurfaceSubtleBrush" : "PaRuXSurfaceCardBrush");
        parts.SelectedBadge.Visibility = selected && _selectMode ? Visibility.Visible : Visibility.Collapsed;
        if (parts.Main is ToggleButton toggle && toggle.IsChecked != selected) toggle.IsChecked = selected;
        AutomationProperties.SetName(parts.Main, ComposeName(parts.Title, selected));
    }

    private string ComposeName(GameTitleDto title, bool selected)
    {
        var parts = new List<string> { title.Name };
        if (title.Favorite) parts.Add(GameUi.T("favorite", "preferito"));
        if (title.Hidden) parts.Add(GameUi.T("hidden", "nascosto"));
        if (title.PlaySeconds >= 60) parts.Add(GameUi.ShortPlayTime(title.PlaySeconds));
        if (_selectMode) parts.Add(selected ? GameUi.T("selected", "selezionato") : GameUi.T("not selected", "non selezionato"));
        else parts.Add(GameUi.T("open details", "apri i dettagli"));
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ building

    private ButtonBase NewMain(GameTitleDto title)
    {
        ButtonBase main = _selectMode ? new ToggleButton() : new Button();
        main.Padding = new Thickness(0);
        main.Background = new SolidColorBrush(Colors.Transparent);
        main.BorderThickness = new Thickness(0);
        main.CornerRadius = new CornerRadius(8);
        main.HorizontalAlignment = HorizontalAlignment.Stretch;
        main.HorizontalContentAlignment = HorizontalAlignment.Left;
        main.VerticalContentAlignment = VerticalAlignment.Top;
        main.UseSystemFocusVisuals = true;
        AutomationProperties.SetAutomationId(main, _openPrefix + title.TitleKey);
        main.Click += (_, _) =>
        {
            if (!_tiles.TryGetValue(title.TitleKey, out TileParts? current)) return;
            if (_selectMode) SelectionToggled?.Invoke(current.Title);
            else OpenRequested?.Invoke(current.Title);
        };
        return main;
    }

    private Button NewPlay(GameTitleDto title)
    {
        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        var label = new TextBlock { Text = GameUi.PlayLabel(chosen), FontSize = _console ? 20 : 14, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = _console ? 20 : 14 });
        content.Children.Add(label);
        var play = new Button
        {
            Content = content,
            Height = PlayHeight,
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !_selectMode && GameUi.CanStart(chosen),
            Visibility = _selectMode ? Visibility.Collapsed : Visibility.Visible,
            UseSystemFocusVisuals = true
        };
        if (GameUi.AccentButtonStyle is { } accent) play.Style = accent;
        AutomationProperties.SetAutomationId(play, _playPrefix + title.TitleKey);
        AutomationProperties.SetName(play, GameUi.PlayLabel(chosen) + " " + title.Name);
        if (chosen is not null && !GameUi.CanStart(chosen))
            ToolTipService.SetToolTip(play, GameUi.StateLabel(chosen.InstallState));
        play.Click += (_, _) =>
        {
            if (_tiles.TryGetValue(title.TitleKey, out TileParts? current)) PlayRequested?.Invoke(current.Title);
        };
        return play;
    }

    // Configure opens the EXISTING detail panel; no process is started and no settings
    // are changed until the user explicitly chooses a control in that panel.
    private Button NewConfigure(GameTitleDto title)
    {
        var configure = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = _console ? 20 : 16 },
            Width = PlayHeight,
            Height = PlayHeight,
            MinWidth = PlayHeight,
            CornerRadius = new CornerRadius(10),
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceSubtleBrush"),
            Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush"),
            BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeStrongBrush"),
            BorderThickness = new Thickness(1),
            IsEnabled = !_selectMode,
            Visibility = _selectMode ? Visibility.Collapsed : Visibility.Visible,
            UseSystemFocusVisuals = true
        };
        AutomationProperties.SetAutomationId(configure, _configurePrefix + title.TitleKey);
        string label = GameUi.T("Configure ", "Configura ") + title.Name;
        AutomationProperties.SetName(configure, label);
        ToolTipService.SetToolTip(configure, label);
        configure.Click += (_, _) =>
        {
            if (_tiles.TryGetValue(title.TitleKey, out TileParts? current))
                OpenRequested?.Invoke(current.Title);
        };
        return configure;
    }

    private UIElement BuildCover(GameTitleDto title, double width, double height, out UIElement selectedBadge)
    {
        var cover = new Grid { Width = width, Height = height };
        var surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush")
        };
        cover.Children.Add(surface);

        bool hasImage = false;
        string? path = GameCoverStore.TryGetPath(title.TitleKey);
        if (path is not null)
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(width * 1.5) };
                bitmap.UriSource = new Uri(path);
                surface.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
                hasImage = true;
            }
            catch (Exception) { /* A damaged local copy falls back to the lettered tile. */ }
        }
        if (!hasImage)
        {
            cover.Children.Add(new TextBlock
            {
                Text = GameUi.Initials(title.Name),
                FontSize = Math.Max(18, height / 3.2),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (title.Favorite)
            cover.Children.Add(Pill("★", GameUi.T("Favorite", "Preferito"), HorizontalAlignment.Right, VerticalAlignment.Top));
        if (title.Hidden)
            cover.Children.Add(Pill(GameUi.T("Hidden", "Nascosto"), null, HorizontalAlignment.Left, VerticalAlignment.Bottom));

        var badge = new Border
        {
            Background = GameUi.ThemeBrush(this, "PaRuXBrandBrush"),
            CornerRadius = new CornerRadius(12),
            Width = 28, Height = 28,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "✓", FontSize = 16,
                Foreground = GameUi.ThemeBrush(this, "PaRuXOnBrandBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        cover.Children.Add(badge);
        selectedBadge = badge;
        return cover;
    }

    private Border Pill(string text, string? accessibleName, HorizontalAlignment h, VerticalAlignment v)
    {
        var pill = new Border
        {
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush"),
            BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8),
            HorizontalAlignment = h,
            VerticalAlignment = v,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = _console ? 16 : 12,
                Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush")
            }
        };
        return pill;
    }

    private TextBlock NameText(GameTitleDto title) => new()
    {
        Text = title.Name,
        FontSize = NameSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Height = NameHeight,
        Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush")
    };

    private TextBlock SubText(GameTitleDto title)
    {
        string text = Large ? GameUi.PlaySummary(title) : GameUi.SourceSummary(title);
        if (!Large && title.PlaySeconds >= 60) text += " · " + GameUi.ShortPlayTime(title.PlaySeconds);
        var block = new TextBlock
        {
            Text = text,
            FontSize = SubSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = SubHeight,
            Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush")
        };
        ToolTipService.SetToolTip(block, text);
        return block;
    }

    // Hover only updates the hovered card, with no animation, polling or library refresh.
    private TileParts MakeInteractiveParts(GameTitleDto title, Border card, ButtonBase main,
        UIElement badge, Button play)
    {
        var parts = new TileParts { Title = title, Card = card, Main = main, SelectedBadge = badge, Play = play };
        card.PointerEntered += (_, _) => { parts.Hovering = true; ApplyState(parts); };
        card.PointerExited += (_, _) => { parts.Hovering = false; ApplyState(parts); };
        return parts;
    }

    private TileParts BuildTile(GameTitleDto title)
    {
        var face = new StackPanel { Spacing = 6, Width = CoverWidth };
        face.Children.Add(BuildCover(title, CoverWidth, CoverHeight, out UIElement badge));
        face.Children.Add(NameText(title));
        face.Children.Add(SubText(title));
        ButtonBase main = NewMain(title);
        main.Content = face;
        Button play = NewPlay(title);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(play);
        var configure = NewConfigure(title);
        Grid.SetColumn(configure, 1);
        actions.Children.Add(configure);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(main);
        stack.Children.Add(actions);
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush"),
            BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush"),
            Child = stack
        };
        return MakeInteractiveParts(title, card, main, badge, play);
    }

    private TileParts BuildRow(GameTitleDto title)
    {
        double thumb = RowHeight - 24;
        var face = new Grid { ColumnSpacing = 12 };
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        UIElement cover = BuildCover(title, thumb, thumb, out UIElement badge);
        face.Children.Add(cover);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var name = NameText(title);
        name.MaxLines = 1;
        name.Height = double.NaN;
        texts.Children.Add(name);
        var sub = SubText(title);
        sub.Text = GameUi.SourceSummary(title) + " · " + GameUi.PlaySummary(title);
        sub.Height = double.NaN;
        texts.Children.Add(sub);
        Grid.SetColumn(texts, 1);
        face.Children.Add(texts);
        ButtonBase main = NewMain(title);
        main.Content = face;
        main.MinHeight = RowHeight - 24;

        Button play = NewPlay(title);
        play.Width = 140 * ScaleF;
        play.HorizontalAlignment = HorizontalAlignment.Right;
        play.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(main);
        Grid.SetColumn(play, 1);
        row.Children.Add(play);
        var configure = NewConfigure(title);
        Grid.SetColumn(configure, 2);
        row.Children.Add(configure);
        var card = new Border
        {
            MinHeight = RowHeight,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush"),
            BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush"),
            Child = row
        };
        return MakeInteractiveParts(title, card, main, badge, play);
    }
}
