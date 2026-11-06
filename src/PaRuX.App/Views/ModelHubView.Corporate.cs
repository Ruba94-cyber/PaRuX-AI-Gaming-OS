using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>Corporate Lite first-class model actions. The CPU runtime is bundled; users can
    /// add local GGUF models without network, or download verified GGUF models in a separately
    /// IT-approved online session. No game, media or cloud inference services are involved.</summary>
    public sealed partial class ModelHubView
    {
        private UIElement BuildCorporateModelCard()
        {
            bool it = StartItalian;
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = it ? "Modelli locali · Corporate" : "Local models · Corporate",
                FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = it
                    ? "Il motore llama.cpp CPU è già incluso: non devi scaricare o installare un runtime. Puoi aggiungere modelli GGUF dal PC e scegliere quale usare per Chat."
                    : "The llama.cpp CPU engine is already included; no runtime download or installation is needed. Add GGUF models from this PC and choose which one Chat uses.",
                TextWrapping = TextWrapping.Wrap
            });
            var status = new TextBlock
            {
                Text = CorporateMode.ModelDownloadsAllowed
                    ? (it ? "Download dei modelli autorizzati per questa sessione. L'IA resta locale; Gaming, runtime online e aggiornamenti automatici rimangono disabilitati."
                          : "Model downloads are enabled for this session. Inference stays local; Gaming, online runtime installers and automatic updates remain disabled.")
                    : (it ? "Modalità offline: l'importazione dei file locali è disponibile. Per scaricare modelli, usa l'avvio dedicato dopo l'autorizzazione IT."
                          : "Offline mode: local file import is available. To download models, use the dedicated launcher after IT approval."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.82
            };
            AutomationProperties.SetAutomationId(status, "ModelHub_Corporate_ModelNetwork_Status");
            panel.Children.Add(status);

            var import = new Button
            {
                Content = it ? "Importa file GGUF…" : "Import GGUF file…",
                MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(import, "ModelHub_Corporate_ImportGguf_Button");
            import.Click += async (_, _) =>
            {
                import.IsEnabled = false;
                try { await ImportCorporateGgufAsync(status); }
                finally { import.IsEnabled = true; }
            };
            panel.Children.Add(import);

            var existing = new Button
            {
                Content = it ? "Importa da Ollama / LM Studio…" : "Import from Ollama / LM Studio…",
                MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(existing, "ModelHub_Corporate_ImportExternal_Button");
            existing.Click += async (_, _) =>
            {
                existing.IsEnabled = false;
                try
                {
                    await ExternalModelImportPicker.ShowAsync(XamlRoot,
                        message => status.Text = message,
                        () => RefreshAllAsync());
                }
                catch (Exception ex)
                {
                    Log.Write("Corporate external model import: " + ex.GetType().Name);
                    status.Text = UserFailureRecoveryMapper.Map(ex,
                        it ? "Importazione non riuscita" : "Import failed").FormatStatusLine();
                }
                finally { existing.IsEnabled = true; }
            };
            panel.Children.Add(existing);

            if (CorporateMode.ModelDownloadsAllowed)
            {
                var download = new Button
                {
                    Content = it ? "Cerca e scarica modelli GGUF" : "Browse and download GGUF models",
                    MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(download, "ModelHub_Corporate_BrowseDownloads_Button");
                download.Click += (_, _) => ShowHubTab(ModelHubTab.Text);
                panel.Children.Add(download);
            }

            return Section(it ? "AGGIUNGI O CAMBIA MODELLO" : "ADD OR CHANGE MODELS", panel);
        }

        private async Task ImportCorporateGgufAsync(TextBlock status)
        {
            if (XamlRoot == null || App.MainWin == null) return;
            bool it = StartItalian;
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".gguf");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile chosen = await picker.PickSingleFileAsync();
                if (chosen == null) return;

                var file = new FileInfo(chosen.Path);
                if (!file.Exists) throw new FileNotFoundException("The selected file is not accessible.", chosen.Path);
                if (!CorporateModelImportPolicy.IsAcceptableModel(file.Name, file.Length)
                    || !CorporateModelImportPolicy.HasGgufHeader(file.FullName))
                    throw new InvalidDataException("Choose a genuine text GGUF model, not a projector, empty file or renamed file.");

                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = it ? "Importare " + file.Name + "?" : "Import " + file.Name + "?",
                    Content = (it
                        ? "PaRuX copierà il modello nella propria libreria portatile. Occorrono "
                        : "PaRuX will copy this model into its portable library. It needs ")
                        + Math.Ceiling(file.Length / (1024d * 1024)) + " MiB"
                        + (it ? " aggiuntivi. Il file originale non viene modificato."
                              : " of additional space. The original file is not changed."),
                    PrimaryButtonText = it ? "Importa e usa per Chat" : "Import and use for Chat",
                    SecondaryButtonText = it ? "Importa soltanto" : "Import only",
                    CloseButtonText = it ? "Annulla" : "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                ContentDialogResult action = await confirm.ShowAsync();
                if (action is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return;

                status.Text = it ? "Importazione in corso…" : "Importing model…";
                ManagedModelInfo imported = await ModelDownloadClient.Current.ImportManagedModelAsync(
                    new ImportManagedModelRequest { SourcePath = file.FullName }, CancellationToken.None);
                if (action == ContentDialogResult.Primary)
                {
                    try
                    {
                        await ModelRolesClient.Current.SetRoleAsync("standard", imported.FilePath, CancellationToken.None);
                        status.Text = (it ? "Importato e assegnato alla Chat: " : "Imported and assigned to Chat: ")
                            + imported.FileName + (it ? ". Il modello sarà caricato quando necessario."
                                                      : ". It loads when needed.");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Corporate model role assignment: " + ex.GetType().Name);
                        status.Text = (it ? "Modello importato, ma non assegnato alla Chat: "
                                          : "Model imported, but not assigned to Chat: ")
                            + UserFailureRecoveryMapper.Map(ex, "Role assignment failed").FormatStatusLine();
                    }
                }
                else
                    status.Text = (it ? "Modello importato: " : "Model imported: ") + imported.FileName;
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Corporate GGUF file import: " + ex.GetType().Name);
                status.Text = UserFailureRecoveryMapper.Map(ex,
                    it ? "Importazione del modello non riuscita" : "Model import failed").FormatStatusLine();
            }
        }
    }
}
