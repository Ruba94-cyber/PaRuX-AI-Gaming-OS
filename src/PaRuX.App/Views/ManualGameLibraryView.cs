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
    private readonly TextBox _arguments = new();
    private readonly TextBox _workingDirectory = new();
    private readonly ComboBox _kind = new() { MinWidth = 220 };
    private readonly StackPanel _games = new() { Spacing = 8 };
    private readonly StackPanel _root = new() { Spacing = 16, Padding = new Thickness(24), MaxWidth = 1500, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _heading = new();
    private bool _consolePresentation;
    private readonly StackPanel _discoveredGames = new() { Spacing = 8 };
    private readonly TextBlock _discoveryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _steamRefresh = new();
    private readonly Button _epicRefresh = new();
    private readonly Button _gogRefresh = new();
    private readonly Button _allStoresRefresh = new();
    // First-visit voluntary local store scan; no disk scan on app startup.
    private readonly Border _storeDiscoveryOffer = new()
    {
        Visibility = Visibility.Collapsed, Padding = new Thickness(16),
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
    };
    private readonly TextBlock _storeDiscoveryOfferText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _storeDiscoveryOfferScan = new() { MinHeight = 44 };
    private readonly Button _storeDiscoveryOfferDismiss = new() { MinHeight = 44 };
    private readonly Button _displayRefresh = new();
    private readonly Button _controllerRefresh = new();
    private readonly Button _returnToAi = new();
    // Compact strip above every section: shown only while Gaming mode is active, so Return to AI is one action away from any section.
    private readonly Border _gamingStrip = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _gamingStripText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _gamingStripReturn = new();
    private readonly TextBlock _sessionHeading = new();
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
    // An unreachable Core may temporarily disable actions, but must not erase the
    // last confirmed Gaming observation needed for foreground-return reconciliation.
    private bool _lastConfirmedGamingActive;
    private bool _consoleForegroundRefreshInFlight;
    private readonly TextBlock _controllerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _displayStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _displayItems = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    private readonly Button _browse = new();
    private bool _busy;
    private readonly TextBox _filter = new();
    /// <summary>On-demand, manually confirmed, no auto-send Game-to-Chat handoff.</summary>
    public event Func<string, IReadOnlyList<string>, bool>? GameGuideChatRequested;
    private UniversalLibraryPanel? _universal;
    private readonly GameDownloadsPanel _downloads = new();
    private readonly ComboBox _storeFilter = new() { MinWidth = 180 };
    private readonly ComboBox _stateFilter = new() { MinWidth = 180 };
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
        BuildStoreDiscoveryOffer(root);

        // The Gaming home (Continue shelf, tile library, detail panel) opens first; it also hosts the opt-in advanced section.
        _universal = new UniversalLibraryPanel(LaunchInstallationAsync, LoadAsync);
        _universal.GameGuideChatRequested += (question, sources) =>
            GameGuideChatRequested?.Invoke(question, sources) == true;
        root.Children.Add(_universal);

        // Real resource/session status lives on the same Games home in both TV and
        // desktop presentations. No background polling or controller worker.
        int sessionStart = root.Children.Count;
        _sessionHeading.Text = T("AI / Gaming session", "Sessione AI / Gaming");
        _sessionHeading.FontSize = 20;
        _sessionHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        root.Children.Add(_sessionHeading);
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

        // Explicit, desktop-only downloads; never create a polling workload in the TV/Console presentation.
        int downloadsStart = root.Children.Count;
        root.Children.Add(_downloads);

        int addStart = root.Children.Count;
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
        _kind.Header = T("Entry type", "Tipo di voce");
        _kind.Items.Add(new ComboBoxItem { Content = T("Game (prepares Gaming Mode)", "Gioco (prepara la modalità Gaming)"), Tag = ManualEntryKinds.Game });
        _kind.Items.Add(new ComboBoxItem { Content = T("Application / tool (started as-is)", "Applicazione / strumento (avviata così com'è)"), Tag = ManualEntryKinds.Application });
        _kind.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_kind, "GameLibrary_Kind_ComboBox");
        _arguments.Header = T("Arguments (optional)", "Argomenti (facoltativi)");
        _arguments.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_arguments, "GameLibrary_Arguments_TextBox");
        _workingDirectory.Header = T("Working folder (optional)", "Cartella di lavoro (facoltativa)");
        _workingDirectory.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_workingDirectory, "GameLibrary_WorkingFolder_TextBox");
        root.Children.Add(_name); root.Children.Add(_path);
        root.Children.Add(_kind); root.Children.Add(_arguments); root.Children.Add(_workingDirectory);
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
        root.Children.Add(_filter);
        _storeFilter.Header = T("Store", "Negozio");
        _stateFilter.Header = T("Install state", "Stato installazione");
        foreach (var (value, label) in new[] { ("", T("All stores", "Tutti i negozi")), ("steam", "Steam"), ("epic", "Epic"), ("gog", "GOG") })
            _storeFilter.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (var (value, label) in new[]
        {
            ("", T("Any state", "Qualsiasi stato")), (GameInstallStates.Installed, T("Installed", "Installato")),
            (GameInstallStates.Updating, T("Updating", "In aggiornamento")), (GameInstallStates.Incomplete, T("Incomplete", "Incompleto")),
            (GameInstallStates.Unknown, T("Not reported", "Non riportato"))
        })
            _stateFilter.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _storeFilter.SelectedIndex = 0; _stateFilter.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_storeFilter, "GameLibrary_StoreFilter_ComboBox");
        AutomationProperties.SetAutomationId(_stateFilter, "GameLibrary_StateFilter_ComboBox");
        _storeFilter.SelectionChanged += (_, _) => { _detectedPage = 0; RenderDiscoveredGames(); };
        _stateFilter.SelectionChanged += (_, _) => { _detectedPage = 0; RenderDiscoveredGames(); };
        var storeFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        storeFilters.Children.Add(_storeFilter); storeFilters.Children.Add(_stateFilter);
        root.Children.Add(storeFilters);
        root.Children.Add(_games);
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

        int displayStart = root.Children.Count;
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
        int displayEnd = root.Children.Count;
        root.Children.Add(_installerSection);

        // One endless page was hard to use. The same controls (and AutomationIds) are grouped into four sections,
        // like a launcher: Library first; downloads, adding/importing and devices one click away.
        List<UIElement> all = root.Children.ToList();
        root.Children.Clear();
        root.Children.Add(all[0]);   // page title
        var sections = new (string Id, string Title, List<UIElement> Items)[]
        {
            ("Library", T("Library", "Libreria"), all.Skip(1).Take(sessionStart - 1).ToList()),
            ("Downloads", T("Downloads", "Download"), all.Skip(downloadsStart).Take(addStart - downloadsStart).ToList()),
            ("Add", T("Add & import", "Aggiungi e importa"),
                all.Skip(addStart).Take(displayStart - addStart).Concat(all.Skip(displayEnd)).ToList()),
            ("Devices", T("Devices & session", "Dispositivi e sessione"),
                all.Skip(sessionStart).Take(downloadsStart - sessionStart)
                    .Concat(all.Skip(displayStart).Take(displayEnd - displayStart)).ToList()),
        };
        var bar = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(bar, "GameLibrary_Sections");
        AutomationProperties.SetName(bar, T("Game library sections", "Sezioni della libreria giochi"));
        var panels = new Dictionary<string, StackPanel>();
        foreach (var section in sections)
        {
            var item = new SelectorBarItem { Text = section.Title, Tag = section.Id, MinHeight = 44 };
            AutomationProperties.SetAutomationId(item, "GameLibrary_Section_" + section.Id);
            bar.Items.Add(item);
            var panel = new StackPanel { Spacing = 16, Visibility = section.Id == "Library" ? Visibility.Visible : Visibility.Collapsed };
            foreach (UIElement element in section.Items) panel.Children.Add(element);
            panels[section.Id] = panel;
        }
        bar.SelectionChanged += (sender, _) =>
        {
            string? tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
            foreach (var pair in panels) pair.Value.Visibility = pair.Key == tag ? Visibility.Visible : Visibility.Collapsed;
        };
        root.Children.Add(bar);
        BuildGamingStrip();
        root.Children.Add(_gamingStrip);
        foreach (var panel in panels.Values) root.Children.Add(panel);
        bar.SelectedItem = bar.Items[0];

        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void BuildGamingStrip()
    {
        _gamingStripText.Text = T("Gaming mode is active. AI is off.", "Modalità Gaming attiva. L'AI è spenta.");
        AutomationProperties.SetAutomationId(_gamingStripText, "GameLibrary_Strip_Status_Text");
        _gamingStripReturn.Content = T("Return to AI", "Torna all'AI");
        _gamingStripReturn.MinHeight = 44;
        _gamingStripReturn.MinWidth = 44;
        _gamingStripReturn.IsEnabled = false;
        AutomationProperties.SetAutomationId(_gamingStripReturn, "GameLibrary_Strip_ReturnToAi_Button");
        AutomationProperties.SetName(_gamingStripReturn, T("Return to AI without closing the game", "Torna all'AI senza chiudere il gioco"));
        // The same handler as the Devices & session button: same confirmation dialog and outcome message.
        _gamingStripReturn.Click += async (_, _) => await ReturnToAiAsync();
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_gamingStripReturn, 1);
        layout.Children.Add(_gamingStripText);
        layout.Children.Add(_gamingStripReturn);
        _gamingStrip.Child = layout;
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out object? brush) && brush is Microsoft.UI.Xaml.Media.Brush fill)
            _gamingStrip.Background = fill;
        AutomationProperties.SetAutomationId(_gamingStrip, "GameLibrary_Strip");
        AutomationProperties.SetName(_gamingStrip, T("Gaming session", "Sessione Gaming"));
    }

    /// <summary>Shows the strip only while Gaming mode is active (same condition as the Return to AI button on Devices &amp; session).</summary>
    private void SyncGamingStrip()
    {
        _gamingStrip.Visibility = _returnToAiAvailable && !GamingOnlySetup ? Visibility.Visible : Visibility.Collapsed;
        _gamingStripReturn.IsEnabled = _returnToAiAvailable && !_busy;
    }

    /// <summary>
    /// A TV-scale presentation of the SAME game library and its existing actions.
    /// Changes only WinUI spacing/type, not stored settings, monitor modes or Windows
    /// input configuration. The desktop layout is restored when Console exits.
    /// </summary>
    public void ApplyConsoleMode(bool enabled)
    {
        _consolePresentation = enabled;
        _root.MaxWidth = enabled ? 1800 : 1500;
        // Console Home is for playing and managing the session. Desktop-only
        // filesystem editing remains available unchanged in normal mode.
        Visibility editorVisibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _manualAddIntro.Visibility = editorVisibility;
        _name.Visibility = editorVisibility;
        _path.Visibility = editorVisibility;
        _manualAddActions.Visibility = editorVisibility;
        _installerSection.Visibility = editorVisibility;
        _downloads.Visibility = editorVisibility;
        if (enabled) _inspectionCancellation?.Cancel();
        _root.Padding = new Thickness(enabled ? 32 : 24);
        _root.Spacing = enabled ? 24 : 16;
        _heading.FontSize = enabled ? 40 : 28;
        _games.Spacing = enabled ? 16 : 8;
        _discoveredGames.Spacing = enabled ? 16 : 8;

        double bodySize = enabled ? 20 : 14;
        foreach (TextBox box in new[] { _name, _path, _filter })
            box.FontSize = bodySize;
        _gamingStripText.FontSize = bodySize;
        _gamingStripReturn.FontSize = bodySize;
        _gamingStripReturn.MinHeight = enabled ? 56 : 44;
        foreach (Button button in new[] { _add, _browse, _returnToAi, _gamingStatusRefresh, _allStoresRefresh, _displayRefresh, _controllerRefresh, _steamRefresh, _epicRefresh, _gogRefresh, _previous, _next, _detectedPrevious, _detectedNext })
        {
            button.FontSize = bodySize;
            button.MinHeight = enabled ? 56 : 32;
        }
        _status.FontSize = bodySize;
        _storeDiscoveryOfferText.FontSize = bodySize;
        _storeDiscoveryOfferScan.FontSize = bodySize;
        _storeDiscoveryOfferDismiss.FontSize = bodySize;
        _storeDiscoveryOfferScan.MinHeight = enabled ? 56 : 44;
        _storeDiscoveryOfferDismiss.MinHeight = enabled ? 56 : 44;
        _universal?.SetConsolePresentation(enabled);
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

    /// <summary>Starts one concrete installation of a title through the same confirmation dialogs as the lists below.</summary>
    private async Task LaunchInstallationAsync(GameInstallationDto installation)
    {
        if (installation.Source is GameInstallationSources.Manual or GameInstallationSources.Emulator)
        {
            ManualGameDto? entry = _allGames.FirstOrDefault(g => g.Id == installation.SourceGameId);
            if (entry is null) { await LoadAsync(); entry = _allGames.FirstOrDefault(g => g.Id == installation.SourceGameId); }
            if (entry is null)
            {
                _status.Text = T("That library entry no longer exists.", "Questa voce della libreria non esiste più.");
                return;
            }
            await LaunchAsync(entry);
            return;
        }

        // Store launches only need the store and the game id; the Core re-discovers and re-validates the install.
        var discovered = new DiscoveredGameDto(installation.Source, installation.SourceGameId, installation.Name, string.Empty);
        switch (installation.Source)
        {
            case GameInstallationSources.Steam: await LaunchSteamAsync(discovered); break;
            case GameInstallationSources.Epic: await LaunchEpicAsync(discovered); break;
            case GameInstallationSources.Gog: await LaunchGogAsync(discovered); break;
            case GameInstallationSources.Ea:
            case GameInstallationSources.BattleNet:
            case GameInstallationSources.Ubisoft:
            case GameInstallationSources.Xbox: await LaunchExtraStoreAsync(discovered); break;
        }
    }

    /// <summary>Console shoulder buttons: page the manual list, or the store list when the manual list has no further page.</summary>
    public void PageLists(int delta)
    {
        if (_busy) return;
        if (delta > 0)
        {
            if (_next.IsEnabled) { _page++; RenderGames(); }
            else if (_lastDetectedWindow.HasNext) { _detectedPage++; RenderDiscoveredGames(); }
        }
        else
        {
            if (_detectedPage > 0) { _detectedPage--; RenderDiscoveredGames(); }
            else if (_page > 0) { _page--; RenderGames(); }
        }
    }

    /// <summary>Console right stick: scroll the page without moving focus.</summary>
    public void ScrollBy(double pixels)
    {
        if (Content is ScrollViewer viewer)
            viewer.ChangeView(null, Math.Clamp(viewer.VerticalOffset + pixels, 0, viewer.ScrollableHeight), null, disableAnimation: true);
    }

    /// <summary>Console Menu button: jump to the filter field, where A/Y opens the on-screen keyboard.</summary>
    public void FocusSearch() => _filter.Focus(FocusState.Programmatic);

    /// <summary>Leaving the Games workspace cancels any expensive local hash
    /// before the next workspace resumes interactive AI activity.</summary>
    public void CancelExpensiveWork()
    {
        _inspectionCancellation?.Cancel();
        _universal?.CancelPendingQueries();
    }

    private bool GamingOnlySetup =>
        SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) == SetupProfile.GamingOnly;

    private void ApplySetupProfilePresentation()
    {
        bool gamingOnly = GamingOnlySetup;
        _returnToAi.Visibility = gamingOnly ? Visibility.Collapsed : Visibility.Visible;
        SyncGamingStrip();
        _sessionHeading.Text = gamingOnly
            ? T("Gaming session", "Sessione Gaming")
            : T("AI / Gaming session", "Sessione AI / Gaming");
        _universal?.ApplySetupProfilePresentation();
    }

    public async void OnShown()
    {
        ApplySetupProfilePresentation();
        UpdateStoreDiscoveryOffer();
        if (_universal is not null)
            await _universal.RefreshVisibleTitlesAsync();
        await RefreshAsync();
        await UpdateGamingStatusAsync();
        UpdateStoreDiscoveryOffer();
        // The initial catalog load briefly disables buttons; restore a useful
        // gamepad focus target once the real server status has been fetched.
        if (_consolePresentation && !_busy)
            FocusConsoleHome();
    }

    /// <summary>Returns null when status cannot be confirmed. A failed read never
    /// counts as a game exit or an unlocked AI runtime.</summary>
    private async Task<GamingStatusDto?> UpdateGamingStatusAsync()
    {
        GamingStatusDto? observed = null;
        try
        {
            GamingStatusDto? status = await GamingOrchestratorClient.Current.GetStatusAsync();
            observed = status;
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
                _lastConfirmedGamingActive = _returnToAiAvailable;
                _installerInspectionAllowed = !_returnToAiAvailable;
                _gamingStatus.Text = status.State == "GamingLocal" && status.IsGamingActive &&
                    status.ManualReturnRequiredReason is { Length: > 0 } manualReason
                    ? manualReason switch
                    {
                        "bootstrap-exited-early" => T(
                            "The launched process ended too quickly to confirm the game ended. Gaming remains active; return to AI manually when finished.",
                            "Il processo avviato si è chiuso troppo presto per confermare la fine del gioco. Gaming rimane attivo; torna all'AI manualmente quando hai finito."),
                        "child-still-active" => T(
                            "A child process is still running after the game launcher closed. Gaming remains active; use Return to AI after playing.",
                            "Dopo la chiusura del launcher è ancora attivo un processo figlio. Gaming rimane attivo; usa Torna all'AI dopo aver giocato."),
                        _ => T(
                            "PaRuX could not verify that every game process exited. Gaming stays active; use Return to AI manually.",
                            "PaRuX non può confermare che tutti i processi del gioco siano terminati. Gaming rimane attivo; usa Torna all'AI manualmente.")
                    }
                    : status.State == "AiReady" && status.IsGamingActive
                    ? T("Gaming resource lock is still active after Core restart. Select Return to AI to recover without closing the game.",
                        "Il blocco delle risorse Gaming è ancora attivo dopo il riavvio del Core. Seleziona Torna all'AI per recuperare senza chiudere il gioco.")
                    : status.State == "AiReady"
                        ? T("AI ready · No Gaming session active.", "AI pronta · Nessuna sessione Gaming attiva.")
                        : T($"Gaming state: {GamingStateLabel(status.State)}. Library search uses the last cached catalogue; Return to AI leaves the game running.",
                            $"Stato Gaming: {GamingStateLabel(status.State)}. La ricerca usa il catalogo in cache; tornando all'AI il gioco rimane aperto.");
            }
        }
        catch (Exception)
        {
            _returnToAiAvailable = false;
            _installerInspectionAllowed = false;
            _gamingStatus.Text = T("Gaming status unavailable.", "Stato Gaming non disponibile.");
        }
        _returnToAi.IsEnabled = _returnToAiAvailable && !_busy;
        SyncGamingStrip();
        _inspectInstaller.IsEnabled = _installerInspectionAllowed && !_busy;
        _refreshStaged.IsEnabled = _installerInspectionAllowed && !_busy;
        UpdatePackageStageAvailability();
        return observed;
    }

    /// <summary>Refresh only when the user brings the Console window back into
    /// the foreground. Do not steal focus from a running emulator or poll in the
    /// background. An unconfirmed Core response never implies a game ended.</summary>
    public async Task OnConsoleForegroundActivatedAsync()
    {
        if (!_consolePresentation || _busy || _consoleForegroundRefreshInFlight) return;
        _consoleForegroundRefreshInFlight = true;
        try
        {
            bool previouslyGaming = _lastConfirmedGamingActive;
            GamingStatusDto? state = await UpdateGamingStatusAsync();
            if (GameLibraryForegroundReturnPolicy.CanRefreshTitles(previouslyGaming, state))
            {
                if (_universal is not null)
                    await _universal.RefreshTitlesAfterGameAsync();
                _gamingStatus.Text = GameLibraryForegroundReturnPolicy.HasConfirmedOwnedExit(state)
                    ? T("The verified game process exited. PaRuX returned resources to AI; Console Home is ready.",
                        "Il processo del gioco verificato è terminato. PaRuX ha restituito le risorse all'AI; la Home Console è pronta.")
                    : T("AI resources available again. PaRuX did not terminate the game or launcher.",
                        "Risorse AI nuovamente disponibili. PaRuX non ha terminato il gioco o il launcher.");
            }
        }
        catch (Exception)
        {
            _gamingStatus.Text = T("Could not refresh Gaming status after returning to PaRuX.",
                "Impossibile aggiornare lo stato Gaming al ritorno in PaRuX.");
        }
        finally { _consoleForegroundRefreshInFlight = false; }
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
        if (GamingOnlySetup || _busy || !_returnToAiAvailable) return;
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
            GamingStatusDto? state = await UpdateGamingStatusAsync();
            // HTTP success alone is not a confirmed state transition. In particular,
            // a failed status read must never display "AI ready" after a gaming exit.
            if (state is { IsGamingActive: false } && !result.Succeeded)
                // Gaming Mode is over, but nothing could be restored (typically: no chat engine/model installed yet).
                _gamingStatus.Text = T("Gaming Mode ended. No AI model could be restored (none is ready yet): install or start one in Model Hub.",
                    "Modalità Gaming terminata. Nessun modello IA da ripristinare (nessuno è pronto): installalo o avvialo da Model Hub.");
            else if (!result.Succeeded || !GameLibraryForegroundReturnPolicy.CanRefreshTitles(true, state))
                _gamingStatus.Text = T("Return to AI could not be confirmed. Inspect Gaming status and retry.",
                    "Ritorno all'AI non confermato. Controlla lo stato Gaming e riprova.");
            else
            {
                if (_universal is not null)
                    await _universal.RefreshTitlesAfterGameAsync();
                _gamingStatus.Text = T("AI resources ready. PaRuX did not close the game.",
                    "Risorse AI pronte. PaRuX non ha chiuso il gioco.");
            }
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
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(_name.Text, _path.Text, _arguments.Text, _workingDirectory.Text,
                (_kind.SelectedItem as ComboBoxItem)?.Tag as string ?? ManualEntryKinds.Game));
            if (!result.Succeeded) { _status.Text = T("Could not add the entry. Check the name, local .exe path, arguments, working folder, or whether it is already listed.", "Impossibile aggiungere la voce. Controlla nome, percorso .exe locale, argomenti, cartella di lavoro o eventuali duplicati."); return; }
            _name.Text = ""; _path.Text = ""; _arguments.Text = ""; _workingDirectory.Text = "";
            await LoadAsync();
            if (_universal is not null) await _universal.RefreshTitlesAfterGameAsync();
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

    private void BuildStoreDiscoveryOffer(StackPanel root)
    {
        _storeDiscoveryOfferText.Text = T(
            "Find installed Steam, Epic and GOG games with one local read-only scan. Nothing is installed, launched or uploaded. Or skip and scan later.",
            "Trova i giochi Steam, Epic e GOG installati con una scansione locale di sola lettura. Nessuna installazione, avvio o invio di dati. Oppure salta e rileva in seguito.");
        AutomationProperties.SetAutomationId(_storeDiscoveryOffer, "GameLibrary_FirstRunScan_Offer");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferText, "GameLibrary_FirstRunScan_Description");
        AutomationProperties.SetLiveSetting(_storeDiscoveryOfferText, AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_storeDiscoveryOfferText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _storeDiscoveryOfferScan.Content = T("Find my installed games", "Trova i giochi installati");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferScan, "GameLibrary_FirstRunScan_Start");
        _storeDiscoveryOfferScan.Click += async (_, _) => await RefreshAllStoresAsync();
        _storeDiscoveryOfferDismiss.Content = T("Not now", "Non ora");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferDismiss, "GameLibrary_FirstRunScan_Dismiss");
        _storeDiscoveryOfferDismiss.Click += (_, _) => DismissStoreDiscoveryOffer();
        actions.Children.Add(_storeDiscoveryOfferScan);
        actions.Children.Add(_storeDiscoveryOfferDismiss);
        content.Children.Add(actions);
        _storeDiscoveryOffer.Child = content;
        root.Children.Add(_storeDiscoveryOffer);
        UpdateStoreDiscoveryOffer();
    }

    private void UpdateStoreDiscoveryOffer()
    {
        _storeDiscoveryOffer.Visibility =
            !SettingsStore.Current.Data.GameStoreDiscoveryPromptDismissed && !CorporateMode.IsActive
                && !GamingManager.Current.IsActive && !_lastConfirmedGamingActive
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DismissStoreDiscoveryOffer()
    {
        SettingsStore.Current.Data.GameStoreDiscoveryPromptDismissed = true;
        SettingsStore.Current.Save();
        UpdateStoreDiscoveryOffer();
    }

    private Task RefreshSteamAsync() => RefreshDetectedAsync(
        "Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());

    private Task RefreshEpicAsync() => RefreshDetectedAsync(
        "Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());

    private Task RefreshGogAsync() => RefreshDetectedAsync(
        "GOG", () => GamingOrchestratorClient.Current.DiscoverGogGamesAsync());

    private bool CanDiscoverStores()
    {
        if (!GamingManager.Current.IsActive && !_lastConfirmedGamingActive) return true;
        _discoveryStatus.Text = T(
            "Store detection is paused during Gaming to protect game performance. Return to AI and try again.",
            "Rilevamento store sospeso durante il Gaming per proteggere le prestazioni. Torna all'AI e riprova.");
        return false;
    }

    private static async Task<GamingCallResult<List<DiscoveredGameDto>>> TryDiscoverStoreAsync(
        string provider, Func<Task<GamingCallResult<List<DiscoveredGameDto>>>> discover)
    {
        try { return await discover(); }
        catch (Exception ex)
        {
            // One failed provider must not discard titles returned by other providers.
            Log.Write("Gaming store discovery " + provider + ": " + ex.GetType().Name);
            return GamingCallResult<List<DiscoveredGameDto>>.Fail(provider + " discovery unavailable.");
        }
    }

    private async Task RefreshAllStoresAsync()
    {
        if (_busy || !CanDiscoverStores()) return;
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
            var steam = TryDiscoverStoreAsync("Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());
            var epic = TryDiscoverStoreAsync("Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());
            var gog = TryDiscoverStoreAsync("GOG", () => GamingOrchestratorClient.Current.DiscoverGogGamesAsync());
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
            // Dismiss only after at least one real provider responded. A full outage
            // keeps the banner available to retry; never claim success on a failure.
            if (results.Any(x => x.Result.Succeeded && x.Result.Value != null))
                DismissStoreDiscoveryOffer();
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
        if (_busy || !CanDiscoverStores()) return;
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

    private static string InstallStateLabel(string state) => state switch
    {
        GameInstallStates.Installed => T("installed", "installato"),
        GameInstallStates.Updating => T("updating", "in aggiornamento"),
        GameInstallStates.Incomplete => T("incomplete", "incompleto"),
        _ => T("state not reported", "stato non riportato")
    };

    private void RenderDiscoveredGames()
    {
        _discoveredGames.Children.Clear();
        var matches = GameLibraryQuery.Filter(_lastDetectedGames, _filter.Text,
            (_storeFilter.SelectedItem as ComboBoxItem)?.Tag as string,
            (_stateFilter.SelectedItem as ComboBoxItem)?.Tag as string).ToList();
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
                Text = game.ProviderId.ToUpperInvariant() + " #" + game.ProviderGameId + " · " + InstallStateLabel(game.InstallState),
                FontSize = _consolePresentation ? 18 : 14,
                TextWrapping = TextWrapping.Wrap
            });
            var otherStores = GameLibraryQuery.OtherStores(game, _lastDetectedGames);
            if (otherStores.Count > 0)
                item.Children.Add(new TextBlock
                {
                    Text = T("Also found in: ", "Presente anche in: ") + string.Join(", ", otherStores),
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
            bool isApplicationEntry = string.Equals(game.Kind, ManualEntryKinds.Application, StringComparison.OrdinalIgnoreCase);
            string details = (isApplicationEntry ? T("Application", "Applicazione") : T("Game", "Gioco")) +
                (string.IsNullOrEmpty(game.Arguments) ? "" : " · " + T("with arguments", "con argomenti"));
            card.Children.Add(new TextBlock { Text = details, FontSize = _consolePresentation ? 16 : 13, Opacity = 0.8 });
            var launch = new Button
            {
                Content = isApplicationEntry ? T("Start", "Avvia") : T("Play", "Gioca"),
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

    /// <summary>EA app, Battle.net, Ubisoft Connect and Xbox: confirmed handoff through the client's own URI protocol.</summary>
    private async Task LaunchExtraStoreAsync(DiscoveredGameDto game)
    {
        if (_busy || !GameStoreLaunchIdPolicy.SupportsOfficialLaunch(game.ProviderId, game.ProviderGameId)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using the store client?", "Avviare tramite il client dello store?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode and ask Windows to open the store client's own registered launch protocol for this installed game. PaRuX does not sign in, read credentials or verify ownership; it only observes the game to count play time. Return to AI manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà a Windows di aprire il protocollo di avvio registrato dal client dello store per questo gioco installato. PaRuX non accede, non legge credenziali e non verifica la proprietà; osserva il gioco solo per contare il tempo di gioco. Torna manualmente all'AI al termine."),
                PrimaryButtonText = T("Request launch", "Richiedi avvio"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestExtraStoreGameLaunchAsync(game.ProviderId, game.ProviderGameId);
            await UpdateGamingStatusAsync();
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent through the store client. Return to AI manually after playing.",
                    "Richiesta di avvio inviata tramite il client dello store. Torna manualmente all'AI dopo aver giocato.")
                : T("Launch was not accepted. Check the installed client, its Windows protocol registration and Gaming state.",
                    "Avvio non accettato. Controlla il client installato, la registrazione del protocollo Windows e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request the launch.", "Impossibile richiedere l'avvio.");
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
            bool isApplication = string.Equals(game.Kind, ManualEntryKinds.Application, StringComparison.OrdinalIgnoreCase);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isApplication ? T("Start selected application?", "Avviare l'applicazione selezionata?")
                                      : T("Launch selected game?", "Avviare il gioco selezionato?"),
                Content = isApplication
                    ? T("PaRuX will start only this saved program with its saved arguments. It does not change AI or Gaming state and does not monitor or close the program.",
                        "PaRuX avvierà solo questo programma salvato con i suoi argomenti. Non modifica lo stato AI o Gaming e non monitora né chiude il programma.")
                    : T(
                    "PaRuX will pause local AI resources and start only this saved executable. It will not install software, suspend the game, or forcibly close it. Automatic return is enabled only if Windows confirms the owned game process.",
                    "PaRuX sospenderà le risorse AI locali e avvierà solo questo eseguibile salvato. Non installerà software, non sospenderà il gioco né lo chiuderà forzatamente. Il ritorno automatico è disponibile solo quando Windows conferma il processo del gioco avviato."),
                PrimaryButtonText = T("Launch", "Avvia"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            // Make the optional TV window routing visible BEFORE permission
            // to launch. A chosen TV does not promise fullscreen routing.
            var displaySettings = SettingsStore.Current.Data;
            // Routing is opt-in through choosing a TV; with none chosen the game launches normally.
            bool routeToTv = !isApplication && displaySettings.RouteVerifiedWindowedGamesToTv &&
                !string.IsNullOrWhiteSpace(displaySettings.ConsoleTargetDisplayId);
            string? gameDisplayId = null;
            if (routeToTv)
            {
                var snapshot = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!snapshot.Succeeded ||
                    !GameTvLaunchAdmissionPolicy.TryResolveTarget(true, snapshot.Value,
                        displaySettings.ConsoleDisplays, displaySettings.ConsoleTargetDisplayId,
                        out gameDisplayId))
                {
                    _status.Text = T(
                        "TV routing is enabled but no connected, user-classified TV is selected. No game started. Check Gaming display settings.",
                        "Instradamento TV attivo, ma nessuna TV collegata e classificata è selezionata. Nessun gioco avviato. Controlla le impostazioni Gaming.");
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(gameDisplayId))
                dialog.Content = (dialog.Content?.ToString() ?? string.Empty) + "\n\n" +
                    T("PaRuX will attempt to place one verified windowed game window on your selected TV. Exclusive fullscreen, child processes and external store launchers are not moved.",
                      "PaRuX proverà a posizionare una finestra verificata del gioco sulla TV selezionata. Fullscreen esclusivo, processi secondari e launcher esterni non vengono spostati.");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (routeToTv)
            {
                // Roles/topology may change while the confirmation is shown.
                // Never silently launch onto a different monitor.
                var latest = SettingsStore.Current.Data;
                var snapshot = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!latest.RouteVerifiedWindowedGamesToTv ||
                    !string.Equals(latest.ConsoleTargetDisplayId, gameDisplayId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !snapshot.Succeeded ||
                    !GameTvLaunchAdmissionPolicy.TryResolveTarget(true, snapshot.Value,
                        latest.ConsoleDisplays, gameDisplayId, out _))
                {
                    _status.Text = T(
                        "The selected TV changed or disconnected before launch. No game was started; select it again in Gaming settings.",
                        "La TV selezionata è cambiata o scollegata prima dell'avvio. Nessun gioco avviato; riselezionala nelle impostazioni Gaming.");
                    return;
                }
            }

            var result = await GamingOrchestratorClient.Current.LaunchManualGameAsync(
                game.Id, displayId: gameDisplayId);
            if (!result.Succeeded || result.Value is not { Launched: true } launch)
            {
                _status.Text = !string.IsNullOrWhiteSpace(gameDisplayId) &&
                    (result.Error?.Contains("selected game TV is unavailable",
                        StringComparison.OrdinalIgnoreCase) == true)
                    ? T("The selected TV is unavailable or its Windows display ID changed. The game was not started. Open Gaming settings and select the TV again.",
                        "La TV selezionata non è disponibile o il suo identificativo Windows è cambiato. Il gioco non è stato avviato. Apri le impostazioni Gaming e seleziona nuovamente la TV.")
                    : T("Could not launch the game. Check Gaming state and the executable.",
                        "Impossibile avviare il gioco. Controlla lo stato Gaming e l'eseguibile.");
                return;
            }
            await UpdateGamingStatusAsync();
            // Switches an owner-armed virtual pad to this game's profile; never starts forwarding by itself.
            _ = VirtualPadActivationController.Current.NotifyGameStartedAsync(game.Id);
            if (isApplication)
            {
                _status.Text = T("Application started. PaRuX does not monitor it and AI state is unchanged.",
                    "Applicazione avviata. PaRuX non la monitora e lo stato AI resta invariato.");
                return;
            }
            _status.Text = launch.StartState == GameStartStates.Verified
                ? T("Game process verified. Automatic return to AI is monitoring it.",
                    "Processo del gioco verificato. Il ritorno automatico all'AI lo sta monitorando.")
                : T("Game process started but its identity is not verified. Return to AI manually when finished.",
                    "Processo del gioco avviato ma identità non verificata. Torna manualmente all'AI al termine.");

            // Report what Windows ACTUALLY allowed, not a generic promise that
            // every emulator/exclusive fullscreen frame moved successfully.
            if (!string.IsNullOrEmpty(gameDisplayId))
                _status.Text += "\n" + (launch.WindowPlacement switch
                {
                    GameWindowPlacementStates.Placed => T("Window moved onto the selected TV without changing focus or display settings.",
                        "Finestra spostata sulla TV scelta senza cambiare focus o impostazioni degli schermi."),
                    GameWindowPlacementStates.AlreadyOnTv => T("The game window was already positioned on the TV.",
                        "La finestra del gioco era già posizionata sulla TV."),
                    GameWindowPlacementStates.UnsupportedWindow => T(
                        "TV window placement not confirmed (fullscreen, multiple windows or incompatible window). The game is still running; position it in its own settings.",
                        "Spostamento sulla TV non confermato (fullscreen, più finestre o finestra incompatibile). Il gioco resta avviato; imposta lo schermo nelle sue opzioni."),
                    _ => T("TV placement could not be confirmed. The game remains running; use its own display settings.",
                        "Posizionamento sulla TV non confermato. Il gioco resta avviato; usa le sue impostazioni schermo.")
                });
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
            if (_universal is not null) await _universal.RefreshTitlesAfterGameAsync();
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

    private void SetBusy(bool value) { TrackFocusAcrossBusy(value); _busy = value; _add.IsEnabled = !value; _browse.IsEnabled = !value; _steamRefresh.IsEnabled = !value; _epicRefresh.IsEnabled = !value; _gogRefresh.IsEnabled = !value; _allStoresRefresh.IsEnabled = !value; _displayRefresh.IsEnabled = !value; _controllerRefresh.IsEnabled = !value; _inspectInstaller.IsEnabled = !value && _installerInspectionAllowed; _refreshStaged.IsEnabled = !value && _installerInspectionAllowed; UpdatePackageStageAvailability(); _cancelInspection.IsEnabled = value && _inspectionCancellation != null; _gamingStatusRefresh.IsEnabled = !value; _returnToAi.IsEnabled = !value && _returnToAiAvailable; SyncGamingStrip(); _games.IsHitTestVisible = !value; _discoveredGames.IsHitTestVisible = !value; _detectedPrevious.IsEnabled = !value && _lastDetectedWindow.HasPrevious; _detectedNext.IsEnabled = !value && _lastDetectedWindow.HasNext; _storeDiscoveryOfferScan.IsEnabled = !value; _storeDiscoveryOfferDismiss.IsEnabled = !value; }
}
