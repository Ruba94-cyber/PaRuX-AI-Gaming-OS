using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
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

    // titles: the Gaming home (Continue shelf, tile grid, detail panel) lives in GamingHomeView
    private readonly GamingHomeView _home;
    public event Func<string, IReadOnlyList<string>, bool>? GameGuideChatRequested;

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
    private readonly CheckBox _emulatorPcsx2BigPicture = new() { IsEnabled = false, IsChecked = false };
    private readonly StackPanel _emulatorGames = new() { Spacing = 4 };
    private readonly TextBlock _emulatorStatus = new() { TextWrapping = TextWrapping.Wrap };
    private EmulatorListDto? _emulators;
    private readonly List<(CheckBox Box, EmulatedGameDto Game)> _gameChecks = new();

    public UniversalLibraryPanel(Func<GameInstallationDto, Task> launch, Func<Task> refreshLibrary)
    {
        _launch = launch;
        _refreshLibrary = refreshLibrary;
        Spacing = 12;
        _home = new GamingHomeView(launch, refreshLibrary);
        _home.GameGuideChatRequested += (question, sources) =>
            GameGuideChatRequested?.Invoke(question, sources) == true;
        Children.Add(_home);
        BuildAdvanced();
    }

    public void SetConsolePresentation(bool enabled)
    {
        _console = enabled;
        if (_advanced is not null)
        {
            _advanced.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            if (enabled) _advanced.IsExpanded = false;
        }
        foreach (var box in new[] { _folder, _emulatorExe, _emulatorFolder, _emulatorCore, _emulatorExtra, _emulatorInputProfile })
            box.FontSize = enabled ? 20 : 14;
        foreach (var button in new[] { _browseFolder, _inspectFolder })
        {
            button.FontSize = enabled ? 20 : 14;
            button.MinHeight = enabled ? 56 : 32;
        }
        _home.SetConsolePresentation(enabled);
    }

    // ---------------------------------------------------------------- titles

    /// <summary>One-shot catalog refresh after a foreground return from Gaming.
    /// Never scan stores periodically or while the game owns the PC.</summary>
    internal Task RefreshTitlesAfterGameAsync() => _home.RefreshAfterGameAsync();

    /// <summary>Console Menu button: jump to the Gaming home search field.</summary>
    internal bool FocusSearch() => _home.FocusSearch();

    private Task ReloadTitlesAsync() => _home.ReloadAsync();

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
        _emulatorExe.TextChanged += (_, _) => UpdatePcsx2ControllerUiAvailability();
        _emulatorFolder.Header = T("Folder with your game files", "Cartella con i tuoi file di gioco");
        _emulatorCore.Header = T("Core file (RetroArch only)", "File core (solo RetroArch)");
        _emulatorExtra.Header = T("Extra arguments (optional)", "Argomenti aggiuntivi (facoltativi)");
        _emulatorInputProfile.Header = T("Eden controller input profile (player 1, optional)",
            "Profilo input controller Eden (giocatore 1, facoltativo)");
        AutomationProperties.SetAutomationId(_emulatorInputProfile, "Emulators_InputProfile_TextBox");
        _emulatorLaunchMode.Header = T("Display mode on launch", "Modalità schermo all'avvio");
        AutomationProperties.SetAutomationId(_emulatorLaunchMode, "Emulators_LaunchMode_ComboBox");
        _emulatorPcsx2BigPicture.Content = T(
            "PCSX2 Qt: controller-first Big Picture interface",
            "PCSX2 Qt: interfaccia Big Picture per controller");
        AutomationProperties.SetAutomationId(_emulatorPcsx2BigPicture, "Emulators_Pcsx2_BigPicture_CheckBox");
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
        content.Children.Add(_emulatorLaunchMode); content.Children.Add(_emulatorPcsx2BigPicture);
        content.Children.Add(_emulatorFolder);
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

    private void UpdatePcsx2ControllerUiAvailability()
    {
        bool supported = EmulatorControllerUiPolicy.SupportsPcsx2BigPicture(
            SelectedEmulator?.Id, _emulatorExe.Text);
        _emulatorPcsx2BigPicture.IsEnabled = supported;
        if (!supported) _emulatorPcsx2BigPicture.IsChecked = false;
    }

    private void OnEmulatorSelected()
    {
        if (SelectedEmulator is not { } emulator || _emulators is null) return;
        var detected = _emulators.Detected.FirstOrDefault(d => d.EmulatorId == emulator.Id);
        _emulatorExe.Text = detected?.ExecutablePath ?? string.Empty;
        _emulatorCore.IsEnabled = emulator.RequiresCore;
        _emulatorInputProfile.IsEnabled = emulator.Id is "eden" or "eden-cli";
        if (!_emulatorInputProfile.IsEnabled) _emulatorInputProfile.Text = string.Empty;
        _emulatorPcsx2BigPicture.IsChecked = false;
        UpdatePcsx2ControllerUiAvailability();
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
                string.IsNullOrWhiteSpace(_emulatorInputProfile.Text) ? null : _emulatorInputProfile.Text,
                _emulatorPcsx2BigPicture.IsEnabled && _emulatorPcsx2BigPicture.IsChecked == true));
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
