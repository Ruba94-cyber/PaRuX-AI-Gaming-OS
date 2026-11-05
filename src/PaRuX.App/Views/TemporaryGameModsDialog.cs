using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Human-in-the-loop UI for AI-proposed reversible config edits on a game that
/// PaRuX actually paused. This UI never changes process memory, patches EXEs
/// or automatically launches scripts or installers.
/// </summary>
internal static class TemporaryGameModsDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);
    private static readonly GamingLibraryClient Client = GamingLibraryClient.Current;

    public static async Task ShowAsync(XamlRoot root, QuickResumeAppDto paused)
    {
        if (!paused.Suspended)
        {
            await MessageAsync(root, T("Game is not paused", "Gioco non in pausa"),
                T("Pause the game with Quick Resume before proposing a change.",
                  "Metti in pausa il gioco con Quick Resume prima di proporre una modifica."));
            return;
        }
        var games = await Client.GetManualGamesForModsAsync();
        var existing = await Client.GetTemporaryGameModsAsync();
        if (!games.Succeeded || games.Value is null || !existing.Succeeded || existing.Value is null)
        {
            await MessageAsync(root, T("Game Mods unavailable", "Game Mods non disponibile"),
                T("The local library or recovery journal could not be read.",
                  "Impossibile leggere la libreria locale o il registro di ripristino."));
            return;
        }

        var select = new ComboBox
        {
            Header = T("PaRuX portable game", "Gioco portatile PaRuX"),
            PlaceholderText = T("Choose the paused game", "Seleziona il gioco in pausa"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(select, "QuickResume_Mods_Game");
        foreach (ManualGameDto game in games.Value.Where(x => x.Kind == ManualEntryKinds.Game
            && string.Equals(Path.GetFileNameWithoutExtension(x.ExecutablePath), paused.ProcessName,
                StringComparison.OrdinalIgnoreCase)))
            select.Items.Add(new ComboBoxItem { Content = game.Name, Tag = game });
        if (select.Items.Count > 0) select.SelectedIndex = 0;

        var relative = new TextBox
        {
            Header = T("Existing text config relative to game folder", "Configurazione di testo relativa alla cartella del gioco"),
            PlaceholderText = "config/settings.ini",
            MaxLength = 240
        };
        var proposed = new TextBox
        {
            Header = T("AI-suggested full replacement text", "Testo completo proposto dall'IA"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120, MaxHeight = 210, MaxLength = 2048
        };
        var offline = new CheckBox
        {
            Content = T("I confirm this is my offline single-player game, without an online/anti-cheat session",
                "Confermo che è un mio gioco offline e single-player, senza sessioni online/anti-cheat"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(relative, "QuickResume_Mods_File");
        AutomationProperties.SetAutomationId(proposed, "QuickResume_Mods_Proposed");
        AutomationProperties.SetAutomationId(offline, "QuickResume_Mods_OfflineConsent");
        var content = new StackPanel { Spacing = 10, MaxWidth = 520 };
        content.Children.Add(new TextBlock
        {
            Text = T("Only .ini/.cfg/.json/.txt in PaRuX-managed portable games are supported. "
              + "The game may need to restart before it notices a changed config. Always review the preview. "
              + "No memory editing or EXE patching.",
                "Sono supportati solo .ini/.cfg/.json/.txt dei giochi portatili gestiti da PaRuX. "
              + "Il gioco potrebbe dover essere riavviato per rilevare le modifiche. "
              + "Controlla sempre l'anteprima. Nessuna modifica della memoria o degli EXE."),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(select);
        content.Children.Add(relative);
        content.Children.Add(proposed);
        content.Children.Add(offline);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Temporary AI Game Mods", "Mod temporanee IA per giochi"),
            Content = new ScrollViewer { Content = content, MaxHeight = 470 },
            PrimaryButtonText = T("Preview changes", "Anteprima modifiche"),
            SecondaryButtonText = T("Restore temporary mod", "Ripristina mod temporanea"),
            IsSecondaryButtonEnabled = existing.Value.Count > 0,
            CloseButtonText = T("Close", "Chiudi"),
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult selected = await dialog.ShowAsync();
        if (selected == ContentDialogResult.Secondary)
        {
            await RestoreAsync(root, existing.Value);
            return;
        }
        if (selected != ContentDialogResult.Primary) return;
        if (select.SelectedItem is not ComboBoxItem { Tag: ManualGameDto gameToEdit } ||
            string.IsNullOrWhiteSpace(relative.Text) || string.IsNullOrWhiteSpace(proposed.Text) ||
            offline.IsChecked != true)
        {
            await MessageAsync(root, T("Preview refused", "Anteprima rifiutata"),
                T("Select a registered paused game, config file, proposed text and confirm offline single-player use.",
                  "Seleziona gioco in pausa, file, testo proposto e conferma l'utilizzo offline single-player."));
            return;
        }
        var preview = await Client.PreviewTemporaryGameModAsync(new GameModProposalRequest(
            gameToEdit.Id, paused.ProcessId, paused.StartedUtc,
            relative.Text.Trim(), proposed.Text, OfflineSinglePlayerConfirmed: true));
        if (!preview.Succeeded || preview.Value is null)
        {
            await MessageAsync(root, T("Preview refused", "Anteprima rifiutata"),
                preview.Error ?? T("This game/config is not eligible for a paused temporary mod.",
                    "Questo gioco/file non consente una mod temporanea in pausa."));
            return;
        }
        await ReviewAndApplyAsync(root, preview.Value);
    }

    private static async Task ReviewAndApplyAsync(XamlRoot root, GameModPreviewDto preview)
    {
        var before = new TextBox
        {
            Header = T("Original (read-only)", "Originale (sola lettura)"),
            Text = preview.OriginalText, IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 170
        };
        var after = new TextBox
        {
            Header = T("Proposed (read-only)", "Proposta (sola lettura)"),
            Text = preview.ProposedText, IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 170
        };
        AutomationProperties.SetAutomationId(before, "QuickResume_Mods_Original");
        AutomationProperties.SetAutomationId(after, "QuickResume_Mods_Preview");
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = preview.GameName + " · " + preview.RelativePath +
                Environment.NewLine +
                T("Backups are kept until you explicitly restore. Games may not read config changes until restart.",
                  "Il backup resta finché non ripristini manualmente. Il gioco potrebbe leggere le modifiche solo al riavvio."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(before);
        panel.Children.Add(after);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Review the temporary modification", "Rivedi la modifica temporanea"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 490 },
            PrimaryButtonText = T("Apply reviewed change", "Applica modifica verificata"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var applied = await Client.ApplyTemporaryGameModAsync(preview.PreviewId, consent: true);
        await MessageAsync(root,
            applied.Succeeded ? T("Mod applied", "Mod applicata") : T("Mod refused", "Mod rifiutata"),
            applied.Succeeded
                ? T("Text config updated and original backed up. Restore it from AI Game Mods after play.",
                    "Configurazione modificata e originale salvato. Ripristina la mod da AI Game Mods dopo il gioco.")
                : applied.Error ?? T("The game resumed, file changed or preview expired. No new write was authorized.",
                    "Il gioco è stato ripreso, il file cambiato o l'anteprima scaduta. Nessuna scrittura autorizzata."));
    }

    public static async Task ShowPendingAiProposalsAsync(XamlRoot root)
    {
        var listed = await Client.GetPendingGameModPreviewsAsync();
        if (!listed.Succeeded || listed.Value is null)
        {
            await MessageAsync(root, T("AI proposals unavailable", "Proposte IA non disponibili"),
                listed.Error ?? T("Could not read proposals.", "Impossibile leggere le proposte."));
            return;
        }
        if (listed.Value.Count == 0)
        {
            await MessageAsync(root, T("No pending AI mods", "Nessuna mod IA in attesa"),
                T("Proposals require the same game to remain paused and expire in five minutes.",
                    "Le proposte richiedono che lo stesso gioco rimanga in pausa e scadono dopo cinque minuti."));
            return;
        }
        var selector = new ComboBox
        {
            Header = T("AI suggestions waiting for review", "Proposte dell'IA in attesa di revisione"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(selector, "QuickResume_Mods_PendingSelect");
        foreach (GameModPreviewDto proposal in listed.Value)
            selector.Items.Add(new ComboBoxItem
            {
                Content = proposal.GameName + " · " + proposal.RelativePath,
                Tag = proposal
            });
        selector.SelectedIndex = 0;
        var choose = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Review AI game mod", "Rivedi la mod IA per il gioco"),
            Content = selector,
            PrimaryButtonText = T("Review before applying", "Rivedi prima di applicare"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await choose.ShowAsync() != ContentDialogResult.Primary ||
            selector.SelectedItem is not ComboBoxItem { Tag: GameModPreviewDto proposal }) return;
        await ReviewAndApplyAsync(root, proposal);
    }

    public static async Task ShowRestoreListAsync(XamlRoot root)
    {
        var listed = await Client.GetTemporaryGameModsAsync();
        if (!listed.Succeeded || listed.Value is null)
        {
            await MessageAsync(root, T("Mod recovery unavailable", "Recupero mod non disponibile"),
                listed.Error ?? T("Could not read saved mod backups.", "Impossibile leggere i backup delle mod."));
            return;
        }
        if (listed.Value.Count == 0)
        {
            await MessageAsync(root, T("No temporary mods", "Nessuna mod temporanea"),
                T("There are no active changes to restore.", "Non ci sono modifiche attive da ripristinare."));
            return;
        }
        await RestoreAsync(root, listed.Value);
    }

    private static async Task RestoreAsync(XamlRoot root, System.Collections.Generic.IReadOnlyList<GameTemporaryModDto> mods)
    {
        var select = new ComboBox
        {
            Header = T("Saved temporary mods", "Mod temporanee salvate"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(select, "QuickResume_Mods_RestoreSelect");
        foreach (GameTemporaryModDto mod in mods)
            select.Items.Add(new ComboBoxItem { Content = mod.GameName + " · " + mod.RelativePath, Tag = mod });
        if (select.Items.Count > 0) select.SelectedIndex = 0;
        var panel = new StackPanel { Spacing = 10, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = T("Restore is permitted when the game is paused or closed, if the modified file has not been changed again.",
                "Il ripristino è consentito a gioco in pausa o chiuso, se il file modificato non è stato ulteriormente cambiato."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(select);
        var confirm = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Restore a temporary mod", "Ripristina una mod temporanea"),
            Content = panel,
            PrimaryButtonText = T("Restore original", "Ripristina originale"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary ||
            select.SelectedItem is not ComboBoxItem { Tag: GameTemporaryModDto chosen }) return;
        var restored = await Client.RestoreTemporaryGameModAsync(chosen.Id, consent: true);
        await MessageAsync(root,
            restored.Succeeded ? T("Original restored", "Originale ripristinato")
                : T("Restore refused", "Ripristino rifiutato"),
            restored.Succeeded ? T("Your original config is back; no game files were deleted.",
                "La configurazione originale è stata ripristinata; nessun file del gioco eliminato.")
                : restored.Error ?? T("The file or game state changed. Backup kept for manual recovery.",
                    "Lo stato del file o del gioco è cambiato. Backup conservato per il recupero manuale."));
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
