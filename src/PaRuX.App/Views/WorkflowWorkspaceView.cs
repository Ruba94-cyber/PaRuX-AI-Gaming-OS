using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Version-aware Workflow Builder surface. The Core never edits a workflow row: saving a
    /// revision creates a new immutable version linked through PreviousVersionId. This view makes
    /// that rule visible by grouping lineage, exposing history and allowing revisions only from the
    /// current leaf.
    /// </summary>
    public sealed class WorkflowWorkspaceView : UserControl
    {
        private static string T(string en, string it) => PaRuX.Services.UiText.Pick(en, it);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly StackPanel _families = new StackPanel { Spacing = 12 };
        private List<WorkflowDto> _snapshot = new();

        public WorkflowWorkspaceView()
        {
            AutomationProperties.SetAutomationId(_status, "Workflows_Status_Text");
            AutomationProperties.SetName(_status, T("Workflows status", "Stato dei workflow"));
            AutomationProperties.SetAutomationId(_busy, "Workflows_Busy_ProgressBar");
            AutomationProperties.SetName(_busy, T("Workflows loading indicator", "Indicatore di caricamento dei workflow"));
            AutomationProperties.SetAutomationId(_families, "Workflows_Families_List");
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAsync(); };
        }

        public async void OnShown() => await RefreshAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 16,
                Margin = new Thickness(24),
                MaxWidth = 1100,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = T("Workflows", "Workflow"), FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            AutomationProperties.SetAutomationId((FrameworkElement)header.Children[0], "Workflows_Title_Text");
            var refresh = new Button { Content = T("Refresh", "Aggiorna") };
            AutomationProperties.SetAutomationId(refresh, "Workflows_Refresh_Button");
            refresh.Click += async (_, _) => await RefreshAsync();
            Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
            body.Children.Add(header);
            body.Children.Add(new TextBlock
            {
                Text = T("Reusable AgentTask graphs with immutable version history. Revising never overwrites a version; every saved change becomes the next version in the same lineage.", "Grafi AgentTask riutilizzabili con cronologia di versioni immutabili. Rivedere non sovrascrive mai una versione; ogni modifica salvata diventa la versione successiva nella stessa linea."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            var create = new Button { Content = T("New workflow…", "Nuovo workflow…") };
            AutomationProperties.SetAutomationId(create, "Workflows_New_Button");
            create.Click += async (_, _) => await CreateOrReviseAsync(null);
            body.Children.Add(create);
            body.Children.Add(_busy);
            body.Children.Add(_status);
            body.Children.Add(_families);
            return scroll;
        }

        private async Task RefreshAsync()
        {
            SetBusy(true, "Loading workflow history…");
            try
            {
                _snapshot = await WorkflowClient.Current.ListAsync(CancellationToken.None);
                Render();
                _status.Text = _snapshot.Count == 0
                    ? T("No workflows yet.", "Nessun workflow per ora.")
                    : _snapshot.Count + T(" immutable workflow version(s) loaded.", " versioni di workflow immutabili caricate.");
            }
            catch (Exception ex) { _status.Text = T("Workflow refresh failed: ", "Aggiornamento dei workflow non riuscito: ") + ex.Message; }
            finally { SetBusy(false); }
        }

        private void Render()
        {
            _families.Children.Clear();
            if (_snapshot.Count == 0)
            {
                _families.Children.Add(new TextBlock { Text = T("Create a workflow to start a version lineage.", "Crea un workflow per iniziare una linea di versioni."), Opacity = 0.7 });
                return;
            }

            Dictionary<string, WorkflowDto> byId = _snapshot.ToDictionary(w => w.Id, StringComparer.Ordinal);
            var successorIds = new HashSet<string>(
                _snapshot.Where(w => !string.IsNullOrWhiteSpace(w.PreviousVersionId))
                    .Select(w => w.PreviousVersionId!), StringComparer.Ordinal);
            List<WorkflowDto> leaves = _snapshot.Where(w => !successorIds.Contains(w.Id))
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(w => w.VersionNumber)
                .ToList();

            foreach (WorkflowDto leaf in leaves)
            {
                List<WorkflowDto> lineage = BuildLineage(leaf, byId);
                _families.Children.Add(BuildFamilyCard(leaf, lineage));
            }
        }

        private Border BuildFamilyCard(WorkflowDto latest, IReadOnlyList<WorkflowDto> lineage)
        {
            var card = new Border { Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
            var body = new StackPanel { Spacing = 8 };
            card.Child = body;

            body.Children.Add(new TextBlock
            {
                Text = latest.Name + T(" · current v", " · attuale v") + latest.VersionNumber,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 17,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(latest.Description))
                body.Children.Add(new TextBlock { Text = latest.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.74 });
            body.Children.Add(new TextBlock
            {
                Text = latest.Steps.Count + T(" step(s) · ", " passaggi · ") + lineage.Count + T(" saved version(s) · updated ", " versioni salvate · aggiornato ") + latest.UpdatedUtc.ToLocalTime().ToString("g"),
                FontSize = 12,
                Opacity = 0.66
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var run = new Button { Content = T("Run current…", "Esegui la versione attuale…") };
            AutomationProperties.SetAutomationId(run, "Workflows_RunCurrent_Button");
            run.Click += async (_, _) => await RunAsync(latest);
            actions.Children.Add(run);
            var revise = new Button { Content = T("Revise as v", "Rivedi come v") + (latest.VersionNumber + 1) + "…" };
            AutomationProperties.SetAutomationId(revise, "Workflows_Revise_Button");
            revise.Click += async (_, _) => await CreateOrReviseAsync(latest);
            actions.Children.Add(revise);
            var delete = new Button { Content = T("Delete current leaf", "Elimina l'ultima versione attuale") };
            AutomationProperties.SetAutomationId(delete, "Workflows_DeleteLeaf_Button");
            delete.Click += async (_, _) => await DeleteLeafAsync(latest);
            actions.Children.Add(delete);
            body.Children.Add(actions);

            var history = new Expander { Header = T("Version history", "Cronologia delle versioni"), IsExpanded = false };
            AutomationProperties.SetAutomationId(history, "Workflows_VersionHistory_Expander");
            var versions = new StackPanel { Spacing = 7, Margin = new Thickness(4, 8, 0, 0) };
            foreach (WorkflowDto version in lineage.OrderByDescending(v => v.VersionNumber))
                versions.Children.Add(BuildVersionRow(version, version.Id == latest.Id));
            history.Content = versions;
            body.Children.Add(history);
            return card;
        }

        private UIElement BuildVersionRow(WorkflowDto workflow, bool current)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = "v" + workflow.VersionNumber + (current ? T(" · current", " · attuale") : string.Empty) + " · " + workflow.Name,
                FontWeight = current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            });
            text.Children.Add(new TextBlock
            {
                Text = workflow.Steps.Count + T(" step(s) · saved ", " passaggi · salvato ") + workflow.CreatedUtc.ToLocalTime().ToString("g")
                    + (string.IsNullOrWhiteSpace(workflow.PreviousVersionId) ? T(" · root", " · radice") : T(" · previous ", " · precedente ") + workflow.PreviousVersionId[..Math.Min(8, workflow.PreviousVersionId.Length)]),
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 0); grid.Children.Add(text);

            var run = new Button { Content = T("Run v", "Esegui la v") + workflow.VersionNumber, FontSize = 12 };
            AutomationProperties.SetAutomationId(run, "Workflows_RunVersion_Button");
            run.Click += async (_, _) => await RunAsync(workflow);
            Grid.SetColumn(run, 1); grid.Children.Add(run);
            return grid;
        }

        private readonly record struct WorkflowStarter(string Title, string StepsJson);

        /// <summary>Working step graphs to start from, so the first workflow does not begin with an
        /// empty JSON box. Each one uses roles that exist and dependencies that point backwards.</summary>
        private static readonly WorkflowStarter[] WorkflowStarters =
        {
            new("Plan, then build",
                "[\n  {\"name\":\"Plan\",\"goal\":\"Plan {{input}}\",\"role\":\"planner\",\"dependsOnStepIndexes\":[]},\n"
                + "  {\"name\":\"Execute\",\"goal\":\"Execute the plan for {{input}}\",\"role\":\"coding\",\"dependsOnStepIndexes\":[0]}\n]"),
            new("Research, then write it up",
                "[\n  {\"name\":\"Research\",\"goal\":\"Research {{input}} and collect the evidence\",\"role\":\"research\",\"dependsOnStepIndexes\":[]},\n"
                + "  {\"name\":\"Document\",\"goal\":\"Write up the findings on {{input}}\",\"role\":\"documentation\",\"dependsOnStepIndexes\":[0]}\n]"),
            new("Build, test, review",
                "[\n  {\"name\":\"Build\",\"goal\":\"Implement {{input}}\",\"role\":\"coding\",\"dependsOnStepIndexes\":[]},\n"
                + "  {\"name\":\"Test\",\"goal\":\"Write and run tests covering {{input}}\",\"role\":\"testing\",\"dependsOnStepIndexes\":[0]},\n"
                + "  {\"name\":\"Review\",\"goal\":\"Review the change for {{input}}\",\"role\":\"review\",\"dependsOnStepIndexes\":[1]}\n]"),
            new("Security review of a change",
                "[\n  {\"name\":\"Inspect\",\"goal\":\"Inspect {{input}} for security-relevant behaviour\",\"role\":\"security\",\"dependsOnStepIndexes\":[]},\n"
                + "  {\"name\":\"Report\",\"goal\":\"Report the security findings for {{input}}\",\"role\":\"documentation\",\"dependsOnStepIndexes\":[0]}\n]")
        };

        private async Task CreateOrReviseAsync(WorkflowDto? previous)
        {
            var name = new TextBox { Text = previous?.Name ?? string.Empty, PlaceholderText = T("Workflow name", "Nome del workflow") };
            var description = new TextBox { Text = previous?.Description ?? string.Empty, PlaceholderText = T("Optional description", "Descrizione (facoltativa)") };
            var steps = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 220,
                Text = previous == null
                    ? "[\n  {\"name\":\"Plan\",\"goal\":\"Plan {{input}}\",\"role\":\"planner\",\"dependsOnStepIndexes\":[]},\n  {\"name\":\"Execute\",\"goal\":\"Execute the plan for {{input}}\",\"role\":\"coding\",\"dependsOnStepIndexes\":[0]}\n]"
                    : JsonSerializer.Serialize(previous.Steps, JsonOptions)
            };
            AutomationProperties.SetAutomationId(name, "Workflows_Name_TextBox");
            AutomationProperties.SetAutomationId(description, "Workflows_Description_TextBox");
            AutomationProperties.SetAutomationId(steps, "Workflows_StepsJson_TextBox");
            AutomationProperties.SetName(steps, T("Workflow steps JSON", "JSON dei passaggi del workflow"));
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(name);
            panel.Children.Add(description);

            // Asking someone to hand-write a step graph as JSON is a steep first move for what is
            // otherwise an ordinary "do these things in order" idea. The presets fill the box with a
            // working graph to start from and edit; the JSON stays the source of truth.
            if (previous == null)
            {
                var starter = new ComboBox { PlaceholderText = T("Start from a template…", "Parti da un modello…"), MinWidth = 260 };
                foreach (WorkflowStarter template in WorkflowStarters)
                    starter.Items.Add(new ComboBoxItem { Content = template.Title, Tag = template.StepsJson });
                AutomationProperties.SetAutomationId(starter, "Workflows_Template_ComboBox");
                starter.SelectionChanged += (_, _) =>
                {
                    if (starter.SelectedItem is ComboBoxItem { Tag: string json }) steps.Text = json;
                };
                panel.Children.Add(starter);
            }

            panel.Children.Add(new TextBlock
            {
                Text = previous == null
                    ? T("Steps JSON. Dependencies may point only to earlier step indexes.", "JSON dei passaggi. Le dipendenze possono puntare solo a indici di passaggi precedenti.")
                    : T("Editing creates v", "La modifica crea la v") + (previous.VersionNumber + 1) + ". v" + previous.VersionNumber + T(" remains immutable and runnable.", " resta immutabile ed eseguibile."),
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(steps);

            var dialog = new ContentDialog
            {
                Title = previous == null ? T("Create workflow", "Crea il workflow") : T("Revise workflow · current v", "Rivedi il workflow · attuale v") + previous.VersionNumber,
                Content = panel,
                PrimaryButtonText = previous == null ? T("Create", "Crea") : T("Save new version", "Salva la nuova versione"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "Workflows_CreateRevise_Dialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                List<WorkflowStepTemplate>? parsed = JsonSerializer.Deserialize<List<WorkflowStepTemplate>>(steps.Text ?? string.Empty, JsonOptions);
                if (parsed == null || parsed.Count == 0) throw new InvalidDataException("At least one workflow step is required.");
                WorkflowDto created = await WorkflowClient.Current.CreateAsync(new CreateWorkflowRequest
                {
                    Name = (name.Text ?? string.Empty).Trim(),
                    Description = string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim(),
                    Steps = parsed,
                    PreviousVersionId = previous?.Id
                }, CancellationToken.None);
                _status.Text = previous == null
                    ? T("Workflow created as v1.", "Workflow creato come v1.")
                    : T("Saved immutable workflow v", "Workflow immutabile salvato: v") + created.VersionNumber + "; v" + previous.VersionNumber + T(" was preserved.", " è stata conservata.");
                await RefreshAsync();
            }
            catch (JsonException ex) { _status.Text = T("Workflow step JSON is invalid: ", "Il JSON dei passaggi del workflow non è valido: ") + ex.Message; }
            catch (Exception ex) { _status.Text = T("Workflow save rejected: ", "Salvataggio del workflow rifiutato: ") + ex.Message; }
        }

        private async Task RunAsync(WorkflowDto workflow)
        {
            var input = new TextBox { PlaceholderText = T("Optional {{input}} value", "Valore {{input}} facoltativo"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
            AutomationProperties.SetAutomationId(input, "Workflows_RunInput_TextBox");
            var dialog = new ContentDialog
            {
                Title = T("Run ", "Esegui ") + workflow.Name + " · v" + workflow.VersionNumber,
                Content = input,
                PrimaryButtonText = T("Create tasks", "Crea le attività"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                WorkflowRunResult result = await WorkflowClient.Current.RunAsync(workflow.Id, input.Text, CancellationToken.None);
                _status.Text = T("Workflow v", "Workflow v") + workflow.VersionNumber + T(" created ", " creato ") + result.CreatedTaskIds.Count + T(" real AgentTask(s).", " AgentTask reali.");
            }
            catch (Exception ex) { _status.Text = T("Workflow run failed: ", "Esecuzione del workflow non riuscita: ") + ex.Message; }
        }

        private async Task DeleteLeafAsync(WorkflowDto workflow)
        {
            var dialog = new ContentDialog
            {
                Title = T("Delete current workflow version?", "Eliminare la versione attuale del workflow?"),
                Content = T("Delete v", "Elimina la v") + workflow.VersionNumber + " of “" + workflow.Name + T("”? Earlier immutable versions remain. A version with a successor cannot be deleted.", "”? Le versioni immutabili precedenti restano. Una versione con un successore non può essere eliminata."),
                PrimaryButtonText = T("Delete current leaf", "Elimina l'ultima versione attuale"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await WorkflowClient.Current.DeleteAsync(workflow.Id, CancellationToken.None);
                _status.Text = T("Current workflow leaf deleted; previous version, if any, is current again.", "Ultima versione del workflow eliminata; la versione precedente, se esiste, torna attuale.");
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = T("Workflow delete failed: ", "Eliminazione del workflow non riuscita: ") + ex.Message; }
        }

        private static List<WorkflowDto> BuildLineage(WorkflowDto leaf, IReadOnlyDictionary<string, WorkflowDto> byId)
        {
            var result = new List<WorkflowDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            WorkflowDto? current = leaf;
            while (current != null && seen.Add(current.Id))
            {
                result.Add(current);
                if (string.IsNullOrWhiteSpace(current.PreviousVersionId)
                    || !byId.TryGetValue(current.PreviousVersionId, out WorkflowDto? previous))
                    break;
                current = previous;
            }
            return result;
        }

        private void SetBusy(bool busy, string? message = null)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message)) _status.Text = message;
        }
    }
}
