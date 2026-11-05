using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

/// <summary>
/// Explicit, desktop-only management of user-provided game/download URLs.
/// The Core owns the transfer, resume, Gaming pause and integrity checks.
/// No automatic refresh timer, store scraper, installer execution or game-time polling.
/// </summary>
internal sealed class GameDownloadsPanel : StackPanel
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private readonly TextBox _url = new() { MaxLength = 2048 };
    private readonly ComboBox _sourceKind = new() { MinWidth = 220 };
    private readonly Button _chooseTorrent = new();
    private readonly TextBox _destination = new() { MaxLength = 1024 };
    private readonly TextBox _fileName = new() { MaxLength = 160 };
    private readonly TextBox _sha256 = new() { MaxLength = 64 };
    private readonly CheckBox _autoPortable = new();
    private readonly TextBox _portableExe = new() { MaxLength = 240 };
    private readonly TextBox _portableTitle = new() { MaxLength = 200 };
    private readonly StackPanel _portableOptions = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox _priority = new() { MinWidth = 180 };
    private readonly TextBox _speed = new() { Text = "0", MaxLength = 8, MinWidth = 160 };
    private readonly Button _applySpeed = new();
    private readonly Button _browse = new();
    private readonly Button _enqueue = new();
    private readonly Button _refresh = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _torrentStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _activeRefresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly StackPanel _jobs = new() { Spacing = 8 };
    private bool _busy;

    public GameDownloadsPanel()
    {
        Spacing = 10;
        AutomationProperties.SetAutomationId(this, "GameDownloads_Panel");
        Children.Add(new TextBlock
        {
            Text = T("Downloads", "Download"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = T(
                "Download files from addresses you choose. PaRuX never searches for unauthorized copies, starts an installer or bypasses a store. Transfers pause for Gaming. A SHA-256 checksum proves file integrity only when you obtained the expected hash independently.",
                "Scarica file dagli indirizzi che scegli. PaRuX non cerca copie non autorizzate, non avvia installer e non aggira gli store. I trasferimenti si sospendono durante il gioco. SHA-256 verifica l'integrità solo rispetto a un hash ottenuto indipendentemente."),
            TextWrapping = TextWrapping.Wrap
        });

        _sourceKind.Header = T("Download source", "Origine download");
        _sourceKind.Items.Add(new ComboBoxItem { Content = T("Direct HTTPS", "HTTPS diretto"), Tag = GameDownloadKinds.Http });
        _sourceKind.Items.Add(new ComboBoxItem { Content = T("Magnet link", "Link magnet"), Tag = GameDownloadKinds.Magnet });
        _sourceKind.Items.Add(new ComboBoxItem { Content = T("Local .torrent file", "File .torrent locale"), Tag = GameDownloadKinds.TorrentFile });
        AutomationProperties.SetAutomationId(_sourceKind, "GameDownloads_Kind");
        _sourceKind.SelectionChanged += (_, _) => UpdateDownloadSourceUi();
        _sourceKind.SelectedIndex = 0;
        Children.Add(_sourceKind);

        _url.Header = T("Download URL (HTTPS; local HTTP only for testing)", "URL download (HTTPS; HTTP locale solo per test)");
        _url.PlaceholderText = "https://";
        AutomationProperties.SetAutomationId(_url, "GameDownloads_Url");
        Children.Add(_url);
        _chooseTorrent.Content = T("Choose .torrent file", "Seleziona file .torrent");
        AutomationProperties.SetAutomationId(_chooseTorrent, "GameDownloads_TorrentBrowse");
        _chooseTorrent.Visibility = Visibility.Collapsed;
        _chooseTorrent.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".torrent");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) _url.Text = file.Path;
            }
            catch (Exception) { _status.Text = T("The .torrent picker is unavailable.", "Selezione file .torrent non disponibile."); }
            finally { _busy = false; }
        };
        Children.Add(_chooseTorrent);

        _destination.Header = T("Destination folder", "Cartella di destinazione");
        AutomationProperties.SetAutomationId(_destination, "GameDownloads_Destination");
        _browse.Content = T("Choose folder", "Scegli cartella");
        AutomationProperties.SetAutomationId(_browse, "GameDownloads_Browse");
        _browse.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null) _destination.Text = folder.Path;
            }
            catch (Exception)
            {
                _status.Text = T("Folder selection is unavailable.", "Selezione cartella non disponibile.");
            }
            finally { _busy = false; }
        };
        var destinationRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        destinationRow.Children.Add(_destination);
        destinationRow.Children.Add(_browse);
        Children.Add(destinationRow);

        _fileName.Header = T("File name (optional)", "Nome file (facoltativo)");
        AutomationProperties.SetAutomationId(_fileName, "GameDownloads_FileName");
        Children.Add(_fileName);
        _sha256.Header = T("Independent SHA-256 (optional, recommended)", "SHA-256 indipendente (facoltativo, consigliato)");
        AutomationProperties.SetAutomationId(_sha256, "GameDownloads_SHA256");
        Children.Add(_sha256);

        // Owner authorizes at enqueue time; never infer an auto-install request
        // from archive content, the source site, or its torrent metadata.
        _autoPortable.Content = T("After download: unpack verified portable ZIP and add game to library (do not launch)",
            "Dopo il download: estrai ZIP portatile verificato e aggiungi il gioco alla libreria (non avviare)");
        AutomationProperties.SetAutomationId(_autoPortable, "GameDownloads_AutoPortable");
        _autoPortable.Checked += (_, _) => _portableOptions.Visibility = Visibility.Visible;
        _autoPortable.Unchecked += (_, _) => _portableOptions.Visibility = Visibility.Collapsed;
        Children.Add(_autoPortable);
        _portableTitle.Header = T("Game title for your library", "Titolo del gioco per la libreria");
        _portableExe.Header = T("Executable inside ZIP (relative path, e.g. bin/Game.exe)",
            "Eseguibile nello ZIP (percorso relativo, es. bin/Game.exe)");
        AutomationProperties.SetAutomationId(_portableTitle, "GameDownloads_PortableTitle");
        AutomationProperties.SetAutomationId(_portableExe, "GameDownloads_PortableExe");
        _portableOptions.Children.Add(_portableTitle);
        _portableOptions.Children.Add(_portableExe);
        _portableOptions.Children.Add(new TextBlock {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Requires an independently obtained SHA-256 and ZIP output filename. Only extracts and registers a portable game. Setup.exe/MSI/scripts will never run automatically.",
                     "Richiede uno SHA-256 ottenuto indipendentemente e nome del file ZIP. Estrae e registra solo un gioco portatile. Setup.exe/MSI/script non vengono mai avviati automaticamente.")
        });
        Children.Add(_portableOptions);

        _priority.Header = T("Queue priority", "Priorità in coda");
        _priority.Items.Add(new ComboBoxItem { Content = T("Low", "Bassa"), Tag = -5 });
        _priority.Items.Add(new ComboBoxItem { Content = T("Normal", "Normale"), Tag = 0 });
        _priority.Items.Add(new ComboBoxItem { Content = T("High", "Alta"), Tag = 5 });
        _priority.SelectedIndex = 1;
        AutomationProperties.SetAutomationId(_priority, "GameDownloads_Priority");
        Children.Add(_priority);

        _speed.Header = T("Maximum speed in KiB/s (0 = unlimited)", "Velocità massima in KiB/s (0 = illimitata)");
        AutomationProperties.SetAutomationId(_speed, "GameDownloads_SpeedKiB");
        _applySpeed.Content = T("Apply speed limit", "Applica limite velocità");
        AutomationProperties.SetAutomationId(_applySpeed, "GameDownloads_ApplySpeed");
        _applySpeed.Click += async (_, _) => await ApplySpeedAsync();
        var speedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        speedRow.Children.Add(_speed);
        speedRow.Children.Add(_applySpeed);
        Children.Add(speedRow);

        _enqueue.Content = T("Start download", "Avvia download");
        AutomationProperties.SetAutomationId(_enqueue, "GameDownloads_Start");
        _enqueue.Click += async (_, _) => await EnqueueAsync();
        _refresh.Content = T("Refresh transfers", "Aggiorna trasferimenti");
        AutomationProperties.SetAutomationId(_refresh, "GameDownloads_Refresh");
        _refresh.Click += async (_, _) => await RefreshAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_enqueue);
        actions.Children.Add(_refresh);
        Children.Add(actions);
        AutomationProperties.SetAutomationId(_status, "GameDownloads_Status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.Text = T("No download starts without your confirmation.", "Nessun download parte senza una tua conferma.");
        AutomationProperties.SetAutomationId(_torrentStatus, "GameDownloads_TorrentEngine_Status");
        _torrentStatus.Text = T("BitTorrent engine: checking…", "Motore BitTorrent: verifica…");
        Children.Add(_torrentStatus);
        Children.Add(_status);
        Children.Add(_jobs);
        _activeRefresh.Tick += async (_, _) =>
        {
            // The timer is UI-only, in effect solely while active transfers are
            // visible. Never poll the Core or compete with an active game.
            if (!_busy && Visibility == Visibility.Visible && !GamingManager.Current.IsActive)
                await RefreshAsync();
        };
        Loaded += async (_, _) =>
        {
            await RefreshTorrentStatusAsync();
            await RefreshAsync();
        };
        Unloaded += (_, _) => _activeRefresh.Stop();
    }

    private string SelectedKind =>
        (_sourceKind.SelectedItem as ComboBoxItem)?.Tag as string ?? GameDownloadKinds.Http;

    private void UpdateDownloadSourceUi()
    {
        string kind = SelectedKind;
        _chooseTorrent.Visibility = kind == GameDownloadKinds.TorrentFile
            ? Visibility.Visible : Visibility.Collapsed;
        _url.Header = kind switch
        {
            GameDownloadKinds.Magnet => T("Paste magnet link", "Incolla il link magnet"),
            GameDownloadKinds.TorrentFile => T("Selected local .torrent path", "Percorso file .torrent locale"),
            _ => T("Download URL (HTTPS; local HTTP only for testing)", "URL download (HTTPS; HTTP locale solo per test)")
        };
        _url.PlaceholderText = kind switch
        {
            GameDownloadKinds.Magnet => "magnet:?xt=urn:btih:",
            GameDownloadKinds.TorrentFile => T("Use Choose .torrent file", "Usa Seleziona file .torrent"),
            _ => "https://"
        };
    }

    private async Task EnqueueAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        string address = (_url.Text ?? "").Trim();
        string destination = (_destination.Text ?? "").Trim();
        string checksum = (_sha256.Text ?? "").Trim();
        string kind = SelectedKind;
        Uri? source = null;
        bool validSource = kind switch
        {
            GameDownloadKinds.Magnet =>
                address.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase)
                && address.Length <= 2048,
            GameDownloadKinds.TorrentFile =>
                Path.IsPathFullyQualified(address) && !address.StartsWith(@"\\", StringComparison.Ordinal)
                && string.Equals(Path.GetExtension(address), ".torrent", StringComparison.OrdinalIgnoreCase),
            _ => Uri.TryCreate(address, UriKind.Absolute, out source)
                 && (source.Scheme == Uri.UriSchemeHttps ||
                     (source.Scheme == Uri.UriSchemeHttp && source.IsLoopback))
        };
        bool autoPortable = _autoPortable.IsChecked == true;
        string requestedName = (_fileName.Text ?? "").Trim();
        string portableExecutable = (_portableExe.Text ?? "").Trim();
        string portableTitle = (_portableTitle.Text ?? "").Trim();
        bool invalidPortable = autoPortable &&
            (checksum.Length != 64 ||
             requestedName.Length == 0 ||
             !string.Equals(Path.GetExtension(requestedName), ".zip", StringComparison.OrdinalIgnoreCase) ||
             portableTitle.Length == 0 ||
             portableExecutable.Length == 0 || portableExecutable.Length > 240 ||
             Path.IsPathRooted(portableExecutable) ||
             portableExecutable.Contains("..", StringComparison.Ordinal) ||
             !string.Equals(Path.GetExtension(portableExecutable), ".exe", StringComparison.OrdinalIgnoreCase));
        if (!validSource || string.IsNullOrWhiteSpace(destination) || invalidPortable ||
            (checksum.Length != 0 && (checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))))
        {
            _status.Text = T("Check address, destination and SHA-256. Auto-install additionally requires a ZIP filename, independent SHA-256, game title and relative .exe path.",
                "Controlla indirizzo, destinazione e SHA-256. Per l'installazione automatica servono inoltre nome ZIP, SHA-256 indipendente, titolo e percorso .exe relativo.");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Confirm download", "Conferma download"),
            Content = (kind == GameDownloadKinds.Http
                ? T("Download from ", "Scaricare da ") + source!.Host + "? " +
                    T("The file is not automatically certified safe or licensed.",
                      "Il file non è automaticamente certificato sicuro o autorizzato.")
                : T(
                    "Start the selected BitTorrent transfer? Peers and trackers can see your IP address and exchange pieces while the download runs. Download only content you are entitled to share.",
                    "Avviare il trasferimento BitTorrent? Peer e tracker possono vedere il tuo indirizzo IP e scambiare parti durante il download. Scarica solo contenuti di cui hai i diritti."))
                + (autoPortable
                    ? T(" AFTER completion, PaRuX will check the independently supplied SHA-256, unpack the ZIP into its own managed game folder and register the specified executable. No setup, command or game will launch automatically. Continue?",
                        " DOPO il completamento, PaRuX verificherà lo SHA-256 indipendente, estrarrà lo ZIP nella cartella giochi gestita e registrerà l'eseguibile indicato. Non partiranno setup, comandi o giochi automaticamente. Continuare?")
                    : T(" Nothing will be unpacked or executed automatically.",
                        " Nulla verrà estratto o eseguito automaticamente.")),
            PrimaryButtonText = T("Start", "Avvia"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        _busy = true;
        _enqueue.IsEnabled = false;
        try
        {
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var request = new GameDownloadRequest(
                address, destination,
                string.IsNullOrWhiteSpace(_fileName.Text) ? null : _fileName.Text.Trim(),
                checksum.Length == 0 ? null : checksum,
                (_priority.SelectedItem as ComboBoxItem)?.Tag is int selectedPriority ? selectedPriority : 0,
                kind, autoPortable, autoPortable ? portableExecutable : null,
                autoPortable ? portableTitle : null);
            var result = await GamingOrchestratorClient.Current.EnqueueGameDownloadAsync(request);
            _status.Text = result.Succeeded
                ? T("Download queued. Refresh to see its progress.", "Download in coda. Aggiorna per vedere l'avanzamento.")
                : T("Download refused. Check the address, folder, available space or Gaming status.",
                    "Download rifiutato. Verifica indirizzo, cartella, spazio disponibile o stato Gaming.");
            if (result.Succeeded && autoPortable)
            {
                _autoPortable.IsChecked = false;
                _portableExe.Text = string.Empty;
                _portableTitle.Text = string.Empty;
            }
            if (result.Succeeded && kind != GameDownloadKinds.Http)
                _url.Text = string.Empty; // do not keep magnet tracker addresses in the editable UI
        }
        catch (Exception)
        {
            _status.Text = T("The download could not be started.", "Impossibile avviare il download.");
        }
        finally { _busy = false; _enqueue.IsEnabled = true; }
        await RefreshAsync();
    }

    private async Task RefreshTorrentStatusAsync()
    {
        GamingCallResult<GameTorrentEngineStatusDto> result =
            await GamingOrchestratorClient.Current.GetGameTorrentEngineAsync();
        _torrentStatus.Text = result.Succeeded && result.Value is { } value
            ? (value.EmbeddedAvailable
                ? (value.NewJobTransport == "external"
                    ? T("BitTorrent: optional external client selected. Single-file downloads only.",
                        "BitTorrent: client esterno facoltativo selezionato. Solo torrent a file singolo.")
                    : T("BitTorrent: integrated PaRuX engine. No external app needed. Single-file downloads only.",
                        "BitTorrent: motore PaRuX integrato. Nessuna app esterna necessaria. Solo torrent a file singolo."))
                : T("BitTorrent is disabled in Corporate Lite.", "BitTorrent disattivato in Corporate Lite."))
            : T("BitTorrent engine status is unavailable.", "Stato del motore BitTorrent non disponibile.");
    }

    private async Task ApplySpeedAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        if (!long.TryParse(_speed.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long kibPerSecond)
            || kibPerSecond < 0 || kibPerSecond > 1_000_000)
        {
            _status.Text = T("Enter a speed between 0 and 1,000,000 KiB/s.",
                             "Inserisci una velocità tra 0 e 1.000.000 KiB/s.");
            return;
        }
        _busy = true;
        _applySpeed.IsEnabled = false;
        try
        {
            var result = await GamingOrchestratorClient.Current.SetGameDownloadLimitAsync(kibPerSecond * 1024);
            _status.Text = result.Succeeded
                ? T("Speed limit updated. Zero means unlimited.", "Limite aggiornato. Zero significa illimitato.")
                : T("Could not update the speed limit.", "Impossibile aggiornare il limite.");
        }
        catch (Exception)
        {
            _status.Text = T("Could not update the speed limit.", "Impossibile aggiornare il limite.");
        }
        finally { _busy = false; _applySpeed.IsEnabled = true; }
    }

    private async Task RefreshAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        _busy = true;
        _refresh.IsEnabled = false;
        try
        {
            var result = await GamingOrchestratorClient.Current.ListGameDownloadsAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _status.Text = T("Download history is unavailable.", "Cronologia download non disponibile.");
                return;
            }
            _jobs.Children.Clear();
            List<GameDownloadDto> jobs = result.Value;
            foreach (GameDownloadDto job in jobs.Take(50))
                _jobs.Children.Add(BuildJob(job));
            if (jobs.Count == 0)
                _status.Text = T("No transfers yet.", "Nessun trasferimento.");
            else
                _status.Text = T($"{jobs.Count} transfers. Progress refreshes while downloads run.",
                                 $"{jobs.Count} trasferimenti. L'avanzamento si aggiorna durante il download.");
            // Pause all UI polling as soon as work finishes or Gaming begins.
            if (Visibility == Visibility.Visible && !GamingManager.Current.IsActive &&
                jobs.Any(x => x.State is GameDownloadStates.Running or GameDownloadStates.Queued ||
                              x.PostInstallState == GamePostInstallStates.Installing))
                _activeRefresh.Start();
            else
                _activeRefresh.Stop();
        }
        catch (Exception)
        {
            _status.Text = T("Could not read transfers.", "Impossibile leggere i trasferimenti.");
        }
        finally { _busy = false; _refresh.IsEnabled = true; }
    }

    private UIElement BuildJob(GameDownloadDto job)
    {
        var card = new StackPanel { Spacing = 6, Padding = new Thickness(10) };
        card.Children.Add(new TextBlock
        {
            Text = job.FileName + " · " + StateLabel(job.State),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        double mbDone = job.BytesDone / 1048576.0;
        string progress = job.TotalBytes is { } total && total > 0
            ? $"{mbDone:F1} / {total / 1048576.0:F1} MiB"
            : $"{mbDone:F1} MiB";
        card.Children.Add(new TextBlock
        {
            Text = job.Source + " · " + progress +
                (job.IntegrityChecked ? T(" · SHA-256 verified", " · SHA-256 verificato") : ""),
            TextWrapping = TextWrapping.Wrap
        });
        if (job.TotalBytes is > 0)
        {
            var meter = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(job.BytesDone * 100.0 / job.TotalBytes.Value, 0, 100),
                Height = 7
            };
            AutomationProperties.SetAutomationId(meter, "GameDownloads_Progress_" + job.Id);
            card.Children.Add(meter);
        }
        if (!string.IsNullOrEmpty(job.Reason))
            card.Children.Add(new TextBlock { Text = job.Reason, TextWrapping = TextWrapping.Wrap });
        if (job.PostInstallState != GamePostInstallStates.None)
        {
            string stage = job.PostInstallState switch
            {
                GamePostInstallStates.Pending => T("Portable install waiting for verified download", "Installazione portatile in attesa del download verificato"),
                GamePostInstallStates.Installing => T("Extracting and adding to library…", "Estrazione e aggiunta alla libreria…"),
                GamePostInstallStates.Installed => T("Portable game added to library (not launched)", "Gioco portatile aggiunto alla libreria (non avviato)"),
                GamePostInstallStates.Interrupted => T("Portable installation interrupted; archive kept", "Installazione portatile interrotta; archivio conservato"),
                _ => T("Portable installation refused; archive kept", "Installazione portatile rifiutata; archivio conservato")
            };
            var installStatus = new TextBlock { Text = stage, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(installStatus, "GameDownloads_PostInstall_" + job.Id);
            card.Children.Add(installStatus);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (job.State == GameDownloadStates.Completed)
        {
            var inspect = new Button { Content = T("Inspect downloaded package", "Verifica file scaricato") };
            AutomationProperties.SetAutomationId(inspect, "GameDownloads_Inspect_" + job.Id);
            inspect.Click += async (_, _) => await InspectCompletedAsync(job, inspect);
            actions.Children.Add(inspect);
            if (job.IntegrityChecked)
            {
                var stage = new Button
                {
                    Content = T("Stage checked copy", "Prepara copia verificata")
                };
                AutomationProperties.SetAutomationId(stage, "GameDownloads_Stage_" + job.Id);
                stage.Click += async (_, _) => await StageCompletedAsync(job, stage);
                actions.Children.Add(stage);
            }
        }
        if (job.State == GameDownloadStates.Completed &&
            job.PostInstallState is GamePostInstallStates.Failed or GamePostInstallStates.Interrupted)
        {
            var retry = new Button { Content = T("Retry verified ZIP installation", "Riprova installazione ZIP verificato") };
            AutomationProperties.SetAutomationId(retry, "GameDownloads_RetryPortable_" + job.Id);
            retry.Click += async (_, _) =>
            {
                if (_busy || Visibility != Visibility.Visible) return;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = T("Retry extracting the verified portable ZIP?", "Riprovare l'estrazione dello ZIP portatile verificato?"),
                    Content = T(
                        "PaRuX will check the same independently supplied SHA-256 again, extract the managed ZIP and register the game. It will not run any programs.",
                        "PaRuX ricontrollerà lo stesso SHA-256 indipendente, estrarrà lo ZIP gestito e registrerà il gioco. Non avvierà alcun programma."),
                    PrimaryButtonText = T("Retry", "Riprova"),
                    CloseButtonText = T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                _busy = true;
                retry.IsEnabled = false;
                try
                {
                    var result = await GamingOrchestratorClient.Current.RetryPortableInstallAsync(job.Id);
                    _status.Text = result.Succeeded && result.Value is { } updated
                        ? updated.PostInstallState == GamePostInstallStates.Installed
                            ? T("Game added to library, not launched.", "Gioco aggiunto alla libreria, non avviato.")
                            : T("Portable deployment did not complete; ZIP retained.",
                                "Installazione portatile non completata; ZIP conservato.")
                        : T("Retry unavailable. The game may be running or this download belongs to a previous session.",
                            "Riprova non disponibile: il gioco potrebbe essere avviato o il download appartiene a una sessione precedente.");
                }
                catch (Exception)
                {
                    _status.Text = T("Could not retry ZIP installation.", "Impossibile riprovare l'installazione ZIP.");
                }
                finally { _busy = false; retry.IsEnabled = true; }
                await RefreshAsync();
            };
            actions.Children.Add(retry);
        }
        if (job.State is GameDownloadStates.Queued or GameDownloadStates.Running)
            AddAction(T("Pause", "Pausa"), "Pause", () => GamingOrchestratorClient.Current.PauseGameDownloadAsync(job.Id));
        if (job.State == GameDownloadStates.Paused)
            AddAction(T("Resume", "Riprendi"), "Resume", () => GamingOrchestratorClient.Current.ResumeGameDownloadAsync(job.Id));
        AddAction(GameDownloadStates.IsTerminal(job.State) ? T("Remove history", "Elimina cronologia") : T("Cancel", "Annulla"),
            "Remove", async () =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = T("Confirm removal", "Conferma rimozione"),
                    Content = job.PostInstallState == GamePostInstallStates.Installing
                        ? T("Stop the portable installation and remove this history entry? The downloaded ZIP is kept; incomplete managed extraction is rolled back.",
                            "Interrompere l'installazione portatile e rimuovere questa voce? Lo ZIP scaricato rimane; l'estrazione incompleta viene annullata.")
                        : GameDownloadStates.IsTerminal(job.State)
                        ? T("Remove this transfer from the history? Downloaded files are not deleted.",
                            "Rimuovere il trasferimento dalla cronologia? I file scaricati non saranno eliminati.")
                        : T("Cancel this transfer and remove its incomplete file?",
                            "Annullare il trasferimento e rimuovere il file incompleto?"),
                    PrimaryButtonText = T("Confirm", "Conferma"),
                    CloseButtonText = T("Back", "Indietro"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return GamingCallResult<object>.Ok(null);
                return await GamingOrchestratorClient.Current.RemoveGameDownloadAsync(job.Id);
            });
        card.Children.Add(actions);
        return card;

        void AddAction(string title, string suffix, Func<Task<GamingCallResult<object>>> call)
        {
            var button = new Button { Content = title };
            AutomationProperties.SetAutomationId(button, "GameDownloads_" + suffix + "_" + job.Id);
            button.Click += async (_, _) =>
            {
                if (_busy || Visibility != Visibility.Visible) return;
                _busy = true;
                button.IsEnabled = false;
                try
                {
                    var response = await call();
                    if (!response.Succeeded)
                        _status.Text = T("Download operation was refused.", "Operazione download rifiutata.");
                }
                catch (Exception)
                {
                    _status.Text = T("Download operation could not finish.", "Operazione download non completata.");
                }
                finally { _busy = false; button.IsEnabled = true; }
                await RefreshAsync();
            };
            actions.Children.Add(button);
        }
    }

    private async Task StageCompletedAsync(GameDownloadDto job, Button button)
    {
        if (_busy || Visibility != Visibility.Visible || !job.IntegrityChecked) return;
        _busy = true;
        button.IsEnabled = false;
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Prepare a separate managed copy?", "Preparare una copia gestita separata?"),
                Content = T(
                    "PaRuX will recheck this downloaded file against the SHA-256 you supplied at download creation and copy it into managed staging. It will not install, unpack or run anything. PaRuX cannot verify whether your checksum came from the publisher.",
                    "PaRuX verificherà nuovamente il file scaricato rispetto allo SHA-256 inserito all'avvio del download e ne creerà una copia nell'area gestita. Non installerà, estrarrà né eseguirà nulla. PaRuX non può verificare che l'hash provenga dal produttore."),
                PrimaryButtonText = T("Verify and copy", "Verifica e copia"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            _status.Text = T("Copying to verified staging…", "Preparazione della copia verificata…");
            var result = await GamingOrchestratorClient.Current.StageCompletedGameDownloadAsync(job.Id);
            _status.Text = result.Succeeded && result.Value is { } staged
                ? T($"Managed copy prepared: {staged.FileName}. Nothing was installed or executed.",
                    $"Copia gestita preparata: {staged.FileName}. Nessuna installazione o esecuzione effettuata.")
                : T("Staging was refused. Check the original digest, file integrity and free space.",
                    "Preparazione rifiutata. Controlla l'hash originario, l'integrità e lo spazio disponibile.");
        }
        catch (Exception)
        {
            _status.Text = T("The checked copy could not be prepared.",
                             "Impossibile preparare la copia verificata.");
        }
        finally { _busy = false; button.IsEnabled = true; }
    }

    private async Task InspectCompletedAsync(GameDownloadDto job, Button button)
    {
        if (_busy || Visibility != Visibility.Visible) return;
        _busy = true;
        button.IsEnabled = false;
        try
        {
            _status.Text = T("Inspecting file bytes without executing the package…",
                             "Verifica dei byte senza eseguire il pacchetto…");
            var result = await GamingOrchestratorClient.Current.InspectCompletedGameDownloadAsync(job.Id);
            if (result.Succeeded && result.Value is { } inspected)
                _status.Text = T(
                    $"{inspected.FileName}: SHA-256 {inspected.Sha256}. The file matches the original download, but is not certified safe, licensed or authentic. Compare the hash with an independent publisher source.",
                    $"{inspected.FileName}: SHA-256 {inspected.Sha256}. Il file corrisponde al download originale, ma non è certificato sicuro, autorizzato o autentico. Confronta l'hash con una fonte indipendente del produttore.");
            else
                _status.Text = T(
                    "Inspection unavailable. Only supported local package formats completed during this PaRuX session can be inspected here. You can still choose a local file in the installer inspection panel.",
                    "Verifica non disponibile. Qui si possono esaminare solo i formati supportati completati nella sessione PaRuX corrente. Puoi comunque selezionare il file nel pannello di verifica installer.");
        }
        catch (Exception)
        {
            _status.Text = T("File inspection could not complete.", "Impossibile completare la verifica del file.");
        }
        finally { _busy = false; button.IsEnabled = true; }
    }

    private static string StateLabel(string state) => state switch
    {
        GameDownloadStates.Queued => T("Queued", "In coda"),
        GameDownloadStates.Running => T("Downloading", "Download in corso"),
        GameDownloadStates.Paused => T("Paused", "In pausa"),
        GameDownloadStates.Completed => T("Completed", "Completato"),
        GameDownloadStates.Failed => T("Failed", "Non riuscito"),
        GameDownloadStates.Cancelled => T("Cancelled", "Annullato"),
        _ => T("Unknown", "Sconosciuto")
    };
}
