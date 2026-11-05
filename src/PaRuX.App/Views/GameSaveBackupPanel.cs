using System;
using System.Globalization;
using System.Linq;
using System.Threading;
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
/// Desktop-only, opt-in backup of an explicitly selected game save directory.
/// No game save discovery, restore, installation, hidden timer, or filesystem mutation
/// outside the PaRuX-owned backup directory.
/// </summary>
internal sealed class GameSaveBackupPanel : StackPanel
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private readonly TextBox _source = new() { MaxLength = 1024 };
    private readonly TextBox _label = new() { MaxLength = 100 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _archives = new() { Spacing = 8 };
    private readonly Button _create = new(), _refresh = new(), _cancel = new();
    private CancellationTokenSource? _operation;
    private bool _busy;
    private readonly string? _gameName;
    private readonly string _suffix;

    private string Id(string baseId) => baseId + _suffix;

    /// <param name="gameName">When set, the panel serves one game: the backup label is fixed to its name and only its backups are listed.</param>
    /// <param name="idSuffix">Appended to every AutomationId so a per-game panel and the shared panel never clash.</param>
    public GameSaveBackupPanel(string? gameName = null, string idSuffix = "")
    {
        _gameName = gameName;
        _suffix = idSuffix;
        Spacing = 10;
        AutomationProperties.SetAutomationId(this, Id("GameSaves_Panel"));
        Children.Add(new TextBlock
        {
            Text = gameName is null ? T("Save game backups", "Backup salvataggi") : T("Save games", "Salvataggi"),
            FontSize = gameName is null ? 20 : 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = T("Choose the save folder yourself (PaRuX does not detect save locations automatically). A local copy is archived with SHA-256 per file. Restore shows a preview first, saves a copy of the current files, never deletes anything and re-checks every file. Disabled during Gaming.",
                     "Scegli tu la cartella dei salvataggi (PaRuX non la rileva automaticamente). Una copia locale viene archiviata con SHA-256 per file. Il ripristino mostra prima un'anteprima, salva una copia dei file attuali, non elimina nulla e ricontrolla ogni file. Disattivato durante il gioco."),
            TextWrapping = TextWrapping.Wrap
        });

        _source.Header = T("Save folder", "Cartella salvataggi");
        AutomationProperties.SetAutomationId(_source, Id("GameSaves_Source"));
        Children.Add(_source);
        var browse = new Button { Content = T("Choose save folder", "Scegli cartella salvataggi") };
        AutomationProperties.SetAutomationId(browse, Id("GameSaves_Browse"));
        browse.Click += async (_, _) =>
        {
            if (_busy) return;
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var choice = await picker.PickSingleFolderAsync();
                if (choice is not null) _source.Text = choice.Path;
            }
            catch (Exception)
            {
                _status.Text = T("Could not open folder selection.", "Impossibile aprire la selezione cartelle.");
            }
        };
        Children.Add(browse);

        _label.Header = T("Backup label", "Etichetta del backup");
        _label.Text = gameName is null ? T("My save", "Il mio salvataggio") : GameSaveHookPresenter.BackupLabel(gameName);
        if (gameName is not null) _label.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_label, Id("GameSaves_Label"));
        Children.Add(_label);

        _create.Content = T("Create local backup", "Crea backup locale");
        AutomationProperties.SetAutomationId(_create, Id("GameSaves_Create"));
        _create.Click += async (_, _) => await CreateAsync();
        _refresh.Content = T("Show backups", "Mostra backup");
        AutomationProperties.SetAutomationId(_refresh, Id("GameSaves_Refresh"));
        _refresh.Click += async (_, _) => await RefreshAsync();
        _cancel.Content = T("Cancel current operation", "Annulla operazione in corso");
        AutomationProperties.SetAutomationId(_cancel, Id("GameSaves_Cancel"));
        _cancel.IsEnabled = false;
        _cancel.Click += (_, _) => _operation?.Cancel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_create);
        actions.Children.Add(_refresh);
        actions.Children.Add(_cancel);
        Children.Add(actions);
        AutomationProperties.SetAutomationId(_status, Id("GameSaves_Status"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        Children.Add(_archives);
        Unloaded += (_, _) => _operation?.Cancel();
    }

    private async Task CreateAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        string source = (_source.Text ?? "").Trim();
        string label = (_gameName is null ? (_label.Text ?? "") : GameSaveHookPresenter.BackupLabel(_gameName)).Trim();
        if (source.Length == 0 || label.Length == 0)
        {
            _status.Text = T("Choose a save folder and enter a label.", "Scegli una cartella e indica un'etichetta.");
            return;
        }
        _busy = true;
        _create.IsEnabled = false;
        _refresh.IsEnabled = false;
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Create a local copy of these save files?", "Creare una copia locale di questi salvataggi?"),
                Content = T("The selected folder will only be read. PaRuX will never overwrite it or launch a game.",
                    "La cartella selezionata sarà solo letta. PaRuX non la sovrascriverà né avvierà il gioco."),
                PrimaryButtonText = T("Create backup", "Crea backup"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            _operation = new CancellationTokenSource();
            _cancel.IsEnabled = true;
            _status.Text = T("Archiving the selected save files…", "Archiviazione dei salvataggi selezionati…");
            var result = await GamingOrchestratorClient.Current.CreateGameSaveBackupAsync(
                new GameSaveBackupRequest(source, label), _operation.Token);
            _status.Text = result.Succeeded && result.Value is { } done
                ? T($"Backup created: {done.Files} files. Use Verify to check the archive.",
                    $"Backup creato: {done.Files} file. Usa Verifica per controllare l'archivio.")
                : T("Backup failed or refused. Original save files were not modified.",
                    "Backup non riuscito o rifiutato. I salvataggi originali non sono stati modificati.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("Backup interrupted; original saves were preserved.",
                "Backup interrotto; salvataggi originali preservati.");
        }
        catch (Exception)
        {
            _status.Text = T("Backup could not complete.", "Impossibile completare il backup.");
        }
        finally
        {
            _cancel.IsEnabled = false;
            _operation?.Dispose();
            _operation = null;
            _busy = false;
            _create.IsEnabled = true;
            _refresh.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        _busy = true;
        _refresh.IsEnabled = false;
        try
        {
            var result = await GamingOrchestratorClient.Current.ListGameSaveBackupsAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _status.Text = T("Could not list local backups.", "Impossibile elencare i backup locali.");
                return;
            }
            _archives.Children.Clear();
            var shown = result.Value.Where(a => _gameName is null || GameSaveHookPresenter.BelongsToGame(a, _gameName)).ToList();
            foreach (var archive in shown.Take(50))
            {
                var row = new StackPanel { Spacing = 5, Padding = new Thickness(8) };
                row.Children.Add(new TextBlock
                {
                    Text = archive.Label + $" · {archive.Files} " + T("files", "file") +
                           $" · {archive.CreatedUtc.ToLocalTime():g}",
                    TextWrapping = TextWrapping.Wrap
                });
                var verify = new Button { Content = T("Verify archive", "Verifica archivio") };
                AutomationProperties.SetAutomationId(verify, Id("GameSaves_Verify_") + archive.Id);
                verify.Click += async (_, _) => await VerifyAsync(archive.Id);
                var restore = new Button { Content = T("Restore...", "Ripristina...") };
                AutomationProperties.SetAutomationId(restore, Id("GameSaves_Restore_") + archive.Id);
                AutomationProperties.SetName(restore, T("Restore backup ", "Ripristina il backup ") + archive.Label);
                restore.Click += async (_, _) => await RestoreAsync(archive.Id);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(verify);
                buttons.Children.Add(restore);
                row.Children.Add(buttons);
                _archives.Children.Add(row);
            }
            _status.Text = shown.Count == 0
                ? T("No local backups.", "Nessun backup locale.")
                : T($"{shown.Count} local backups.", $"{shown.Count} backup locali.");
        }
        catch (Exception) { _status.Text = T("Could not read local backups.", "Impossibile leggere i backup locali."); }
        finally { _busy = false; _refresh.IsEnabled = true; }
    }

    /// <summary>
    /// Dry run first (nothing written), then an explicit confirmation that states exactly what will happen, then the restore.
    /// The Core makes the automatic pre-restore copy, refuses path escapes/links and re-verifies every file; any refusal is
    /// shown in meaning instead of a generic failure.
    /// </summary>
    private async Task RestoreAsync(string id)
    {
        if (_busy || Visibility != Visibility.Visible) return;
        string target = (_source.Text ?? "").Trim();
        if (target.Length == 0)
        {
            _status.Text = T("Choose the folder to restore into first (the save folder field above).",
                "Scegli prima la cartella in cui ripristinare (il campo cartella salvataggi qui sopra).");
            return;
        }
        _busy = true;
        _create.IsEnabled = false;
        _refresh.IsEnabled = false;
        _operation = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        try
        {
            _status.Text = T("Checking the backup and what a restore would change...", "Controllo del backup e delle modifiche del ripristino...");
            var plan = await GamingOrchestratorClient.Current.PlanGameSaveRestoreAsync(id, target, _operation.Token);
            if (!plan.Succeeded || plan.Value is null)
            {
                _status.Text = T("The restore preview could not be completed: ", "Anteprima del ripristino non riuscita: ") + (plan.Error ?? "");
                return;
            }
            if (!plan.Value.Ok)
            {
                _status.Text = T("Restore refused, nothing was changed: ", "Ripristino rifiutato, nulla è stato modificato: ") + plan.Value.Message;
                return;
            }
            var (create, overwrite, _, _) = GameSaveHookPresenter.Summarize(plan.Value);
            if (create + overwrite == 0)
            {
                _status.Text = T("The folder already matches this backup. Nothing to restore.", "La cartella coincide già con questo backup. Niente da ripristinare.");
                return;
            }
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Restore these save files?", "Ripristinare questi salvataggi?"),
                Content = new TextBlock { Text = GameSaveHookPresenter.ConfirmationText(plan.Value, Italian), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = T("Restore", "Ripristina"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                _status.Text = T("Restore cancelled. Nothing was changed.", "Ripristino annullato. Nulla è stato modificato.");
                return;
            }
            _status.Text = T("Restoring and verifying...", "Ripristino e verifica in corso...");
            var done = await GamingOrchestratorClient.Current.RestoreGameSaveAsync(id, target, _operation.Token);
            _status.Text = done.Succeeded && done.Value is { } result
                ? GameSaveHookPresenter.RestoreOutcome(result, Italian)
                : T("The restore could not be completed: ", "Il ripristino non è stato completato: ") + (done.Error ?? "");
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("Restore interrupted. The pre-restore copy, if any, is among the backups.",
                "Ripristino interrotto. L'eventuale copia preventiva è tra i backup.");
        }
        catch (Exception) { _status.Text = T("The restore did not complete.", "Il ripristino non è stato completato."); }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            _cancel.IsEnabled = false;
            _busy = false;
            _create.IsEnabled = true;
            _refresh.IsEnabled = true;
        }
    }

    private async Task VerifyAsync(string id)
    {
        if (_busy || Visibility != Visibility.Visible) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _create.IsEnabled = false;
        _operation = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        try
        {
            _status.Text = T("Verifying the archive and file hashes…", "Verifica dell'archivio e degli hash dei file…");
            var result = await GamingOrchestratorClient.Current.VerifyGameSaveBackupAsync(id, _operation.Token);
            _status.Text = result.Succeeded && result.Value is { Valid: true } verified
                ? T($"Verified {verified.VerifiedFiles} files successfully.", $"Verificati correttamente {verified.VerifiedFiles} file.")
                : T("Backup verification failed. Do not rely on this copy.",
                    "Verifica del backup non riuscita. Non considerare affidabile questa copia.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("Verification cancelled.", "Verifica annullata.");
        }
        catch (Exception) { _status.Text = T("Verification did not complete.", "Verifica non completata."); }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            _cancel.IsEnabled = false;
            _busy = false;
            _create.IsEnabled = true;
            _refresh.IsEnabled = true;
        }
    }
}
