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
        string theme = ActiveTheme(owner);
        // Realizing a tile asks for about fifteen brushes; resolving each through the theme dictionaries (and building an
        // AccessibilitySettings object) while scrolling a 2,000-title grid is wasted work. Brushes are shared resources
        // anyway, so the resolved instance is remembered per theme + key and dropped when the high-contrast state changes.
        string cacheKey = theme + "|" + key;
        if (BrushCache.TryGetValue(cacheKey, out Brush? cached)) return cached;
        if (TryFind(Application.Current.Resources, key, theme, 0, out object? value) && value is Brush brush)
        {
            BrushCache[cacheKey] = brush;
            return brush;
        }
        return new SolidColorBrush(Colors.Gray);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.Ordinal);
    private static Windows.UI.ViewManagement.AccessibilitySettings? _accessibility;

    private static string ActiveTheme(FrameworkElement? owner)
    {
        bool highContrast = false;
        try
        {
            if (_accessibility is null)
            {
                _accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
                _accessibility.HighContrastChanged += (_, _) => BrushCache.Clear();
            }
            highContrast = _accessibility.HighContrast;
        }
        catch (Exception) { /* Fall back to the element's own light/dark theme. */ }
        return highContrast ? "HighContrast" : ThemeName(owner);
    }

    /// <summary>Drops the remembered brushes (an element's theme changed, so the same key may now resolve differently).</summary>
    internal static void ResetBrushCache() => BrushCache.Clear();

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
        GameInstallationSources.Ea => "EA app",
        GameInstallationSources.BattleNet => "Battle.net",
        GameInstallationSources.Ubisoft => "Ubisoft Connect",
        GameInstallationSources.Xbox => "Xbox",
        GameInstallationSources.Playnite => "Playnite",
        GameInstallationSources.Heroic => "Heroic",
        GameInstallationSources.Lutris => "Lutris",
        GameInstallationSources.Emulator => string.IsNullOrWhiteSpace(i.EmulatorName)
            ? T("Emulator", "Emulatore")
            : T("Emulator · ", "Emulatore · ") + i.EmulatorName,
        _ => i.Kind == ManualEntryKinds.Application ? T("Application on this PC", "Applicazione su questo PC") : T("Copy on this PC", "Copia su questo PC"),
    };

    /// <summary>Install state plus the store's pending-update note ("Installed · update available").</summary>
    internal static string StateLabel(GameInstallationDto installation) =>
        StateLabel(installation.InstallState) + (installation.UpdatePending ? " · " + T("update available", "aggiornamento disponibile") : string.Empty);

    internal static string StateLabel(string state) => state switch
    {
        GameInstallStates.Installed => T("Installed", "Installato"),
        GameInstallStates.Updating => T("Updating", "In aggiornamento"),
        GameInstallStates.Incomplete => T("Incomplete", "Incompleto"),
        GameInstallStates.MetadataOnly => T("Imported title only · not installed", "Solo titolo importato · non installato"),
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

    internal static string StatusLabel(string? status) => (status ?? string.Empty) switch
    {
        GameCompletionStatuses.Backlog => T("Plan to play", "Da giocare"),
        GameCompletionStatuses.Playing => T("Playing", "In corso"),
        GameCompletionStatuses.Completed => T("Completed", "Completato"),
        GameCompletionStatuses.OnHold => T("On hold", "In pausa"),
        GameCompletionStatuses.Dropped => T("Dropped", "Abbandonato"),
        _ => T("No status", "Nessuno stato"),
    };

    internal static GameTitleDto WithState(GameTitleDto title, bool? favorite = null, bool? hidden = null,
        IReadOnlyList<string>? tags = null, IReadOnlyList<string>? collections = null,
        string? status = null, int? score = null) =>
        title with
        {
            Status = status ?? title.Status,
            Score = score ?? title.Score,
            Favorite = favorite ?? title.Favorite,
            Hidden = hidden ?? title.Hidden,
            Tags = tags ?? title.Tags,
            Collections = collections ?? title.Collections,
        };
}

/// <summary>
/// The tiles of the Gaming library: a virtualized cover grid, a virtualized compact list, or a horizontal shelf of large tiles
/// (the Continue row, at most ten tiles). The grid and the list are an <see cref="ItemsRepeater"/> over the lightweight title
/// list: only the tiles near the viewport exist, finished tiles are recycled (rebound to the next title, never rebuilt), and
/// a 2,000-title library costs the same as a 50-title one. Tiles show a local cover image when the user chose one and a lettered
/// fallback otherwise; covers are decoded at the displayed size when a tile is realized and released when it is recycled.
/// A tile has three purposeful focus stops (details, Play, Configure); their order remains predictable
/// for keyboard and controller users. In multi-select mode only the title toggle stays focusable.
/// Play never launches by itself: it only
/// raises <see cref="PlayRequested"/>, and the host runs the existing launch-consent flow.
/// </summary>
internal sealed class GameTileGrid : StackPanel, IGameTileSurface
{
    internal enum Presentation { Grid, List, Shelf }

    private const double TileGap = 12;

    /// <summary>A decoded (or decoding) cover shared by every tile that shows the same file at the same size.</summary>
    private sealed class CoverEntry
    {
        public required string Key { get; init; }
        public required BitmapImage Bitmap { get; init; }
        public bool Opened { get; set; }
        public HashSet<TileParts> Owners { get; } = new();
    }

    /// <summary>The elements of one tile. Created once, then rebound to different titles as the tile is recycled.</summary>
    private sealed class TileParts
    {
        public GameTitleDto Title { get; set; } = null!;
        public int Index { get; set; }
        public required Border Card { get; init; }
        public required ButtonBase Main { get; init; }
        public required Button Play { get; init; }
        public required TextBlock PlayText { get; init; }
        public required Button Configure { get; init; }
        public required Border Surface { get; init; }
        public required TextBlock Initials { get; init; }
        public required Border FavoritePill { get; init; }
        public required Border HiddenPill { get; init; }
        public required Border SelectedBadge { get; init; }
        public required TextBlock BadgeInk { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Sub { get; init; }
        public required ImageBrush CoverBrush { get; init; }
        public CoverEntry? Cover { get; set; }
        public bool Hovering { get; set; }
        public bool Live { get; set; }
    }

    /// <summary>Creates, binds and recycles the tiles of the <see cref="ItemsRepeater"/>.</summary>
    private sealed class TileFactory : IElementFactory
    {
        private const int MaxPooled = 48;
        private readonly GameTileGrid _owner;
        private readonly Stack<TileParts> _pool = new();

        public TileFactory(GameTileGrid owner) => _owner = owner;

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            TileParts parts = _pool.Count > 0 ? _pool.Pop() : _owner.CreateTile();
            var title = (GameTitleDto)args.Data;
            _owner.Bind(parts, title, _owner._indexByKey.TryGetValue(title.TitleKey, out int index) ? index : -1);
            return parts.Card;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not Border { Tag: TileParts parts }) return;
            _owner.Unbind(parts);
            if (_pool.Count < MaxPooled) _pool.Push(parts);
        }

        public void Clear() => _pool.Clear();
    }

    // Decoded covers stay in memory only up to this many entries (about one screen and a half of tiles per cache slot); older ones are
    // dropped and decoded again when scrolled back into view.
    private static readonly BoundedLruCache<string, CoverEntry> CoverCache = new(160);

    private readonly Presentation _presentation;
    private readonly string _openPrefix;
    private readonly string _playPrefix;
    private readonly string _configurePrefix;
    private readonly HashSet<TileParts> _live = new();
    private readonly TileFactory _factory;
    private ItemsRepeater? _repeater;
    private StackPanel? _strip;
    private IReadOnlyList<GameTitleDto> _titles = Array.Empty<GameTitleDto>();
    // The list the repeater actually reads. It is a private copy of the host's list (references only, so cheap) so that rebinding
    // changed titles in place never depends on the host mutating or replacing its own list.
    private List<GameTitleDto> _source = new();
    private Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);
    private ISet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
    private string? _openKey;
    private bool _console;
    private bool _selectMode;
    private double _coverScale = 1.0;

    public event Action<GameTitleDto>? OpenRequested;
    public event Action<GameTitleDto>? PlayRequested;
    public event Action<GameTitleDto>? SelectionToggled;

    /// <summary>False when a parent (the grouped library) owns arrow / Home / End / Page movement across several grids.</summary>
    internal bool HandlesNavigationKeys { get; set; } = true;
    public FrameworkElement View => this;

    internal GameTileGrid(Presentation presentation)
    {
        _presentation = presentation;
        _factory = new TileFactory(this);
        bool shelf = presentation == Presentation.Shelf;
        _openPrefix = shelf ? "GameContinue_" : "GameTile_";
        _playPrefix = shelf ? "GameContinuePlay_" : "GameTilePlay_";
        _configurePrefix = shelf ? "GameContinueConfigure_" : "GameTileConfigure_";
        Spacing = 8;
        // Repaint visible cards in place: do not reset the scroll position,
        // focused Play/Configure buttons or decoded cover bitmaps on theme switches.
        ActualThemeChanged += (_, _) => RepaintTheme();
        // A grid that leaves the visual tree gives its cover bitmaps back; when it returns the live tiles ask for them again.
        Unloaded += (_, _) => { foreach (TileParts parts in _live) ReleaseCover(parts); };
        Loaded += (_, _) => { foreach (TileParts parts in _live) LoadCover(parts); };
        KeyDown += OnTileKeyDown;
    }

    private void RepaintTheme()
    {
        GameUi.ResetBrushCache();
        foreach (TileParts parts in _live)
        {
            RepaintTile(parts);
            ApplyState(parts);
        }
    }

    public int Count => _titles.Count;

    public void SetConsole(bool enabled)
    {
        if (_console == enabled) return;
        _console = enabled;
        Render(_titles, _selected, _openKey, forceReset: true);
    }

    /// <summary>Cover size of the grid tiles (a <see cref="GameCoverSizes"/> value). The list and the shelf keep their size.</summary>
    public void SetCoverSize(string size)
    {
        double scale = GameCoverSizes.Scale(size);
        if (Math.Abs(scale - _coverScale) < 0.001) return;
        _coverScale = scale;
        if (_presentation == Presentation.Grid) Render(_titles, _selected, _openKey, forceReset: true);
    }

    public void SetSelectMode(bool enabled)
    {
        if (_selectMode == enabled) return;
        _selectMode = enabled;
        Render(_titles, _selected, _openKey, forceReset: true);
    }

    private double ScaleF => _console ? 1.3 : 1.0;
    private bool Large => _presentation == Presentation.Shelf;
    private double CoverScaleF => _presentation == Presentation.Grid ? _coverScale : 1.0;
    private double CoverWidth => (Large ? 280 : 176) * ScaleF * CoverScaleF;
    private double CoverHeight => (Large ? 158 : 220) * ScaleF * CoverScaleF;
    private double NameSize => _console ? 20 : 15;
    private double SubSize => _console ? 16 : 12;
    private double PlayHeight => _console ? 56 : 44;
    private double NameHeight => NameSize * 2.7;
    private double SubHeight => SubSize * 1.7;
    private double CardWidth => CoverWidth + 26;
    private double CardHeight => 26 + CoverHeight + 12 + NameHeight + SubHeight + 6 + PlayHeight;
    private double RowHeight => _console ? 108 : 88;

    /// <summary>
    /// Shows <paramref name="titles"/>. The grid and the list keep their tiles when the new list only differs in what a tile shows
    /// (rebinding those tiles in place) and swap the items source only when the set or the order changed; nothing is rebuilt.
    /// </summary>
    public void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey) =>
        Render(titles, selected, openKey, forceReset: false);

    private void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey, bool forceReset)
    {
        _selected = selected;
        _openKey = openKey;

        if (_presentation == Presentation.Shelf)
        {
            _titles = titles;
            _indexByKey = GameTileListModel.IndexByKey(titles);
            RenderShelf();
            return;
        }

        GameTileListDelta delta = forceReset || _repeater is null
            ? new GameTileListDelta(GameTileListChange.Reset, Array.Empty<int>())
            : GameTileListModel.Diff(_source, titles);
        _titles = titles;
        if (delta.Change != GameTileListChange.None || forceReset) _indexByKey = GameTileListModel.IndexByKey(titles);

        if (_repeater is null)
        {
            _repeater = new ItemsRepeater
            {
                ItemTemplate = _factory,
                VerticalCacheLength = 1.0,
                HorizontalCacheLength = 0.5,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Children.Add(_repeater);
        }

        if (forceReset)
        {
            // Sizes, tile type or control kind changed: recycled tiles no longer fit, so drop the pool and build fresh ones.
            _repeater.ItemsSource = null;
            _factory.Clear();
        }
        if (_repeater.Layout is null || forceReset) _repeater.Layout = NewLayout();

        switch (delta.Change)
        {
            case GameTileListChange.None:
                foreach (TileParts parts in _live) ApplyState(parts);
                break;
            case GameTileListChange.ContentOnly:
                foreach (int index in delta.ChangedIndexes)
                {
                    _source[index] = titles[index];
                    if (_repeater.TryGetElement(index) is Border { Tag: TileParts parts }) Bind(parts, titles[index], index);
                }
                foreach (TileParts parts in _live) ApplyState(parts);
                break;
            default:
                // A new list instance resets the repeater: realized tiles are recycled and only the visible ones are realized again.
                _source = new List<GameTitleDto>(titles);
                _repeater.ItemsSource = _source;
                break;
        }
    }

    private Layout NewLayout() => _presentation == Presentation.Grid
        ? new UniformGridLayout
        {
            Orientation = Orientation.Horizontal,
            MinItemWidth = CardWidth,
            MinItemHeight = CardHeight,
            MinColumnSpacing = TileGap,
            MinRowSpacing = TileGap,
            ItemsStretch = UniformGridLayoutItemsStretch.None,
            ItemsJustification = UniformGridLayoutItemsJustification.Start
        }
        : new StackLayout { Orientation = Orientation.Vertical, Spacing = 8 };

    // The Continue shelf holds at most ten tiles in a single scrolling row; it does not need virtualization.
    private void RenderShelf()
    {
        foreach (TileParts parts in _live.ToList()) Unbind(parts);
        Children.Clear();
        _strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        Children.Add(new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            TabNavigation = KeyboardNavigationMode.Local
        });
        for (int i = 0; i < _titles.Count; i++)
        {
            if (_indexByKey.TryGetValue(_titles[i].TitleKey, out int first) && first != i) continue;
            TileParts parts = CreateTile();
            Bind(parts, _titles[i], i);
            _strip.Children.Add(parts.Card);
        }
    }

    /// <summary>Updates selection and the open title in place (no rebuild, focus is kept).</summary>
    public void RefreshState(ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        foreach (TileParts parts in _live) ApplyState(parts);
    }

    /// <summary>Replaces one title's displayed state after a local edit; the tile itself is kept.</summary>
    public void UpdateTitle(GameTitleDto title)
    {
        if (!_indexByKey.TryGetValue(title.TitleKey, out int index)) return;
        if (_titles is IList<GameTitleDto> { IsReadOnly: false } list && index < list.Count) list[index] = title;
        if (index < _source.Count) _source[index] = title;
        foreach (TileParts parts in _live)
            if (parts.Index == index) { Bind(parts, title, index); break; }
    }

    public bool FocusTile(string key) => _indexByKey.TryGetValue(key, out int index) && FocusIndex(index);

    /// <summary>
    /// Scrolls the tile at <paramref name="index"/> into view (realizing it first when it is off-screen) and focuses its title button.
    /// False when there is no such tile. A tile outside the realized range does not exist yet: the page is scrolled to where the layout
    /// puts it, and the tile is focused once the repeater has realized it there (an element created on demand and focused before the
    /// scroll is applied is recycled by the next layout pass, which left End / Page Down on a tile that was never shown).
    /// </summary>
    public bool FocusIndex(int index)
    {
        if (index < 0 || index >= _titles.Count) return false;
        if (_presentation == Presentation.Shelf)
        {
            if (_live.FirstOrDefault(p => p.Index == index)?.Card is not Border { Tag: TileParts shelfTile } shelfCard) return false;
            shelfCard.UpdateLayout();
            shelfCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            shelfTile.Main.Focus(FocusState.Programmatic);
            return true;
        }
        if (_repeater is null) return false;
        FocusRealized(index, 8, ++_focusRequest);
        return true;
    }

    private int _focusRequest;

    private void FocusRealized(int index, int attemptsLeft, int request)
    {
        if (request != _focusRequest || _repeater is null || index >= _titles.Count) return;
        Border? realizedCard = _repeater.TryGetElement(index) is Border { Tag: TileParts } border && border.ActualHeight > 0 ? border : null;
        bool realized = realizedCard is not null, tookFocus = false, inView = false;
        if (realizedCard is not null)
        {
            Border card = realizedCard;
            var parts = (TileParts)card.Tag;
            card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            tookFocus = parts.Main.Focus(FocusState.Programmatic);
            inView = tookFocus && IsInView(card);
        }
        switch (GameTileListModel.NextFocusStep(realized, tookFocus, inView, attemptsLeft))
        {
            case GameTileListModel.FocusStep.Done:
            case GameTileListModel.FocusStep.GiveUp:
                return;
            case GameTileListModel.FocusStep.ScrollAndRetry:
                ScrollTowards(index);
                break;
        }
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FocusRealized(index, attemptsLeft - 1, request));
    }

    private ScrollViewer? HostViewer()
    {
        for (DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this); parent is not null;
             parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }

    private bool IsInView(FrameworkElement element)
    {
        if (HostViewer() is not { Content: UIElement content } viewer) return true;
        double top = element.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        return top + element.ActualHeight > viewer.VerticalOffset && top < viewer.VerticalOffset + viewer.ViewportHeight;
    }

    /// <summary>Scrolls the hosting page to where the layout puts the tile at <paramref name="index"/> (rows are uniform).</summary>
    private void ScrollTowards(int index)
    {
        if (_repeater is null || HostViewer() is not { Content: UIElement content } viewer) return;
        int columns = _presentation == Presentation.List
            ? 1 : GameTileListModel.Columns(_repeater.ActualWidth, CardWidth, TileGap);
        double itemHeight = _presentation == Presentation.List ? RowHeight : CardHeight;
        double rowPitch = itemHeight + (_presentation == Presentation.List ? 8 : TileGap);
        double gridTop = _repeater.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        double target = GameTileListModel.ScrollOffsetFor(index, columns, gridTop, rowPitch, itemHeight,
            viewer.ViewportHeight, viewer.ScrollableHeight);
        viewer.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>
    /// Deterministic arrow / D-pad / bumper / Home / End / Page movement between the title buttons of the grid (one stop per
    /// tile, like a console shelf). Enter or A opens the details, where Play is the first control; Tab still reaches each
    /// tile's own Play and Configure buttons. Only keys pressed on a tile's title button are handled. Movement works on indexes,
    /// so it reaches tiles that are not realized yet.
    /// </summary>
    private void OnTileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!HandlesNavigationKeys || _selectMode || e.OriginalSource is not ButtonBase source) return;
        TileParts? current = _live.FirstOrDefault(p => ReferenceEquals(p.Main, source));
        if (current is null || current.Index < 0) return;
        GameLibraryHome.Direction? direction = DirectionOf(e.Key);
        if (direction is not { } move) return;
        // A single-row shelf must not steal Up/Down focus movement from the page.
        if (_presentation == Presentation.Shelf && move is GameLibraryHome.Direction.Up or GameLibraryHome.Direction.Down) return;
        int next = GameLibraryHome.Move(current.Index, _titles.Count, ColumnCount, move);
        if (next >= 0 && next != current.Index && FocusIndex(next)) e.Handled = true;
    }

    public bool FocusFirst() => FocusIndex(0);

    /// <summary>Number of tiles per row right now (1 for the list); the grouped library moves focus with it.</summary>
    internal int ColumnCount => _presentation switch
    {
        Presentation.Shelf => Math.Max(1, _titles.Count),
        Presentation.List => 1,
        _ => GameTileListModel.Columns(_repeater?.ActualWidth ?? ActualWidth, CardWidth, TileGap),
    };

    /// <summary>Position of the tile whose title button is <paramref name="source"/>, or -1.</summary>
    internal int IndexOfSource(object? source) =>
        source is ButtonBase button ? _live.FirstOrDefault(p => ReferenceEquals(p.Main, button))?.Index ?? -1 : -1;

    internal static GameLibraryHome.Direction? DirectionOf(Windows.System.VirtualKey key) => key switch
    {
        Windows.System.VirtualKey.Left or Windows.System.VirtualKey.GamepadDPadLeft => GameLibraryHome.Direction.Left,
        Windows.System.VirtualKey.Right or Windows.System.VirtualKey.GamepadDPadRight => GameLibraryHome.Direction.Right,
        Windows.System.VirtualKey.Up or Windows.System.VirtualKey.GamepadDPadUp => GameLibraryHome.Direction.Up,
        Windows.System.VirtualKey.Down or Windows.System.VirtualKey.GamepadDPadDown => GameLibraryHome.Direction.Down,
        Windows.System.VirtualKey.Home => GameLibraryHome.Direction.First,
        Windows.System.VirtualKey.End => GameLibraryHome.Direction.Last,
        Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.GamepadLeftShoulder => GameLibraryHome.Direction.PageUp,
        Windows.System.VirtualKey.PageDown or Windows.System.VirtualKey.GamepadRightShoulder => GameLibraryHome.Direction.PageDown,
        _ => null,
    };

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
        if (!string.IsNullOrEmpty(title.Status)) parts.Add(GameUi.StatusLabel(title.Status).ToLowerInvariant());
        if (title.Score > 0) parts.Add(GameUi.T("score ", "voto ") + title.Score + "/10");
        if (title.PlaySeconds >= 60) parts.Add(GameUi.ShortPlayTime(title.PlaySeconds));
        if (_selectMode) parts.Add(selected ? GameUi.T("selected", "selezionato") : GameUi.T("not selected", "non selezionato"));
        else parts.Add(GameUi.T("open details", "apri i dettagli"));
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ binding and recycling

    /// <summary>Fills a (new or recycled) tile with one title. Everything title-specific is set here and only here.</summary>
    private void Bind(TileParts parts, GameTitleDto title, int index)
    {
        parts.Title = title;
        parts.Index = index;
        parts.Hovering = false;
        if (!parts.Live)
        {
            parts.Live = true;
            _live.Add(parts);
        }

        AutomationProperties.SetAutomationId(parts.Main, _openPrefix + title.TitleKey);
        parts.Name.Text = title.Name;
        string sub = _presentation switch
        {
            Presentation.Shelf => GameUi.PlaySummary(title),
            Presentation.List => GameUi.SourceSummary(title) + " · " + GameUi.PlaySummary(title),
            _ => title.PlaySeconds >= 60
                ? GameUi.SourceSummary(title) + " · " + GameUi.ShortPlayTime(title.PlaySeconds)
                : GameUi.SourceSummary(title),
        };
        parts.Sub.Text = sub;
        ToolTipService.SetToolTip(parts.Sub, sub);
        parts.Initials.Text = GameUi.Initials(title.Name);
        parts.FavoritePill.Visibility = title.Favorite ? Visibility.Visible : Visibility.Collapsed;
        parts.HiddenPill.Visibility = title.Hidden ? Visibility.Visible : Visibility.Collapsed;

        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        parts.PlayText.Text = GameUi.PlayLabel(chosen);
        parts.Play.IsEnabled = !_selectMode && GameUi.CanStart(chosen);
        AutomationProperties.SetAutomationId(parts.Play, _playPrefix + title.TitleKey);
        AutomationProperties.SetName(parts.Play, GameUi.PlayLabel(chosen) + " " + title.Name);
        ToolTipService.SetToolTip(parts.Play, chosen is not null && !GameUi.CanStart(chosen) ? GameUi.StateLabel(chosen.InstallState) : null);

        AutomationProperties.SetAutomationId(parts.Configure, _configurePrefix + title.TitleKey);
        string configureLabel = GameUi.T("Configure ", "Configura ") + title.Name;
        AutomationProperties.SetName(parts.Configure, configureLabel);
        ToolTipService.SetToolTip(parts.Configure, configureLabel);

        LoadCover(parts);
        ApplyState(parts);
    }

    /// <summary>Called when the repeater recycles a tile: forgets the title and gives the cover back before the tile is reused.</summary>
    private void Unbind(TileParts parts)
    {
        ReleaseCover(parts);
        parts.Hovering = false;
        parts.Live = false;
        _live.Remove(parts);
    }

    private void LoadCover(TileParts parts)
    {
        ReleaseCover(parts);
        string? path = GameCoverStore.TryGetPath(parts.Title.TitleKey);
        if (path is null)
        {
            ShowFallbackCover(parts);
            return;
        }
        // Decode at (a little above) the displayed size; the bitmap decodes on a background thread and appears when ready.
        double width = _presentation == Presentation.List ? RowHeight - 24 : CoverWidth;
        int pixels = (int)Math.Ceiling(width * 1.5);
        string cacheKey = pixels + "|" + path;
        if (!CoverCache.TryGet(cacheKey, out CoverEntry? entry))
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = pixels };
                entry = new CoverEntry { Key = cacheKey, Bitmap = bitmap };
                CoverEntry created = entry;
                bitmap.ImageOpened += (_, _) => created.Opened = true;
                bitmap.ImageFailed += (_, _) =>
                {
                    // A damaged local copy falls back to the lettered tile on every tile that shows it.
                    CoverCache.Remove(created.Key);
                    foreach (TileParts owner in created.Owners.ToList()) { created.Owners.Remove(owner); owner.Cover = null; ShowFallbackCover(owner); }
                };
                bitmap.UriSource = new Uri(path);
                CoverCache.Set(cacheKey, entry);
            }
            catch (Exception)
            {
                ShowFallbackCover(parts);
                return;
            }
        }
        entry.Owners.Add(parts);
        parts.Cover = entry;
        parts.CoverBrush.ImageSource = entry.Bitmap;
        parts.Surface.Background = parts.CoverBrush;
        parts.Initials.Visibility = Visibility.Collapsed;
    }

    private void ShowFallbackCover(TileParts parts)
    {
        parts.CoverBrush.ImageSource = null;
        parts.Surface.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        parts.Initials.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Detaches the tile from its cover. A decode that has not finished yet and is wanted by no other tile is cancelled
    /// (the source is cleared and the entry dropped), so scrolling quickly never leaves a queue of decodes nobody will see.
    /// </summary>
    private void ReleaseCover(TileParts parts)
    {
        CoverEntry? entry = parts.Cover;
        parts.Cover = null;
        parts.CoverBrush.ImageSource = null;
        if (entry is null) return;
        entry.Owners.Remove(parts);
        if (entry.Owners.Count == 0 && !entry.Opened)
        {
            CoverCache.Remove(entry.Key);
            try { entry.Bitmap.UriSource = null; } catch (Exception) { /* Already torn down. */ }
        }
    }

    // ------------------------------------------------------------------ building

    private ButtonBase NewMain()
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
        return main;
    }

    private Button NewPlay(out TextBlock label)
    {
        label = new TextBlock { FontSize = _console ? 20 : 14, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = _console ? 20 : 14 });
        content.Children.Add(label);
        var play = new Button
        {
            Content = content,
            Height = PlayHeight,
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = _selectMode ? Visibility.Collapsed : Visibility.Visible,
            UseSystemFocusVisuals = true
        };
        if (GameUi.AccentButtonStyle is { } accent) play.Style = accent;
        return play;
    }

    // Configure opens the EXISTING detail panel; no process is started and no settings
    // are changed until the user explicitly chooses a control in that panel.
    private Button NewConfigure() => new()
    {
        Content = new FontIcon { Glyph = "", FontSize = _console ? 20 : 16 },
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

    private Border Pill(string text, HorizontalAlignment h, VerticalAlignment v) => new()
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
        Visibility = Visibility.Collapsed,
        Child = new TextBlock
        {
            Text = text,
            FontSize = _console ? 16 : 12,
            Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush")
        }
    };

    /// <summary>The cover square: surface (or cover image), lettered fallback, state pills and the selected check.</summary>
    private Grid NewCover(double width, double height, out Border surface, out TextBlock initials, out Border favoritePill,
        out Border hiddenPill, out Border badge, out TextBlock badgeInk)
    {
        var cover = new Grid { Width = width, Height = height };
        surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush")
        };
        cover.Children.Add(surface);
        initials = new TextBlock
        {
            FontSize = Math.Max(18, height / 3.2),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        cover.Children.Add(initials);
        favoritePill = Pill("★", HorizontalAlignment.Right, VerticalAlignment.Top);
        hiddenPill = Pill(GameUi.T("Hidden", "Nascosto"), HorizontalAlignment.Left, VerticalAlignment.Bottom);
        cover.Children.Add(favoritePill);
        cover.Children.Add(hiddenPill);

        badgeInk = new TextBlock
        {
            Text = "✓", FontSize = 16,
            Foreground = GameUi.ThemeBrush(this, "PaRuXOnBrandBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        badge = new Border
        {
            Background = GameUi.ThemeBrush(this, "PaRuXBrandBrush"),
            CornerRadius = new CornerRadius(12),
            Width = 28, Height = 28,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = badgeInk
        };
        cover.Children.Add(badge);
        return cover;
    }

    private TextBlock NewName(bool oneLine) => new()
    {
        FontSize = NameSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = oneLine ? 1 : 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Height = oneLine ? double.NaN : NameHeight,
        Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush")
    };

    private TextBlock NewSub(bool autoHeight) => new()
    {
        FontSize = SubSize,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Height = autoHeight ? double.NaN : SubHeight,
        Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush")
    };

    // The image brush must survive the repaint. Only flat fallback surfaces,
    // text and small state badges change with the active OS theme.
    private void RepaintTile(TileParts parts)
    {
        if (parts.Cover is null) parts.Surface.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        parts.Initials.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        foreach (Border pill in new[] { parts.FavoritePill, parts.HiddenPill })
        {
            pill.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
            pill.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
            if (pill.Child is TextBlock label) label.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        }
        parts.SelectedBadge.Background = GameUi.ThemeBrush(this, "PaRuXBrandBrush");
        parts.BadgeInk.Foreground = GameUi.ThemeBrush(this, "PaRuXOnBrandBrush");
        parts.Name.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        parts.Sub.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        RepaintConfigure(parts.Configure);
    }

    private void RepaintConfigure(Button configure)
    {
        configure.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceSubtleBrush");
        configure.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeStrongBrush");
        configure.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
    }

    /// <summary>
    /// Builds one empty tile (grid/shelf card or list row). All handlers are attached here, once, and read the tile's current
    /// title when they fire, so recycling a tile to another title never adds or leaks a handler.
    /// </summary>
    private TileParts CreateTile()
    {
        bool row = _presentation == Presentation.List;
        double thumb = RowHeight - 24;
        Grid cover = row
            ? NewCover(thumb, thumb, out Border surface, out TextBlock initials, out Border favoritePill, out Border hiddenPill, out Border badge, out TextBlock badgeInk)
            : NewCover(CoverWidth, CoverHeight, out surface, out initials, out favoritePill, out hiddenPill, out badge, out badgeInk);
        TextBlock name = NewName(oneLine: row);
        TextBlock sub = NewSub(autoHeight: row);
        ButtonBase main = NewMain();
        Button play = NewPlay(out TextBlock playText);
        Button configure = NewConfigure();
        var coverBrush = new ImageBrush { Stretch = Stretch.UniformToFill };

        Border card;
        if (row)
        {
            var face = new Grid { ColumnSpacing = 12 };
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            face.Children.Add(cover);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            texts.Children.Add(name);
            texts.Children.Add(sub);
            Grid.SetColumn(texts, 1);
            face.Children.Add(texts);
            main.Content = face;
            main.MinHeight = RowHeight - 24;
            play.Width = 140 * ScaleF;
            play.HorizontalAlignment = HorizontalAlignment.Right;
            play.VerticalAlignment = VerticalAlignment.Center;
            var layout = new Grid { ColumnSpacing = 8 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.Children.Add(main);
            Grid.SetColumn(play, 1);
            layout.Children.Add(play);
            Grid.SetColumn(configure, 2);
            layout.Children.Add(configure);
            card = new Border
            {
                MinHeight = RowHeight,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = layout
            };
        }
        else
        {
            var face = new StackPanel { Spacing = 6, Width = CoverWidth };
            face.Children.Add(cover);
            face.Children.Add(name);
            face.Children.Add(sub);
            main.Content = face;
            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.Children.Add(play);
            Grid.SetColumn(configure, 1);
            actions.Children.Add(configure);
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(main);
            stack.Children.Add(actions);
            card = new Border
            {
                Width = CardWidth,
                Height = CardHeight,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = stack
            };
        }

        var parts = new TileParts
        {
            Card = card, Main = main, Play = play, PlayText = playText, Configure = configure, Surface = surface,
            Initials = initials, FavoritePill = favoritePill, HiddenPill = hiddenPill, SelectedBadge = badge, BadgeInk = badgeInk,
            Name = name, Sub = sub, CoverBrush = coverBrush
        };
        card.Tag = parts;

        main.Click += (_, _) =>
        {
            if (parts.Title is null) return;
            if (_selectMode) SelectionToggled?.Invoke(parts.Title);
            else OpenRequested?.Invoke(parts.Title);
        };
        play.Click += (_, _) =>
        {
            if (parts.Title is not null) PlayRequested?.Invoke(parts.Title);
        };
        configure.Click += (_, _) =>
        {
            if (parts.Title is not null) OpenRequested?.Invoke(parts.Title);
        };
        // Hover only updates the hovered card, with no animation, polling or library refresh.
        card.PointerEntered += (_, _) => { parts.Hovering = true; ApplyState(parts); };
        card.PointerExited += (_, _) => { parts.Hovering = false; ApplyState(parts); };
        return parts;
    }
}
