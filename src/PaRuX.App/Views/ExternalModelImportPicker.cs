using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Model Hub picker for already-present Ollama and LM Studio models.</summary>
    internal static class ExternalModelImportPicker
    {
        private static string T(string en, string it) => PaRuX.Services.UiText.Pick(en, it);
        public static async Task ShowAsync(XamlRoot xamlRoot, Action<string> reportStatus, Func<Task> refreshLibrary)
        {
            if (xamlRoot == null) throw new ArgumentNullException(nameof(xamlRoot));
            if (reportStatus == null) throw new ArgumentNullException(nameof(reportStatus));
            if (refreshLibrary == null) throw new ArgumentNullException(nameof(refreshLibrary));

            while (true)
            {
                List<ExternalModelCandidate> candidates;
                try
                {
                    candidates = await ModelDownloadClient.Current.ListExternalModelsAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    reportStatus("Could not read models from Ollama or LM Studio: " + ex.Message);
                    return;
                }

                ExternalModelCandidate? selected = await PickModelAsync(xamlRoot, candidates);
                if (selected == null) return;

                ExternalModelImportRoute route = ExternalModelImportPolicy.Decide(selected, copyConsentAccepted: false);
                if (route == ExternalModelImportRoute.AlreadyImported)
                {
                    reportStatus("That model is already in PaRuX's managed library.");
                    continue;
                }

                bool copyConsent = false;
                if (route == ExternalModelImportRoute.CopyConsentRequired)
                {
                    copyConsent = await ConfirmCopyAsync(xamlRoot, selected);
                    if (!copyConsent)
                    {
                        reportStatus("Import cancelled. No copy was made.");
                        return;
                    }

                    route = ExternalModelImportPolicy.Decide(selected, copyConsentAccepted: true);
                }

                try
                {
                    await ImportAsync(selected, route == ExternalModelImportRoute.Copy);
                    reportStatus("Imported " + selected.DisplayName + " into PaRuX's managed library.");
                    await RefreshLibraryAfterImportAsync(refreshLibrary, reportStatus, selected.DisplayName);
                }
                catch (Exception ex) when (route == ExternalModelImportRoute.HardLink && IsHardLinkFailure(ex))
                {
                    bool allowCopy = await ConfirmCopyAsync(xamlRoot, selected);
                    if (!allowCopy)
                    {
                        reportStatus("Copy cancelled. No model was imported.");
                        return;
                    }

                    try
                    {
                        ExternalModelImportRoute copyRoute = ExternalModelImportPolicy.Decide(
                            selected, copyConsentAccepted: true, hardLinkFailed: true);
                        await ImportAsync(selected, copyRoute == ExternalModelImportRoute.Copy);
                        reportStatus("Copied " + selected.DisplayName + " into PaRuX's managed library.");
                        await RefreshLibraryAfterImportAsync(refreshLibrary, reportStatus, selected.DisplayName);
                    }
                    catch (Exception copyError)
                    {
                        reportStatus("Could not import " + selected.DisplayName + ": " + copyError.Message);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    reportStatus("Could not import " + selected.DisplayName + ": " + ex.Message);
                    return;
                }
            }
        }

        private static async Task RefreshLibraryAfterImportAsync(
            Func<Task> refreshLibrary,
            Action<string> reportStatus,
            string displayName)
        {
            try
            {
                await refreshLibrary();
            }
            catch (Exception ex)
            {
                reportStatus("Imported " + displayName + ", but the managed library could not refresh: " + ex.Message);
            }
        }

        private static async Task ImportAsync(ExternalModelCandidate candidate, bool copyConsent) =>
            await ModelDownloadClient.Current.ImportExternalModelAsync(
                new ExternalModelImportRequest { SourcePath = candidate.SourcePath, CopyConsent = copyConsent },
                CancellationToken.None);

        private static bool IsHardLinkFailure(Exception ex) =>
            ex.Message.Contains("cannot be hard-linked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Copying uses extra disk space", StringComparison.OrdinalIgnoreCase);

        private static async Task<ExternalModelCandidate?> PickModelAsync(
            XamlRoot xamlRoot,
            IReadOnlyList<ExternalModelCandidate> candidates)
        {
            var options = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 440
            };

            foreach (ExternalModelCandidate candidate in candidates)
            {
                var details = new StackPanel { Spacing = 3 };
                details.Children.Add(new TextBlock
                {
                    Text = candidate.DisplayName,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                });
                details.Children.Add(new TextBlock
                {
                    Text = SourceName(candidate.Source) + " · " + FormatBytes(candidate.SizeBytes)
                        + (candidate.SameVolumeAsLibrary ? T(" · can link without copying", " · collegabile senza copiare") : T(" · copy may be needed", " · potrebbe servire una copia")),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                });
                if (candidate.ProjectorSourcePath != null)
                    details.Children.Add(new TextBlock { Text = T("A paired vision projector will be imported too.", "Verrà importato anche un proiettore di visione abbinato."), TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(candidate.TextOnlyNote))
                    details.Children.Add(new TextBlock { Text = candidate.TextOnlyNote, TextWrapping = TextWrapping.Wrap });
                if (candidate.AlreadyImported)
                    details.Children.Add(new TextBlock
                    {
                        Text = T("Already imported (matching filename and size)", "Già importato (nome file e dimensione corrispondenti)"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });

                var item = new ListViewItem
                {
                    Content = details,
                    IsEnabled = !candidate.AlreadyImported,
                    Tag = candidate
                };
                AutomationProperties.SetAutomationId(item, "ModelHub_ExternalModel_" + options.Items.Count.ToString(CultureInfo.InvariantCulture));
                options.Items.Add(item);
            }

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = candidates.Count == 0
                    ? T("No GGUF models were found in the default Ollama or LM Studio stores.", "Nessun modello GGUF trovato negli archivi predefiniti di Ollama o LM Studio.")
                    : T("Discovered stores are read-only. The Already imported flag matches the suggested filename and size; flagged models cannot be imported again.", "Gli archivi scoperti sono di sola lettura. Il flag Già importato confronta nome file e dimensione suggeriti; i modelli segnalati non possono essere importati di nuovo."),
                TextWrapping = TextWrapping.Wrap
            });
            if (candidates.Count > 0) content.Children.Add(options);

            var dialog = new ContentDialog
            {
                Title = T("Import existing models", "Importa i modelli esistenti"),
                Content = content,
                PrimaryButtonText = T("Import selected", "Importa la selezione"),
                CloseButtonText = T("Close", "Chiudi"),
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
                XamlRoot = xamlRoot
            };
            ExternalModelCandidate? selected = null;
            options.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = options.SelectedItem is ListViewItem item
                    && item.Tag is ExternalModelCandidate candidate
                    && !candidate.AlreadyImported;
            };
            dialog.PrimaryButtonClick += (_, _) =>
            {
                selected = (options.SelectedItem as ListViewItem)?.Tag as ExternalModelCandidate;
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? selected : null;
        }

        private static async Task<bool> ConfirmCopyAsync(XamlRoot xamlRoot, ExternalModelCandidate candidate)
        {
            string files = candidate.ProjectorSourcePath == null ? "the model" : "the model and its paired vision projector";
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = T("PaRuX cannot hard-link this import into its library. Copying ", "PaRuX non può creare un hard link per questa importazione nella sua libreria. La copia di ") + files + T(" uses ", " usa ")
                    + FormatBytes(candidate.CopySizeBytes) + T(" of additional disk space.", " di spazio su disco aggiuntivo."),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = T("PaRuX checks that the copy can finish while keeping its free-space reserve. The source store remains unchanged.", "PaRuX controlla che la copia possa terminare mantenendo la sua riserva di spazio libero. L'archivio di origine resta invariato."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var dialog = new ContentDialog
            {
                Title = T("Copy model into PaRuX?", "Copiare il modello in PaRuX?"),
                Content = content,
                PrimaryButtonText = T("Copy into library", "Copia nella libreria"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static string SourceName(string source) => source switch
        {
            "ollama" => "Ollama",
            "lmstudio" => "LM Studio",
            "jan" => "Jan",
            "koboldcpp" => "KoboldCpp",
            _ => source
        };

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "unknown size";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double gib = bytes / (1024d * 1024 * 1024);
            if (gib >= 1) return gib.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            double mib = bytes / (1024d * 1024);
            return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
        }
    }
}
