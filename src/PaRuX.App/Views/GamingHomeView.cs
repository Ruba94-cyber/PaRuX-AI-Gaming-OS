using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly ComboBox _presets = new();
    private readonly Button _presetSave = new();
    private readonly Button _presetDelete = new();
    private readonly ToggleButton _metadata = new();
    private bool _metadataRunning;
    private readonly Button _steamGridDb = new();
    private readonly Button _igdb = new();
    private readonly Button _externalPreview = new();
    private readonly Button _externalSaved = new();
    private readonly Button _wishlist = new();
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
    private readonly GameHeroCard _hero = new();
    private readonly GameSideFilterPanel _side = new();
    private readonly Grid _main = new();
    private readonly StackPanel _mainColumn = new() { Spacing = 12 };
    private readonly TextBlock _quickResumeSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _quickResumeManage = new() { MinHeight = 44 };
    private readonly Button _gameAssetStudio = new() { MinHeight = 44 };
    private readonly Button _installedMods = new() { MinHeight = 44 };
    private readonly QuickResumePanel _quickResume = new();
    private List<GameTitleDto> _allTitles = new();
    private bool _recentOnly;
    private string _genre = string.Empty;

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
    private static readonly string[] SourceKeys = { "", GameInstallationSources.Steam, GameInstallationSources.Epic, GameInstallationSources.Gog, GameInstallationSources.Manual, GameInstallationSources.Emulator,
        GameInstallationSources.Ea, GameInstallationSources.BattleNet, GameInstallationSources.Ubisoft, GameInstallationSources.Xbox,
        GameInstallationSources.Playnite, GameInstallationSources.Heroic, GameInstallationSources.Lutris };

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
        // Headers above every filter doubled the toolbar's height and repeated what the selected entry already says
        // ("All sources", "All platforms"): the controls now carry their meaning in the entry itself, like Playnite's filter bar.
        _search.PlaceholderText = T("Search your games", "Cerca nei tuoi giochi");
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

        _sort.Items.Add(Choice(T("Sort: Name", "Ordina: Nome"), GameTitleSorts.Name));
        _sort.Items.Add(Choice(T("Sort: Recently played", "Ordina: Giocati di recente"), GameTitleSorts.LastPlayed));
        _sort.Items.Add(Choice(T("Sort: Play time", "Ordina: Tempo di gioco"), GameTitleSorts.PlayTime));
        _sort.Items.Add(Choice(T("Sort: Recently added", "Ordina: Aggiunti di recente"), GameTitleSorts.RecentlyAdded));
        _sort.SelectedIndex = 0;
        Track(_sort, "GameHome_Sort", T("Sort games by", "Ordina i giochi per"));

        _source.Items.Add(Choice(T("All sources", "Tutte le origini"), ""));
        _source.Items.Add(Choice("Steam", GameInstallationSources.Steam));
        _source.Items.Add(Choice("Epic Games", GameInstallationSources.Epic));
        _source.Items.Add(Choice("GOG", GameInstallationSources.Gog));
        _source.Items.Add(Choice("EA app", GameInstallationSources.Ea));
        _source.Items.Add(Choice("Battle.net", GameInstallationSources.BattleNet));
        _source.Items.Add(Choice("Ubisoft Connect", GameInstallationSources.Ubisoft));
        _source.Items.Add(Choice("Xbox", GameInstallationSources.Xbox));
        _source.Items.Add(Choice(T("Local on this PC", "Locale su questo PC"), GameInstallationSources.Manual));
        _source.Items.Add(Choice(T("Emulator", "Emulatore"), GameInstallationSources.Emulator));
        _source.Items.Add(Choice("Playnite", GameInstallationSources.Playnite));
        _source.Items.Add(Choice("Heroic", GameInstallationSources.Heroic));
        _source.Items.Add(Choice("Lutris", GameInstallationSources.Lutris));
        _source.SelectedIndex = 0;
        Track(_source, "GameHome_SourceFilter", T("Filter by source", "Filtra per origine"));

        Track(_platform, "GameHome_PlatformFilter", T("Filter by platform", "Filtra per piattaforma"));

        _installed.Items.Add(Choice(T("Installed or not", "Installati e non"), ""));
        _installed.Items.Add(Choice(T("Installed", "Installati"), "installed"));
        _installed.Items.Add(Choice(T("Not installed", "Non installati"), "notinstalled"));
        _installed.SelectedIndex = 0;
        Track(_installed, "GameHome_InstalledFilter", T("Filter by install state", "Filtra per stato di installazione"));

        Track(_tag, "GameHome_TagFilter", T("Filter by tag", "Filtra per tag"));
        RebuildFilterChoices("", "");

        foreach (ComboBox box in new[] { _sort, _source, _platform, _installed, _tag })
            box.SelectionChanged += async (_, _) =>
            {
                if (_applying) return;
                SavePreferences();
                await ReloadAsync();
            };

        _collectionFilter.PlaceholderText = T("Collection (exact name)", "Collezione (nome esatto)");
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

        // Named saved filters (search + sort + filters), kept in the desktop settings.
        Track(_presets, "GameHome_Presets", T("Saved filters", "Filtri salvati"));
        _presetSave.Content = T("Save filter...", "Salva filtro...");
        Track(_presetSave, "GameHome_PresetSave", T("Save the current search and filters under a name", "Salva ricerca e filtri correnti con un nome"));
        _presetSave.Click += async (_, _) => await SavePresetAsync();
        _presetDelete.Content = T("Delete filter", "Elimina filtro");
        Track(_presetDelete, "GameHome_PresetDelete", T("Delete the selected saved filter", "Elimina il filtro salvato selezionato"));
        _presetDelete.Click += (_, _) => DeleteSelectedPreset();
        _presets.SelectionChanged += async (_, _) =>
        {
            if (_applying) return;
            string name = TagOf(_presets) ?? "";
            _presetDelete.IsEnabled = name.Length > 0;
            GameFilterPreset? preset = SettingsStore.Current.Data.GameLibraryPresets?
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (preset is not null) await ApplyPresetAsync(preset);
        };
        RebuildPresetChoices(null);

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

        _steamGridDb.Content = T("SteamGridDB key...", "Chiave SteamGridDB...");
        Track(_steamGridDb, "GameHome_SteamGridDb_Button",
            T("Set the optional SteamGridDB API key for better covers", "Imposta la chiave API SteamGridDB facoltativa per copertine migliori"));
        _steamGridDb.IsEnabled = _metadata.IsChecked == true;
        _steamGridDb.Click += async (_, _) => await OnSteamGridDbKeyAsync();

        _igdb.Content = T("IGDB key...", "Chiave IGDB...");
        Track(_igdb, "GameHome_Igdb_Button",
            T("Set the optional IGDB credentials for more covers", "Imposta le credenziali IGDB facoltative per altre copertine"));
        _igdb.IsEnabled = _metadata.IsChecked == true;
        _igdb.Click += async (_, _) => await OnIgdbKeyAsync();

        _externalPreview.Content = T("Import library titles", "Importa titoli libreria");
        Track(_externalPreview, "GameHome_ExternalLibraryPreview_Button",
            T("Preview Playnite, Heroic or Lutris JSON before saving title-only metadata",
              "Anteprima JSON Playnite, Heroic o Lutris prima di salvare solo i titoli"));
        _externalPreview.Click += async (_, _) => await ShowExternalLibraryPreviewAsync();

        _externalSaved.Content = T("Imported titles", "Titoli importati");
        Track(_externalSaved, "GameHome_ExternalLibrarySaved_Button",
            T("Review or remove saved third-party library titles",
              "Controlla o rimuovi i titoli salvati di librerie esterne"));
        _externalSaved.Click += async (_, _) => await ShowSavedExternalLibraryAsync();

        _wishlist.Content = T("Wishlist", "Lista desideri");
        Track(_wishlist, "GameHome_Wishlist_Button",
            T("Manage your offline Steam wishlist and explicit price thresholds",
              "Gestisci la lista desideri Steam locale e le soglie prezzo esplicite"));
        _wishlist.Click += async (_, _) =>
        {
            _wishlist.IsEnabled = false;
            try { await GamingWishlistDialog.ShowAsync(XamlRoot); }
            finally { _wishlist.IsEnabled = true; }
        };

        _refresh.Content = T("Refresh", "Aggiorna");
        Track(_refresh, "GameTitles_Refresh_Button", T("Refresh the game list", "Aggiorna l'elenco dei giochi"));
        _refresh.Click += async (_, _) => await ReloadAsync(refresh: true);

        foreach (FrameworkElement element in new FrameworkElement[]
                 { _search, _sort, _source, _platform, _installed, _tag, _collectionFilter, _collectionApply,
                   _favoritesOnly, _showHidden, _layout, _presets, _presetSave, _presetDelete,
                   _metadata, _steamGridDb, _igdb, _externalPreview, _externalSaved, _wishlist, _selectMode, _refresh })
            AddToolbar(element);

        AutomationProperties.SetAutomationId(_status, "GameTitles_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
    }

    /// <summary>
    /// Read-only import preview. User supplies JSON explicitly; neither PaRuX nor
    /// the Core opens an executable, archive or vendor account in this workflow.
    /// Never echoes untrusted JSON or launch arguments in the output.
    /// </summary>
    private async Task ShowExternalLibraryPreviewAsync()
    {
        var provider = new ComboBox { Header = T("Export source", "Origine export") };
        provider.Items.Add(Choice("Playnite", "playnite"));
        provider.Items.Add(Choice("Heroic", "heroic"));
        provider.Items.Add(Choice("Lutris", "lutris"));
        provider.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(provider, "GameHome_ExternalLibraryProvider");
        var json = new TextBox
        {
            Header = T("Paste JSON export", "Incolla export JSON"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 512000,
            MinHeight = 100,
            MaxHeight = 260
        };
        AutomationProperties.SetAutomationId(json, "GameHome_ExternalLibraryJson");
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Preview first, then optionally save titles and source IDs only. No launch paths or account data are imported.",
                "Prima l'anteprima, poi puoi salvare solo titoli e identificativi. Nessun percorso di avvio o dato account viene importato.")
        });
        panel.Children.Add(provider);
        panel.Children.Add(json);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("External library preview", "Anteprima libreria esterna"),
            Content = panel,
            PrimaryButtonText = T("Preview", "Anteprima"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string source = (provider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        if (json.Text.Length == 0 || json.Text.Length > 512000) return;
        _externalPreview.IsEnabled = false;
        try
        {
            GamingCallResult<List<ExternalLibraryGamePreviewDto>> response =
                await GamingLibraryClient.Current.PreviewExternalLibraryAsync(source, json.Text);
            var output = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 100,
                MaxHeight = 420,
                Text = response.Succeeded && response.Value is { } games
                    ? (games.Count == 0
                        ? T("No supported entries found.", "Nessuna voce supportata trovata.")
                        : string.Join(Environment.NewLine,
                            games.Select(g => g.Title + " [" + g.Provider + ": " + g.ExternalId + "]")))
                    : T("This export could not be previewed. Nothing was imported.",
                        "Impossibile visualizzare questo export. Non è stato importato nulla.")
            };
            AutomationProperties.SetAutomationId(output, "GameHome_ExternalLibraryPreview_Result");
            var resultDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Preview results · title-only", "Risultati anteprima · solo titoli"),
                Content = output,
                PrimaryButtonText = response.Succeeded && response.Value?.Count > 0
                    ? T("Save titles only", "Salva solo i titoli") : string.Empty,
                CloseButtonText = T("Close without saving", "Chiudi senza salvare"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await resultDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                GamingCallResult<ExternalLibraryImportResultDto> imported =
                    await GamingLibraryClient.Current.ImportExternalLibraryAsync(source, json.Text, consent: true);
                if (imported.Succeeded && imported.Value is { } outcome)
                {
                    _status.Text = T($"Imported {outcome.Added} titles ({outcome.AlreadyPresent} already saved). Nothing was launched.",
                        $"Importati {outcome.Added} titoli ({outcome.AlreadyPresent} già salvati). Nessun gioco è stato avviato.");
                    await ReloadAsync(refresh: true);
                }
                else
                    _status.Text = T("Import failed; nothing was added.",
                        "Importazione non riuscita; non è stato aggiunto nulla.");
            }
        }
        finally
        {
            _externalPreview.IsEnabled = true;
        }
    }

    /// <summary>Metadata-only review and individual deletion, never game file deletion.</summary>
    private async Task ShowSavedExternalLibraryAsync()
    {
        _externalSaved.IsEnabled = false;
        try
        {
            GamingCallResult<List<ExternalLibraryGamePreviewDto>> response =
                await GamingLibraryClient.Current.GetImportedLibraryAsync();
            if (!response.Succeeded || response.Value is null)
            {
                _status.Text = T("Saved external titles could not be loaded.",
                    "Impossibile leggere i titoli esterni salvati.");
                return;
            }
            var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.Single };
            AutomationProperties.SetAutomationId(list, "GameHome_ExternalLibrarySaved_List");
            foreach (ExternalLibraryGamePreviewDto game in response.Value
                .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
                list.Items.Add(new ListViewItem
                {
                    Content = game.Title + " [" + game.Provider + ": " + game.ExternalId + "]",
                    Tag = game
                });
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = T("Saved titles only, not installed games. Select an item to forget its metadata. Game files are never deleted.",
                    "Solo titoli salvati, non giochi installati. Seleziona una voce per eliminarne i metadati. Nessun file del gioco sarà cancellato."),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(list);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Imported game titles", "Titoli giochi importati"),
                Content = content,
                PrimaryButtonText = response.Value.Count > 0
                    ? T("Remove selected", "Rimuovi selezionato") : string.Empty,
                CloseButtonText = T("Close", "Chiudi"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                list.SelectedItem is not ListViewItem { Tag: ExternalLibraryGamePreviewDto selected })
                return;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Remove imported title?", "Rimuovere il titolo importato?"),
                Content = T("Only the saved title and ID will be removed. No original game or launcher is changed.",
                    "Verranno rimossi solo titolo e identificativo salvati. Giochi e launcher originali non saranno modificati."),
                PrimaryButtonText = T("Remove metadata", "Rimuovi metadati"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            GamingCallResult<object> result =
                await GamingLibraryClient.Current.RemoveImportedLibraryGameAsync(selected.Provider, selected.ExternalId);
            _status.Text = result.Succeeded
                ? T("Imported metadata removed; game files unchanged.", "Metadati rimossi; file dei giochi invariati.")
                : T("Could not remove imported title.", "Impossibile rimuovere il titolo importato.");
            if (result.Succeeded) await ReloadAsync(refresh: true);
        }
        finally { _externalSaved.IsEnabled = true; }
    }

    private void RebuildPresetChoices(string? select)
    {
        bool previous = _applying;
        _applying = true;
        try
        {
            _presets.Items.Clear();
            _presets.Items.Add(Choice(T("Saved filters", "Filtri salvati"), ""));
            foreach (GameFilterPreset p in GameFilterPresets.Sanitize(SettingsStore.Current.Data.GameLibraryPresets))
                _presets.Items.Add(Choice(p.Name, p.Name));
            SelectByTag(_presets, select ?? "");
            _presetDelete.IsEnabled = !string.IsNullOrEmpty(TagOf(_presets));
        }
        finally { _applying = previous; }
    }

    private GameFilterPreset CurrentPreset(string name) => new()
    {
        Name = name,
        Text = _search.Text,
        Sort = TagOf(_sort) ?? "",
        Source = TagOf(_source) ?? "",
        Platform = TagOf(_platform) ?? "",
        Installed = TagOf(_installed) ?? "",
        FavoritesOnly = _favoritesOnly.IsChecked == true,
        Tag = TagOf(_tag) ?? "",
        Collection = _collectionFilter.Text,
        ShowHidden = _showHidden.IsChecked == true,
    };

    private async Task SavePresetAsync()
    {
        var name = new TextBox { MaxLength = GameFilterPresets.MaxNameLength, PlaceholderText = T("Name of this filter", "Nome di questo filtro") };
        AutomationProperties.SetAutomationId(name, "GameHome_PresetName");
        AutomationProperties.SetName(name, T("Name of the saved filter", "Nome del filtro salvato"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(error, "GameHome_PresetError");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Saves the search, sort and filters now shown. Saving under an existing name replaces it. Up to 12 filters.",
                "Salva ricerca, ordinamento e filtri attualmente mostrati. Salvare con un nome esistente lo sostituisce. Fino a 12 filtri.")
        });
        panel.Children.Add(name);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Save filter", "Salva filtro"),
            Content = panel,
            PrimaryButtonText = T("Save", "Salva"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Primary
        };
        List<GameFilterPreset>? updated = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            updated = GameFilterPresets.Upsert(SettingsStore.Current.Data.GameLibraryPresets, CurrentPreset(name.Text));
            if (updated is null)
            {
                args.Cancel = true;
                error.Text = T("Enter a name (up to 40 characters). If you already have 12 filters, delete one or reuse a name.",
                    "Inserisci un nome (massimo 40 caratteri). Se hai già 12 filtri, eliminane uno o riusa un nome.");
                error.Visibility = Visibility.Visible;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || updated is null) return;
        SettingsStore.Current.Data.GameLibraryPresets = updated;
        SettingsStore.Current.Save();
        RebuildPresetChoices(GameFilterPresets.Normalize(CurrentPreset(name.Text))?.Name);
    }

    private void DeleteSelectedPreset()
    {
        string selected = TagOf(_presets) ?? "";
        if (selected.Length == 0) return;
        SettingsStore.Current.Data.GameLibraryPresets = GameFilterPresets.Remove(SettingsStore.Current.Data.GameLibraryPresets, selected);
        SettingsStore.Current.Save();
        RebuildPresetChoices(null);
    }

    private async Task ApplyPresetAsync(GameFilterPreset preset)
    {
        ++_searchEditGeneration;
        _recentOnly = false;
        _genre = string.Empty;
        _applying = true;
        try
        {
            _search.Text = preset.Text;
            SelectByTag(_sort, SortKeys.Contains(preset.Sort) ? preset.Sort : GameTitleSorts.Name);
            SelectByTag(_source, SourceKeys.Contains(preset.Source) ? preset.Source : "");
            if (!string.IsNullOrWhiteSpace(preset.Platform)) _knownPlatforms.Add(preset.Platform);
            if (!string.IsNullOrWhiteSpace(preset.Tag)) _knownTags.Add(preset.Tag);
            RebuildFilterChoices(preset.Platform, preset.Tag);
            SelectByTag(_installed, preset.Installed);
            _collectionFilter.Text = preset.Collection;
            _favoritesOnly.IsChecked = preset.FavoritesOnly;
            _showHidden.IsChecked = preset.ShowHidden;
        }
        finally { _applying = false; }
        ++_searchEditGeneration;
        SavePreferences();
        await ReloadAsync();
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
            "Every game once, however you start it: Steam, Epic, GOG, a local copy or an emulator.",
            "Ogni gioco una sola volta, comunque lo avvii: Steam, Epic, GOG, copia locale o emulatore.");
        Children.Add(_introDescription);

        // Quick Resume status near the top: what is paused now and what the start-up recovery did. The full panel stays below.
        AutomationProperties.SetAutomationId(_quickResumeSummary, "GameHome_QuickResume_Summary");
        AutomationProperties.SetLiveSetting(_quickResumeSummary, AutomationLiveSetting.Polite);
        _quickResumeManage.Content = T("Quick Resume", "Quick Resume");
        Track(_quickResumeManage, "GameHome_QuickResume_Manage", T("Show Quick Resume: pause and resume open apps", "Mostra Quick Resume: metti in pausa e riprendi le app aperte"));
        _quickResumeManage.Click += (_, _) => { _quickResume.StartBringIntoView(); _ = _quickResume.RefreshAsync(); };
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        quickRow.Children.Add(_quickResumeManage);
        quickRow.Children.Add(new Border { Child = _quickResumeSummary, VerticalAlignment = VerticalAlignment.Center });
        _gameAssetStudio.Content = T("AI Game Asset Mods", "Mod IA asset dei giochi");
        Track(_gameAssetStudio, "GameHome_AssetStudio_Open",
            T("Open local AI texture and asset mod studio for compatible games",
              "Apri lo studio mod texture e asset IA locale per giochi compatibili"));
        _gameAssetStudio.Click += async (_, _) =>
            await GameAssetModStudioDialog.ShowAsync(XamlRoot);
        Children.Add(quickRow);
        // A first-class gaming-home workspace, not restricted to Minecraft or
        // requiring a paused game/process. Kept off the tight Quick Resume row.
        _installedMods.Content = T("Installed AI mods", "Mod IA installate");
        Track(_installedMods, "GameHome_InstalledMods_Open",
            T("Review and remove PaRuX-installed ZIP mod packages",
              "Controlla e rimuovi i pacchetti ZIP mod installati da PaRuX"));
        _installedMods.Click += async (_, _) =>
            await GameModDropInDialog.ShowInstalledAsync(XamlRoot);
        var modRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modRow.Children.Add(_gameAssetStudio);
        modRow.Children.Add(_installedMods);
        Children.Add(modRow);

        _hero.PlayRequested += async t => await PlayAsync(t);
        _hero.DetailsRequested += async t => await OpenAsync(t);
        Children.Add(_hero);

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
        _mainColumn.Children.Add(_libraryHeading);
        _mainColumn.Children.Add(_toolbar);
        _mainColumn.Children.Add(_status);
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
        _mainColumn.Children.Add(_emptyState);
        _mainColumn.Children.Add(_bulkBar);

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
        _mainColumn.Children.Add(_body);

        // Side filter rail + library. Arrow keys / D-pad move focus spatially between the rail, the toolbar and the tiles.
        _side.StoreChosen += source => Quiet(() => SelectByTag(_source, source));
        _side.InstalledChosen += state => Quiet(() => SelectByTag(_installed, state));
        _side.FavoritesChosen += on => Quiet(() => _favoritesOnly.IsChecked = on);
        _side.RecentChosen += on => _recentOnly = on;
        _side.GenreChosen += genre => _genre = genre;
        _side.Applied += async () =>
        {
            SavePreferences();
            await ReloadAsync();
        };
        _main.ColumnSpacing = 16;
        _main.RowSpacing = 12;
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _main.Children.Add(_side);
        _main.Children.Add(_mainColumn);
        _main.SizeChanged += (_, _) => ApplyMainLayout();
        ApplyMainLayout();
        Children.Add(_main);
        Children.Add(_quickResume);
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
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

    private void Quiet(Action change)
    {
        bool previous = _applying;
        _applying = true;
        try { change(); }
        finally { _applying = previous; }
    }

    /// <summary>A wide window shows the filter rail beside the library; a narrow one puts it above.</summary>
    private void ApplyMainLayout()
    {
        double width = _main.ActualWidth > 0 ? _main.ActualWidth : ActualWidth;
        bool rail = width >= 900;
        _side.Width = rail ? (_console ? 300 : 230) : double.NaN;
        _side.VerticalAlignment = VerticalAlignment.Top;
        if (rail)
        {
            Grid.SetRow(_side, 0); Grid.SetColumn(_side, 0); Grid.SetColumnSpan(_side, 1);
            Grid.SetRow(_mainColumn, 0); Grid.SetColumn(_mainColumn, 1); Grid.SetColumnSpan(_mainColumn, 1);
        }
        else
        {
            Grid.SetRow(_side, 0); Grid.SetColumn(_side, 0); Grid.SetColumnSpan(_side, 2);
            Grid.SetRow(_mainColumn, 1); Grid.SetColumn(_mainColumn, 0); Grid.SetColumnSpan(_mainColumn, 2);
        }
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
        foreach (ToggleButton toggle in new[] { _favoritesOnly, _showHidden, _layout, _metadata, _selectMode }) { toggle.FontSize = size; toggle.MinHeight = enabled ? 56 : 44; }
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
        _hero.SetConsole(enabled);
        _side.SetConsole(enabled);
        _quickResumeManage.MinHeight = enabled ? 56 : 44;
        _quickResumeManage.FontSize = enabled ? 20 : 14;
        _quickResumeSummary.FontSize = enabled ? 20 : 14;
        _gameAssetStudio.MinHeight = _installedMods.MinHeight = enabled ? 56 : 44;
        _gameAssetStudio.FontSize = _installedMods.FontSize = enabled ? 20 : 14;
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
        || !string.IsNullOrWhiteSpace(_collectionFilter.Text) || _showHidden.IsChecked == true || _recentOnly || _genre.Length > 0;

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
            _recentOnly = false;
            _genre = string.Empty;
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
            _steamGridDb.IsEnabled = true;
            _igdb.IsEnabled = true;
            await RunMetadataAsync();
        }
        else
        {
            data.GameMetadataEnabled = false;
            SettingsStore.Current.Save();
            _steamGridDb.IsEnabled = false;
            _igdb.IsEnabled = false;
        }
    }

    /// <summary>
    /// Optional SteamGridDB API key. The dialog says exactly what is sent; the key is stored with DPAPI (never in settings.json)
    /// and is never shown again, so the field is always empty and "Save" replaces a stored key.
    /// </summary>
    private async Task OnSteamGridDbKeyAsync()
    {
        if (!SettingsStore.Current.Data.GameMetadataEnabled) return;
        bool hasKey = SteamGridDbKeyStore.HasKey;
        var box = new PasswordBox
        {
            PlaceholderText = hasKey ? T("A key is saved. Paste a new one to replace it.", "Una chiave è salvata. Incollane una nuova per sostituirla.")
                                     : T("Paste your SteamGridDB API key", "Incolla la tua chiave API SteamGridDB")
        };
        AutomationProperties.SetAutomationId(box, "GameHome_SteamGridDb_PasswordBox");
        AutomationProperties.SetName(box, T("SteamGridDB API key", "Chiave API SteamGridDB"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(error, "GameHome_SteamGridDb_Error");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T(
                "SteamGridDB gives better portrait covers for games that Steam and Wikipedia cover poorly. It needs your own free API key (steamgriddb.com, Preferences, API). The key is optional. " +
                "When set, the game names and your key are sent to steamgriddb.com to find a cover; nothing else is sent. Nothing is contacted while Covers & info is off or while a game is running. " +
                "The key is stored encrypted for your Windows account only, never in the settings file, and is not shown again.",
                "SteamGridDB offre copertine verticali migliori per i giochi che Steam e Wikipedia coprono male. Serve la tua chiave API gratuita (steamgriddb.com, Preferences, API). La chiave è facoltativa. " +
                "Se impostata, i nomi dei giochi e la chiave vengono inviati a steamgriddb.com per trovare una copertina; non viene inviato altro. Non viene contattato nulla mentre Copertine e info è spento o mentre un gioco è in esecuzione. " +
                "La chiave è salvata cifrata solo per il tuo account Windows, mai nel file delle impostazioni, e non viene più mostrata.")
        });
        panel.Children.Add(box);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("SteamGridDB key", "Chiave SteamGridDB"),
            Content = panel,
            PrimaryButtonText = T("Save", "Salva"),
            SecondaryButtonText = T("Remove", "Rimuovi"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = hasKey
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (SteamGridDbKeyStore.Clean(box.Password) is null)
            {
                args.Cancel = true;
                error.Text = T("That does not look like an API key. Paste the key exactly as SteamGridDB shows it.",
                    "Non sembra una chiave API. Incolla la chiave esattamente come la mostra SteamGridDB.");
                error.Visibility = Visibility.Visible;
            }
            else if (!SteamGridDbKeyStore.Save(box.Password))
            {
                args.Cancel = true;
                error.Text = T("The key could not be saved securely.", "Impossibile salvare la chiave in modo sicuro.");
                error.Visibility = Visibility.Visible;
            }
        };
        ContentDialogResult result = await dialog.ShowAsync();
        box.Password = string.Empty;
        if (result == ContentDialogResult.Primary)
        {
            _status.Text = T("SteamGridDB key saved. Covers are looked up there first from the next refresh.",
                "Chiave SteamGridDB salvata. Le copertine verranno cercate prima lì dal prossimo aggiornamento.");
            _ = RunMetadataAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _status.Text = SteamGridDbKeyStore.Remove()
                ? T("SteamGridDB key removed.", "Chiave SteamGridDB rimossa.")
                : T("The SteamGridDB key could not be removed.", "Impossibile rimuovere la chiave SteamGridDB.");
        }
    }

    /// <summary>
    /// Optional IGDB credentials: the Client ID and Client Secret of the user's own free Twitch application. The dialog states what is
    /// sent and where; they are stored with DPAPI (never in settings.json) and never shown again.
    /// </summary>
    private async Task OnIgdbKeyAsync()
    {
        if (!SettingsStore.Current.Data.GameMetadataEnabled) return;
        bool hasKey = IgdbKeyStore.HasKey;
        var id = new TextBox { MaxLength = 64, PlaceholderText = T("Client ID", "Client ID") };
        var secret = new PasswordBox
        {
            PlaceholderText = hasKey ? T("Credentials are saved. Enter a new Client Secret to replace them.", "Credenziali salvate. Inserisci un nuovo Client Secret per sostituirle.")
                                     : T("Client Secret", "Client Secret")
        };
        AutomationProperties.SetAutomationId(id, "GameHome_Igdb_ClientId");
        AutomationProperties.SetName(id, T("IGDB Client ID", "Client ID IGDB"));
        AutomationProperties.SetAutomationId(secret, "GameHome_Igdb_ClientSecret");
        AutomationProperties.SetName(secret, T("IGDB Client Secret", "Client Secret IGDB"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(error, "GameHome_Igdb_Error");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T(
                "IGDB can fill covers and details that Steam, Wikipedia and SteamGridDB miss. It needs your own free Twitch application (dev.twitch.tv, Applications): paste its Client ID and Client Secret. This is optional. " +
                "When set, your Client ID and Secret are sent to id.twitch.tv to get a short-lived token, and the game names are sent to api.igdb.com; nothing else is sent. Nothing is contacted while Covers & info is off or while a game is running. " +
                "The credentials are stored encrypted for your Windows account only, never in the settings file, and are not shown again. Covers are cached on this PC and keep working offline.",
                "IGDB può completare copertine e dettagli che Steam, Wikipedia e SteamGridDB non trovano. Serve una tua applicazione Twitch gratuita (dev.twitch.tv, Applications): incolla Client ID e Client Secret. È facoltativo. " +
                "Se impostate, Client ID e Secret vengono inviati a id.twitch.tv per ottenere un token temporaneo e i nomi dei giochi a api.igdb.com; non viene inviato altro. Non viene contattato nulla mentre Copertine e info è spento o mentre un gioco è in esecuzione. " +
                "Le credenziali sono salvate cifrate solo per il tuo account Windows, mai nel file delle impostazioni, e non vengono più mostrate. Le copertine restano in cache su questo PC e funzionano anche offline.")
        });
        panel.Children.Add(id);
        panel.Children.Add(secret);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("IGDB credentials", "Credenziali IGDB"),
            Content = panel,
            PrimaryButtonText = T("Save", "Salva"),
            SecondaryButtonText = T("Remove", "Rimuovi"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = hasKey
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string joined = (id.Text ?? string.Empty).Trim() + ":" + secret.Password.Trim();
            if (GameMetadataParsing.ParseIgdbCredentials(joined) is null)
            {
                args.Cancel = true;
                error.Text = T("That does not look like a Twitch Client ID and Secret. Paste both exactly as the Twitch console shows them (letters and digits only).",
                    "Non sembrano un Client ID e un Secret Twitch. Incollali esattamente come li mostra la console Twitch (solo lettere e cifre).");
                error.Visibility = Visibility.Visible;
            }
            else if (!IgdbKeyStore.Save(joined))
            {
                args.Cancel = true;
                error.Text = T("The credentials could not be saved securely.", "Impossibile salvare le credenziali in modo sicuro.");
                error.Visibility = Visibility.Visible;
            }
        };
        ContentDialogResult result = await dialog.ShowAsync();
        secret.Password = string.Empty;
        if (result == ContentDialogResult.Primary)
        {
            _status.Text = T("IGDB credentials saved. They are used from the next refresh for games without a cover.",
                "Credenziali IGDB salvate. Verranno usate dal prossimo aggiornamento per i giochi senza copertina.");
            _ = RunMetadataAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _status.Text = IgdbKeyStore.Remove()
                ? T("IGDB credentials removed.", "Credenziali IGDB rimosse.")
                : T("The IGDB credentials could not be removed.", "Impossibile rimuovere le credenziali IGDB.");
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
            if (GameMetadataService.TakeNotice() is string notice)
                _status.Text = T(notice, notice.StartsWith("IGDB", StringComparison.Ordinal)
                    ? "IGDB ha rifiutato Client ID/Secret: le copertine arrivano solo dalle altre fonti. Controlla o sostituisci le credenziali con \"Chiave IGDB...\"."
                    : "SteamGridDB ha rifiutato la chiave API: le copertine arrivano solo da Steam e Wikipedia. Controlla o sostituisci la chiave con \"Chiave SteamGridDB...\".");
        }
        catch (Exception) { /* offline or cancelled: covers simply stay as they are */ }
        finally { _metadataRunning = false; }
    }

    private int _reloadRetries;

    /// <summary>The Core may still be starting when the Gaming tab is first opened: ask again a few times, with growing pauses,
    /// instead of leaving "unavailable" on screen until the user presses Refresh.</summary>
    private void ScheduleReloadRetry(int generation)
    {
        if (_reloadRetries >= 6) return;
        int delayMs = 1500 * (1 << Math.Min(_reloadRetries++, 3));
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (generation == _generation) _ = ReloadAsync();
            });
        });
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
                ScheduleReloadRetry(generation);
                return;
            }
            _titles = ApplySideFilter(result.Value);
            _reloadRetries = 0;
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

    private static IReadOnlyList<string> GenresOf(string titleKey) =>
        GameMetadataService.TryGetInfo(titleKey)?.Genres ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The recently-played and genre rail choices are applied here; store, install state and favourites go to the Core with the query.</summary>
    private List<GameTitleDto> ApplySideFilter(List<GameTitleDto> fromCore)
    {
        if (!_recentOnly && _genre.Length == 0) return fromCore;
        var filter = new GameHomeFilter { RecentlyPlayedOnly = _recentOnly, Genre = _genre };
        // The Core already ordered the list; keep that order by filtering without re-sorting.
        var keep = GameLibraryHome.Apply(fromCore, filter, null, GenresOf, DateTime.UtcNow).Select(t => t.TitleKey).ToHashSet(StringComparer.Ordinal);
        return fromCore.Where(t => keep.Contains(t.TitleKey)).ToList();
    }

    private GameHomeFilter ActiveSideFilter() => new()
    {
        Source = TagOf(_source) ?? string.Empty,
        Installed = TagOf(_installed) switch { "installed" => true, "notinstalled" => false, _ => null },
        FavoritesOnly = _favoritesOnly.IsChecked == true,
        RecentlyPlayedOnly = _recentOnly,
        Genre = _genre,
    };

    /// <summary>
    /// The unfiltered library feeds the hero (last played game), the Continue shelf (the next most recent), the filter rail counts and the
    /// Quick Resume summary. Titles with a play history only; hidden titles never appear in any of them.
    /// </summary>
    private async Task LoadContinueAsync(CancellationToken ct, int generation)
    {
        var result = await GamingLibraryClient.Current.QueryTitlesAsync(new GameTitleListQuery { Sort = GameTitleSorts.LastPlayed }, ct);
        if (generation != _generation || ct.IsCancellationRequested) return;
        _allTitles = result.Succeeded && result.Value is not null ? result.Value : new List<GameTitleDto>();
        GameTitleDto? hero = GameLibraryHome.PickHero(_allTitles);
        _hero.Show(hero);
        List<GameTitleDto> recent = _allTitles
            .Where(t => !t.Hidden && (t.LastPlayedUtc.HasValue || t.PlaySeconds > 0) && (hero is null || t.TitleKey != hero.TitleKey))
            .OrderByDescending(t => t.LastPlayedUtc.HasValue).ThenByDescending(t => t.LastPlayedUtc).ThenByDescending(t => t.PlaySeconds)
            .Take(10).ToList();
        bool any = recent.Count > 0;
        _continueHeading.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _shelf.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _shelf.Render(recent, new HashSet<string>(StringComparer.Ordinal), null);
        _side.Update(GameLibraryHome.Facets(_allTitles, GenresOf, DateTime.UtcNow), ActiveSideFilter());
        await RefreshQuickResumeSummaryAsync(ct, generation);
    }

    private async Task RefreshQuickResumeSummaryAsync(CancellationToken ct, int generation)
    {
        var status = await GamingLibraryClient.Current.GetQuickResumeAsync(ct);
        if (generation != _generation || ct.IsCancellationRequested) return;
        bool italian = T("en", "it") == "it";
        _quickResumeSummary.Text = status.Succeeded && status.Value is not null
            ? "Quick Resume: " + GameLibraryHome.QuickResumeSummary(status.Value, italian)
            : "Quick Resume: " + T("status unavailable right now.", "stato non disponibile in questo momento.");
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
