using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Streams;

namespace PaRuX.Views;

/// <summary>
/// Phase 1 of AI Game Asset Studio: generate an image with PaRuX's LOCAL media
/// runtime, review the actual 64x64 pixels, and explicitly export a PNG skin.
/// Minecraft Java skin/account import is deliberately a separate user action.
/// No running game, executable, DRM, account or third-party mod directory is touched.
/// </summary>
internal static class MinecraftSkinStudioDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);

    public static async Task ShowAsync(XamlRoot root)
    {
        var prompt = new TextBox
        {
            Header = T("Describe your character", "Descrivi il tuo personaggio"),
            PlaceholderText = T("Pixel-art explorer with a red jacket and brown boots",
                "Esploratore pixel art con giacca rossa e stivali marroni"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 800, Height = 110
        };
        AutomationProperties.SetAutomationId(prompt, "SkinStudio_Prompt");
        var arms = new ComboBox
        {
            Header = T("Minecraft Java model", "Modello Minecraft Java"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        arms.Items.Add(new ComboBoxItem { Tag = "classic", Content = "Classic / Steve" });
        arms.Items.Add(new ComboBoxItem { Tag = "slim", Content = "Slim / Alex" });
        arms.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(arms, "SkinStudio_Arms");
        var instructions = new TextBlock
        {
            Text = T(
                "PaRuX uses your LOCAL image model to attempt a 64x64 skin UV sheet. A generated image may map the character incorrectly: inspect or manually refine the texture before importing it in Minecraft. Generation is unavailable in Gaming Mode so your GPU remains free for games.",
                "PaRuX usa il modello immagini LOCALE per creare un possibile skin UV 64x64. L'immagine potrebbe avere una mappatura errata: controlla o rifinisci la texture prima di importarla in Minecraft. Durante la modalità Gaming la generazione è disattivata per lasciare libera la GPU."),
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        panel.Children.Add(instructions);
        panel.Children.Add(prompt);
        panel.Children.Add(arms);
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Local AI — Minecraft skin studio", "IA locale — Studio skin Minecraft"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 425 },
            PrimaryButtonText = T("Generate locally", "Genera in locale"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(prompt.Text))
        {
            await MessageAsync(root, T("No description", "Descrizione mancante"),
                T("Enter a character description.", "Inserisci una descrizione del personaggio."));
            return;
        }

        var armStyle = (arms.SelectedItem as ComboBoxItem)?.Tag as string ?? "classic";
        var result = await GamingLibraryClient.Current.GenerateMinecraftSkinAsync(
            new GameSkinDraftRequest(prompt.Text.Trim(), armStyle));
        if (!result.Succeeded || result.Value is null)
        {
            await MessageAsync(root, T("Skin not generated", "Skin non generata"),
                result.Error ?? T("Local image generation could not complete. Check that the media model is installed and Gaming Mode is off.",
                    "La generazione locale non è riuscita. Controlla che il modello immagini sia installato e che la modalità Gaming sia disattivata."));
            return;
        }

        var draft = result.Value;
        BitmapImage preview;
        try
        {
            byte[] bytes = Convert.FromBase64String(draft.PngBase64);
            using var imageStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(imageStream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            imageStream.Seek(0);
            preview = new BitmapImage();
            await preview.SetSourceAsync(imageStream);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            await MessageAsync(root, T("Invalid image", "Immagine non valida"),
                T("The returned PNG could not be displayed. No game files were changed.",
                  "Impossibile visualizzare il PNG. Nessun file di gioco è stato modificato."));
            return;
        }

        var image = new Image { Source = preview, Width = 256, Height = 256 };
        AutomationProperties.SetAutomationId(image, "SkinStudio_Preview");
        var confirm = new CheckBox
        {
            Content = T("I reviewed the skin and want to export it (no automatic game changes)",
                "Ho controllato la skin e voglio esportarla (nessuna modifica automatica al gioco)")
        };
        AutomationProperties.SetAutomationId(confirm, "SkinStudio_ExportConsent");
        var review = new StackPanel { Spacing = 12, MaxWidth = 500 };
        review.Children.Add(new TextBlock
        {
            Text = T("64x64 PNG preview — Minecraft Java skin candidate. Model-created pixels are not guaranteed to follow the correct UV map.",
                "Anteprima PNG 64x64 — skin candidata Minecraft Java. I pixel generati potrebbero non rispettare la mappa UV corretta."),
            TextWrapping = TextWrapping.Wrap
        });
        review.Children.Add(image);
        review.Children.Add(confirm);
        var approve = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Review generated skin", "Controlla la skin generata"),
            Content = new ScrollViewer { Content = review, MaxHeight = 480 },
            PrimaryButtonText = T("Export PNG", "Esporta PNG"),
            CloseButtonText = T("Discard", "Scarta"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await approve.ShowAsync() != ContentDialogResult.Primary) return;
        if (confirm.IsChecked != true)
        {
            await MessageAsync(root, T("Export cancelled", "Esportazione annullata"),
                T("You must review and explicitly approve the skin before exporting.",
                    "Prima di esportare devi controllare e approvare esplicitamente la skin."));
            return;
        }
        var exported = await GamingLibraryClient.Current.ExportMinecraftSkinAsync(draft.DraftId, consent: true);
        await MessageAsync(root,
            exported.Succeeded ? T("Skin PNG saved", "PNG skin salvato") :
                T("Export unavailable", "Esportazione non disponibile"),
            exported.Succeeded && exported.Value is not null
                ? T("Saved locally: ", "Salvato in locale: ") + exported.Value.FilePath + Environment.NewLine +
                  T("Check the UV layout, then import the PNG using Minecraft's official skin interface.",
                    "Controlla la mappa UV, poi importa il PNG tramite l'interfaccia skin ufficiale di Minecraft.")
                : exported.Error ?? T("The draft expired or its output directory was not safe.",
                    "La bozza è scaduta o la cartella di destinazione non è sicura."));
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
