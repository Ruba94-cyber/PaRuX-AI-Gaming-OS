using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Progressive disclosure for Model Hub: keep catalogue rows short and
    /// open technical metadata and quantization choices only on demand.</summary>
    public sealed partial class ModelHubView
    {
        // Session-only user intent. A manual choice must not be replaced by a
        // later hardware recommendation or a refreshed catalogue.
        private readonly Dictionary<string, string> _manuallyReviewedQuantByRepo =
            new(StringComparer.OrdinalIgnoreCase);

        private async Task ShowModelDetailsAsync(
            string title, IEnumerable<(string Label, string? Value)> fields, Uri? modelCardUri = null)
        {
            var content = new StackPanel { Spacing = 10 };
            foreach ((string label, string? value) in fields)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var section = new StackPanel { Spacing = 3 };
                section.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 12, Opacity = 0.72,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                section.Children.Add(new TextBlock
                {
                    Text = value, TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
                content.Children.Add(section);
            }
            if (modelCardUri is { Scheme: "https", Host: "huggingface.co" })
                content.Children.Add(new HyperlinkButton
                {
                    Content = T("Read model card and repository terms", "Leggi la scheda del modello e i termini del repository"),
                    NavigateUri = modelCardUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });

            var scroll = new ScrollViewer
            {
                Content = content, MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var dialog = new ContentDialog
            {
                Title = title, Content = scroll, CloseButtonText = T("Close", "Chiudi"),
                XamlRoot = XamlRoot, MaxWidth = 720
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            AutomationProperties.SetAutomationId(dialog, "ModelHub_ModelDetails_Dialog");
            await dialog.ShowAsync();
        }

        private async Task ReviewSearchVariantsAsync(
            ModelSearchResult repo,
            IReadOnlyList<ModelHubSplitSetPresentation.QuantChoiceOption> choices,
            int preferredIndex)
        {
            if (choices.Count == 0)
            {
                _status.Text = T("This repository has no GGUF variants to review.", "Questo repository non ha varianti GGUF da controllare.");
                return;
            }

            var picker = new ComboBox
            {
                MinWidth = 0, // Let WinUI size the picker within narrow / high-DPI dialogs.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = T("Select a complete quantization", "Seleziona una quantizzazione completa")
            };
            AutomationProperties.SetAutomationId(picker, "ModelHub_VariantPicker_ComboBox");
            foreach (ModelHubSplitSetPresentation.QuantChoiceOption option in choices)
                picker.Items.Add(new ComboBoxItem
                {
                    Content = option.Label
                        + (option.Choice.SizeBytes > 0
                            ? " · ~" + FormatBytes(option.Choice.SizeBytes)
                            : T(" · size not published", " · dimensione non pubblicata")),
                    Tag = option.Choice,
                    IsEnabled = option.CanDownload
                });

            var hardwareText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
            };
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                Text = repo.RepoId, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = T("Choose a complete GGUF set. Size and hardware fit are estimates; the pinned download plan and licence are reviewed before any transfer.", "Scegli un set GGUF completo. Dimensione e compatibilità hardware sono stime; il piano di download fissato e la licenza vengono controllati prima di qualsiasi trasferimento."),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
            });
            body.Children.Add(picker);
            body.Children.Add(hardwareText);
            var dialog = new ContentDialog
            {
                Title = T("Quantization options", "Opzioni di quantizzazione"),
                Content = body,
                PrimaryButtonText = T("Review download", "Controlla il download"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                MaxWidth = 720
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            AutomationProperties.SetAutomationId(dialog, "ModelHub_QuantizationDialog");
            picker.SelectionChanged += (_, _) =>
            {
                ModelQuantChoice? selected = (picker.SelectedItem as ComboBoxItem)?.Tag as ModelQuantChoice;
                dialog.IsPrimaryButtonEnabled = selected?.Complete == true;
                if (selected == null) { hardwareText.Text = T("No complete variant selected.", "Nessuna variante completa selezionata."); return; }
                if (selected.SizeBytes <= 0)
                {
                    hardwareText.Text = T("Hardware fit unknown until the repository provides an exact file size.", "Compatibilità hardware sconosciuta finché il repository non fornisce la dimensione esatta del file.");
                    return;
                }
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(selected.SizeBytes, _hardware);
                hardwareText.Text = T("On this PC: ", "Su questo PC: ") + ModelHubRowPresentation.HardwareFit(fit.Verdict, fit.Summary);
            };
            if (preferredIndex >= 0 && preferredIndex < picker.Items.Count)
                picker.SelectedIndex = preferredIndex;
            dialog.IsPrimaryButtonEnabled = picker.SelectedItem is ComboBoxItem { Tag: ModelQuantChoice choice } && choice.Complete;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (picker.SelectedItem is not ComboBoxItem { Tag: ModelQuantChoice selectedChoice } || !selectedChoice.Complete) return;
            // The owner explicitly chose this exact set. Keep the choice only
            // for this Model Hub session; do not auto-download or persist paths.
            _manuallyReviewedQuantByRepo[repo.RepoId] = selectedChoice.SetName;
            try { await ChooseQuantChoiceAsync(repo.RepoId, selectedChoice); }
            catch (Exception ex) { _status.Text = T("Could not review this variant: ", "Impossibile controllare questa variante: ") + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }
    }
}
