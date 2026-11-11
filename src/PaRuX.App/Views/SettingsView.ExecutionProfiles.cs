using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Desktop control plane for Core-owned cross-surface Execution Profiles. This UI
    /// chooses a durable quality envelope and shows/removes benchmark-derived model overrides; it
    /// deliberately does not reimplement budget validation or invent manual tuning values.</summary>
    public sealed partial class SettingsView
    {
        private bool _executionProfileSettingsAdded;
        private bool _executionProfileUiLoading;
        private ComboBox? _executionQualityCombo;
        private TextBlock? _executionProfileSummary;
        private TextBlock? _executionProfileStatus;
        private StackPanel? _executionOverrideList;

        private void AddExecutionProfileSettingsSection()
        {
            if (_executionProfileSettingsAdded || SettingsContentPanel == null) return;
            _executionProfileSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "PaRuX Platform",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "PaRuX Platform", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            section.Children.Add(new TextBlock
            {
                Text = T("EXECUTION PROFILES", "PROFILI DI ESECUZIONE"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = T("One Core-owned quality/resource envelope for Chat, Agents, Training, Media and the scheduler. Adaptive routing remains authoritative inside that envelope, so Maximum Quality does not make trivial chat waste hidden reasoning.", "Un unico involucro di qualità/risorse gestito dal Core per Chat, Agenti, Training, Media e scheduler. L'instradamento adattivo resta autorevole dentro quell'involucro, quindi Qualità massima non fa sprecare ragionamento nascosto alle chat banali."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _executionQualityCombo = new ComboBox { MinWidth = 190 };
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = T("Fast", "Veloce"), Tag = ExecutionQualityProfile.Fast });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = T("Balanced", "Bilanciato"), Tag = ExecutionQualityProfile.Balanced });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = T("Maximum quality", "Qualità massima"), Tag = ExecutionQualityProfile.MaximumQuality });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = T("Overnight", "Notturno"), Tag = ExecutionQualityProfile.Overnight });
            _executionQualityCombo.SelectionChanged += async (_, _) =>
            {
                if (_executionProfileUiLoading || _executionQualityCombo.SelectedItem is not ComboBoxItem item) return;
                string quality = item.Tag?.ToString() ?? ExecutionQualityProfile.Balanced;
                await SetExecutionQualityAsync(quality);
            };
            row.Children.Add(_executionQualityCombo);

            var refresh = new Button { Content = T("Refresh", "Aggiorna") };
            refresh.Click += async (_, _) => await RefreshExecutionProfilesAsync();
            row.Children.Add(refresh);
            content.Children.Add(row);

            _executionProfileSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
            content.Children.Add(_executionProfileSummary);
            _executionProfileStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            content.Children.Add(_executionProfileStatus);

            content.Children.Add(new TextBlock
            {
                Text = T("MODEL-SPECIFIC BENCHMARK OVERRIDES", "OVERRIDE DI BENCHMARK PER MODELLO"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            _executionOverrideList = new StackPanel { Spacing = 6 };
            content.Children.Add(_executionOverrideList);

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
            _ = RefreshExecutionProfilesAsync();
        }

        private async Task SetExecutionQualityAsync(string quality)
        {
            try
            {
                if (_executionProfileStatus != null) _executionProfileStatus.Text = T("Applying…", "Applicazione in corso…");
                await ExecutionProfileClient.Current.SetQualityAsync(quality, null, CancellationToken.None);
                await RefreshExecutionProfilesAsync();
                if (_executionProfileStatus != null) _executionProfileStatus.Text = T("Execution profile applied across PaRuX Core.", "Profilo di esecuzione applicato in tutto PaRuX Core.");
            }
            catch (Exception ex)
            {
                Log.Write("SetExecutionQualityAsync failed: " + ex.Message);
                if (_executionProfileStatus != null) _executionProfileStatus.Text = T("Couldn't apply Execution Profile: ", "Impossibile applicare il profilo di esecuzione: ") + ex.Message;
            }
        }

        private async Task RefreshExecutionProfilesAsync()
        {
            if (_executionQualityCombo == null || _executionProfileSummary == null || _executionOverrideList == null) return;
            try
            {
                ExecutionProfileStatus status = await ExecutionProfileClient.Current.GetAsync(null, CancellationToken.None);
                _executionProfileUiLoading = true;
                try
                {
                    ComboBoxItem? selected = _executionQualityCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), status.ActiveQualityProfile, StringComparison.Ordinal));
                    _executionQualityCombo.SelectedItem = selected ?? _executionQualityCombo.Items.OfType<ComboBoxItem>().ElementAtOrDefault(1);
                }
                finally { _executionProfileUiLoading = false; }

                ExecutionResourceBudget b = status.Effective.Budget;
                _executionProfileSummary.Text =
                    T($"Active: {status.ActiveQualityProfile} · wall clock {b.MaxWallClockMinutes} min · tools {b.MaxToolSteps:N0} · ", $"Attivo: {status.ActiveQualityProfile} · tempo reale {b.MaxWallClockMinutes} min · strumenti {b.MaxToolSteps:N0} · ") +
                    T($"parallel tasks {b.MaxParallelTasks} · subagents {b.MaxDirectSubagents} · verification {b.VerificationPasses} pass(es) · ", $"attività parallele {b.MaxParallelTasks} · subagenti {b.MaxDirectSubagents} · verifica {b.VerificationPasses} passaggi · ") +
                    T($"context {(b.ContextFraction * 100):0}% · artifact cap {FormatExecutionProfileBytes(b.MaxArtifactBytes)} · RAM ≤ {b.MaxSystemRamPercent}% · VRAM ≤ {b.MaxVramPercent}%.", $"contesto {(b.ContextFraction * 100):0}% · limite artefatti {FormatExecutionProfileBytes(b.MaxArtifactBytes)} · RAM ≤ {b.MaxSystemRamPercent}% · VRAM ≤ {b.MaxVramPercent}%.");

                _executionOverrideList.Children.Clear();
                if (status.ModelOverrides.Count == 0)
                {
                    _executionOverrideList.Children.Add(new TextBlock
                    {
                        Text = T("No model-specific override. PaRuX is using the deterministic profile defaults.", "Nessun override specifico del modello. PaRuX usa i valori predefiniti deterministici del profilo."), Opacity = 0.70
                    });
                }
                else
                {
                    foreach (ModelExecutionProfile model in status.ModelOverrides.OrderBy(x => x.ModelKey, StringComparer.OrdinalIgnoreCase))
                        _executionOverrideList.Children.Add(BuildExecutionOverrideRow(model));
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshExecutionProfilesAsync failed: " + ex.Message);
                _executionProfileSummary.Text = T("Execution Profiles unavailable: ", "Profili di esecuzione non disponibili: ") + ex.Message;
            }
        }

        private UIElement BuildExecutionOverrideRow(ModelExecutionProfile model)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = model.ModelKey, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = T($"{model.EvidenceSource} · context {model.PreferredContextTokens:N0} · reasoning {model.ReasoningEffort} · ", $"fonte {model.EvidenceSource} · contesto {model.PreferredContextTokens:N0} · ragionamento {model.ReasoningEffort} · ") +
                    T($"delegation threshold {model.DelegationThreshold} · {model.ToolStrategy} · {model.StructuredOutputStrategy}", $"soglia di delega {model.DelegationThreshold} · {model.ToolStrategy} · {model.StructuredOutputStrategy}"),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.70
            });
            grid.Children.Add(text);

            var remove = new Button { Content = T("Use defaults", "Usa i predefiniti"), Padding = new Thickness(8, 2, 8, 2) };
            string key = model.ModelKey;
            remove.Click += async (_, _) =>
            {
                try
                {
                    await ExecutionProfileClient.Current.DeleteModelOverrideAsync(key, CancellationToken.None);
                    await RefreshExecutionProfilesAsync();
                }
                catch (Exception ex)
                {
                    Log.Write("Delete execution model override failed: " + ex.Message);
                    if (_executionProfileStatus != null) _executionProfileStatus.Text = T("Couldn't remove override: ", "Impossibile rimuovere l'override: ") + ex.Message;
                }
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
            return grid;
        }

        private static string FormatExecutionProfileBytes(long bytes)
        {
            double value = Math.Max(0, bytes);
            string[] suffix = { "B", "KiB", "MiB", "GiB", "TiB" };
            int index = 0;
            while (value >= 1024 && index < suffix.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "0" : "0.#") + " " + suffix[index];
        }
    }
}
