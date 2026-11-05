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
using Windows.Storage.Pickers;

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
        var useOriginal = new CheckBox
        {
            Content = T("Restyle an existing Minecraft skin (recommended: UV-safe)",
                "Modifica una skin Minecraft esistente (consigliato: UV corretti)"),
            IsChecked = true
        };
        AutomationProperties.SetAutomationId(useOriginal, "SkinStudio_RestyleExisting");
        var chosenFile = new TextBox
        {
            Header = T("Original skin (64x64 PNG)", "Skin originale (PNG 64x64)"),
            PlaceholderText = T("Choose a Minecraft skin PNG", "Scegli una skin Minecraft PNG"),
            IsReadOnly = true
        };
        AutomationProperties.SetAutomationId(chosenFile, "SkinStudio_OriginalPath");
        var chooseFile = new Button { Content = T("Choose PNG", "Scegli PNG"), MinHeight = 44 };
        AutomationProperties.SetAutomationId(chooseFile, "SkinStudio_ChooseOriginal");
        chooseFile.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) chosenFile.Text = file.Path;
            }
            catch (Exception)
            {
                chosenFile.Text = string.Empty;
            }
        };
        var part = new ComboBox
        {
            Header = T("Which part should the AI recolour?", "Quale parte deve ricolorare l'IA?"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var pair in new[] {
            ("torso", T("Torso and jacket", "Busto e giacca")),
            ("head", T("Head and hat", "Testa e copricapo")),
            ("arms", T("Both arms and sleeves", "Braccia e maniche")),
            ("legs", T("Both legs and trousers", "Gambe e pantaloni")),
            ("full", T("Full character", "Personaggio intero"))
        })
            part.Items.Add(new ComboBoxItem { Tag = pair.Item1, Content = pair.Item2 });
        part.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(part, "SkinStudio_RestylePart");
        var originalOptions = new StackPanel { Spacing = 8 };
        originalOptions.Children.Add(chosenFile);
        originalOptions.Children.Add(chooseFile);
        originalOptions.Children.Add(part);
        useOriginal.Checked += (_, _) => originalOptions.Visibility = Visibility.Visible;
        useOriginal.Unchecked += (_, _) => originalOptions.Visibility = Visibility.Collapsed;

        var instructions = new TextBlock
        {
            Text = T(
                "Recommended: import a 64x64 skin to preserve its real UV surfaces and recolour just one body part with your LOCAL image model. Generating a brand-new skin is experimental. Image generation is disabled during Gaming.",
                "Consigliato: importa una skin 64x64 per mantenere la mappatura UV originale e ricolorare una sola parte usando l'IA immagini LOCALE. Generare una nuova skin è sperimentale. La generazione è disattivata durante Gaming."),
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        panel.Children.Add(instructions);
        panel.Children.Add(prompt);
        panel.Children.Add(arms);
        panel.Children.Add(useOriginal);
        panel.Children.Add(originalOptions);
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
        bool isRestyle = useOriginal.IsChecked == true;
        GamingCallResult<GameSkinDraftDto> result;
        if (isRestyle)
        {
            if (string.IsNullOrWhiteSpace(chosenFile.Text))
            {
                await MessageAsync(root, T("Select original PNG", "Seleziona PNG originale"),
                    T("Choose a 64x64 skin PNG before restyling.", "Scegli una skin PNG 64x64 prima di modificarla."));
                return;
            }
            try
            {
                var info = new FileInfo(chosenFile.Text);
                if (!info.Exists || info.Length is < 100 or > 2 * 1024 * 1024 ||
                    info.Extension.ToLowerInvariant() != ".png")
                    throw new InvalidDataException("Unsupported Minecraft skin PNG.");
                byte[] original = await File.ReadAllBytesAsync(info.FullName);
                string selected = (part.SelectedItem as ComboBoxItem)?.Tag as string ?? "torso";
                result = await GamingLibraryClient.Current.RestyleMinecraftSkinAsync(
                    new GameSkinRestyleRequest(
                        Convert.ToBase64String(original), prompt.Text.Trim(), selected, armStyle));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                await MessageAsync(root, T("Invalid skin PNG", "PNG skin non valido"),
                    T("Choose a readable 64x64 Minecraft skin (max 2 MiB). No files were changed.",
                      "Scegli una skin Minecraft 64x64 leggibile (massimo 2 MiB). Nessun file è stato modificato."));
                return;
            }
        }
        else
        {
            result = await GamingLibraryClient.Current.GenerateMinecraftSkinAsync(
                new GameSkinDraftRequest(prompt.Text.Trim(), armStyle));
        }
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
                writer.DetachStream(); // Closing DataWriter must not close the PNG preview stream.
            }
            imageStream.Seek(0);
            preview = new BitmapImage();
            await preview.SetSourceAsync(imageStream);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException
            or InvalidOperationException or System.Runtime.InteropServices.COMException)
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
            Text = isRestyle
                ? T("UV-preserving restyle: original skin shape and other parts are unchanged. Only existing selected faces were recoloured. Check texture quality.",
                    "Modifica con UV conservati: forma della skin e altre parti immutate. Sono state ricolorate soltanto le superfici selezionate. Verifica il risultato.")
                : T("64x64 PNG preview — Minecraft Java skin candidate. Model-created pixels are not guaranteed to follow the correct UV map.",
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
