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

    public GameSaveBackupPanel()
    {
        Spacing = 10;
        AutomationProperties.SetAutomationId(this, "GameSaves_Panel");
        Children.Add(new TextBlock
        {
            Text = T("Save game backups", "Backup salvataggi"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = T("Choose the save folder yourself. A local copy is archived with SHA-256 per file. This does not restore, replace, modify or automatically detect saved games. Disabled during Gaming.",
                     "Scegli manualmente la cartella dei salvataggi. Una copia locale viene archiviata con SHA-256 per file. Non ripristina, sostituisce o modifica i salvataggi, né li rileva automaticamente. Disattivato durante il gioco."),
            TextWrapping = TextWrapping.Wrap
        });

        _source.Header = T("Save folder", "Cartella salvataggi");
        AutomationProperties.SetAutomationId(_source, "GameSaves_Source");
        Children.Add(_source);
        var browse = new Button { Content = T("Choose save folder", "Scegli cartella salvataggi") };
        AutomationProperties.SetAutomationId(browse, "GameSaves_Browse");
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
        _label.Text = T("My save", "Il mio salvataggio");
        AutomationProperties.SetAutomationId(_label, "GameSaves_Label");
        Children.Add(_label);

        _create.Content = T("Create local backup", "Crea backup locale");
        AutomationProperties.SetAutomationId(_create, "GameSaves_Create");
        _create.Click += async (_, _) => await CreateAsync();
        _refresh.Content = T("Show backups", "Mostra backup");
        AutomationProperties.SetAutomationId(_refresh, "GameSaves_Refresh");
        _refresh.Click += async (_, _) => await RefreshAsync();
        _cancel.Content = T("Cancel current operation", "Annulla operazione in corso");
        AutomationProperties.SetAutomationId(_cancel, "GameSaves_Cancel");
        _cancel.IsEnabled = false;
        _cancel.Click += (_, _) => _operation?.Cancel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_create);
        actions.Children.Add(_refresh);
        actions.Children.Add(_cancel);
        Children.Add(actions);
        AutomationProperties.SetAutomationId(_status, "GameSaves_Status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        Children.Add(_archives);
        Unloaded += (_, _) => _operation?.Cancel();
    }

    private async Task CreateAsync()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        string source = (_source.Text ?? "").Trim();
        string label = (_label.Text ?? "").Trim();
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
            foreach (var archive in result.Value.Take(50))
            {
                var row = new StackPanel { Spacing = 5, Padding = new Thickness(8) };
                row.Children.Add(new TextBlock
                {
                    Text = archive.Label + $" · {archive.Files} " + T("files", "file") +
                           $" · {archive.CreatedUtc.ToLocalTime():g}",
                    TextWrapping = TextWrapping.Wrap
                });
                var verify = new Button { Content = T("Verify archive", "Verifica archivio") };
                AutomationProperties.SetAutomationId(verify, "GameSaves_Verify_" + archive.Id);
                verify.Click += async (_, _) => await VerifyAsync(archive.Id);
                row.Children.Add(verify);
                _archives.Children.Add(row);
            }
            _status.Text = result.Value.Count == 0
                ? T("No local backups.", "Nessun backup locale.")
                : T($"{result.Value.Count} local backups.", $"{result.Value.Count} backup locali.");
        }
        catch (Exception) { _status.Text = T("Could not read local backups.", "Impossibile leggere i backup locali."); }
        finally { _busy = false; _refresh.IsEnabled = true; }
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
