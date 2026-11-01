using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

/// <summary>
/// The "one title, many ways to start it" list of the Games workspace plus the opt-in Advanced Game Management
/// section (import an installed or portable game from a folder, emulators). It lives inside the existing library
/// view and uses the same Core client, busy state and launch confirmations; it adds no separate screen.
/// </summary>
internal sealed class UniversalLibraryPanel : StackPanel
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private readonly Func<GameInstallationDto, Task> _launch;
    private readonly Func<Task> _refreshLibrary;
    private bool _console;
    private Expander? _advanced;

    // titles
    private readonly TextBox _search = new();
    private readonly TextBox _collectionFilter = new() { MaxLength = 40 };
    private readonly ToggleButton _favoritesOnly = new() { Content = "★" };
    private readonly Button _reload = new();
    private readonly StackPanel _titleList = new() { Spacing = 8 };
    private readonly TextBlock _titleStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private List<GameTitleDto> _titles = new();
    private int _page;
    private const int PageSize = 25;

    // advanced
    private readonly TextBox _folder = new();
    private readonly Button _browseFolder = new();
    private readonly Button _inspectFolder = new();
    private readonly StackPanel _candidates = new() { Spacing = 6 };
    private readonly TextBlock _folderStatus = new() { TextWrapping = TextWrapping.Wrap };

    private readonly ComboBox _emulatorBox = new() { MinWidth = 260 };
    private readonly TextBox _emulatorExe = new();
    private readonly TextBox _emulatorFolder = new();
    private readonly TextBox _emulatorCore = new();
    private readonly TextBox _emulatorExtra = new();
    private readonly TextBox _emulatorInputProfile = new() { MaxLength = 80, IsEnabled = false };
    private readonly ComboBox _emulatorLaunchMode = new() { MinWidth = 230 };
    private readonly StackPanel _emulatorGames = new() { Spacing = 4 };
    private readonly TextBlock _emulatorStatus = new() { TextWrapping = TextWrapping.Wrap };
    private EmulatorListDto? _emulators;
    private readonly List<(CheckBox Box, EmulatedGameDto Game)> _gameChecks = new();

    public UniversalLibraryPanel(Func<GameInstallationDto, Task> launch, Func<Task> refreshLibrary)
    {
        _launch = launch;
        _refreshLibrary = refreshLibrary;
        Spacing = 12;
        BuildTitles();
        BuildAdvanced();
        Loaded += async (_, _) => await ReloadTitlesAsync();
    }

    public void SetConsolePresentation(bool enabled)
    {
        _console = enabled;
        if (_advanced is not null)
        {
            _advanced.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            if (enabled) _advanced.IsExpanded = false;
        }
        foreach (var box in new[] { _search, _folder, _emulatorExe, _emulatorFolder, _emulatorCore, _emulatorExtra, _emulatorInputProfile })
            box.FontSize = enabled ? 20 : 14;
        foreach (var button in new[] { _reload, _previous, _next, _browseFolder, _inspectFolder })
        {
            button.FontSize = enabled ? 20 : 14;
            button.MinHeight = enabled ? 56 : 32;
        }
        RenderTitles();
    }

    // ---------------------------------------------------------------- titles

    private void BuildTitles()
    {
        Children.Add(new TextBlock
        {
            Text = T("Your games", "I tuoi giochi"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = T("One card per game. A game found in a store, as a portable copy or through an emulator is shown once, with a choice of how to start it.",
                     "Una scheda per gioco. Un gioco trovato in uno store, come copia portable o tramite emulatore compare una sola volta, con la scelta di come avviarlo."),
            TextWrapping = TextWrapping.Wrap
        });
        _search.Header = T("Search your games", "Cerca nei tuoi giochi");
        _search.MaxLength = 100;
        AutomationProperties.SetAutomationId(_search, "GameTitles_Search_TextBox");
        _search.TextChanged += async (_, _) => { _page = 0; await ReloadTitlesAsync(); };
        _favoritesOnly.Click += async (_, _) => { _page = 0; await ReloadTitlesAsync(); };
        AutomationProperties.SetAutomationId(_favoritesOnly, "GameTitles_FavoritesOnly_Toggle");
        AutomationProperties.SetName(_favoritesOnly, T("Show favorites only", "Mostra solo i preferiti"));
        _reload.Content = T("Refresh", "Aggiorna");
        AutomationProperties.SetAutomationId(_reload, "GameTitles_Refresh_Button");
        _reload.Click += async (_, _) => await ReloadTitlesAsync();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(_search); bar.Children.Add(_favoritesOnly); bar.Children.Add(_reload);
        Children.Add(bar);
        _collectionFilter.Header = T("Filter collection (exact name, optional)", "Filtra collezione (nome esatto, facoltativo)");
        AutomationProperties.SetAutomationId(_collectionFilter, "GameTitles_Collection_Filter");
        var filterCollection = new Button { Content = T("Apply collection filter", "Applica filtro collezione") };
        AutomationProperties.SetAutomationId(filterCollection, "GameTitles_Collection_Apply");
        filterCollection.Click += async (_, _) => { _page = 0; await ReloadTitlesAsync(); };
        var collectionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        collectionRow.Children.Add(_collectionFilter);
        collectionRow.Children.Add(filterCollection);
        Children.Add(collectionRow);
        AutomationProperties.SetAutomationId(_titleStatus, "GameTitles_Status_Text");
        AutomationProperties.SetLiveSetting(_titleStatus, AutomationLiveSetting.Polite);
        Children.Add(_titleStatus);
        Children.Add(_titleList);
        _previous.Content = T("Previous", "Precedente");
        _next.Content = T("Next", "Successiva");
        _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderTitles(); } };
        _next.Click += (_, _) => { if ((_page + 1) * PageSize < _titles.Count) { _page++; RenderTitles(); } };
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pages.Children.Add(_previous); pages.Children.Add(_next);
        Children.Add(pages);
    }

    private async Task ReloadTitlesAsync()
    {
        try
        {
            var result = await GamingOrchestratorClient.Current.GetTitlesAsync(
                _search.Text, null, _favoritesOnly.IsChecked == true, collection: _collectionFilter.Text);
            if (!result.Succeeded || result.Value is null)
            {
                _titleStatus.Text = T("The game list is unavailable right now.", "L'elenco dei giochi non è disponibile in questo momento.");
                return;
            }
            _titles = result.Value;
            RenderTitles();
        }
        catch (Exception)
        {
            _titleStatus.Text = T("Could not read your games.", "Impossibile leggere i tuoi giochi.");
        }
    }

    private void RenderTitles()
    {
        _titleList.Children.Clear();
        int pages = Math.Max(1, (_titles.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page + 1 < pages;
        _titleStatus.Text = _titles.Count == 0
            ? T("No games yet. Add one below, or use Advanced Game Management to import a folder or an emulator.",
                "Ancora nessun gioco. Aggiungine uno qui sotto, oppure usa Advanced Game Management per importare una cartella o un emulatore.")
            : T($"{_titles.Count} games · page {_page + 1} of {pages}", $"{_titles.Count} giochi · pagina {_page + 1} di {pages}");

        foreach (GameTitleDto title in _titles.Skip(_page * PageSize).Take(PageSize))
            _titleList.Children.Add(BuildTitleCard(title));
    }

    private UIElement BuildTitleCard(GameTitleDto title)
    {
        var card = new StackPanel { Spacing = _console ? 10 : 4, Padding = new Thickness(_console ? 20 : 12) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var star = new ToggleButton { Content = title.Favorite ? "★" : "☆", IsChecked = title.Favorite, MinHeight = _console ? 56 : 32, FontSize = _console ? 22 : 14 };
        AutomationProperties.SetName(star, T("Favorite: ", "Preferito: ") + title.Name);
        AutomationProperties.SetAutomationId(star, "GameTitle_Favorite_" + title.TitleKey);
        star.Click += async (_, _) =>
        {
            bool wanted = star.IsChecked == true;
            var result = await GamingOrchestratorClient.Current.SetTitleFavoriteAsync(title.TitleKey, wanted);
            if (!result.Succeeded) { star.IsChecked = !wanted; return; }
            star.Content = wanted ? "★" : "☆";
        };
        header.Children.Add(star);
        header.Children.Add(new TextBlock
        {
            Text = title.Name,
            FontSize = _console ? 24 : 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        card.Children.Add(header);

        card.Children.Add(new TextBlock
        {
            Text = PlaySummary(title),
            FontSize = _console ? 18 : 13,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        });

        GameInstallationDto chosen = title.Installations.FirstOrDefault(i => i.InstallationId == title.DefaultInstallationId)
                                     ?? title.Installations[0];
        if (title.Installations.Count > 1)
        {
            var choice = new ComboBox { MinWidth = 320, Header = T("Start with", "Avvia con"), FontSize = _console ? 20 : 14 };
            foreach (GameInstallationDto installation in title.Installations)
                choice.Items.Add(new ComboBoxItem { Content = Describe(installation), Tag = installation });
            choice.SelectedIndex = Math.Max(0, title.Installations.ToList().IndexOf(chosen));
            AutomationProperties.SetAutomationId(choice, "GameTitle_Installation_" + title.TitleKey);
            choice.SelectionChanged += async (_, _) =>
            {
                if ((choice.SelectedItem as ComboBoxItem)?.Tag is GameInstallationDto picked)
                {
                    chosen = picked;
                    await GamingOrchestratorClient.Current.SetTitleDefaultInstallationAsync(title.TitleKey, picked.InstallationId);
                }
            };
            card.Children.Add(choice);
        }
        else
        {
            card.Children.Add(new TextBlock { Text = Describe(chosen), FontSize = _console ? 18 : 13, Opacity = 0.8 });
        }

        var tags = new TextBox
        {
            Header = T("Tags (comma separated)", "Tag (separati da virgola)"),
            Text = string.Join(", ", title.Tags),
            MaxLength = 400,
            FontSize = _console ? 20 : 14
        };
        AutomationProperties.SetAutomationId(tags, "GameTitle_Tags_" + title.TitleKey);
        tags.LostFocus += async (_, _) =>
        {
            var parsed = (tags.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var result = await GamingOrchestratorClient.Current.SetTitleTagsAsync(title.TitleKey, parsed);
            if (!result.Succeeded)
                _titleStatus.Text = T("Tags were not saved: at most 20 tags of up to 40 characters.", "Tag non salvati: al massimo 20 tag da 40 caratteri.");
        };
        card.Children.Add(tags);

        // Collections are independent from descriptive tags. The additive field
        // defaults to empty when loading a title from a pre-collections Core.
        var collections = new TextBox
        {
            Header = T("Collections (comma separated)", "Collezioni (separate da virgola)"),
            Text = string.Join(", ", title.Collections),
            MaxLength = 500,
            FontSize = _console ? 20 : 14,
            Visibility = _console ? Visibility.Collapsed : Visibility.Visible
        };
        AutomationProperties.SetAutomationId(collections, "GameTitle_Collections_" + title.TitleKey);
        collections.LostFocus += async (_, _) =>
        {
            var parsed = (collections.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var result = await GamingOrchestratorClient.Current.SetTitleCollectionsAsync(title.TitleKey, parsed);
            if (!result.Succeeded)
                _titleStatus.Text = T("Collections not saved: up to 12 names, each at most 40 characters.",
                                      "Collezioni non salvate: massimo 12 nomi da 40 caratteri.");
        };
        card.Children.Add(collections);

        var play = new Button
        {
            Content = chosen.Kind == ManualEntryKinds.Application ? T("Start", "Avvia") : T("Play", "Gioca"),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinHeight = _console ? 56 : 32,
            FontSize = _console ? 20 : 14
        };
        AutomationProperties.SetAutomationId(play, "GameTitle_Play_" + title.TitleKey);
        AutomationProperties.SetName(play, T("Start ", "Avvia ") + title.Name);
        play.Click += async (_, _) =>
        {
            play.IsEnabled = false;
            try { await _launch(chosen); await ReloadTitlesAsync(); }
            finally { play.IsEnabled = true; }
        };
        card.Children.Add(play);
        return card;
    }

    private static string Describe(GameInstallationDto i) => i.Source switch
    {
        GameInstallationSources.Steam => "Steam",
        GameInstallationSources.Epic => "Epic Games",
        GameInstallationSources.Gog => "GOG",
        GameInstallationSources.Emulator => T("Emulator", "Emulatore"),
        _ => i.Kind == ManualEntryKinds.Application ? T("Application on this PC", "Applicazione su questo PC") : T("Copy on this PC", "Copia su questo PC"),
    };

    private static string PlaySummary(GameTitleDto title)
    {
        string played = title.PlaySeconds < 60
            ? T("Not played yet", "Mai giocato")
            : T("Played ", "Giocato ") + (title.PlaySeconds >= 3600
                ? $"{title.PlaySeconds / 3600} h {title.PlaySeconds % 3600 / 60} min"
                : $"{title.PlaySeconds / 60} min");
        return title.LastPlayedUtc is { } last
            ? played + " · " + T("last ", "ultima volta ") + last.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)
            : played;
    }

    // ---------------------------------------------------------------- advanced

    private void BuildAdvanced()
    {
        var content = new StackPanel { Spacing = 14, Padding = new Thickness(0, 8, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Text = T("For games you already have: installed or portable folders and emulators. PaRuX starts only programs you pick, shows you what it will run, and never downloads game files, firmware or keys.",
                     "Per i giochi che hai già: cartelle installate o portable ed emulatori. PaRuX avvia solo i programmi che scegli, ti mostra cosa eseguirà e non scarica mai file di gioco, firmware o chiavi."),
            TextWrapping = TextWrapping.Wrap
        });

        // import a folder
        content.Children.Add(Heading(T("Import an installed or portable game", "Importa un gioco installato o portable")));
        _folder.Header = T("Game folder", "Cartella del gioco");
        _folder.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_folder, "GameImport_Folder_TextBox");
        _browseFolder.Content = T("Browse", "Sfoglia");
        AutomationProperties.SetAutomationId(_browseFolder, "GameImport_Browse_Button");
        _browseFolder.Click += async (_, _) => { string? path = await PickFolderAsync(); if (path != null) _folder.Text = path; };
        _inspectFolder.Content = T("Find the game program", "Trova il programma del gioco");
        AutomationProperties.SetAutomationId(_inspectFolder, "GameImport_Inspect_Button");
        _inspectFolder.Click += async (_, _) => await InspectFolderAsync();
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(_browseFolder); folderRow.Children.Add(_inspectFolder);
        content.Children.Add(_folder); content.Children.Add(folderRow);
        AutomationProperties.SetAutomationId(_folderStatus, "GameImport_Status_Text");
        AutomationProperties.SetLiveSetting(_folderStatus, AutomationLiveSetting.Polite);
        content.Children.Add(_folderStatus); content.Children.Add(_candidates);

        // Opt-in save backups are fully isolated from game launch and never restore files.
        content.Children.Add(new GameSaveBackupPanel());

        // emulators
        content.Children.Add(Heading(T("Emulators", "Emulatori")));
        var loadEmulators = new Button { Content = T("Look for installed emulators", "Cerca gli emulatori installati"), MinHeight = 32 };
        AutomationProperties.SetAutomationId(loadEmulators, "Emulators_Detect_Button");
        loadEmulators.Click += async (_, _) => await LoadEmulatorsAsync();
        _emulatorBox.Header = T("Emulator", "Emulatore");
        AutomationProperties.SetAutomationId(_emulatorBox, "Emulators_Select_ComboBox");
        _emulatorBox.SelectionChanged += (_, _) => OnEmulatorSelected();
        _emulatorExe.Header = T("Emulator program (.exe)", "Programma dell'emulatore (.exe)");
        _emulatorFolder.Header = T("Folder with your game files", "Cartella con i tuoi file di gioco");
        _emulatorCore.Header = T("Core file (RetroArch only)", "File core (solo RetroArch)");
        _emulatorExtra.Header = T("Extra arguments (optional)", "Argomenti aggiuntivi (facoltativi)");
        _emulatorInputProfile.Header = T("Eden controller input profile (player 1, optional)",
            "Profilo input controller Eden (giocatore 1, facoltativo)");
        AutomationProperties.SetAutomationId(_emulatorInputProfile, "Emulators_InputProfile_TextBox");
        _emulatorLaunchMode.Header = T("Display mode on launch", "Modalità schermo all'avvio");
        AutomationProperties.SetAutomationId(_emulatorLaunchMode, "Emulators_LaunchMode_ComboBox");
        foreach (var (box, id) in new[] { (_emulatorExe, "Emulators_Exe_TextBox"), (_emulatorFolder, "Emulators_Folder_TextBox"),
                                          (_emulatorCore, "Emulators_Core_TextBox"), (_emulatorExtra, "Emulators_Extra_TextBox") })
        {
            box.MaxLength = 1024;
            AutomationProperties.SetAutomationId(box, id);
        }
        var browseFolder = new Button { Content = T("Browse folder", "Sfoglia cartella"), MinHeight = 32 };
        browseFolder.Click += async (_, _) => { string? path = await PickFolderAsync(); if (path != null) _emulatorFolder.Text = path; };
        var scan = new Button { Content = T("Find games", "Trova i giochi"), MinHeight = 32 };
        AutomationProperties.SetAutomationId(scan, "Emulators_Scan_Button");
        scan.Click += async (_, _) => await ScanEmulatorAsync();
        var import = new Button { Content = T("Add selected games to my library", "Aggiungi i giochi selezionati alla libreria"), MinHeight = 32 };
        AutomationProperties.SetAutomationId(import, "Emulators_Import_Button");
        import.Click += async (_, _) => await ImportEmulatorGamesAsync();
        content.Children.Add(loadEmulators); content.Children.Add(_emulatorBox);
        content.Children.Add(_emulatorExe); content.Children.Add(_emulatorCore); content.Children.Add(_emulatorExtra);
        content.Children.Add(_emulatorInputProfile);
        content.Children.Add(_emulatorLaunchMode); content.Children.Add(_emulatorFolder);
        content.Children.Add(new TextBlock
        {
            Text = T("Fullscreen and windowed launch switches are offered only where documented. 'Use emulator settings' keeps its own saved video preferences. Your selected mode is stored with the imported game; verify it on your installed emulator version.",
                     "I comandi schermo intero e finestra sono disponibili solo dove documentati. 'Usa impostazioni emulatore' mantiene le preferenze video dell'emulatore. Il modo scelto viene salvato con il gioco importato: verificalo sulla versione installata."),
            TextWrapping = TextWrapping.Wrap
        });
        var emulatorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        emulatorRow.Children.Add(browseFolder); emulatorRow.Children.Add(scan); emulatorRow.Children.Add(import);
        content.Children.Add(emulatorRow);
        AutomationProperties.SetAutomationId(_emulatorStatus, "Emulators_Status_Text");
        AutomationProperties.SetLiveSetting(_emulatorStatus, AutomationLiveSetting.Polite);
        content.Children.Add(_emulatorStatus); content.Children.Add(_emulatorGames);

        var expander = new Expander
        {
            Header = T("Advanced Game Management", "Advanced Game Management"),
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "AdvancedGameManagement_Expander");
        _advanced = expander;
        Children.Add(expander);
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontSize = 16,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task InspectFolderAsync()
    {
        _candidates.Children.Clear();
        _folderStatus.Text = T("Looking for the game program…", "Ricerca del programma del gioco…");
        try
        {
            var result = await GamingOrchestratorClient.Current.InspectGameFolderAsync(_folder.Text ?? "");
            if (!result.Succeeded || result.Value is null) { _folderStatus.Text = T("The folder could not be inspected.", "Impossibile esaminare la cartella."); return; }
            GameFolderInspectionDto inspection = result.Value;
            if (inspection.Error != null || inspection.Candidates.Count == 0)
            {
                _folderStatus.Text = inspection.Error ?? T("No game program was found.", "Nessun programma di gioco trovato.");
                return;
            }
            _folderStatus.Text = T($"Suggested title: {inspection.SuggestedTitle}. Choose the program that starts the game.",
                                   $"Titolo suggerito: {inspection.SuggestedTitle}. Scegli il programma che avvia il gioco.");
            foreach (ExecutableCandidateDto candidate in inspection.Candidates.Take(8))
            {
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(inspection.FolderPath, candidate.RelativePath));
                var add = new Button
                {
                    Content = $"{candidate.RelativePath}  ({candidate.SizeBytes / (1024 * 1024)} MB, {candidate.Reason})",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinHeight = _console ? 56 : 32,
                    FontSize = _console ? 18 : 13
                };
                AutomationProperties.SetAutomationId(add, "GameImport_Candidate_" + candidate.RelativePath);
                add.Click += async (_, _) => await AddCandidateAsync(inspection.SuggestedTitle, full);
                _candidates.Children.Add(add);
            }
        }
        catch (Exception) { _folderStatus.Text = T("Could not inspect the folder.", "Impossibile esaminare la cartella."); }
    }

    private async Task AddCandidateAsync(string title, string executable)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Add this game?", "Aggiungere questo gioco?"),
                Content = T($"\"{title}\" will start from:\n{executable}\n\nNothing is run now. PaRuX asks again before each launch.",
                            $"\"{title}\" verrà avviato da:\n{executable}\n\nOra non viene eseguito nulla. PaRuX chiede di nuovo conferma a ogni avvio."),
                PrimaryButtonText = T("Add", "Aggiungi"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(title, executable));
            _folderStatus.Text = result.Succeeded
                ? T("Added to your library.", "Aggiunto alla libreria.")
                : T("Could not add it. It may already be in your library.", "Impossibile aggiungerlo. Potrebbe essere già nella libreria.");
            if (result.Succeeded) { _candidates.Children.Clear(); await _refreshLibrary(); await ReloadTitlesAsync(); }
        }
        catch (Exception) { _folderStatus.Text = T("Could not add the game.", "Impossibile aggiungere il gioco."); }
    }

    private async Task LoadEmulatorsAsync()
    {
        _emulatorStatus.Text = T("Looking for installed emulators…", "Ricerca degli emulatori installati…");
        try
        {
            var result = await GamingOrchestratorClient.Current.GetEmulatorsAsync();
            if (!result.Succeeded || result.Value is null) { _emulatorStatus.Text = T("Emulator detection is unavailable.", "Il rilevamento degli emulatori non è disponibile."); return; }
            _emulators = result.Value;
            _emulatorBox.Items.Clear();
            foreach (EmulatorDescriptorDto emulator in _emulators.Emulators)
            {
                bool found = _emulators.Detected.Any(d => d.EmulatorId == emulator.Id);
                _emulatorBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{emulator.Name} — {emulator.Platform}" + (found ? "  ✓" : ""),
                    Tag = emulator
                });
            }
            _emulatorBox.SelectedIndex = Math.Max(0, _emulators.Emulators.FindIndex(e => _emulators.Detected.Any(d => d.EmulatorId == e.Id)));
            int count = _emulators.Detected.Count;
            _emulatorStatus.Text = count == 0
                ? T("No emulator was found automatically. Pick the program yourself below.", "Nessun emulatore trovato automaticamente. Scegli tu il programma qui sotto.")
                : T($"{count} emulator(s) found. The launch arguments are built-in templates not yet checked against your installed version.",
                    $"{count} emulatore/i trovato/i. Gli argomenti di avvio sono modelli integrati non ancora verificati con la tua versione installata.");
        }
        catch (Exception) { _emulatorStatus.Text = T("Could not detect emulators.", "Impossibile rilevare gli emulatori."); }
    }

    private EmulatorDescriptorDto? SelectedEmulator => (_emulatorBox.SelectedItem as ComboBoxItem)?.Tag as EmulatorDescriptorDto;

    private void OnEmulatorSelected()
    {
        if (SelectedEmulator is not { } emulator || _emulators is null) return;
        var detected = _emulators.Detected.FirstOrDefault(d => d.EmulatorId == emulator.Id);
        _emulatorExe.Text = detected?.ExecutablePath ?? string.Empty;
        _emulatorCore.IsEnabled = emulator.RequiresCore;
        _emulatorInputProfile.IsEnabled = emulator.Id is "eden" or "eden-cli";
        if (!_emulatorInputProfile.IsEnabled) _emulatorInputProfile.Text = string.Empty;
        _emulatorLaunchMode.Items.Clear();
        _emulatorLaunchMode.Items.Add(new ComboBoxItem
        {
            Content = T("Use emulator settings", "Usa impostazioni emulatore"),
            Tag = EmulatorLaunchModes.Default
        });
        if (emulator.FullscreenSupported)
            _emulatorLaunchMode.Items.Add(new ComboBoxItem
            {
                Content = T("Force fullscreen", "Forza schermo intero"),
                Tag = EmulatorLaunchModes.Fullscreen
            });
        if (emulator.WindowedSupported)
            _emulatorLaunchMode.Items.Add(new ComboBoxItem
            {
                Content = T("Force windowed", "Forza finestra"),
                Tag = EmulatorLaunchModes.Windowed
            });
        _emulatorLaunchMode.SelectedIndex = emulator.FullscreenSupported ? 1 : 0;
        _emulatorGames.Children.Clear();
        _gameChecks.Clear();
    }

    private async Task ScanEmulatorAsync()
    {
        _emulatorGames.Children.Clear();
        _gameChecks.Clear();
        if (SelectedEmulator is not { } emulator) { _emulatorStatus.Text = T("Choose an emulator first.", "Scegli prima un emulatore."); return; }
        _emulatorStatus.Text = T("Looking for game files…", "Ricerca dei file di gioco…");
        try
        {
            var result = await GamingOrchestratorClient.Current.ScanEmulatorGamesAsync(emulator.Id, _emulatorFolder.Text ?? "");
            if (!result.Succeeded || result.Value is null) { _emulatorStatus.Text = T("The scan failed.", "La ricerca non è riuscita."); return; }
            if (result.Value.Error != null) { _emulatorStatus.Text = result.Value.Error; return; }
            foreach (EmulatedGameDto game in result.Value.Games.Take(200))
            {
                var box = new CheckBox { Content = game.Title, IsChecked = true, MinHeight = _console ? 48 : 28, FontSize = _console ? 18 : 14 };
                AutomationProperties.SetName(box, game.Title);
                _gameChecks.Add((box, game));
                _emulatorGames.Children.Add(box);
            }
            _emulatorStatus.Text = result.Value.Games.Count == 0
                ? T("No compatible files in that folder.", "Nessun file compatibile in quella cartella.")
                : T($"{result.Value.Games.Count} compatible files" + (result.Value.Truncated ? " (list truncated)" : "") + ". Untick anything you do not want.",
                    $"{result.Value.Games.Count} file compatibili" + (result.Value.Truncated ? " (elenco troncato)" : "") + ". Togli la spunta a ciò che non vuoi.");
        }
        catch (Exception) { _emulatorStatus.Text = T("Could not scan the folder.", "Impossibile esaminare la cartella."); }
    }

    private async Task ImportEmulatorGamesAsync()
    {
        if (SelectedEmulator is not { } emulator) { _emulatorStatus.Text = T("Choose an emulator first.", "Scegli prima un emulatore."); return; }
        var selected = _gameChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Game.Path).Take(200).ToList();
        if (selected.Count == 0) { _emulatorStatus.Text = T("Select at least one game.", "Seleziona almeno un gioco."); return; }
        try
        {
            string mode = (_emulatorLaunchMode.SelectedItem as ComboBoxItem)?.Tag as string
                ?? EmulatorLaunchModes.Default;
            var result = await GamingOrchestratorClient.Current.ImportEmulatorGamesAsync(new EmulatorImportRequestDto(
                emulator.Id, _emulatorExe.Text ?? "", selected, mode == EmulatorLaunchModes.Fullscreen,
                string.IsNullOrWhiteSpace(_emulatorExtra.Text) ? null : _emulatorExtra.Text,
                string.IsNullOrWhiteSpace(_emulatorCore.Text) ? null : _emulatorCore.Text,
                mode,
                string.IsNullOrWhiteSpace(_emulatorInputProfile.Text) ? null : _emulatorInputProfile.Text));
            if (!result.Succeeded || result.Value is null)
            {
                _emulatorStatus.Text = T("Nothing was added. Check that the program is the selected emulator and the files are local.",
                                         "Non è stato aggiunto nulla. Controlla che il programma sia l'emulatore scelto e che i file siano locali.");
                return;
            }
            _emulatorStatus.Text = T($"Added {result.Value.Added}, already present {result.Value.Skipped}"
                                     + (result.Value.Problems.Count > 0 ? $", {result.Value.Problems.Count} skipped." : "."),
                                     $"Aggiunti {result.Value.Added}, già presenti {result.Value.Skipped}"
                                     + (result.Value.Problems.Count > 0 ? $", {result.Value.Problems.Count} esclusi." : "."));
            await _refreshLibrary();
            await ReloadTitlesAsync();
        }
        catch (Exception) { _emulatorStatus.Text = T("Could not add the games.", "Impossibile aggiungere i giochi."); }
    }
}
