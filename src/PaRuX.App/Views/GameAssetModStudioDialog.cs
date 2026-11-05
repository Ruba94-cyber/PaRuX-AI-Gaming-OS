using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace PaRuX.Views;

/// <summary>
/// Cross-game AI texture workspace. User supplies a game/title, its already
/// existing local PNG and the documented relative replacement path for that
/// game's mod/resource loader. Generation is LOCAL, proposal-only. ZIP export
/// needs separate approval, never installs or modifies an executable or game.
/// </summary>
internal static class GameAssetModStudioDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);

    public static async Task ShowAsync(XamlRoot root)
    {
        var game = new TextBox
        {
            Header = T("Game title", "Titolo del gioco"),
            PlaceholderText = T("My offline game", "Il mio gioco offline"),
            MaxLength = 100
        };
        AutomationProperties.SetAutomationId(game, "GameAssetStudio_GameName");
        var original = new TextBox
        {
            Header = T("Source texture (existing PNG)", "Texture originale (PNG esistente)"),
            PlaceholderText = T("Select a local PNG texture", "Seleziona una texture PNG locale"),
            IsReadOnly = true
        };
        AutomationProperties.SetAutomationId(original, "GameAssetStudio_Source");
        var browse = new Button { Content = T("Choose original PNG", "Scegli PNG originale"), MinHeight = 44 };
        AutomationProperties.SetAutomationId(browse, "GameAssetStudio_Browse");
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) original.Text = file.Path;
            }
            catch (Exception) { original.Text = string.Empty; }
        };
        var relative = new TextBox
        {
            Header = T("Documented virtual replacement path in the game's mod pack",
                "Percorso virtuale di sostituzione previsto dal mod pack del gioco"),
            PlaceholderText = "textures/characters/outfit.png",
            MaxLength = 240
        };
        AutomationProperties.SetAutomationId(relative, "GameAssetStudio_RelativePath");
        var prompt = new TextBox
        {
            Header = T("Describe how to change this texture with local AI",
                "Descrivi come modificare la texture con l'IA locale"),
            PlaceholderText = T("Blue jacket with silver detailing",
                "Giacca blu con dettagli argentati"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 800, MinHeight = 86
        };
        AutomationProperties.SetAutomationId(prompt, "GameAssetStudio_Prompt");
        var offline = new CheckBox
        {
            Content = T("I am editing an authorized offline game asset, and I know its supported mod-loader path",
                "Modifico un asset autorizzato di un gioco offline e conosco il percorso previsto dal suo mod loader"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(offline, "GameAssetStudio_OfflineConsent");
        var panel = new StackPanel { Spacing = 10, MaxWidth = 540 };
        panel.Children.Add(new TextBlock
        {
            Text = T(
                "Not Minecraft-only: PaRuX can propose PNG texture edits for different games. Use the exact asset replacement path documented by a compatible game's mod loader. The model preserves image dimensions and alpha, not game-specific UV correctness. Only a ZIP package is exported for manual installation; no game files, saves, running process or DRM are changed. During Gaming, GPU image generation is disabled.",
                "Non solo Minecraft: PaRuX può proporre modifiche a texture PNG di giochi diversi. Usa il percorso esatto previsto dal mod loader del gioco. L'IA mantiene dimensioni e trasparenza, non garantisce le UV specifiche. Viene esportato solo un pacchetto ZIP da installare manualmente: nessuna modifica a giochi, salvataggi, processi o DRM. Durante Gaming la generazione immagini è disattivata."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(game);
        panel.Children.Add(original);
        panel.Children.Add(browse);
        panel.Children.Add(relative);
        panel.Children.Add(prompt);
        panel.Children.Add(offline);
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Local AI — game texture mod studio", "IA locale — Studio mod texture giochi"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 470 },
            PrimaryButtonText = T("Generate preview", "Genera anteprima"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        if (offline.IsChecked != true || string.IsNullOrWhiteSpace(game.Text) ||
            string.IsNullOrWhiteSpace(relative.Text) || string.IsNullOrWhiteSpace(prompt.Text) ||
            string.IsNullOrWhiteSpace(original.Text))
        {
            await MessageAsync(root, T("Missing details", "Dati mancanti"),
                T("Select the texture, enter a game and compatible mod path, describe the edit and confirm authorized offline use.",
                  "Seleziona la texture, inserisci gioco e percorso mod compatibile, descrivi la modifica e conferma l'uso offline autorizzato."));
            return;
        }
        byte[] originalPng;
        try
        {
            var file = new FileInfo(original.Text);
            if (!file.Exists || file.Length is < 100 or > 2 * 1024 * 1024 ||
                !file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid source texture.");
            originalPng = await File.ReadAllBytesAsync(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await MessageAsync(root, T("Invalid PNG", "PNG non valido"),
                T("Choose a readable PNG file of at most 2 MiB and 16-1024 pixels per side.",
                    "Scegli un PNG leggibile di massimo 2 MiB e 16-1024 pixel per lato."));
            return;
        }
        var result = await GamingLibraryClient.Current.GenerateGameAssetModAsync(
            new GameAssetModDraftRequest(game.Text.Trim(), relative.Text.Trim(),
                Convert.ToBase64String(originalPng), prompt.Text.Trim()));
        if (!result.Succeeded || result.Value is null)
        {
            await MessageAsync(root, T("Generation unavailable", "Generazione non disponibile"),
                result.Error ?? T("Check that a local image model is installed and Gaming Mode is off.",
                    "Controlla il modello immagini locale e disattiva Gaming Mode."));
            return;
        }

        GameAssetModDraftDto draft = result.Value;
        BitmapImage preview;
        try
        {
            byte[] bytes = Convert.FromBase64String(draft.PreviewPngBase64);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            preview = new BitmapImage();
            await preview.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or FormatException or
            System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            await MessageAsync(root, T("Preview cannot be shown", "Anteprima non visualizzabile"),
                T("The image is invalid. No files were changed.", "L'immagine è non valida. Nessun file modificato."));
            return;
        }

        var image = new Image { Source = preview, Width = 256, Height = 256 };
        AutomationProperties.SetAutomationId(image, "GameAssetStudio_Preview");
        var consent = new CheckBox
        {
            Content = T("I reviewed the replacement PNG and approve a ZIP export for manual installation",
                "Ho controllato il PNG sostitutivo e approvo l'esportazione ZIP per installazione manuale"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(consent, "GameAssetStudio_ExportConsent");
        var review = new StackPanel { Spacing = 12, MaxWidth = 520 };
        review.Children.Add(new TextBlock
        {
            Text = draft.GameName + " — " + draft.RelativeAssetPath + Environment.NewLine +
                draft.Width + "×" + draft.Height + " · " +
                T("changed visible pixels: ", "pixel visibili modificati: ") + draft.ChangedPixels +
                Environment.NewLine + T(
                    "The ZIP contains a replacement PNG at this path and a manifest. It will not edit your game or load itself while the game is paused.",
                    "Lo ZIP contiene il PNG sostitutivo in questo percorso e un manifesto. Non modifica il gioco né si carica automaticamente mentre è in pausa."),
            TextWrapping = TextWrapping.Wrap
        });
        review.Children.Add(image);
        review.Children.Add(consent);
        var approval = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Review AI texture mod", "Controlla la mod texture IA"),
            Content = new ScrollViewer { Content = review, MaxHeight = 470 },
            PrimaryButtonText = T("Export mod ZIP", "Esporta ZIP mod"),
            CloseButtonText = T("Discard", "Scarta"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await approval.ShowAsync() != ContentDialogResult.Primary) return;
        if (consent.IsChecked != true)
        {
            await MessageAsync(root, T("Approval required", "Autorizzazione necessaria"),
                T("You must approve the displayed asset before any export.",
                    "Devi approvare l'asset mostrato prima di esportarlo."));
            return;
        }
        var saved = await GamingLibraryClient.Current.ExportGameAssetModAsync(draft.DraftId, consent: true);
        await MessageAsync(root, saved.Succeeded
            ? T("Mod package exported", "Pacchetto mod esportato")
            : T("Export refused", "Esportazione rifiutata"),
            saved.Succeeded && saved.Value is not null
                ? T("Saved: ", "Salvato: ") + saved.Value.BundlePath +
                  Environment.NewLine + T("Manual installation through a compatible game/mod loader is required.",
                    "È necessaria un'installazione manuale con un gioco/mod loader compatibile.")
                : saved.Error ?? T("The draft expired or its output folder was unsafe.",
                    "La bozza è scaduta o la cartella di esportazione non è sicura."));
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
