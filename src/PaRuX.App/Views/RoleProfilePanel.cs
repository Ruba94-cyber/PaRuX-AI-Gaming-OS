using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The "Use for" selector (Chat / Coding / Reasoning / Vision / Documents): shows the model PaRuX recommends for each use from
    /// what is already installed and this PC's hardware, and applies it through the existing role store. Shared by the Model Hub
    /// Start board and Settings. Nothing is downloaded; a role the person set is never replaced by "Apply recommended". The
    /// Advanced switch adds a per-row manual model choice. Decisions live in <see cref="RoleProfilePolicy"/> and
    /// <see cref="RoleProfilePresenter"/>; this class only reads the stores and draws.
    /// </summary>
    public sealed class RoleProfilePanel : UserControl
    {
        private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        private readonly StackPanel _rows = new StackPanel { Spacing = 8 };
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly Button _applyAll = new Button { IsEnabled = false };
        private readonly ToggleSwitch _advanced = new ToggleSwitch { OnContent = string.Empty, OffContent = string.Empty };
        private List<RoleModelCandidate> _installed = new List<RoleModelCandidate>();
        private RuntimeManagerHardwareStatus? _hardware;
        private List<RoleCurrentAssignment> _current = new List<RoleCurrentAssignment>();
        private RoleProfileBoard? _board;
        private int _generation;
        private bool _busy;
        private bool _suppress;

        public RoleProfilePanel(bool startAdvanced = false)
        {
            bool it = Italian;
            AutomationProperties.SetAutomationId(this, RoleProfileBoard.PanelAutomationId);
            AutomationProperties.SetAutomationId(_summary, RoleProfileBoard.SummaryAutomationId);
            AutomationProperties.SetAutomationId(_status, "RoleProfile_Status");
            AutomationProperties.SetAutomationId(_applyAll, RoleProfileBoard.ApplyAllAutomationId);
            AutomationProperties.SetName(_applyAll, RoleProfilePresenter.ApplyAllLabel(it));
            AutomationProperties.SetAutomationId(_advanced, RoleProfileBoard.AdvancedToggleAutomationId);
            AutomationProperties.SetName(_advanced, RoleProfilePresenter.AdvancedLabel(it));
            _applyAll.Content = RoleProfilePresenter.ApplyAllLabel(it);
            _applyAll.Click += async (_, _) => await ApplyAllAsync();
            _suppress = true;
            _advanced.IsOn = startAdvanced || CorporateMode.IsActive;
            _suppress = false;
            _advanced.Toggled += (_, _) => { if (!_suppress) Render(); };

            var root = new StackPanel { Spacing = 10 };
            var heading = new TextBlock { Text = RoleProfilePresenter.Heading(it), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
            root.Children.Add(heading);
            root.Children.Add(new TextBlock
            {
                Text = CorporateMode.IsActive
                    ? (it ? "Il motore CPU è già incluso. Seleziona il modello GGUF da usare per Chat; Documenti utilizza automaticamente lo stesso modello."
                          : "The CPU engine is already included. Choose a GGUF model for Chat; Documents automatically uses the same model.")
                    : RoleProfilePresenter.Intro(it),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.82
            });
            root.Children.Add(_summary);
            root.Children.Add(_rows);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            if (!CorporateMode.IsActive) footer.Children.Add(_applyAll);
            if (!CorporateMode.IsActive)
            {
                footer.Children.Add(new TextBlock { Text = RoleProfilePresenter.AdvancedLabel(it), VerticalAlignment = VerticalAlignment.Center });
                footer.Children.Add(_advanced);
            }
            root.Children.Add(footer);
            root.Children.Add(_status);
            Content = root;

            Loaded += async (_, _) => await RefreshAsync();
        }

        /// <summary>Re-reads installed models, hardware and role assignments, then redraws. Safe to call repeatedly.</summary>
        public async Task RefreshAsync()
        {
            int generation = Interlocked.Increment(ref _generation);
            try
            {
                Task<List<ManagedModelInfo>> modelsTask = ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None);
                Task<List<ModelRoleStatus>> rolesTask = ModelRolesClient.Current.GetRolesAsync(CancellationToken.None);
                Task<RuntimeManagerHardwareStatus?> hardwareTask = CorporateMode.IsActive
                    ? Task.FromResult<RuntimeManagerHardwareStatus?>(null)
                    : TryHardwareAsync();
                await Task.WhenAll(modelsTask, rolesTask, hardwareTask);
                if (generation != _generation) return;

                // Corporate's bundled CPU llama.cpp accepts text GGUFs, not vision
                // projectors or unrelated managed checkpoint files. Such files must
                // never be offered by the manual Chat model selector.
                _installed = modelsTask.Result
                    .Where(m => !CorporateMode.IsActive
                        || CorporateModelImportPolicy.IsAcceptableModel(m.FileName, m.SizeBytes))
                    .Select(ToCandidate).ToList();
                _hardware = hardwareTask.Result;
                _current = rolesTask.Result
                    .Where(r => string.Equals(r.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    .Select(r => new RoleCurrentAssignment(
                        r.Role, r.IsOverridden ? r.ModelPath : null,
                        r.IsOverridden && !string.Equals(r.OverrideOrigin, "auto-assigned", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                Render();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("Role profiles refresh failed: " + ex.GetType().Name + ": " + ex.Message);
                _status.Text = Italian ? "Non riesco a leggere i modelli ora. Riprova tra poco." : "Could not read the models right now. Try again shortly.";
            }
        }

        private static async Task<RuntimeManagerHardwareStatus?> TryHardwareAsync()
        {
            try { return await RuntimeManagerClient.Current.GetStatusAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private static RoleModelCandidate ToCandidate(ManagedModelInfo m)
        {
            string name = ModelHubRowPresentation.CompactManualLabel(m.Alias, m.FileName);
            IReadOnlyList<string> caps = ModelCategoryClassifier.CapabilitiesOf(m.FileName, m.Alias, m.RepoId);
            string lower = (m.FileName + " " + m.Alias).ToLowerInvariant();
            bool uncensored = lower.Contains("uncensored") || lower.Contains("abliterated") || lower.Contains("unfiltered");
            return new RoleModelCandidate(
                m.FilePath, name, m.SizeBytes,
                caps.Contains(ModelCategoryClassifier.Coding), caps.Contains(ModelCategoryClassifier.Reasoning),
                caps.Contains(ModelCategoryClassifier.Vision), !string.IsNullOrWhiteSpace(m.ProjectorFileName), uncensored,
                File.Exists(m.FilePath));
        }

        private void Render()
        {
            bool it = Italian;
            IReadOnlyList<RoleRecommendation> recs = RoleProfilePolicy.Recommend(_installed, _hardware, it);
            _board = RoleProfilePresenter.Build(recs, _current, _installed, _advanced.IsOn, it);
            if (CorporateMode.IsActive)
            {
                // Corporate runs one CPU text model. Vision/Coding/Reasoning slots
                // do not correspond to separate engines, so do not clutter the UI
                // or accidentally apply recommendations to unavailable roles.
                var relevant = _board.Rows
                    .Where(r => r.Profile is RoleProfile.Chat or RoleProfile.Documents)
                    .ToList();
                _board = new RoleProfileBoard(relevant,
                    it ? "La Chat utilizza un modello locale; i documenti usano la stessa Chat."
                       : "Chat uses one local model; documents use the same Chat model.",
                    relevant.Any(r => r.State == RoleRowState.CanApply), true);
            }
            _summary.Text = _board.Summary;
            _applyAll.IsEnabled = _board.CanApplyAll && !_busy;
            _rows.Children.Clear();
            foreach (RoleProfileRow row in _board.Rows) _rows.Children.Add(BuildRow(row, it));
        }

        private UIElement BuildRow(RoleProfileRow row, bool it)
        {
            var card = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = null
            };
            AutomationProperties.SetAutomationId(card, row.AutomationPrefix + "_Row");
            AutomationProperties.SetName(card, row.Title + ": " + row.RecommendedText + ". " + row.StateText);

            var stack = new StackPanel { Spacing = 4 };
            var top = new Grid { ColumnSpacing = 12 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = row.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var model = new TextBlock
            {
                Text = (it ? "Consigliato: " : "Recommended: ") + row.RecommendedText
                    + (row.State == RoleRowState.Unavailable ? string.Empty : " · " + (it ? "ora: " : "now: ") + row.CurrentText),
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetAutomationId(model, row.AutomationPrefix + "_Model");
            titles.Children.Add(model);
            Grid.SetColumn(titles, 0);
            top.Children.Add(titles);

            if (row.CanApply && PlanApplyRowOp(row) is RoleApplyOperation op)
            {
                var apply = new Button { Content = RoleProfilePresenter.ApplyLabel(it), IsEnabled = !_busy, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(apply, row.AutomationPrefix + "_Apply");
                AutomationProperties.SetName(apply, RoleProfilePresenter.ApplyLabel(it) + ": " + row.Title);
                apply.Click += async (_, _) => await ApplyAsync(new[] { op });
                Grid.SetColumn(apply, 1);
                top.Children.Add(apply);
            }
            stack.Children.Add(top);

            var explain = new TextBlock { Text = row.StateText + ". " + row.Explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.78, FontSize = 12 };
            AutomationProperties.SetAutomationId(explain, row.AutomationPrefix + "_Explain");
            stack.Children.Add(explain);

            if (row.AdvancedChoices.Count > 0)
            {
                var combo = new ComboBox
                {
                    MinWidth = 0, MaxWidth = 720, MinHeight = 44,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = it ? "Scegli un modello…" : "Choose a model…"
                };
                AutomationProperties.SetAutomationId(combo, row.AutomationPrefix + "_Override");
                AutomationProperties.SetName(combo, (it ? "Modello per " : "Model for ") + row.Title);
                foreach (RoleOverrideChoice choice in row.AdvancedChoices)
                {
                    var option = new ComboBoxItem { Content = choice.Name, Tag = choice.Path };
                    ToolTipService.SetToolTip(option, choice.Path);
                    // Stable id from the file name (not the localized label): RoleProfile_Coding_Override_<file>_Item.
                    string fileId = new string(Path.GetFileName(choice.Path).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    AutomationProperties.SetAutomationId(option, row.AutomationPrefix + "_Override_" + fileId + "_Item");
                    combo.Items.Add(option);
                }
                combo.SelectionChanged += async (_, _) =>
                {
                    if (combo.SelectedItem is not ComboBoxItem item) return;
                    RoleApplyOperation? chosen = RoleProfilePresenter.PlanOverride(row, item.Tag as string);
                    if (chosen != null) await ApplyAsync(new[] { chosen });
                };
                stack.Children.Add(combo);
            }

            card.Child = stack;
            return card;
        }

        private static RoleApplyOperation? PlanApplyRowOp(RoleProfileRow row) => RoleProfilePresenter.PlanApplyRow(row);

        private async Task ApplyAllAsync()
        {
            if (_board == null) return;
            await ApplyAsync(RoleProfilePresenter.PlanApplyAll(_board));
        }

        private async Task ApplyAsync(IReadOnlyList<RoleApplyOperation> operations)
        {
            if (_busy || operations.Count == 0) return;
            _busy = true;
            _applyAll.IsEnabled = false;
            bool it = Italian;
            int done = 0;
            string? failure = null;
            foreach (RoleApplyOperation op in operations)
            {
                try
                {
                    await ModelRolesClient.Current.SetRoleAsync(op.RoleKey, op.ModelPath, CancellationToken.None);
                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex.Message;
                    Log.Write("Role profile apply failed for " + op.RoleKey + ": " + ex.GetType().Name);
                    break;
                }
            }
            _busy = false;
            _status.Text = failure == null
                ? (it ? $"Fatto: {done} impostazione/i aggiornate. Nulla è stato scaricato o cancellato." : $"Done: {done} setting(s) updated. Nothing was downloaded or deleted.")
                : (it ? $"Applicate {done}; si è fermato su un errore: " : $"Applied {done}; stopped on an error: ") + failure;
            await RefreshAsync();
        }
    }
}
