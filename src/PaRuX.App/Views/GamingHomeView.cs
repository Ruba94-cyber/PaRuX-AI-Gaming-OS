using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Gaming home (Playnite / Xbox style): a Continue shelf of large tiles with one-click Play, then the full library as
/// a cover grid or list with search, sort, filters, multi-select bulk edits and a detail panel. It reads the library
/// through <see cref="GamingLibraryClient"/> and starts games only through the host's existing launch-consent flow.
/// The view, sort and filters are remembered in the desktop settings.
/// </summary>
internal sealed class GamingHomeView : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly Func<GameInstallationDto, Task> _launch;
    private readonly Func<Task> _refreshLibrary;

    // toolbar
    private readonly TextBox _search = new() { MaxLength = 100 };
    private readonly ComboBox _sort = new();
    private readonly ComboBox _source = new();
    private readonly ComboBox _platform = new();
    private readonly ComboBox _installed = new();
    private readonly ComboBox _tag = new();
    private readonly TextBox _collectionFilter = new() { MaxLength = 40 };
    private readonly Button _collectionApply = new();
    private readonly ToggleButton _favoritesOnly = new() { Content = "★" };
    private readonly ToggleButton _showHidden = new();
    private readonly ToggleButton _layout = new();
    private readonly ToggleButton _metadata = new();
    private bool _metadataRunning;
    private readonly ToggleButton _selectMode = new();
    private readonly Button _refresh = new();
    private readonly VariableSizedWrapGrid _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    // Only shown after a successful query returning no titles. Failed queries keep
    // their error status and never masquerade as an empty game library.
    private readonly Border _emptyState = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1), Padding = new Thickness(24) };
    private readonly TextBlock _emptyTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _emptyDescription = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _clearFilters = new() { Visibility = Visibility.Collapsed, MinWidth = 160 };

    // bulk
    private readonly Border _bulkBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _bulkCount = new();
    private readonly TextBox _bulkTag = new() { MaxLength = 40 };
    private readonly TextBox _bulkCollection = new() { MaxLength = 40 };
    private readonly VariableSizedWrapGrid _bulkActions = new() { Orientation = Orientation.Horizontal };

    // content
    private readonly TextBlock _titleHeading = new();
    private readonly TextBlock _introDescription = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _continueHeading = new();
    private readonly GameTileGrid _shelf = new(GameTileGrid.Presentation.Shelf);
    private readonly TextBlock _libraryHeading = new();
    private GameTileGrid _grid = new(GameTileGrid.Presentation.Grid);
    private readonly Grid _body = new();
    private readonly StackPanel _libraryColumn = new() { Spacing = 10 };
    private readonly GameDetailPanel _detail;

    private readonly List<Button> _buttons = new();
    private readonly List<TextBox> _boxes = new();
    private readonly List<Control> _controls = new();

    private List<GameTitleDto> _titles = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _knownTags = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly SortedSet<string> _knownPlatforms = new(StringComparer.CurrentCultureIgnoreCase);
    private string? _openKey;
    private bool _console;
    private bool _applying;
    private bool _launching;
    private int _searchEditGeneration;
    private int _generation;
    private CancellationTokenSource? _cancellation;
    private GameTileGrid.Presentation _libraryPresentation = GameTileGrid.Presentation.Grid;

    internal GamingHomeView(Func<GameInstallationDto, Task> launch, Func<Task> refreshLibrary)
    {
        _launch = launch;
        _refreshLibrary = refreshLibrary;
        Spacing = 16;
        _detail = new GameDetailPanel(launch, refreshLibrary);
        AutomationProperties.SetAutomationId(this, "GameHome_Root");
        BuildToolbar();
        BuildBulkBar();
        LoadPreferences();
        BuildContent();
        Loaded += async (_, _) => await ReloadAsync();
        SizeChanged += (_, _) => ApplyBodyLayout();
        // The inner grid can change width independently as NavigationView's pane
        // expands/collapses, without firing a size change on this StackPanel.
        _body.SizeChanged += (_, _) => ApplyBodyLayout();
        ActualThemeChanged += (_, _) => { ApplyBrushes(); };
        ApplyBrushes();
    }

    // ------------------------------------------------------------------ preferences

    private static readonly string[] SortKeys = { GameTitleSorts.Name, GameTitleSorts.LastPlayed, GameTitleSorts.PlayTime, GameTitleSorts.RecentlyAdded };
    private static readonly string[] SourceKeys = { "", GameInstallationSources.Steam, GameInstallationSources.Epic, GameInstallationSources.Gog, GameInstallationSources.Manual, GameInstallationSources.Emulator };

    private void LoadPreferences()
    {
        AppSettings data = SettingsStore.Current.Data;
        _applying = true;
        try
        {
            SelectByTag(_sort, SortKeys.Contains(data.GameLibrarySort) ? data.GameLibrarySort : GameTitleSorts.Name);
            SelectByTag(_source, SourceKeys.Contains(data.GameLibrarySource) ? data.GameLibrarySource : "");
            SelectByTag(_installed, data.GameLibraryInstalled is "installed" or "notinstalled" ? data.GameLibraryInstalled : "");
            if (!string.IsNullOrWhiteSpace(data.GameLibraryPlatform)) _knownPlatforms.Add(data.GameLibraryPlatform.Trim());
            if (!string.IsNullOrWhiteSpace(data.GameLibraryTag)) _knownTags.Add(data.GameLibraryTag.Trim());
            RebuildFilterChoices(data.GameLibraryPlatform, data.GameLibraryTag);
            _favoritesOnly.IsChecked = data.GameLibraryFavoritesOnly;
            _showHidden.IsChecked = data.GameLibraryShowHidden;
            bool list = string.Equals(data.GameLibraryLayout, "list", StringComparison.OrdinalIgnoreCase);
            _layout.IsChecked = list;
            _libraryPresentation = list ? GameTileGrid.Presentation.List : GameTileGrid.Presentation.Grid;
            UpdateLayoutLabel();
        }
        finally { _applying = false; }
        _grid = new GameTileGrid(_libraryPresentation);
        WireGrid(_grid);
    }

    private void SavePreferences()
    {
        if (_applying) return;
        AppSettings data = SettingsStore.Current.Data;
        data.GameLibrarySort = TagOf(_sort) ?? GameTitleSorts.Name;
        data.GameLibraryLayout = _layout.IsChecked == true ? "list" : "grid";
        data.GameLibrarySource = TagOf(_source) ?? "";
        data.GameLibraryPlatform = TagOf(_platform) ?? "";
        data.GameLibraryInstalled = TagOf(_installed) ?? "";
        data.GameLibraryFavoritesOnly = _favoritesOnly.IsChecked == true;
        data.GameLibraryTag = TagOf(_tag) ?? "";
        data.GameLibraryShowHidden = _showHidden.IsChecked == true;
        SettingsStore.Current.Save();
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectByTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ building

    private static ComboBoxItem Choice(string text, string tag) => new() { Content = text, Tag = tag };

    private void Track(Control control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
        _controls.Add(control);
        if (control is Button b) _buttons.Add(b);
        if (control is TextBox t) _boxes.Add(t);
    }

    private void AddToolbar(FrameworkElement element) => _toolbar.Children.Add(element);

    private void BuildToolbar()
    {
        _search.Header = T("Search your games", "Cerca nei tuoi giochi");
        Track(_search, "GameTitles_Search_TextBox", T("Search your games", "Cerca nei tuoi giochi"));
        // Debounce typing (including the TV keyboard); an older request cannot overwrite newer results.
        _search.TextChanged += async (_, _) =>
        {
            int generation = ++_searchEditGeneration;
            await Task.Delay(300);
            if (generation != _searchEditGeneration) return;
            await ReloadAsync();
        };
        var searchKeyboard = ConsoleKeyboardFlyout.Create(_search, T("Done", "Fine"), T("Backspace", "Cancella"), T("Clear", "Svuota"), T("Space", "Spazio"));
        _search.KeyDown += (_, e) =>
        {
            if (_console && e.Key is Windows.System.VirtualKey.GamepadA or Windows.System.VirtualKey.GamepadY)
            {
                searchKeyboard.ShowAt(_search);
                e.Handled = true;
            }
        };

        _sort.Header = T("Sort by", "Ordina per");
        _sort.Items.Add(Choice(T("Name", "Nome"), GameTitleSorts.Name));
        _sort.Items.Add(Choice(T("Recently played", "Giocati di recente"), GameTitleSorts.LastPlayed));
        _sort.Items.Add(Choice(T("Play time", "Tempo di gioco"), GameTitleSorts.PlayTime));
        _sort.Items.Add(Choice(T("Recently added", "Aggiunti di recente"), GameTitleSorts.RecentlyAdded));
        _sort.SelectedIndex = 0;
        Track(_sort, "GameHome_Sort", T("Sort games by", "Ordina i giochi per"));

        _source.Header = T("Source", "Origine");
        _source.Items.Add(Choice(T("All sources", "Tutte le origini"), ""));
        _source.Items.Add(Choice("Steam", GameInstallationSources.Steam));
        _source.Items.Add(Choice("Epic Games", GameInstallationSources.Epic));
        _source.Items.Add(Choice("GOG", GameInstallationSources.Gog));
        _source.Items.Add(Choice(T("Local on this PC", "Locale su questo PC"), GameInstallationSources.Manual));
        _source.Items.Add(Choice(T("Emulator", "Emulatore"), GameInstallationSources.Emulator));
        _source.SelectedIndex = 0;
        Track(_source, "GameHome_SourceFilter", T("Filter by source", "Filtra per origine"));

        _platform.Header = T("Platform", "Piattaforma");
        Track(_platform, "GameHome_PlatformFilter", T("Filter by platform", "Filtra per piattaforma"));

        _installed.Header = T("Install state", "Stato installazione");
        _installed.Items.Add(Choice(T("All", "Tutti"), ""));
        _installed.Items.Add(Choice(T("Installed", "Installati"), "installed"));
        _installed.Items.Add(Choice(T("Not installed", "Non installati"), "notinstalled"));
        _installed.SelectedIndex = 0;
        Track(_installed, "GameHome_InstalledFilter", T("Filter by install state", "Filtra per stato di installazione"));

        _tag.Header = T("Tag", "Tag");
        Track(_tag, "GameHome_TagFilter", T("Filter by tag", "Filtra per tag"));
        RebuildFilterChoices("", "");

        foreach (ComboBox box in new[] { _sort, _source, _platform, _installed, _tag })
            box.SelectionChanged += async (_, _) =>
            {
                if (_applying) return;
                SavePreferences();
                await ReloadAsync();
            };

        _collectionFilter.Header = T("Collection (exact name)", "Collezione (nome esatto)");
        Track(_collectionFilter, "GameTitles_Collection_Filter", T("Filter by collection, exact name", "Filtra per collezione, nome esatto"));
        var collectionsKeyboard = ConsoleKeyboardFlyout.Create(_collectionFilter, T("Done", "Fine"), T("Backspace", "Cancella"), T("Clear", "Svuota"), T("Space", "Spazio"));
        _collectionFilter.KeyDown += (_, e) =>
        {
            if (_console && e.Key is Windows.System.VirtualKey.GamepadA or Windows.System.VirtualKey.GamepadY)
            {
                collectionsKeyboard.ShowAt(_collectionFilter);
                e.Handled = true;
            }
        };
        _collectionApply.Content = T("Apply collection filter", "Applica filtro collezione");
        Track(_collectionApply, "GameTitles_Collection_Apply", T("Apply collection filter", "Applica filtro collezione"));
        _collectionApply.Click += async (_, _) => await ReloadAsync();

        _favoritesOnly.Content = "★ " + T("Favorites", "Preferiti");
        Track(_favoritesOnly, "GameTitles_FavoritesOnly_Toggle", T("Show favorites only", "Mostra solo i preferiti"));
        _favoritesOnly.Click += async (_, _) => { SavePreferences(); await ReloadAsync(); };

        _showHidden.Content = T("Show hidden", "Mostra nascosti");
        Track(_showHidden, "GameHome_ShowHidden", T("Show hidden games", "Mostra i giochi nascosti"));
        _showHidden.Click += async (_, _) => { SavePreferences(); await ReloadAsync(); };

        Track(_layout, "GameHome_LayoutToggle", T("Switch between grid and list", "Passa tra griglia ed elenco"));
        _layout.Click += (_, _) =>
        {
            SavePreferences();
            UpdateLayoutLabel();
            SwapGrid(_layout.IsChecked == true ? GameTileGrid.Presentation.List : GameTileGrid.Presentation.Grid);
        };

        _selectMode.Content = T("Select several", "Selezione multipla");
        Track(_selectMode, "GameHome_SelectMode", T("Select several games", "Seleziona più giochi"));
        _selectMode.Click += (_, _) =>
        {
            bool on = _selectMode.IsChecked == true;
            if (!on) _selected.Clear();
            _grid.SetSelectMode(on);
            _shelf.SetSelectMode(false);
            UpdateBulkBar();
        };

        _metadata.Content = T("Covers & info", "Copertine e info");
        Track(_metadata, "GameHome_Metadata_Toggle", T("Look up covers and descriptions online", "Cerca copertine e descrizioni online"));
        _metadata.IsChecked = SettingsStore.Current.Data.GameMetadataEnabled;
        _metadata.Click += async (_, _) => await OnMetadataToggledAsync();

        _refresh.Content = T("Refresh", "Aggiorna");
        Track(_refresh, "GameTitles_Refresh_Button", T("Refresh the game list", "Aggiorna l'elenco dei giochi"));
        _refresh.Click += async (_, _) => await ReloadAsync(refresh: true);

        foreach (FrameworkElement element in new FrameworkElement[]
                 { _search, _sort, _source, _platform, _installed, _tag, _collectionFilter, _collectionApply,
                   _favoritesOnly, _showHidden, _layout, _metadata, _selectMode, _refresh })
            AddToolbar(element);

        AutomationProperties.SetAutomationId(_status, "GameTitles_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
    }

    private void UpdateLayoutLabel()
    {
        bool list = _layout.IsChecked == true;
        // The button shows what a click switches to.
        _layout.Content = list ? T("Grid view", "Vista a griglia") : T("List view", "Vista a elenco");
    }

    private void RebuildFilterChoices(string? platform, string? tag)
    {
        bool previous = _applying;
        _applying = true;
        try
        {
            string wantedPlatform = platform ?? TagOf(_platform) ?? "";
            string wantedTag = tag ?? TagOf(_tag) ?? "";
            _platform.Items.Clear();
            _platform.Items.Add(Choice(T("All platforms", "Tutte le piattaforme"), ""));
            foreach (string p in _knownPlatforms) _platform.Items.Add(Choice(p, p));
            SelectByTag(_platform, wantedPlatform);
            _tag.Items.Clear();
            _tag.Items.Add(Choice(T("All tags", "Tutti i tag"), ""));
            foreach (string t in _knownTags) _tag.Items.Add(Choice(t, t));
            SelectByTag(_tag, wantedTag);
        }
        finally { _applying = previous; }
    }

    private void BuildBulkBar()
    {
        AutomationProperties.SetAutomationId(_bulkBar, "GameHome_BulkBar");
        AutomationProperties.SetName(_bulkBar, T("Actions for the selected games", "Azioni per i giochi selezionati"));
        _bulkCount.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(_bulkCount, "GameHome_Bulk_Count");
        AutomationProperties.SetLiveSetting(_bulkCount, AutomationLiveSetting.Polite);

        Button Action(string text, string id, Func<Task> run)
        {
            var button = new Button { Content = text };
            Track(button, id, text);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await run(); }
                finally { button.IsEnabled = true; }
            };
            return button;
        }

        _bulkTag.Header = T("Tag to add", "Tag da aggiungere");
        Track(_bulkTag, "GameHome_Bulk_TagText", T("Tag to add to the selected games", "Tag da aggiungere ai giochi selezionati"));
        _bulkCollection.Header = T("Collection to add", "Collezione da aggiungere");
        Track(_bulkCollection, "GameHome_Bulk_CollectionText", T("Collection to add the selected games to", "Collezione a cui aggiungere i giochi selezionati"));

        var elements = new List<FrameworkElement>
        {
            Action("★ " + T("Favorite", "Preferito"), "GameHome_Bulk_Favorite", () => BulkAsync(favorite: true)),
            Action("☆ " + T("Unfavorite", "Rimuovi preferito"), "GameHome_Bulk_Unfavorite", () => BulkAsync(favorite: false)),
            Action(T("Hide", "Nascondi"), "GameHome_Bulk_Hide", () => BulkAsync(hidden: true)),
            Action(T("Unhide", "Mostra"), "GameHome_Bulk_Unhide", () => BulkAsync(hidden: false)),
            _bulkTag,
            Action(T("Add tag", "Aggiungi tag"), "GameHome_Bulk_AddTag", () => BulkListAsync(_bulkTag, tags: true)),
            _bulkCollection,
            Action(T("Add to collection", "Aggiungi a collezione"), "GameHome_Bulk_AddCollection", () => BulkListAsync(_bulkCollection, tags: false)),
            Action(T("Select all shown", "Seleziona tutti"), "GameHome_Bulk_SelectAll", () =>
            {
                foreach (GameTitleDto t in _titles.Take(GameTileGrid_MaxBulk)) _selected.Add(t.TitleKey);
                _grid.RefreshState(_selected, _openKey);
                UpdateBulkBar();
                return Task.CompletedTask;
            }),
            Action(T("Clear selection", "Deseleziona"), "GameHome_Bulk_Clear", () =>
            {
                _selected.Clear();
                _grid.RefreshState(_selected, _openKey);
                UpdateBulkBar();
                return Task.CompletedTask;
            }),
        };
        foreach (FrameworkElement element in elements) _bulkActions.Children.Add(element);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(_bulkCount);
        content.Children.Add(_bulkActions);
        _bulkBar.Child = content;
    }

    private const int GameTileGrid_MaxBulk = BulkGameTitleRequest.MaxKeys;

    private void BuildContent()
    {
        _titleHeading.Text = T("Your games", "I tuoi giochi");
        _titleHeading.FontSize = 24;
        _titleHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(_titleHeading, AutomationHeadingLevel.Level2);
        AutomationProperties.SetAutomationId(_titleHeading, "GameHome_Heading");
        Children.Add(_titleHeading);
        _introDescription.Text = T(
            "One tile per game. A game found in a store, as a portable copy or through an emulator is shown once, with a choice of how to start it.",
            "Un riquadro per gioco. Un gioco trovato in uno store, come copia portable o tramite emulatore compare una sola volta, con la scelta di come avviarlo.");
        Children.Add(_introDescription);

        _continueHeading.Text = T("Continue", "Continua");
        _continueHeading.FontSize = 18;
        _continueHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(_continueHeading, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(_continueHeading, "GameHome_Continue_Heading");
        _continueHeading.Visibility = Visibility.Collapsed;
        _shelf.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_shelf, "GameHome_Continue_Shelf");
        AutomationProperties.SetName(_shelf, T("Continue playing", "Continua a giocare"));
        _shelf.OpenRequested += async t => await OpenAsync(t);
        _shelf.PlayRequested += async t => await PlayAsync(t);

        Children.Add(_continueHeading);
        Children.Add(_shelf);

        _libraryHeading.FontSize = 18;
        _libraryHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(_libraryHeading, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(_libraryHeading, "GameHome_Library_Heading");
        Children.Add(_libraryHeading);
        Children.Add(_toolbar);
        Children.Add(_status);
        // An intentional empty state is more actionable than a blank card grid.
        // The same panel is used for a new library and a filtered-out library.
        var emptyLayout = new StackPanel { Spacing = 12 };
        AutomationProperties.SetHeadingLevel(_emptyTitle, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(_emptyTitle, "GameHome_EmptyTitle");
        AutomationProperties.SetAutomationId(_emptyDescription, "GameHome_EmptyDescription");
        emptyLayout.Children.Add(_emptyTitle);
        emptyLayout.Children.Add(_emptyDescription);
        _clearFilters.Content = T("Clear filters", "Cancella filtri");
        _clearFilters.MinHeight = 44;
        _clearFilters.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(_clearFilters, "GameHome_ClearFilters");
        AutomationProperties.SetName(_clearFilters, T("Clear all game filters", "Cancella tutti i filtri dei giochi"));
        _clearFilters.Click += async (_, _) => await ClearFiltersAsync();
        emptyLayout.Children.Add(_clearFilters);
        _emptyState.Child = emptyLayout;
        AutomationProperties.SetAutomationId(_emptyState, "GameHome_EmptyState");
        Children.Add(_emptyState);
        Children.Add(_bulkBar);

        _body.ColumnSpacing = 16;
        _body.RowSpacing = 12;
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detail.CloseRequested += CloseDetail;
        _detail.StatusMessage += message => _status.Text = message;
        _detail.Changed += async changed => await OnTitleChangedAsync(changed);
        _detail.Removed += async key =>
        {
            _selected.Remove(key);
            CloseDetail();
            await ReloadAsync();
        };
        _libraryColumn.Children.Add(_grid);
        _body.Children.Add(_libraryColumn);
        _body.Children.Add(_detail);
        ApplyBodyLayout();
        Children.Add(_body);
        SetConsolePresentation(false);
    }

    private void WireGrid(GameTileGrid grid)
    {
        grid.OpenRequested += async t => await OpenAsync(t);
        grid.PlayRequested += async t => await PlayAsync(t);
        grid.SelectionToggled += t =>
        {
            if (!_selected.Remove(t.TitleKey)) _selected.Add(t.TitleKey);
            _grid.RefreshState(_selected, _openKey);
            UpdateBulkBar();
        };
        AutomationProperties.SetAutomationId(grid, "GameHome_Grid");
        AutomationProperties.SetName(grid, T("Game library", "Libreria giochi"));
    }

    private void SwapGrid(GameTileGrid.Presentation presentation)
    {
        _libraryPresentation = presentation;
        _libraryColumn.Children.Remove(_grid);
        _grid = new GameTileGrid(presentation);
        WireGrid(_grid);
        _grid.SetConsole(_console);
        _grid.SetSelectMode(_selectMode.IsChecked == true);
        _libraryColumn.Children.Add(_grid);
        _grid.Render(_titles, _selected, _openKey);
    }

    private void ApplyBrushes()
    {
        _bulkBar.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceSubtleBrush");
        _bulkBar.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeStrongBrush");
        // These are retained fields so a live Dark / Light / High Contrast switch
        // repaints every label, not only headings built after the switch.
        _titleHeading.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _introDescription.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        _continueHeading.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _libraryHeading.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _status.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        _emptyState.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
        _emptyState.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
        _emptyTitle.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _emptyDescription.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
    }

    /// <summary>Wide windows show the detail card beside the library; narrow ones put it above the tiles.</summary>
    private void ApplyBodyLayout()
    {
        // Measure the width the library actually receives after shell/sidebar
        // layout, not the outer page width (which could cause a cramped split).
        double width = _body.ActualWidth > 0 ? _body.ActualWidth : ActualWidth;
        bool side = width >= (_console ? 1480 : 1100);
        _detail.Width = side ? (_console ? 480 : 380) : double.NaN;
        _detail.VerticalAlignment = VerticalAlignment.Top;
        if (side)
        {
            Grid.SetRow(_libraryColumn, 0); Grid.SetColumn(_libraryColumn, 0); Grid.SetColumnSpan(_libraryColumn, 1);
            Grid.SetRow(_detail, 0); Grid.SetColumn(_detail, 1); Grid.SetColumnSpan(_detail, 1);
        }
        else
        {
            Grid.SetRow(_detail, 0); Grid.SetColumn(_detail, 0); Grid.SetColumnSpan(_detail, 2);
            Grid.SetRow(_libraryColumn, 1); Grid.SetColumn(_libraryColumn, 0); Grid.SetColumnSpan(_libraryColumn, 2);
        }
    }

    // ------------------------------------------------------------------ presentation

    internal void SetConsolePresentation(bool enabled)
    {
        _console = enabled;
        double size = enabled ? 20 : 14;
        foreach (TextBox box in _boxes) { box.FontSize = size; box.MinHeight = enabled ? 56 : 44; }
        foreach (Button button in _buttons) { button.FontSize = size; button.MinHeight = enabled ? 56 : 44; }
        foreach (ComboBox box in new[] { _sort, _source, _platform, _installed, _tag }) { box.FontSize = size; box.MinHeight = enabled ? 56 : 44; }
        foreach (ToggleButton toggle in new[] { _favoritesOnly, _showHidden, _layout, _selectMode }) { toggle.FontSize = size; toggle.MinHeight = enabled ? 56 : 44; }
        _status.FontSize = size;
        _emptyTitle.FontSize = enabled ? 26 : 20;
        _emptyDescription.FontSize = size;
        _clearFilters.FontSize = size;
        _clearFilters.MinHeight = enabled ? 56 : 44;
        _bulkCount.FontSize = size;
        _toolbar.ItemWidth = enabled ? 300 : 210;
        _toolbar.ItemHeight = enabled ? 100 : 72;
        foreach (FrameworkElement element in _toolbar.Children) { element.Margin = new Thickness(0, 0, 8, 8); element.VerticalAlignment = VerticalAlignment.Bottom; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
        _bulkActions.ItemWidth = enabled ? 300 : 210;
        _bulkActions.ItemHeight = enabled ? 100 : 72;
        foreach (FrameworkElement element in _bulkActions.Children) { element.Margin = new Thickness(0, 0, 8, 8); element.VerticalAlignment = VerticalAlignment.Bottom; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
        _continueHeading.FontSize = enabled ? 26 : 18;
        _libraryHeading.FontSize = enabled ? 26 : 18;
        _shelf.SetConsole(enabled);
        _grid.SetConsole(enabled);
        _detail.SetConsole(enabled);
        ApplyBodyLayout();
    }

    // ------------------------------------------------------------------ data

    /// <summary>One-shot catalog refresh after a foreground return from Gaming. Never scan stores periodically or while a game owns the PC.</summary>
    internal Task RefreshAfterGameAsync() => ReloadAsync(refresh: true);

    internal bool FocusSearch() => _search.Focus(FocusState.Programmatic);

    private GameTitleListQuery CurrentQuery(bool refresh) => new()
    {
        Text = _search.Text,
        Source = TagOf(_source),
        Platform = TagOf(_platform),
        Installed = TagOf(_installed) switch { "installed" => true, "notinstalled" => false, _ => null },
        FavoritesOnly = _favoritesOnly.IsChecked == true,
        Tag = TagOf(_tag),
        Collection = _collectionFilter.Text,
        Hidden = _showHidden.IsChecked == true ? "include" : null,
        Sort = TagOf(_sort) is { } sort && sort != GameTitleSorts.Name ? sort : null,
        Refresh = refresh,
    };

    private bool FiltersActive =>
        !string.IsNullOrWhiteSpace(_search.Text) || !string.IsNullOrEmpty(TagOf(_source)) || !string.IsNullOrEmpty(TagOf(_platform))
        || !string.IsNullOrEmpty(TagOf(_installed)) || _favoritesOnly.IsChecked == true || !string.IsNullOrEmpty(TagOf(_tag))
        || !string.IsNullOrWhiteSpace(_collectionFilter.Text) || _showHidden.IsChecked == true;

    /// <summary>
    /// Recover from a zero-result filter combination without resetting the user's
    /// preferred sort, library layout or existing saved games. TextChanged's
    /// pending debounce is invalidated so an old search cannot overwrite recovery.
    /// </summary>
    private async Task ClearFiltersAsync()
    {
        if (!FiltersActive) return;
        ++_searchEditGeneration;
        _applying = true;
        try
        {
            _search.Text = string.Empty;
            SelectByTag(_source, "");
            SelectByTag(_platform, "");
            SelectByTag(_installed, "");
            SelectByTag(_tag, "");
            _collectionFilter.Text = string.Empty;
            _favoritesOnly.IsChecked = false;
            _showHidden.IsChecked = false;
        }
        finally { _applying = false; }
        ++_searchEditGeneration;
        SavePreferences();
        await ReloadAsync();
        _search.Focus(FocusState.Programmatic);
    }

    /// <summary>Turning the option on needs an explicit consent (game names leave the PC); turning it off is immediate.</summary>
    private async Task OnMetadataToggledAsync()
    {
        AppSettings data = SettingsStore.Current.Data;
        if (_metadata.IsChecked == true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Look up covers and info online?", "Cercare copertine e info online?"),
                Content = T(GameMetadataService.ConsentText,
                    "Copertine e info cercano i tuoi giochi per nome sullo store pubblico di Steam e su Wikipedia. I nomi dei giochi vengono inviati a questi servizi; non vengono inviati account, chiavi, tempo di gioco né percorsi dei file. Non viene contattato nulla mentre l'opzione è spenta o mentre un gioco è in esecuzione. Una copertina scelta da te non viene mai sostituita."),
                PrimaryButtonText = T("Turn on", "Attiva"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { _metadata.IsChecked = false; return; }
            data.GameMetadataEnabled = true;
            SettingsStore.Current.Save();
            await RunMetadataAsync();
        }
        else
        {
            data.GameMetadataEnabled = false;
            SettingsStore.Current.Save();
        }
    }

    private async Task RunMetadataAsync()
    {
        if (_metadataRunning || _titles.Count == 0) return;
        _metadataRunning = true;
        try
        {
            var snapshot = _titles.ToList();
            int found = await GameMetadataService.FetchMissingAsync(snapshot,
                async token => (await GamingOrchestratorClient.Current.GetStatusAsync(token))?.IsGamingActive == true,
                CancellationToken.None);
            if (found > 0) await ReloadAsync();
        }
        catch (Exception) { /* offline or cancelled: covers simply stay as they are */ }
        finally { _metadataRunning = false; }
    }

    internal async Task ReloadAsync(bool refresh = false)
    {
        int generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        var previous = _cancellation;
        _cancellation = cancellation;
        previous?.Cancel();
        try
        {
            var result = await GamingLibraryClient.Current.QueryTitlesAsync(CurrentQuery(refresh), cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested) return;
            if (!result.Succeeded || result.Value is null)
            {
                // A previous successful zero-result view is not evidence of an
                // empty library when the next query cannot reach Core.
                _emptyState.Visibility = Visibility.Collapsed;
                _clearFilters.Visibility = Visibility.Collapsed;
                _status.Text = T("The game list is unavailable right now.", "L'elenco dei giochi non è disponibile in questo momento.");
                return;
            }
            _titles = result.Value;
            foreach (GameTitleDto title in _titles)
            {
                foreach (string tag in title.Tags) _knownTags.Add(tag);
                foreach (GameInstallationDto installation in title.Installations)
                    if (!string.IsNullOrWhiteSpace(installation.Platform)) _knownPlatforms.Add(installation.Platform);
            }
            RebuildFilterChoices(null, null);
            _selected.IntersectWith(_titles.Select(t => t.TitleKey));
            if (_openKey is not null && _detail.Current is null) _openKey = null;
            RenderLibrary();
            if (SettingsStore.Current.Data.GameMetadataEnabled && !_metadataRunning) _ = RunMetadataAsync();
            await LoadContinueAsync(cancellation.Token, generation);
        }
        catch (OperationCanceledException) { /* Superseded by a newer search or filter. */ }
        catch (Exception)
        {
            if (generation == _generation && !cancellation.IsCancellationRequested)
            {
                _emptyState.Visibility = Visibility.Collapsed;
                _clearFilters.Visibility = Visibility.Collapsed;
                _status.Text = T("Could not read your games.", "Impossibile leggere i tuoi giochi.");
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }

    private void RenderLibrary()
    {
        bool empty = _titles.Count == 0;
        bool filtered = FiltersActive;
        _emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _clearFilters.Visibility = empty && filtered ? Visibility.Visible : Visibility.Collapsed;
        _emptyTitle.Text = filtered
            ? T("Nothing matches your filters", "Nessun risultato per questi filtri")
            : T("Your library starts here", "La tua libreria comincia qui");
        _emptyDescription.Text = filtered
            ? T("Clear the filters to see your complete library. Your games and settings will not be changed.",
                "Cancella i filtri per vedere tutta la libreria. Giochi e impostazioni non verranno modificati.")
            : T("Add a local game below, discover your installed stores, or import your own emulator games in Advanced Game Management.",
                "Aggiungi un gioco locale qui sotto, rileva quelli degli store installati oppure importa i tuoi giochi per emulatore in Gestione giochi avanzata.");
        _libraryHeading.Text = T("Library", "Libreria") + (_titles.Count > 0 ? $" · {_titles.Count}" : string.Empty);
        _status.Text = _titles.Count == 0
            ? FiltersActive
                ? T("No games match these filters.", "Nessun gioco corrisponde ai filtri.")
                : T("No games yet. Add one below, or use Advanced Game Management to import a folder or an emulator.",
                    "Ancora nessun gioco. Aggiungine uno qui sotto, oppure usa Advanced Game Management per importare una cartella o un emulatore.")
            : T($"{_titles.Count} games", $"{_titles.Count} giochi");
        _grid.Render(_titles, _selected, _openKey);
        UpdateBulkBar();
    }

    /// <summary>The Continue shelf: titles with a play history, most recently played first then most played, never hidden.</summary>
    private async Task LoadContinueAsync(CancellationToken ct, int generation)
    {
        var result = await GamingLibraryClient.Current.QueryTitlesAsync(new GameTitleListQuery { Sort = GameTitleSorts.LastPlayed }, ct);
        if (generation != _generation || ct.IsCancellationRequested) return;
        List<GameTitleDto> recent = result.Succeeded && result.Value is not null
            ? result.Value
                .Where(t => !t.Hidden && (t.LastPlayedUtc.HasValue || t.PlaySeconds > 0))
                .OrderByDescending(t => t.LastPlayedUtc.HasValue).ThenByDescending(t => t.LastPlayedUtc).ThenByDescending(t => t.PlaySeconds)
                .Take(10).ToList()
            : new List<GameTitleDto>();
        bool any = recent.Count > 0;
        _continueHeading.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _shelf.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _shelf.Render(recent, new HashSet<string>(StringComparer.Ordinal), null);
    }

    private async Task OnTitleChangedAsync(GameTitleDto changed)
    {
        _grid.UpdateTitle(changed);
        // A silent reload applies server-side filters and sorting and refreshes covers; the detail card is left alone.
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ actions

    private async Task OpenAsync(GameTitleDto title)
    {
        _openKey = title.TitleKey;
        _grid.RefreshState(_selected, _openKey);
        await _detail.ShowAsync(title);
        _detail.StartBringIntoView();
        _detail.FocusPrimary();
    }

    private void CloseDetail()
    {
        string? key = _openKey;
        _openKey = null;
        _detail.Hide();
        _grid.RefreshState(_selected, null);
        if (key is not null && !_grid.FocusTile(key)) _grid.FocusFirst();
    }

    private async Task PlayAsync(GameTitleDto title)
    {
        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        if (_launching || chosen is null || !GameUi.CanStart(chosen)) return;
        _launching = true;
        try
        {
            // The host shows its normal confirmation before anything starts; cancelling it starts nothing.
            await _launch(chosen);
            await ReloadAsync();
        }
        finally { _launching = false; }
    }

    private void UpdateBulkBar()
    {
        bool select = _selectMode.IsChecked == true;
        _bulkBar.Visibility = select ? Visibility.Visible : Visibility.Collapsed;
        _bulkCount.Text = _selected.Count == 0
            ? T("Select games with the tiles below.", "Seleziona i giochi con i riquadri qui sotto.")
            : T($"{_selected.Count} selected", $"{_selected.Count} selezionati");
        foreach (Control control in _controls)
            if (control is Button b && AutomationProperties.GetAutomationId(b).StartsWith("GameHome_Bulk_", StringComparison.Ordinal)
                && AutomationProperties.GetAutomationId(b) != "GameHome_Bulk_SelectAll")
                b.IsEnabled = _selected.Count > 0;
    }

    private List<GameTitleDto> SelectedTitles() => _titles.Where(t => _selected.Contains(t.TitleKey)).ToList();

    private async Task BulkAsync(bool? favorite = null, bool? hidden = null)
    {
        var keys = SelectedTitles().Select(t => t.TitleKey).ToList();
        if (keys.Count == 0) return;
        foreach (var chunk in keys.Chunk(BulkGameTitleRequest.MaxKeys))
        {
            var result = await GamingLibraryClient.Current.BulkUpdateTitlesAsync(
                new BulkGameTitleRequest(chunk.ToList()) { Favorite = favorite, Hidden = hidden });
            if (!result.Succeeded)
            {
                _status.Text = T("The change could not be saved: ", "Impossibile salvare la modifica: ") + result.Error;
                return;
            }
        }
        _status.Text = T($"Updated {keys.Count} games.", $"Aggiornati {keys.Count} giochi.");
        await ReloadAsync();
        _status.Text = T($"Updated {keys.Count} games.", $"Aggiornati {keys.Count} giochi.");
    }

    /// <summary>Adds one tag or collection to every selected title. The Core replaces the whole list per request,
    /// so titles are grouped by their resulting list and each group is one atomic bulk edit; existing entries are kept.</summary>
    private async Task BulkListAsync(TextBox input, bool tags)
    {
        string value = (input.Text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > 40) { _status.Text = T("Enter a name of up to 40 characters.", "Inserisci un nome di al massimo 40 caratteri."); return; }
        var groups = new Dictionary<string, (List<string> Values, List<string> Keys)>(StringComparer.Ordinal);
        foreach (GameTitleDto title in SelectedTitles())
        {
            var current = (tags ? title.Tags : title.Collections).ToList();
            if (!current.Contains(value, StringComparer.CurrentCultureIgnoreCase)) current.Add(value);
            string signature = string.Join("\u001f", current);
            if (!groups.TryGetValue(signature, out var group)) groups[signature] = group = (current, new List<string>());
            group.Keys.Add(title.TitleKey);
        }
        int updated = 0;
        foreach (var (values, keys) in groups.Values)
        {
            foreach (var chunk in keys.Chunk(BulkGameTitleRequest.MaxKeys))
            {
                var request = new BulkGameTitleRequest(chunk.ToList());
                request = tags ? request with { Tags = values } : request with { Collections = values };
                var result = await GamingLibraryClient.Current.BulkUpdateTitlesAsync(request);
                if (!result.Succeeded)
                {
                    _status.Text = (tags
                        ? T("Tags were not saved: ", "Tag non salvati: ")
                        : T("Collections were not saved: ", "Collezioni non salvate: ")) + result.Error;
                    return;
                }
                updated += chunk.Length;
            }
        }
        input.Text = string.Empty;
        await ReloadAsync();
        _status.Text = T($"Updated {updated} games.", $"Aggiornati {updated} giochi.");
    }
}
