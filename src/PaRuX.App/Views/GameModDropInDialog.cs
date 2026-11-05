using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

/// <summary>
/// Explicit management of PaRuX-created drop-in ZIP mods for games with a
/// verified ZIP-compatible loader. The user selects an existing mod directory,
/// separately attests compatibility and the game being closed; no files are
/// silently touched. Removal is SHA-checked and never overwrites user changes.
/// </summary>
internal static class GameModDropInDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);
    private static GamingLibraryClient Client => GamingLibraryClient.Current;

    public static async Task OfferInstallAsync(XamlRoot root, GameAssetModExportDto bundle)
    {
        var proceed = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Optional: install a compatible ZIP mod", "Opzionale: installa ZIP mod compatibile"),
            Content = new TextBlock
            {
                Text = T("The exported file is not automatically recognized by all games. Install it only if you verified that THIS game's mod loader directly accepts ZIP files in an existing mods/resourcepacks/addons folder. PaRuX will add a new file and keep a SHA-checked removal receipt; it will never replace an existing file.",
                    "Il file esportato non è riconosciuto da tutti i giochi. Installalo solo se hai verificato che il mod loader di QUESTO gioco supporta ZIP autonomi in una cartella mods/resourcepacks/addons già esistente. PaRuX aggiungerà un file nuovo e conserverà una ricevuta per rimuoverlo con controllo SHA; non sostituirà file esistenti."),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = T("Select compatible mod folder", "Scegli cartella mod compatibile"),
            CloseButtonText = T("Keep ZIP only", "Conserva solo ZIP"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await proceed.ShowAsync() != ContentDialogResult.Primary) return;

        string? folder = null;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            folder = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception)
        {
            await MessageAsync(root, T("Folder unavailable", "Cartella non disponibile"),
                T("The folder picker could not open. The exported ZIP remains unchanged.",
                    "Impossibile aprire la selezione cartella. Lo ZIP esportato resta invariato."));
            return;
        }
        if (string.IsNullOrWhiteSpace(folder)) return;
        var gameExe = new TextBox
        {
            Header = T("Actual game executable (not the launcher)", "Eseguibile effettivo del gioco (non il launcher)"),
            IsReadOnly = true,
            PlaceholderText = T("Choose the installed game's .exe", "Seleziona il file .exe del gioco installato")
        };
        AutomationProperties.SetAutomationId(gameExe, "GameAssetStudio_DropIn_GameExe");
        var chooseGameExe = new Button
        {
            Content = T("Choose game executable", "Scegli eseguibile gioco"),
            MinHeight = 44
        };
        AutomationProperties.SetAutomationId(chooseGameExe, "GameAssetStudio_DropIn_ChooseExe");
        chooseGameExe.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) gameExe.Text = file.Path;
            }
            catch (Exception) { gameExe.Text = string.Empty; }
        };
        var compatible = new CheckBox
        {
            Content = T("I verified this game's loader accepts this EXACT ZIP format in this folder",
                "Ho verificato che il mod loader di questo gioco accetta QUESTO formato ZIP in questa cartella"),
            IsChecked = false
        };
        var closed = new CheckBox
        {
            Content = T("The game is offline and completely closed (not merely paused)",
                "Il gioco è offline e completamente chiuso (non soltanto in pausa)"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(compatible, "GameAssetStudio_DropIn_Compatible");
        AutomationProperties.SetAutomationId(closed, "GameAssetStudio_DropIn_Closed");
        var content = new StackPanel { Spacing = 12, MaxWidth = 540 };
        content.Children.Add(new TextBlock
        {
            Text = T("Target folder: ", "Cartella destinazione: ") + folder,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = T("PaRuX checks the selected game's running process before modifying this folder. Choose the actual game EXE; other child processes cannot be verified automatically.",
                "PaRuX controlla che il processo del gioco selezionato sia chiuso prima di modificare la cartella. Seleziona l'EXE effettivo del gioco: i processi ausiliari non sono verificabili automaticamente."),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(gameExe);
        content.Children.Add(chooseGameExe);
        content.Children.Add(compatible);
        content.Children.Add(closed);
        var confirm = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Confirm opt-in ZIP mod installation", "Conferma installazione ZIP mod opzionale"),
            Content = new ScrollViewer { Content = content, MaxHeight = 350 },
            PrimaryButtonText = T("Place new ZIP", "Aggiungi nuovo ZIP"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        if (compatible.IsChecked != true || closed.IsChecked != true ||
            string.IsNullOrWhiteSpace(gameExe.Text))
        {
            await MessageAsync(root, T("Install not approved", "Installazione non approvata"),
                T("Choose the real game executable and approve both confirmations. Nothing was copied.",
                    "Seleziona l'eseguibile effettivo del gioco e approva entrambe le conferme. Nessun file copiato."));
            return;
        }
        var installed = await Client.InstallGameModPackageAsync(new GameModDropInInstallRequest(
            bundle.BundlePath, bundle.BundleSha256, folder, true, true, gameExe.Text.Trim()));
        await MessageAsync(root, installed.Succeeded
            ? T("ZIP placed in mod folder", "ZIP aggiunto alla cartella mod")
            : T("Install refused", "Installazione rifiutata"),
            installed.Succeeded && installed.Value is not null
                ? T("Installed file: ", "File installato: ") + installed.Value.InstalledFile +
                  Environment.NewLine + T("A game restart may be needed. This does not guarantee the game will load the ZIP.",
                    "Potrebbe essere necessario riavviare il gioco. Non è garantito che il gioco carichi lo ZIP.")
                : installed.Error ?? T("The folder or ZIP is unsafe or no longer matches the preview; check installed mods for recovery.",
                    "La cartella o lo ZIP non è sicuro o non corrisponde più all'anteprima; controlla le mod installate per il recupero."));
    }

    public static async Task ShowInstalledAsync(XamlRoot root)
    {
        var result = await Client.GetInstalledGameModPackagesAsync();
        if (!result.Succeeded || result.Value is null)
        {
            await MessageAsync(root, T("Mod receipts unavailable", "Ricevute mod non disponibili"),
                result.Error ?? T("Could not read local mod installation receipts.",
                    "Impossibile leggere le ricevute delle installazioni mod."));
            return;
        }
        if (result.Value.Count == 0)
        {
            await MessageAsync(root, T("No PaRuX ZIP mods installed", "Nessuna mod ZIP PaRuX installata"),
                T("ZIP files exported for manual use are not treated as installed.",
                    "Gli ZIP esportati per uso manuale non risultano installati."));
            return;
        }
        var select = new ComboBox
        {
            Header = T("PaRuX-installed ZIP mods", "Mod ZIP installate da PaRuX"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(select, "GameAssetStudio_DropIn_Installed");
        foreach (GameModDropInReceiptDto item in result.Value)
            select.Items.Add(new ComboBoxItem
            {
                Tag = item,
                Content = item.GameName + " — " + item.State + " — " +
                    System.IO.Path.GetFileName(item.InstalledFile)
            });
        select.SelectedIndex = 0;
        var closed = new CheckBox
        {
            Content = T("I confirm the game is offline and fully closed",
                "Confermo che il gioco è offline e completamente chiuso"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(closed, "GameAssetStudio_DropIn_RemoveClosed");
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        select.SelectionChanged += (_, _) =>
        {
            if (select.SelectedItem is ComboBoxItem { Tag: GameModDropInReceiptDto item })
                details.Text = T("Installed path: ", "Percorso installato: ") + item.InstalledFile +
                    Environment.NewLine + T("Game executable: ", "Eseguibile gioco: ") + item.GameExecutablePath +
                    Environment.NewLine + T("State: ", "Stato: ") + item.State +
                    Environment.NewLine + T("Only the exact originally installed ZIP can be removed. Changed files are kept.",
                        "È rimovibile solo lo ZIP identico a quello installato. I file modificati vengono conservati.");
        };
        if (select.SelectedItem is ComboBoxItem { Tag: GameModDropInReceiptDto first })
            details.Text = first.InstalledFile + Environment.NewLine +
                first.GameExecutablePath + Environment.NewLine + first.State;
        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };
        panel.Children.Add(select);
        panel.Children.Add(details);
        panel.Children.Add(closed);
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Installed AI ZIP mods", "Mod IA ZIP installate"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 410 },
            PrimaryButtonText = T("Remove selected ZIP safely", "Rimuovi ZIP selezionato in sicurezza"),
            CloseButtonText = T("Close", "Chiudi"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary ||
            select.SelectedItem is not ComboBoxItem { Tag: GameModDropInReceiptDto selected }) return;
        if (closed.IsChecked != true)
        {
            await MessageAsync(root, T("Game must be closed", "Il gioco deve essere chiuso"),
                T("No change was made. Confirm full game shutdown before removing a mod.",
                    "Nessuna modifica effettuata. Conferma la completa chiusura del gioco prima della rimozione."));
            return;
        }
        var remove = await Client.RemoveGameModPackageAsync(
            new GameModDropInRemoveRequest(selected.Id, Consent: true, OfflineGameClosedConfirmed: true));
        await MessageAsync(root, remove.Succeeded
            ? T("Mod removed safely", "Mod rimossa in sicurezza")
            : T("Mod removal refused", "Rimozione mod rifiutata"),
            remove.Succeeded
                ? T("PaRuX deleted only its own unchanged ZIP and removed the recovery receipt.",
                    "PaRuX ha eliminato soltanto il proprio ZIP invariato e la ricevuta di recupero.")
                : remove.Error ?? T("The ZIP or folder may have changed. No unknown file was deleted.",
                    "Lo ZIP o la cartella potrebbero essere cambiati. Nessun file sconosciuto è stato eliminato."));
    }

    private static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = root, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = T("Close", "Chiudi")
        }.ShowAsync();
    }
}
