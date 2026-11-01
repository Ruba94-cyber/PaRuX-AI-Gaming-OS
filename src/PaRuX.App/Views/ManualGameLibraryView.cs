using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

public sealed class ManualGameLibraryView : UserControl
{
    private readonly TextBox _name = new();
    private readonly TextBlock _manualAddIntro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _manualAddActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBox _path = new();
    private readonly StackPanel _games = new() { Spacing = 8 };
    private readonly StackPanel _root = new() { Spacing = 16, Padding = new Thickness(24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _heading = new();
    private bool _consolePresentation;
    private readonly StackPanel _discoveredGames = new() { Spacing = 8 };
    private readonly TextBlock _discoveryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _steamRefresh = new();
    private readonly Button _epicRefresh = new();
    private readonly Button _gogRefresh = new();
    private readonly Button _allStoresRefresh = new();
    private readonly Button _displayRefresh = new();
    private readonly Button _controllerRefresh = new();
    private readonly Button _returnToAi = new();
    private readonly StackPanel _installerSection = new() { Spacing = 8 };
    private readonly Button _inspectInstaller = new();
    private readonly TextBox _expectedPublisherSha = new();
    private readonly Button _stagePackage = new();
    private readonly Button _refreshStaged = new();
    private readonly StackPanel _stagedPackages = new() { Spacing = 6 };
    private readonly TextBlock _stagedStatus = new() { TextWrapping = TextWrapping.Wrap };
    private string? _inspectedInstallerPath;
    private readonly Button _cancelInspection = new();
    private CancellationTokenSource? _inspectionCancellation;
    private readonly TextBlock _installerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _installerInspectionAllowed;
    private readonly Button _gamingStatusRefresh = new();
    private readonly TextBlock _gamingStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _returnToAiAvailable;
    private readonly TextBlock _controllerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _displayStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _displayItems = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    private readonly Button _browse = new();
    private bool _busy;
    private readonly TextBox _filter = new();
    private List<ManualGameDto> _allGames = new();
    private List<DiscoveredGameDto> _lastDetectedGames = new();
    private int _page;
    private int _detectedPage;
    private GameLibraryPageWindow _lastDetectedWindow;
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly Button _detectedPrevious = new();
    private readonly Button _detectedNext = new();
    private readonly TextBlock _detectedRange = new() { TextWrapping = TextWrapping.Wrap };
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private static string GamingStateLabel(string state) => state switch
    {
        "AiReady" => T("AI ready", "AI pronta"),
        "EnteringGaming" => T("Preparing Gaming", "Preparazione Gaming"),
        "GamingLocal" => T("Local Gaming", "Gioco locale"),
        "GamingRemote" => T("Remote Gaming", "Gioco remoto"),
        "ExitingGaming" => T("Leaving Gaming", "Uscita da Gaming"),
        "RestoringAi" => T("Restoring AI", "Ripristino AI"),
        "GameSuspended" => T("Game suspended", "Gioco sospeso"),
        "Error" => T("Recovery required", "Recupero necessario"),
        _ => T("Unknown", "Sconosciuto")
    };

    public ManualGameLibraryView()
    {
        var root = _root;
        _heading.Text = T("Game library", "Libreria giochi");
        _heading.FontSize = 28;
        _heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        root.Children.Add(_heading);

        // Real resource/session status lives on the same Games home in both TV and
        // desktop presentations. No background polling or controller worker.
        root.Children.Add(new TextBlock
        {
            Text = T("AI / Gaming session", "Sessione AI / Gaming"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _gamingStatus.Text = T("Check the Gaming status to begin.", "Controlla lo stato Gaming per iniziare.");
        AutomationProperties.SetAutomationId(_gamingStatus, "GameLibrary_Gaming_Status_Text");
        AutomationProperties.SetLiveSetting(_gamingStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_gamingStatus);
        _gamingStatusRefresh.Content = T("Refresh Gaming status", "Aggiorna stato Gaming");
        AutomationProperties.SetAutomationId(_gamingStatusRefresh, "GameLibrary_Gaming_Refresh_Button");
        _gamingStatusRefresh.Click += async (_, _) => await RefreshGamingStatusButtonAsync();
        _returnToAi.Content = T("Return to AI", "Torna all'AI");
        _returnToAi.IsEnabled = false;
        AutomationProperties.SetAutomationId(_returnToAi, "GameLibrary_ReturnToAi_Button");
        AutomationProperties.SetName(_returnToAi, T("Return to AI without closing the game", "Torna all'AI senza chiudere il gioco"));
        _returnToAi.Click += async (_, _) => await ReturnToAiAsync();
        var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sessionActions.Children.Add(_gamingStatusRefresh);
        sessionActions.Children.Add(_returnToAi);
        root.Children.Add(sessionActions);

        _manualAddIntro.Text = T("Add local games and launch them only after explicit confirmation. PaRuX never installs them here.",
            "Aggiungi giochi locali e avviali solo dopo una conferma esplicita. PaRuX non li installa da questa schermata.");
        root.Children.Add(_manualAddIntro);
        _name.Header = T("Game name", "Nome del gioco");
        _name.MaxLength = 200;
        AutomationProperties.SetAutomationId(_name, "GameLibrary_Name_TextBox");
        _path.Header = T("Local executable (.exe)", "Eseguibile locale (.exe)");
        _path.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_path, "GameLibrary_Path_TextBox");
        _browse.Content = T("Browse", "Sfoglia");
        AutomationProperties.SetAutomationId(_browse, "GameLibrary_Browse_Button");
        _browse.Click += Browse_Click;
        _add.Content = T("Add game", "Aggiungi gioco");
        AutomationProperties.SetAutomationId(_add, "GameLibrary_Add_Button");
        _add.Click += Add_Click;
        root.Children.Add(_name); root.Children.Add(_path);
        _manualAddActions.Children.Add(_browse);
        _manualAddActions.Children.Add(_add);
        root.Children.Add(_manualAddActions);
        AutomationProperties.SetAutomationId(_status, "GameLibrary_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        root.Children.Add(_status);
        _filter.Header = T("Filter by name", "Filtra per nome");
        AutomationProperties.SetAutomationId(_filter, "GameLibrary_Filter_TextBox");
        _filter.TextChanged += (_, _) => { _page = 0; _detectedPage = 0; RenderGames(); RenderDiscoveredGames(); };
        // Console/TV: gamepad A or Y on the filter opens the controller-driven on-screen keyboard.
        var keyboard = ConsoleKeyboardFlyout.Create(_filter, T("Done", "Fine"), T("Backspace", "Cancella"),
            T("Clear", "Svuota"), T("Space", "Spazio"));
        _filter.KeyDown += (_, e) =>
        {
            if (_consolePresentation && e.Key is Windows.System.VirtualKey.GamepadA or Windows.System.VirtualKey.GamepadY)
            {
                keyboard.ShowAt(_filter);
                e.Handled = true;
            }
        };
        root.Children.Add(_filter); root.Children.Add(_games);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _previous.Content = T("Previous", "Precedente"); _next.Content = T("Next", "Successiva");
        _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderGames(); } };
        _next.Click += (_, _) => { _page++; RenderGames(); };
        pages.Children.Add(_previous); pages.Children.Add(_next); root.Children.Add(pages);

        root.Children.Add(new TextBlock
        {
            Text = T("Installed store games", "Giochi degli store installati"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Steam, Epic and GOG detection reads local installation metadata. Official-client launch requests require explicit confirmation, and do not prove the game started. No store installs or modifies games.",
                "Il rilevamento Steam, Epic e GOG legge i dati locali di installazione. Le richieste di avvio ai client richiedono conferma esplicita e non dimostrano che il gioco sia partito. PaRuX non installa né modifica i giochi."),
            TextWrapping = TextWrapping.Wrap
        });
        _allStoresRefresh.Content = T("Detect all installed games", "Rileva tutti i giochi installati");
        AutomationProperties.SetAutomationId(_allStoresRefresh, "GameLibrary_AllStores_Refresh_Button");
        AutomationProperties.SetName(_allStoresRefresh, T("Detect installed Steam Epic and GOG games", "Rileva i giochi Steam Epic e GOG installati"));
        _allStoresRefresh.Click += async (_, _) => await RefreshAllStoresAsync();
        root.Children.Add(_allStoresRefresh);

        _steamRefresh.Content = T("Detect Steam games", "Rileva giochi Steam");
        AutomationProperties.SetAutomationId(_steamRefresh, "GameLibrary_Steam_Refresh_Button");
        _steamRefresh.Click += async (_, _) => await RefreshSteamAsync();
        root.Children.Add(_steamRefresh);
        _epicRefresh.Content = T("Detect Epic games", "Rileva giochi Epic");
        AutomationProperties.SetAutomationId(_epicRefresh, "GameLibrary_Epic_Refresh_Button");
        _epicRefresh.Click += async (_, _) => await RefreshEpicAsync();
        root.Children.Add(_epicRefresh);
        _gogRefresh.Content = T("Detect GOG games", "Rileva giochi GOG");
        AutomationProperties.SetAutomationId(_gogRefresh, "GameLibrary_Gog_Refresh_Button");
        AutomationProperties.SetName(_gogRefresh, T("Detect installed GOG games", "Rileva i giochi GOG installati"));
        _gogRefresh.Click += async (_, _) => await RefreshGogAsync();
        root.Children.Add(_gogRefresh);
        AutomationProperties.SetAutomationId(_discoveryStatus, "GameLibrary_Steam_Status_Text");
        AutomationProperties.SetLiveSetting(_discoveryStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_discoveryStatus);
        root.Children.Add(_discoveredGames);
        // Console and desktop share the same cached catalog, with bounded
        // page rendering instead of constructing thousands of WinUI controls.
        _detectedPrevious.IsEnabled = false;
        _detectedNext.IsEnabled = false;
        _detectedPrevious.Content = T("Previous store games", "Giochi store precedenti");
        _detectedNext.Content = T("Next store games", "Giochi store successivi");
        AutomationProperties.SetAutomationId(_detectedPrevious, "GameLibrary_Store_Previous_Button");
        AutomationProperties.SetAutomationId(_detectedNext, "GameLibrary_Store_Next_Button");
        _detectedRange.Text = T("No store games scanned yet.", "Nessun gioco store ancora rilevato.");
        AutomationProperties.SetAutomationId(_detectedRange, "GameLibrary_Store_Page_Status");
        AutomationProperties.SetLiveSetting(_detectedRange, AutomationLiveSetting.Polite);
        _detectedPrevious.Click += (_, _) =>
        {
            if (!_busy && _detectedPage > 0) { _detectedPage--; RenderDiscoveredGames(); }
        };
        _detectedNext.Click += (_, _) =>
        {
            if (!_busy && _lastDetectedWindow.HasNext) { _detectedPage++; RenderDiscoveredGames(); }
        };
        var detectedNavigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        detectedNavigation.Children.Add(_detectedPrevious);
        detectedNavigation.Children.Add(_detectedNext);
        root.Children.Add(_detectedRange);
        root.Children.Add(detectedNavigation);

        root.Children.Add(new TextBlock
        {
            Text = T("Display & streaming readiness", "Preparazione schermi e streaming"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Inspect the current Windows monitor layout. No display settings are changed.",
                     "Esamina la disposizione attuale dei monitor Windows. Nessuna impostazione video viene modificata."),
            TextWrapping = TextWrapping.Wrap
        });
        _displayRefresh.Content = T("Inspect connected displays", "Controlla gli schermi collegati");
        AutomationProperties.SetAutomationId(_displayRefresh, "GameLibrary_Displays_Refresh_Button");
        _displayRefresh.Click += async (_, _) => await InspectDisplaysAsync();
        root.Children.Add(_displayRefresh);
        AutomationProperties.SetAutomationId(_displayStatus, "GameLibrary_Displays_Status_Text");
        AutomationProperties.SetLiveSetting(_displayStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_displayStatus);
        root.Children.Add(_displayItems);

        root.Children.Add(new TextBlock
        {
            Text = T("Controller diagnostics", "Diagnostica controller"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Read connected XInput controllers only when requested. No drivers, mapping changes or persistent recording.",
                     "Legge i controller XInput solo su richiesta. Nessun driver, modifica delle mappature o registrazione continua."),
            TextWrapping = TextWrapping.Wrap
        });
        _controllerRefresh.Content = T("Check controllers", "Controlla i controller");
        AutomationProperties.SetAutomationId(_controllerRefresh, "GameLibrary_Controllers_Refresh_Button");
        _controllerRefresh.Click += async (_, _) => await InspectControllersAsync();
        root.Children.Add(_controllerRefresh);
        AutomationProperties.SetAutomationId(_controllerStatus, "GameLibrary_Controllers_Status_Text");
        AutomationProperties.SetLiveSetting(_controllerStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_controllerStatus);
        root.Children.Add(new GamingDevicesPanel());

        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Game installer safety preflight", "Controllo preliminare installer giochi"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Select a completed local installer/archive and calculate SHA-256. This does not install, execute, extract, download or prove the file is safe. Disabled during Gaming.",
                "Seleziona un installer/archivio locale completo e calcola SHA-256. Non installa, esegue, estrae, scarica né dimostra la sicurezza del file. Disattivato durante il Gaming."),
            TextWrapping = TextWrapping.Wrap
        });
        _inspectInstaller.Content = T("Inspect local installer", "Controlla installer locale");
        _inspectInstaller.IsEnabled = false;
        AutomationProperties.SetAutomationId(_inspectInstaller, "GameLibrary_Installer_Inspect_Button");
        AutomationProperties.SetName(_inspectInstaller, T("Calculate SHA-256 of a selected game installer", "Calcola SHA-256 di un installer selezionato"));
        _inspectInstaller.Click += async (_, _) => await InspectInstallerAsync();
        var inspectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        inspectionActions.Children.Add(_inspectInstaller);
        _cancelInspection.Content = T("Cancel inspection", "Annulla controllo");
        _cancelInspection.IsEnabled = false;
        AutomationProperties.SetAutomationId(_cancelInspection, "GameLibrary_Installer_Cancel_Button");
        _cancelInspection.Click += (_, _) =>
        {
            if (_inspectionCancellation is null) return;
            _inspectionCancellation.Cancel();
            _cancelInspection.IsEnabled = false;
            _installerStatus.Text = T("Cancelling inspection…", "Annullamento controllo…");
        };
        inspectionActions.Children.Add(_cancelInspection);
        _installerSection.Children.Add(inspectionActions);
        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Optional: independently verify and stage a copy of the selected package. Paste the SHA-256 published by a trusted independent source. PaRuX never runs or installs the package.",
                     "Facoltativo: verifica e prepara una copia del pacchetto selezionato. Incolla lo SHA-256 pubblicato da una fonte attendibile indipendente. PaRuX non esegue né installa il pacchetto."),
            TextWrapping = TextWrapping.Wrap
        });
        _expectedPublisherSha.PlaceholderText = T("Publisher SHA-256 (64 hex characters)",
            "SHA-256 del produttore (64 caratteri esadecimali)");
        _expectedPublisherSha.MaxLength = 64;
        _expectedPublisherSha.IsEnabled = true;
        AutomationProperties.SetAutomationId(_expectedPublisherSha, "GameLibrary_Installer_PublisherHash_Input");
        AutomationProperties.SetName(_expectedPublisherSha, T("Independent publisher SHA-256",
            "SHA-256 indipendente del produttore"));
        _expectedPublisherSha.TextChanged += (_, _) => UpdatePackageStageAvailability();
        _installerSection.Children.Add(_expectedPublisherSha);
        _stagePackage.Content = T("Verify and stage copy", "Verifica e prepara una copia");
        _stagePackage.IsEnabled = false;
        AutomationProperties.SetAutomationId(_stagePackage, "GameLibrary_Installer_Stage_Button");
        AutomationProperties.SetName(_stagePackage, T("Verify package and stage a copy without executing",
            "Verifica il pacchetto e prepara una copia senza eseguirlo"));
        _stagePackage.Click += async (_, _) => await StageInspectedPackageAsync();
        _installerSection.Children.Add(_stagePackage);
        _refreshStaged.Content = T("Show staged packages", "Mostra pacchetti preparati");
        AutomationProperties.SetAutomationId(_refreshStaged, "GameLibrary_Installer_Staged_Refresh_Button");
        AutomationProperties.SetName(_refreshStaged, T("List managed verified package copies",
            "Elenca le copie di pacchetti verificate e gestite"));
        _refreshStaged.IsEnabled = false;
        _refreshStaged.Click += async (_, _) => await RefreshStagedPackagesAsync();
        _installerSection.Children.Add(_refreshStaged);
        AutomationProperties.SetAutomationId(_stagedStatus, "GameLibrary_Installer_Staged_Status");
        AutomationProperties.SetLiveSetting(_stagedStatus, AutomationLiveSetting.Polite);
        _installerSection.Children.Add(_stagedStatus);
        _installerSection.Children.Add(_stagedPackages);
        AutomationProperties.SetAutomationId(_installerStatus, "GameLibrary_Installer_Status_Text");
        AutomationProperties.SetLiveSetting(_installerStatus, AutomationLiveSetting.Polite);
        _installerSection.Children.Add(_installerStatus);
        root.Children.Add(_installerSection);

        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>
    /// A TV-scale presentation of the SAME game library and its existing actions.
    /// Changes only WinUI spacing/type, not stored settings, monitor modes or Windows
    /// input configuration. The desktop layout is restored when Console exits.
    /// </summary>
    public void ApplyConsoleMode(bool enabled)
    {
        _consolePresentation = enabled;
        _root.MaxWidth = enabled ? 1400 : 900;
        // Console Home is for playing and managing the session. Desktop-only
        // filesystem editing remains available unchanged in normal mode.
        Visibility editorVisibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _manualAddIntro.Visibility = editorVisibility;
        _name.Visibility = editorVisibility;
        _path.Visibility = editorVisibility;
        _manualAddActions.Visibility = editorVisibility;
        _installerSection.Visibility = editorVisibility;
        if (enabled) _inspectionCancellation?.Cancel();
        _root.Padding = new Thickness(enabled ? 32 : 24);
        _root.Spacing = enabled ? 24 : 16;
        _heading.FontSize = enabled ? 40 : 28;
        _games.Spacing = enabled ? 16 : 8;
        _discoveredGames.Spacing = enabled ? 16 : 8;

        double bodySize = enabled ? 20 : 14;
        foreach (TextBox box in new[] { _name, _path, _filter })
            box.FontSize = bodySize;
        foreach (Button button in new[] { _add, _browse, _returnToAi, _gamingStatusRefresh, _allStoresRefresh, _displayRefresh, _controllerRefresh, _steamRefresh, _epicRefresh, _gogRefresh, _previous, _next, _detectedPrevious, _detectedNext })
        {
            button.FontSize = bodySize;
            button.MinHeight = enabled ? 56 : 32;
        }
        _status.FontSize = bodySize;
        _discoveryStatus.FontSize = bodySize;
        _displayStatus.FontSize = bodySize;
        _controllerStatus.FontSize = bodySize;
        _installerStatus.FontSize = bodySize;
        _inspectInstaller.FontSize = bodySize;
        _inspectInstaller.MinHeight = enabled ? 56 : 32;
        _cancelInspection.FontSize = bodySize;
        _cancelInspection.MinHeight = enabled ? 56 : 32;
        _gamingStatus.FontSize = bodySize;
        RenderGames(); // focusable actions remain identical, only geometry changes
        RenderDiscoveredGames(); // reuse the cached scan; never rescan disks for a UI resize
    }

    /// <summary>Controller-first focus target; reuses the exact same UI actions
    /// as desktop and never installs an input remapper.</summary>
    public void FocusConsoleHome() => _gamingStatusRefresh.Focus(FocusState.Programmatic);

    /// <summary>While a consent dialog or command is active, Console-wide B/Escape
    /// must not tear down the fullscreen presenter beneath that interaction.</summary>
    public bool IsInteractionBusy => _busy;

    /// <summary>Leaving the Games workspace cancels any expensive local hash
    /// before the next workspace resumes interactive AI activity.</summary>
    public void CancelExpensiveWork() => _inspectionCancellation?.Cancel();

    public async void OnShown()
    {
        await RefreshAsync();
        await UpdateGamingStatusAsync();
        // The initial catalog load briefly disables buttons; restore a useful
        // gamepad focus target once the real server status has been fetched.
        if (_consolePresentation && !_busy)
            FocusConsoleHome();
    }

    private async Task UpdateGamingStatusAsync()
    {
        try
        {
            GamingStatusDto? status = await GamingOrchestratorClient.Current.GetStatusAsync();
            if (status is null)
            {
                _returnToAiAvailable = false;
                _installerInspectionAllowed = false;
                _gamingStatus.Text = T("Gaming status unavailable. Check the local Core connection.",
                    "Stato Gaming non disponibile. Controlla il collegamento al Core locale.");
            }
            else
            {
                _returnToAiAvailable = status.IsGamingActive || status.State != "AiReady";
                _installerInspectionAllowed = !_returnToAiAvailable;
                _gamingStatus.Text = status.State == "AiReady" && status.IsGamingActive
                    ? T("Gaming resource lock is still active after Core restart. Select Return to AI to recover without closing the game.",
                        "Il blocco delle risorse Gaming è ancora attivo dopo il riavvio del Core. Seleziona Torna all'AI per recuperare senza chiudere il gioco.")
                    : status.State == "AiReady"
                        ? T("AI ready · No Gaming session active.", "AI pronta · Nessuna sessione Gaming attiva.")
                        : T($"Gaming state: {GamingStateLabel(status.State)}. Return to AI leaves the game running.",
                            $"Stato Gaming: {GamingStateLabel(status.State)}. Tornando all'AI il gioco rimane aperto.");
            }
        }
        catch (Exception)
        {
            _returnToAiAvailable = false;
            _installerInspectionAllowed = false;
            _gamingStatus.Text = T("Gaming status unavailable.", "Stato Gaming non disponibile.");
        }
        _returnToAi.IsEnabled = _returnToAiAvailable && !_busy;
        _inspectInstaller.IsEnabled = _installerInspectionAllowed && !_busy;
        _refreshStaged.IsEnabled = _installerInspectionAllowed && !_busy;
        UpdatePackageStageAvailability();
    }

    private async Task RefreshGamingStatusButtonAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await UpdateGamingStatusAsync(); }
        finally { SetBusy(false); }
    }

    private async Task ReturnToAiAsync()
    {
        if (_busy || !_returnToAiAvailable) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Return to AI?", "Tornare all'AI?"),
                Content = T(
                    "PaRuX will try to restore AI resources but will not terminate, suspend or close your game or Steam. If the game is still running, it may compete with AI for GPU resources.",
                    "PaRuX tenterà di ripristinare le risorse AI senza terminare, sospendere o chiudere il gioco o Steam. Se il gioco è ancora aperto, potrebbe competere con l'AI per le risorse GPU."),
                PrimaryButtonText = T("Return to AI", "Torna all'AI"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            GamingCallResult<GamingStatusDto> result = await GamingOrchestratorClient.Current.ExitGamingAsync();
            await UpdateGamingStatusAsync();
            if (!result.Succeeded)
                _gamingStatus.Text = T("Return to AI could not be confirmed. Inspect Gaming status and retry.",
                    "Ritorno all'AI non confermato. Controlla lo stato Gaming e riprova.");
            else if (!_returnToAiAvailable)
                _gamingStatus.Text = T("AI ready. The game was not closed.",
                    "AI pronta. Il gioco non è stato chiuso.");
        }
        catch (Exception)
        {
            _gamingStatus.Text = T("Return to AI failed. The game was not closed.",
                "Ritorno all'AI non riuscito. Il gioco non è stato chiuso.");
        }
        finally { SetBusy(false); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var file = await picker.PickSingleFileAsync();
            if (file != null) _path.Text = file.Path;
        }
        catch (Exception) { _status.Text = T("Could not open the file picker.", "Impossibile aprire la selezione file."); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(_name.Text, _path.Text));
            if (!result.Succeeded) { _status.Text = T("Could not add the game. Check the name, local .exe path, or whether it is already listed.", "Impossibile aggiungere il gioco. Controlla nome, percorso .exe locale o eventuali duplicati."); return; }
            _name.Text = ""; _path.Text = "";
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not add the game.", "Impossibile aggiungere il gioco."); }
        finally { SetBusy(false); }
    }

    private async Task RefreshStagedPackagesAsync()
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation) return;
        SetBusy(true);
        _stagedPackages.Children.Clear();
        _stagedStatus.Text = T("Reading managed package copies…",
            "Lettura delle copie gestite…");
        try
        {
            var result = await GamingOrchestratorClient.Current.ListStagedPackagesAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _stagedStatus.Text = T("The managed package inventory is unavailable.",
                    "Elenco dei pacchetti gestiti non disponibile.");
                return;
            }
            _stagedStatus.Text = result.Value.Count == 0
                ? T("No staged packages.", "Nessun pacchetto preparato.")
                : T($"{result.Value.Count} managed local copies. Remove only those no longer needed.",
                    $"{result.Value.Count} copie locali gestite. Elimina solo quelle che non servono più.");
            foreach (StagedGamePackageDto file in result.Value)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                item.Children.Add(new TextBlock
                {
                    Text = $"{file.FileName} · {file.SizeBytes:N0} B",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 450
                });
                var remove = new Button
                {
                    Content = T("Remove copy", "Elimina copia"),
                    Tag = file.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(remove, T(
                    $"Remove managed copy {file.FileName}", $"Elimina copia gestita {file.FileName}"));
                remove.Click += async (_, _) => await RemoveStagedPackageAsync(file);
                item.Children.Add(remove);
                _stagedPackages.Children.Add(item);
            }
        }
        catch (Exception)
        {
            _stagedStatus.Text = T("Could not list managed copies.",
                "Impossibile elencare le copie gestite.");
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveStagedPackageAsync(StagedGamePackageDto file)
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Remove managed copy?", "Eliminare la copia gestita?"),
            Content = T(
                $"Remove only the PaRuX-managed copy {file.FileName}? The original installer will not be touched.",
                $"Eliminare soltanto la copia gestita da PaRuX {file.FileName}? Il file originale non verrà modificato."),
            PrimaryButtonText = T("Remove copy", "Elimina copia"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true);
        bool refresh = false;
        try
        {
            var result = await GamingOrchestratorClient.Current.DeleteStagedPackageAsync(file.Id);
            refresh = result.Succeeded;
            if (!refresh)
                _stagedStatus.Text = T("Could not remove this managed copy.",
                    "Impossibile eliminare questa copia gestita.");
        }
        catch (Exception)
        {
            _stagedStatus.Text = T("Removal failed. Original file preserved.",
                "Eliminazione non riuscita. File originale preservato.");
        }
        finally { SetBusy(false); }
        if (refresh) await RefreshStagedPackagesAsync();
    }

    private void UpdatePackageStageAvailability()
    {
        string digest = _expectedPublisherSha.Text?.Trim() ?? "";
        _stagePackage.IsEnabled = !_busy && !_consolePresentation && _installerInspectionAllowed &&
            !string.IsNullOrEmpty(_inspectedInstallerPath) &&
            digest.Length == 64 && digest.All(Uri.IsHexDigit);
    }

    private async Task StageInspectedPackageAsync()
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation ||
            string.IsNullOrEmpty(_inspectedInstallerPath)) return;
        string publisherHash = _expectedPublisherSha.Text?.Trim() ?? "";
        if (publisherHash.Length != 64 || !publisherHash.All(Uri.IsHexDigit)) return;

        SetBusy(true);
        CancellationTokenSource? staging = null;
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Verify and stage a copy?", "Verificare e preparare una copia?"),
                Content = T(
                    "Only proceed if you obtained this checksum independently from the trusted publisher. PaRuX will copy the local package into its private application folder after verifying every byte. It will NOT open, run, install or unpack it.",
                    "Procedi solo se hai ottenuto questo codice SHA-256 in modo indipendente da un produttore attendibile. PaRuX copierà il pacchetto nella propria cartella privata dopo averne verificato ogni byte. NON lo aprirà, eseguirà, installerà né estrarrà."),
                PrimaryButtonText = T("Verify and stage", "Verifica e prepara"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            staging = new CancellationTokenSource();
            _inspectionCancellation = staging; // shared Gaming/leave-page cancellation policy
            _cancelInspection.IsEnabled = true;
            _installerStatus.Text = T("Verifying and staging a local package…",
                "Verifica e preparazione del pacchetto locale…");
            var result = await GamingOrchestratorClient.Current.StageGamePackageAsync(
                new GamePackageStageRequest(_inspectedInstallerPath, publisherHash), staging.Token);
            if (result.Succeeded && result.Value is not null)
                _installerStatus.Text = T(
                    $"Checked copy ready: {result.Value.FileName}\nSHA-256: {result.Value.Sha256}\nStored locally in PaRuX app data. No software was installed or executed.",
                    $"Copia verificata pronta: {result.Value.FileName}\nSHA-256: {result.Value.Sha256}\nSalvata nei dati locali di PaRuX. Nessun software è stato installato o eseguito.");
            else
                _installerStatus.Text = T(
                    "Verification or staging was refused. Check the publisher's independent SHA-256 and the Gaming status. No completed package was published.",
                    "Verifica o preparazione rifiutata. Controlla lo SHA-256 indipendente del produttore e lo stato Gaming. Nessun pacchetto completo è stato pubblicato.");
        }
        catch (OperationCanceledException) when (staging?.IsCancellationRequested == true)
        {
            _installerStatus.Text = T("Staging cancelled; no completed package was published.",
                "Preparazione annullata; nessun pacchetto completo è stato pubblicato.");
        }
        catch (Exception)
        {
            _installerStatus.Text = T("Package staging could not be completed.",
                "Impossibile completare la preparazione del pacchetto.");
        }
        finally
        {
            _inspectionCancellation = null;
            staging?.Dispose();
            _cancelInspection.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async Task InspectInstallerAsync()
    {
        if (_busy || !_installerInspectionAllowed) return;
        _inspectedInstallerPath = null;
        _expectedPublisherSha.Text = "";
        SetBusy(true);
        CancellationTokenSource? inspection = null;
        try
        {
            var picker = new FileOpenPicker();
            foreach (string ext in new[] { ".exe", ".msi", ".msix", ".zip", ".7z", ".rar", ".iso" })
                picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var chosen = await picker.PickSingleFileAsync();
            if (chosen is null) return;

            inspection = new CancellationTokenSource();
            _inspectionCancellation = inspection;
            _cancelInspection.IsEnabled = true;
            _installerStatus.Text = T("Computing SHA-256; no file is executed…",
                "Calcolo SHA-256; nessun file viene eseguito…");
            var result = await GamingOrchestratorClient.Current.InspectGameInstallerAsync(
                new GameInstallerInspectionRequest(chosen.Path), inspection.Token);
            if (!result.Succeeded || result.Value is null)
            {
                _installerStatus.Text = T(
                    "Inspection refused or failed. Verify that Gaming is off and the selected file is complete, local and supported.",
                    "Controllo rifiutato o non riuscito. Verifica che il Gaming sia disattivato e che il file sia completo, locale e supportato.");
                return;
            }

            _inspectedInstallerPath = chosen.Path;
            _installerStatus.Text = T(
                $"{result.Value.FileName} · {result.Value.SizeBytes:N0} bytes\nSHA-256: {result.Value.Sha256}\nA matching hash is not a malware or licensing guarantee; check it against the publisher.",
                $"{result.Value.FileName} · {result.Value.SizeBytes:N0} byte\nSHA-256: {result.Value.Sha256}\nL'hash non garantisce sicurezza o licenza: confrontalo con quello del produttore.");
        }
        catch (OperationCanceledException) when (inspection?.IsCancellationRequested == true)
        {
            _installerStatus.Text = T("Inspection cancelled. No file was modified.",
                "Controllo annullato. Nessun file è stato modificato.");
        }
        catch (Exception)
        {
            _installerStatus.Text = T("Local inspection could not be completed.",
                "Impossibile completare il controllo locale.");
        }
        finally
        {
            _inspectionCancellation = null;
            inspection?.Dispose();
            _cancelInspection.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async Task InspectControllersAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _controllerStatus.Text = T("Checking local XInput controllers…", "Verifica dei controller XInput locali…");
        try
        {
            var result = await GamingOrchestratorClient.Current.GetControllersAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _controllerStatus.Text = T("Controller diagnostics are unavailable.", "Diagnostica controller non disponibile.");
                return;
            }
            if (result.Value.Count == 0)
            {
                _controllerStatus.Text = T("No XInput controllers detected. Other input types have not been checked.",
                    "Nessun controller XInput rilevato. Gli altri tipi di input non sono stati verificati.");
                return;
            }
            _controllerStatus.Text = string.Join("   ", result.Value.Select(g =>
                T($"XInput controller {g.Slot + 1}", $"Controller XInput {g.Slot + 1}")));
        }
        catch (Exception)
        {
            _controllerStatus.Text = T("Controller inspection failed.", "Verifica controller non riuscita.");
        }
        finally { SetBusy(false); }
    }

    private async Task InspectDisplaysAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _displayItems.Children.Clear();
        _displayStatus.Text = T("Reading Windows display configuration…", "Lettura configurazione schermi Windows…");
        try
        {
            var result = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            if (!result.Succeeded || result.Value is not { Supported: true } snapshot)
            {
                _displayStatus.Text = T("Windows display information is unavailable.",
                    "Informazioni sugli schermi Windows non disponibili.");
                return;
            }
            _displayStatus.Text = T(
                $"{snapshot.Displays.Count} connected displays. HDR/VRR capabilities have not been validated.",
                $"{snapshot.Displays.Count} schermi collegati. Le funzionalità HDR/VRR non sono state verificate.");
            foreach (GamingDisplayModeDto display in snapshot.Displays)
            {
                string rate = display.RefreshRateHz > 0 ? $" · {display.RefreshRateHz} Hz" : "";
                string primary = display.Primary ? T(" (Primary)", " (Principale)") : "";
                _displayItems.Children.Add(new TextBlock
                {
                    Text = $"{display.DisplayId}{primary}: {display.Width} × {display.Height}{rate}",
                    FontSize = _consolePresentation ? 20 : 14,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        catch (Exception)
        {
            _displayStatus.Text = T("Could not inspect Windows displays.", "Impossibile controllare gli schermi Windows.");
        }
        finally { SetBusy(false); }
    }

    private Task RefreshSteamAsync() => RefreshDetectedAsync(
        "Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());

    private Task RefreshEpicAsync() => RefreshDetectedAsync(
        "Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());

    private Task RefreshGogAsync() => RefreshDetectedAsync(
        "GOG", () => GamingOrchestratorClient.Current.DiscoverGogGamesAsync());

    private async Task RefreshAllStoresAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _lastDetectedGames.Clear();
        _detectedPage = 0;
        RenderDiscoveredGames();
        _discoveryStatus.Text = T("Detecting Steam, Epic and GOG installations…",
                                  "Rilevamento installazioni Steam, Epic e GOG…");
        try
        {
            // Each store remains independent. A broken provider cannot erase a valid
            // catalog result from another store. No background scan is started at boot.
            var steam = GamingOrchestratorClient.Current.DiscoverSteamGamesAsync();
            var epic = GamingOrchestratorClient.Current.DiscoverEpicGamesAsync();
            var gog = GamingOrchestratorClient.Current.DiscoverGogGamesAsync();
            await Task.WhenAll(steam, epic, gog);

            var results = new[]
            {
                (Name: "Steam", Result: await steam),
                (Name: "Epic", Result: await epic),
                (Name: "GOG", Result: await gog)
            };
            string[] failed = results.Where(x => !x.Result.Succeeded || x.Result.Value == null)
                .Select(x => x.Name).ToArray();
            _lastDetectedGames = results
                .Where(x => x.Result.Succeeded && x.Result.Value != null)
                .SelectMany(x => x.Result.Value!)
                .GroupBy(g => g.ProviderId + ":" + g.ProviderGameId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _discoveryStatus.Text = T(
                $"Found {_lastDetectedGames.Count} installed games across stores. " +
                (failed.Length > 0 ? "Unavailable: " + string.Join(", ", failed) : "All providers responded."),
                $"Rilevati {_lastDetectedGames.Count} giochi installati tra gli store. " +
                (failed.Length > 0 ? "Non disponibili: " + string.Join(", ", failed) : "Tutti i provider hanno risposto."));
            RenderDiscoveredGames();
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Store discovery failed. Nothing was changed.",
                "Rilevamento degli store non riuscito. Nessun dato è stato modificato.");
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshDetectedAsync(
        string provider,
        Func<Task<GamingCallResult<List<DiscoveredGameDto>>>> fetch)
    {
        if (_busy) return;
        SetBusy(true);
        _lastDetectedGames.Clear();
        _discoveredGames.Children.Clear();
        _discoveryStatus.Text = T($"Detecting installed {provider} games…", $"Rilevamento giochi {provider} installati…");
        try
        {
            GamingCallResult<List<DiscoveredGameDto>> result = await fetch();
            if (!result.Succeeded || result.Value is null)
            {
                _discoveryStatus.Text = T($"{provider} discovery unavailable. No files were changed.", $"Rilevamento {provider} non disponibile. Nessun file è stato modificato.");
                return;
            }
            _lastDetectedGames = result.Value.ToList();
            _detectedPage = 0;
            _discoveryStatus.Text = result.Value.Count == 0
                ? T($"No installed {provider} games found.", $"Nessun gioco {provider} installato rilevato.")
                : T($"Found {result.Value.Count} installed {provider} games. Use pagination to view them all.",
                    $"Rilevati {result.Value.Count} giochi {provider} installati. Usa le pagine per consultarli tutti.");
            RenderDiscoveredGames();
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T($"{provider} discovery failed. No files were changed.", $"Rilevamento {provider} non riuscito. Nessun file è stato modificato.");
        }
        finally { SetBusy(false); }
    }

    private void RenderDiscoveredGames()
    {
        _discoveredGames.Children.Clear();
        var matches = _lastDetectedGames
            .Where(g => g.Name.Contains(_filter.Text ?? "", StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        GameLibraryPageWindow window = GameLibraryPageWindow.For(matches.Count, _detectedPage);
        _detectedPage = window.Page;
        _lastDetectedWindow = window;
        _detectedPrevious.IsEnabled = !_busy && window.HasPrevious;
        _detectedNext.IsEnabled = !_busy && window.HasNext;
        _detectedRange.Text = window.Count == 0
            ? T("No matching store games.", "Nessun gioco store corrispondente.")
            : T($"Showing store games {window.FirstVisible}–{window.LastVisible} of {window.Total}.",
                $"Mostrati giochi store {window.FirstVisible}–{window.LastVisible} di {window.Total}.");
        foreach (DiscoveredGameDto game in matches.Skip(window.Offset).Take(window.Count))
        {
            var item = new StackPanel
            {
                Spacing = _consolePresentation ? 12 : 2,
                Padding = new Thickness(_consolePresentation ? 20 : 12)
            };
            item.Children.Add(new TextBlock
            {
                Text = game.Name,
                FontSize = _consolePresentation ? 24 : 16,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            item.Children.Add(new TextBlock
            {
                Text = game.ProviderId.ToUpperInvariant() + " #" + game.ProviderGameId,
                FontSize = _consolePresentation ? 18 : 14,
                TextWrapping = TextWrapping.Wrap
            });
            bool canUseStoreHandoff = GameStoreLaunchIdPolicy.SupportsOfficialLaunch(
                game.ProviderId, game.ProviderGameId);
            if (!canUseStoreHandoff)
            {
                item.Children.Add(new TextBlock
                {
                    Text = T("This store entry has no supported launch identifier.",
                        "Questa voce store non ha un identificativo di avvio supportato."),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = _consolePresentation ? 18 : 14
                });
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "steam", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via Steam", "Gioca tramite Steam"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Steam_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request Steam to launch ", "Chiedi a Steam di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchSteamAsync(game);
                item.Children.Add(request);
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "epic", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via Epic Games Launcher", "Gioca tramite Epic Games Launcher"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Epic_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request Epic Games Launcher to start ", "Chiedi a Epic Games Launcher di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchEpicAsync(game);
                item.Children.Add(request);
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via GOG GALAXY", "Gioca tramite GOG GALAXY"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Gog_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request GOG GALAXY to launch ", "Chiedi a GOG GALAXY di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchGogAsync(game);
                item.Children.Add(request);
            }
            _discoveredGames.Children.Add(item);
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await LoadAsync(); }
        catch (Exception) { _status.Text = T("Could not load the library.", "Impossibile caricare la libreria."); }
        finally { SetBusy(false); }
    }

    private async Task LoadAsync()
    {
        _status.Text = T("Loading…", "Caricamento…");
        var result = await GamingOrchestratorClient.Current.ListManualGamesAsync();
        if (!result.Succeeded || result.Value == null) { _status.Text = T("Could not load the library. Try opening it again.", "Impossibile caricare la libreria. Prova a riaprirla."); return; }
        _allGames = result.Value;
        _page = 0;
        RenderGames();
    }

    private void RenderGames()
    {
        _games.Children.Clear();
        var matches = _allGames.Where(game => game.Name.Contains(_filter.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (_page * 100 >= matches.Count) _page = Math.Max(0, (matches.Count - 1) / 100);
        int first = matches.Count == 0 ? 0 : _page * 100 + 1;
        int last = Math.Min(matches.Count, (_page + 1) * 100);
        _status.Text = matches.Count == 0 ? T("No matching games.", "Nessun gioco corrispondente.") :
            T($"Showing {first}–{last} of {matches.Count}.", $"Mostrati {first}–{last} di {matches.Count}.");
        _previous.IsEnabled = _page > 0; _next.IsEnabled = last < matches.Count;
        foreach (ManualGameDto game in matches.Skip(_page * 100).Take(100))
        {
            var card = new StackPanel { Spacing = _consolePresentation ? 12 : 4, Padding = new Thickness(_consolePresentation ? 20 : 12) };
            card.Children.Add(new TextBlock { Text = game.Name, FontSize = _consolePresentation ? 24 : 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = game.ExecutablePath, FontSize = _consolePresentation ? 16 : 14, TextWrapping = TextWrapping.Wrap });
            var launch = new Button
            {
                Content = T("Play", "Gioca"),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = _consolePresentation ? 20 : 14,
                MinHeight = _consolePresentation ? 56 : 32
            };
            AutomationProperties.SetName(launch, T("Launch ", "Avvia ") + game.Name);
            AutomationProperties.SetAutomationId(launch, "GameLibrary_Launch_" + game.Id);
            launch.Click += async (_, _) => await LaunchAsync(game);
            card.Children.Add(launch);
            var remove = new Button { Content = T("Remove from list", "Rimuovi dalla lista"), HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = _consolePresentation ? 20 : 14, MinHeight = _consolePresentation ? 56 : 32 };
            AutomationProperties.SetName(remove, T("Remove ", "Rimuovi ") + game.Name);
            AutomationProperties.SetAutomationId(remove, "GameLibrary_Remove_" + game.Id);
            remove.Click += async (_, _) => await RemoveAsync(game);
            card.Children.Add(remove); _games.Children.Add(card);
        }
    }

    private async Task LaunchSteamAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "steam", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using Steam?", "Avviare tramite Steam?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode, then ask the installed official Steam client to launch this game. Steam owns the actual game process, so automatic Return to AI is not available. Return manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà al client Steam ufficiale installato di avviare il gioco. Steam gestisce il processo effettivo: il ritorno automatico all'AI non è disponibile. Torna manualmente al termine."),
                PrimaryButtonText = T("Request Steam launch", "Richiedi avvio a Steam"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestSteamGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent to Steam. Return to AI manually after playing.",
                    "Richiesta di avvio inviata a Steam. Torna manualmente all'AI dopo aver giocato.")
                : T("Steam launch was not accepted. Check the installed client and Gaming state.",
                    "Avvio tramite Steam non accettato. Controlla il client installato e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request Steam launch.", "Impossibile richiedere l'avvio a Steam.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchEpicAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "epic", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using Epic Games Launcher?", "Avviare tramite Epic Games Launcher?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode and ask Windows to open Epic's registered game-launch protocol. The Windows protocol handler is user-configurable; verify that it opens the real Epic client. PaRuX cannot verify the game process or account ownership. Return to AI manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà a Windows di aprire il protocollo di avvio registrato da Epic. Il gestore di protocollo Windows è configurabile dall'utente: verifica che apra il vero client Epic. PaRuX non può verificare il processo del gioco né la proprietà sull'account. Torna manualmente all'AI al termine."),
                PrimaryButtonText = T("Request Epic launch", "Richiedi avvio a Epic"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestEpicGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Epic protocol request dispatched. Game start is not verified; return to AI manually.",
                    "Richiesta al protocollo Epic inviata. L'avvio del gioco non è verificato: torna manualmente all'AI.")
                : T("Epic request was not dispatched. Check the installed client, Windows protocol registration and Gaming state.",
                    "Richiesta Epic non inviata. Verifica client installato, registrazione del protocollo Windows e stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request Epic launch.", "Impossibile richiedere l'avvio tramite Epic.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchGogAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "gog", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using GOG GALAXY?", "Avviare tramite GOG GALAXY?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode, then ask the installed GOG GALAXY client to launch the selected game. GALAXY owns the actual game process. Automatic Return to AI is not available; return manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà al client GOG GALAXY installato di avviare il gioco selezionato. GALAXY gestisce il processo effettivo. Il ritorno automatico all'AI non è disponibile: torna manualmente al termine."),
                PrimaryButtonText = T("Request GOG launch", "Richiedi avvio a GOG"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestGogGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent to GOG GALAXY. Return to AI manually after playing.",
                    "Richiesta di avvio inviata a GOG GALAXY. Torna manualmente all'AI dopo aver giocato.")
                : T("GOG launch was not accepted. Check the installed client and Gaming state.",
                    "Avvio tramite GOG non accettato. Controlla il client installato e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request GOG launch.", "Impossibile richiedere l'avvio tramite GOG.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch selected game?", "Avviare il gioco selezionato?"),
                Content = T(
                    "PaRuX will pause local AI resources and start only this saved executable. It will not install software, suspend the game, or forcibly close it. Automatic return is enabled only if Windows confirms the owned game process.",
                    "PaRuX sospenderà le risorse AI locali e avvierà solo questo eseguibile salvato. Non installerà software, non sospenderà il gioco né lo chiuderà forzatamente. Il ritorno automatico è disponibile solo quando Windows conferma il processo del gioco avviato."),
                PrimaryButtonText = T("Launch", "Avvia"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await GamingOrchestratorClient.Current.LaunchManualGameAsync(game.Id);
            if (!result.Succeeded || result.Value is not { Launched: true } launch)
            {
                _status.Text = T(
                    "Could not launch the game. Check Gaming state and the executable.",
                    "Impossibile avviare il gioco. Controlla lo stato Gaming e l'eseguibile.");
                return;
            }
            await UpdateGamingStatusAsync();
            _status.Text = launch.AutomaticReturnOnExit
                ? T("Game started. Automatic return to AI is monitoring the owned process.",
                    "Gioco avviato. Il ritorno automatico all'AI monitora il processo avviato.")
                : T("Game started. Return to AI manually when finished.",
                    "Gioco avviato. Torna manualmente all'AI al termine.");
        }
        catch (Exception)
        {
            _status.Text = T("Could not launch the game.", "Impossibile avviare il gioco.");
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = T("Remove game?", "Rimuovere il gioco?"), Content = T("This removes only the entry; game files stay on disk.", "Viene rimossa solo la voce; i file del gioco restano sul disco."), PrimaryButtonText = T("Remove", "Rimuovi"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RemoveManualGameAsync(game.Id);
            if (!result.Succeeded) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); return; }
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); }
        finally { SetBusy(false); }
    }

    private Control? _focusBeforeBusy;

    /// <summary>Remembers the focused control when an interaction starts and puts focus back when it
    /// ends (game launch request, dialogs, rescans), so controller users never land on nothing.
    /// Rendering may replace the control meanwhile; then the Console home target is used.</summary>
    private void TrackFocusAcrossBusy(bool value)
    {
        if (value == _busy) return;
        if (value)
        {
            _focusBeforeBusy = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
            return;
        }
        Control? remembered = _focusBeforeBusy;
        _focusBeforeBusy = null;
        if (!_consolePresentation) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_busy) return;
            if (remembered is { IsLoaded: true, IsEnabled: true } &&
                remembered.Focus(FocusState.Programmatic)) return;
            FocusConsoleHome();
        });
    }

    private void SetBusy(bool value) { TrackFocusAcrossBusy(value); _busy = value; _add.IsEnabled = !value; _browse.IsEnabled = !value; _steamRefresh.IsEnabled = !value; _epicRefresh.IsEnabled = !value; _gogRefresh.IsEnabled = !value; _allStoresRefresh.IsEnabled = !value; _displayRefresh.IsEnabled = !value; _controllerRefresh.IsEnabled = !value; _inspectInstaller.IsEnabled = !value && _installerInspectionAllowed; _refreshStaged.IsEnabled = !value && _installerInspectionAllowed; UpdatePackageStageAvailability(); _cancelInspection.IsEnabled = value && _inspectionCancellation != null; _gamingStatusRefresh.IsEnabled = !value; _returnToAi.IsEnabled = !value && _returnToAiAvailable; _games.IsHitTestVisible = !value; _discoveredGames.IsHitTestVisible = !value; _detectedPrevious.IsEnabled = !value && _lastDetectedWindow.HasPrevious; _detectedNext.IsEnabled = !value && _lastDetectedWindow.HasNext; }
}
