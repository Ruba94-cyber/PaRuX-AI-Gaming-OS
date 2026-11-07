using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private bool _modelSelectorLoaded;
        private bool _suppressModelSelectorEvents;
        private bool _modelRoutedSubscribed;
        private ChatModelRouteInfo? _lastChatModelRoute;

        internal void EnsureModelRoutingSubscribed()
        {
            if (_modelRoutedSubscribed) return;
            _modelRoutedSubscribed = true;
            ChatClient.Current.ModelRouted += OnChatModelRouted;
        }

        private async void ModelSelector_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureModelRoutingSubscribed();
            if (_modelSelectorLoaded) return;
            _modelSelectorLoaded = true;
            InitializeNativeCodingHandoffUpgrade();
            await LoadModelSelectorAsync();
        }

        private void OnChatModelRouted(ChatModelRouteInfo route)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _lastChatModelRoute = route;
                RefreshSimpleModeAnsweringModel();
                string profile = route.ManualLock
                    ? "Manual lock"
                    : "Auto · " + PrettyAutoProfile(route.AutoProfile);
                string capability = route.SelectedIntelligence.HasValue
                    ? " · fit estimate " + (route.SelectedIntelligence.Value * 100).ToString("0") + "%"
                    : string.Empty;
                string speed = route.TokensPerSecond.HasValue
                    ? " · bench " + route.TokensPerSecond.Value.ToString("0.0") + " tok/s"
                    : string.Empty;
                string selected = ChatModelRouteInfo.SanitizeModelName(route.SelectedModel);
                if (string.IsNullOrWhiteSpace(selected)) selected = "configured model";
                ShowTransientStatus(selected + " · " + profile + capability + speed);
                ToolTipService.SetToolTip(ModelSelector,
                    string.IsNullOrWhiteSpace(route.Reason)
                        ? "Last routed turn: " + selected + " · " + profile
                        : "Last routed turn: " + selected + " · " + profile + "\n\n" + route.Reason);
            });
        }

        private async void ModelRouteExplanationButton_Click(object sender, RoutedEventArgs e)
        {
            ChatModelRouteInfo? route = _lastChatModelRoute;
            if (route == null)
            {
                ChatMessage? lastAssistant = ChatSession.Current.Conversation?.Messages
                    .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.ModelId));
                if (lastAssistant != null)
                {
                    route = new ChatModelRouteInfo
                    {
                        SelectedModel = lastAssistant.ModelId!,
                        Reason = lastAssistant.FallbackReason ?? string.Empty
                    };
                }
            }

            var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
            foreach (ChatModelRouteExplanationLine line in ChatModelRouteExplanationBuilder.Build(route))
            {
                var label = new TextBlock
                {
                    Text = line.Label,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                };
                var value = new TextBlock
                {
                    Text = line.Value,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, line.Value);
                if (line.Label == "No route yet")
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(value, "Chat_ModelRoute_Explain_EmptyState_Text");
                content.Children.Add(label);
                content.Children.Add(value);
            }

            var dialog = new ContentDialog
            {
                Title = "Why this model?",
                Content = new ScrollViewer { Content = content, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private bool _quickModelCompareBusy;

        /// <summary>
        /// Explicit Advanced Chat action; model selection, comparison and cancellation all
        /// use the local Arena. Nothing changes the Chat router, active model or persona.
        /// Results are refreshed only by a user click, never by background polling.
        /// </summary>
        private async void QuickModelCompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (_quickModelCompareBusy) return;
            _quickModelCompareBusy = true;
            QuickModelCompareButton.IsEnabled = false;
            string L(string en, string it) => ChatItalian ? it : en;
            try
            {
                var models = _chatModelCatalog
                    .Where(m => !string.IsNullOrWhiteSpace(m.FileName))
                    .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList();
                if (models.Count < 2)
                {
                    ShowTransientStatus(L("Install two local models before comparing them.",
                        "Installa due modelli locali prima di confrontarli."));
                    return;
                }

                // The old two ComboBoxes instantiated one UI element for
                // every installed GGUF (twice). On 100+ models opening this
                // dialog was unnecessarily expensive and hard to navigate.
                // Reuse Chat's searchable bounded catalogue, with explicit
                // assignment of two stable filenames instead of aliases.
                var firstPage = ChatModelPickerPolicy.Browse(models, null, 0);
                string one = firstPage.Visible[0].FileName;
                string two = firstPage.Visible[1].FileName;
                int pageIndex = 0;
                var search = new TextBox
                {
                    Header = L("Find installed models", "Cerca modelli installati"),
                    PlaceholderText = L("Alias, filename or quantization", "Alias, file o quantizzazione"),
                    MinHeight = 40
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    search, "Chat_QuickCompare_Search");
                var sortPicker = new ComboBox
                {
                    Header = L("Order models by", "Ordina modelli per"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 40
                };
                sortPicker.Items.Add(new ComboBoxItem
                {
                    Content = L("Name", "Nome"), Tag = ChatModelBrowseSort.Name
                });
                sortPicker.Items.Add(new ComboBoxItem
                {
                    Content = L("Smallest file first", "File più piccolo prima"),
                    Tag = ChatModelBrowseSort.SmallestFileFirst
                });
                sortPicker.Items.Add(new ComboBoxItem
                {
                    Content = L("Previously measured speed", "Velocità misurata prima"),
                    Tag = ChatModelBrowseSort.MeasuredSpeedFirst
                });
                sortPicker.SelectedIndex = 0;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    sortPicker, "Chat_QuickCompare_Sort");
                var available = new ListView
                {
                    Height = 220,
                    SelectionMode = ListViewSelectionMode.Single,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    available, "Chat_QuickCompare_Choices");
                var previous = new Button { Content = L("Previous", "Precedente"), MinHeight = 40 };
                var next = new Button { Content = L("Next", "Successiva"), MinHeight = 40 };
                var position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    previous, "Chat_QuickCompare_PreviousPage");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    next, "Chat_QuickCompare_NextPage");
                var assignFirst = new Button
                {
                    Content = L("Use as model A", "Usa come modello A"), MinHeight = 40,
                    IsEnabled = false
                };
                var assignSecond = new Button
                {
                    Content = L("Use as model B", "Usa come modello B"), MinHeight = 40,
                    IsEnabled = false
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    assignFirst, "Chat_QuickCompare_FirstModel");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    assignSecond, "Chat_QuickCompare_SecondModel");
                var chosen = new TextBlock { TextWrapping = TextWrapping.Wrap };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    chosen, "Chat_QuickCompare_SelectedModels");
                var prompt = new TextBox
                {
                    Header = L("Same question for both models", "Stessa domanda per entrambi i modelli"),
                    PlaceholderText = L("Type a question to compare", "Scrivi una domanda da confrontare"),
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 80,
                    MaxHeight = 150,
                    MaxLength = 4000
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    prompt, "Chat_QuickCompare_Prompt");

                ContentDialog? choose = null;
                void RefreshAssignments()
                {
                    string NameOf(string file)
                    {
                        ManagedModelInfo? model = models.FirstOrDefault(m =>
                            string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase));
                        return model == null ? file : ModelHubRowPresentation.CompactManualLabel(
                            model.Alias, model.FileName);
                    }
                    bool valid = ChatModelPickerPolicy.CanCompare(models, one, two);
                    chosen.Text = L("Model A: ", "Modello A: ") + NameOf(one)
                        + "\n" + L("Model B: ", "Modello B: ") + NameOf(two)
                        + (valid ? string.Empty : "\n" +
                            L("Choose two different installed models.", "Scegli due modelli installati diversi."));
                    if (choose != null)
                        choose.IsPrimaryButtonEnabled = valid && !string.IsNullOrWhiteSpace(prompt.Text);
                }
                void RenderModels()
                {
                    ChatModelBrowseSort sort = (sortPicker.SelectedItem as ComboBoxItem)?.Tag
                        is ChatModelBrowseSort chosen ? chosen : ChatModelBrowseSort.Name;
                    ChatModelPickerPolicy.Page page = ChatModelPickerPolicy.Browse(
                        models, search.Text, pageIndex, sort);
                    pageIndex = page.PageIndex;
                    available.Items.Clear();
                    foreach (ManagedModelInfo model in page.Visible)
                    {
                        string label = ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName);
                        string size = model.SizeBytes > 0
                            ? ModelHubStartPresenter.FormatSize(model.SizeBytes) : "?";
                        string benchmark = ChatModelPickerPolicy.HasMeasuredSpeed(model)
                            ? " · " + model.LastBenchmarkTokensPerSecond.Value.ToString("0.0")
                                + L(" tok/s measured", " tok/s misurati")
                            : string.Empty;
                        var item = new ListViewItem
                        {
                            Content = new TextBlock
                            {
                                Text = label + " · " + model.Quantization + " · " + size + benchmark,
                                TextWrapping = TextWrapping.Wrap
                            },
                            Tag = model.FileName,
                            MinHeight = 40
                        };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,
                            label + " · " + model.Quantization);
                        available.Items.Add(item);
                    }
                    position.Text = page.TotalMatches + L(" models · page ", " modelli · pagina ")
                        + (page.PageIndex + 1) + "/" + page.TotalPages;
                    previous.IsEnabled = page.PageIndex > 0;
                    next.IsEnabled = page.PageIndex + 1 < page.TotalPages;
                    assignFirst.IsEnabled = false;
                    assignSecond.IsEnabled = false;
                }
                available.SelectionChanged += (_, _) =>
                {
                    bool hasSelection = available.SelectedItem is ListViewItem;
                    assignFirst.IsEnabled = hasSelection;
                    assignSecond.IsEnabled = hasSelection;
                };
                search.TextChanged += (_, _) => { pageIndex = 0; RenderModels(); };
                sortPicker.SelectionChanged += (_, _) => { pageIndex = 0; RenderModels(); };
                previous.Click += (_, _) => { pageIndex--; RenderModels(); };
                next.Click += (_, _) => { pageIndex++; RenderModels(); };
                assignFirst.Click += (_, _) =>
                {
                    if ((available.SelectedItem as ListViewItem)?.Tag is string file)
                    {
                        one = file;
                        RefreshAssignments();
                    }
                };
                assignSecond.Click += (_, _) =>
                {
                    if ((available.SelectedItem as ListViewItem)?.Tag is string file)
                    {
                        two = file;
                        RefreshAssignments();
                    }
                };
                prompt.TextChanged += (_, _) => RefreshAssignments();
                var paging = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8
                };
                paging.Children.Add(previous);
                paging.Children.Add(position);
                paging.Children.Add(next);
                var assigning = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8
                };
                assigning.Children.Add(assignFirst);
                assigning.Children.Add(assignSecond);
                var controls = new StackPanel { Spacing = 10, MaxWidth = 600 };
                controls.Children.Add(new TextBlock
                {
                    Text = L(
                        "Select a model below, assign it to A or B, and use the same question for both. Core runs them sequentially; the Chat model is unchanged. Speed shown only when previously benchmarked on this PC.",
                        "Seleziona un modello, assegnalo ad A o B e confronta la stessa domanda. Il Core li esegue in sequenza; il modello della Chat non cambia. La velocità appare solo se già misurata su questo PC."),
                    TextWrapping = TextWrapping.Wrap
                });
                controls.Children.Add(search);
                controls.Children.Add(sortPicker);
                controls.Children.Add(available);
                controls.Children.Add(paging);
                controls.Children.Add(assigning);
                controls.Children.Add(chosen);
                controls.Children.Add(prompt);
                RenderModels();
                RefreshAssignments();
                choose = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = L("Compare two local models", "Confronta due modelli locali"),
                    Content = new ScrollViewer { Content = controls, MaxHeight = 490 },
                    PrimaryButtonText = L("Start comparison", "Avvia confronto"),
                    CloseButtonText = L("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = false
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    choose, "Chat_QuickCompare_Dialog");
                if (await choose.ShowAsync() != ContentDialogResult.Primary) return;
                if (string.IsNullOrWhiteSpace(prompt.Text)
                    || !ChatModelPickerPolicy.CanCompare(models, one, two))
                {
                    ShowTransientStatus(L("Choose two different models and enter a question.",
                        "Scegli due modelli diversi e inserisci una domanda."));
                    return;
                }

                ArenaRunDto run = await ModelArenaClient.Current.StartQuickCompareAsync(
                    one, two, prompt.Text, 256, CancellationToken.None);
                await ShowQuickModelCompareResultsAsync(run.Id);
            }
            catch (Exception ex)
            {
                Log.Write("Quick model comparison action failed: " + ex.GetType().Name);
                ShowTransientStatus(L("Local model comparison is unavailable. Check Core, installed models and Gaming Mode.",
                    "Confronto locale non disponibile. Controlla Core, modelli installati e modalità Gaming."));
            }
            finally
            {
                _quickModelCompareBusy = false;
                QuickModelCompareButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Explicit user refresh. Never invents answers, quality rankings or GPU evidence.
        /// Closing leaves the Core run intact; Cancel sends the existing Arena cancel action.
        /// </summary>
        private async Task ShowQuickModelCompareResultsAsync(string runId)
        {
            string L(string en, string it) => ChatItalian ? it : en;
            for (int refreshCount = 0; refreshCount < 30; refreshCount++)
            {
                ArenaQuickCompareViewDto view =
                    await ModelArenaClient.Current.GetQuickCompareAsync(runId, ChatItalian, CancellationToken.None);

                var left = new TextBox
                {
                    Header = view.Left.ModelFileName + " — " + view.Left.StatusText,
                    Text = view.Left.AnswerAvailable ? view.Left.AnswerText :
                        L("No completed answer yet.", "Nessuna risposta completata."),
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    MinHeight = 150, MaxHeight = 320
                };
                var right = new TextBox
                {
                    Header = view.Right.ModelFileName + " — " + view.Right.StatusText,
                    Text = view.Right.AnswerAvailable ? view.Right.AnswerText :
                        L("No completed answer yet.", "Nessuna risposta completata."),
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    MinHeight = 150, MaxHeight = 320
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    left, "Chat_QuickCompare_LeftAnswer");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    right, "Chat_QuickCompare_RightAnswer");

                var columns = new Grid { ColumnSpacing = 10, MinWidth = 560 };
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(right, 1);
                columns.Children.Add(left);
                columns.Children.Add(right);

                var layout = new StackPanel { Spacing = 12 };
                layout.Children.Add(new TextBlock
                {
                    Text = view.StatusText,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                layout.Children.Add(columns);
                var result = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = L("Local model comparison", "Confronto modelli locali"),
                    Content = new ScrollViewer
                    {
                        Content = layout,
                        MaxHeight = 440,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                    },
                    PrimaryButtonText = L("Refresh answers", "Aggiorna risposte"),
                    SecondaryButtonText = L("Cancel run", "Annulla esecuzione"),
                    CloseButtonText = L("Close (run continues)", "Chiudi (l'esecuzione continua)"),
                    DefaultButton = ContentDialogButton.Close
                };
                result.Resources["ContentDialogMaxWidth"] = 760d;
                ContentDialogResult action = await result.ShowAsync();
                if (action == ContentDialogResult.Secondary)
                {
                    await ModelArenaClient.Current.CancelAsync(runId, CancellationToken.None);
                    return;
                }
                if (action != ContentDialogResult.Primary) return;
            }
            ShowTransientStatus(L("Refresh limit reached. Reopen comparison from the Arena history.",
                "Limite aggiornamenti raggiunto. Riapri il confronto dalla cronologia Arena."));
        }

        private static string PrettyAutoProfile(string profile) => profile switch
        {
            AutoIntelligenceProfile.MaxIntelligence => "Max intelligence",
            AutoIntelligenceProfile.Responsive => "Responsive",
            AutoIntelligenceProfile.Custom => "Custom",
            _ => "Balanced"
        };

        private async Task LoadModelSelectorAsync()
        {
            _suppressModelSelectorEvents = true;
            try
            {
                string selectedSelector = NormalizeSavedModelSelection(SettingsStore.Current.Data.ChatModelSelection);
                bool routingUnreadable = false;
                List<ManagedModelInfo> models;
                try
                {
                    ChatRoutingPreferenceDto corePreference =
                        await ModelRoutingClient.Current.GetChatPreferenceAsync(CancellationToken.None);
                    if (corePreference.Unreadable) routingUnreadable = true;
                    else
                    {
                        selectedSelector = NormalizeSavedModelSelection(corePreference.Selector);
                        SettingsStore.Current.Data.ChatModelSelection = selectedSelector;
                        SettingsStore.Current.Save();
                    }
                }
                catch (Exception ex) { Log.Write("Chat routing preference could not be read from Core: " + ex.Message); }

                try { models = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None); }
                catch (Exception ex)
                {
                    Log.Write("Chat model selector could not load managed models: " + ex.Message);
                    models = new List<ManagedModelInfo>();
                }

                _chatModelCatalog = models;
                ModelSelector.Items.Clear();
                AddModelChoice("Auto · Balanced", "parux-auto:balanced");
                AddModelChoice("Auto · Max intelligence", "parux-auto:max_intelligence");
                AddModelChoice("Auto · Responsive", "parux-auto:responsive");
                AddModelChoice("Auto · Custom…", "parux-auto:custom");
                foreach (ManagedModelInfo model in models.OrderBy(
                    m => string.IsNullOrWhiteSpace(m.Alias) ? m.FileName : m.Alias + " · " + m.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    // Keep the collapsed selector readable while preserving the exact manual pin.
                    string display = ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName);
                    var item = new ComboBoxItem
                    {
                        Content = "Manual · " + display,
                        Tag = "parux-manual:" + model.FileName
                    };
                    string fullDetails = model.FileName + " · " + model.Quantization;
                    ToolTipService.SetToolTip(item, fullDetails);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,
                        "Manual model " + display + ". File " + fullDetails);
                    ModelSelector.Items.Add(item);
                }

                ComboBoxItem? selected = ModelSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    string.Equals(item.Tag as string, selectedSelector, StringComparison.OrdinalIgnoreCase));
                if (selected == null && selectedSelector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase))
                {
                    string missing = selectedSelector.Substring("parux-manual:".Length);
                    selected = new ComboBoxItem
                    {
                        Content = "Manual · " + missing + " (missing)", Tag = selectedSelector, IsEnabled = false
                    };
                    ModelSelector.Items.Add(selected);
                }
                if (selected == null)
                {
                    selected = ModelSelector.Items.OfType<ComboBoxItem>().First();
                    SettingsStore.Current.Data.ChatModelSelection = selected.Tag as string ?? string.Empty;
                    SettingsStore.Current.Save();
                }
                ModelSelector.SelectedItem = selected;
                if (routingUnreadable)
                {
                    // Never present a corrupt saved preference as "Auto": say it is unreadable. Choosing any
                    // entry re-saves a valid preference.
                    var unreadable = new ComboBoxItem
                    {
                        Content = "Routing preference unreadable (re-select to repair)", Tag = selected.Tag, IsEnabled = false
                    };
                    ModelSelector.Items.Add(unreadable);
                    ModelSelector.SelectedItem = unreadable;
                }
            }
            finally { _suppressModelSelectorEvents = false; }
        }

        private void AddModelChoice(string label, string selector) =>
            ModelSelector.Items.Add(new ComboBoxItem { Content = label, Tag = selector });

        private async void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModelSelectorEvents) return;
            if (ModelSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string selector) return;
            selector = NormalizeSavedModelSelection(selector);
            try
            {
                if (selector == "parux-auto:custom")
                {
                    bool configured = await ConfigureCustomAutoAsync();
                    if (!configured) { await LoadModelSelectorAsync(); return; }
                }
                ChatRoutingPreferenceDto persisted = await ModelRoutingClient.Current.SetChatPreferenceAsync(
                    RequestForSelector(selector), CancellationToken.None);
                string canonical = NormalizeSavedModelSelection(persisted.Selector);
                SettingsStore.Current.Data.ChatModelSelection = canonical;
                SettingsStore.Current.Save();
                RefreshSimpleModeAnsweringModel();
                ShowTransientStatus(canonical.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase)
                    ? (ChatItalian ? "Modello fissato: " : "Model locked: ") + ModelHubRowPresentation.CompactManualLabel(
                        _chatModelCatalog.FirstOrDefault(m => string.Equals("parux-manual:" + m.FileName, canonical, StringComparison.OrdinalIgnoreCase))?.Alias,
                        canonical.Substring("parux-manual:".Length))
                    : (ChatItalian ? "Modello: Auto. PaRuX sceglie per te." : "Model: Auto. PaRuX picks for you."));
            }
            catch (Exception ex)
            {
                Log.Write("Chat model selection could not be persisted in Core: " + ex.Message);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapModelSelector(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
                await LoadModelSelectorAsync();
            }
        }

        private async Task<bool> ConfigureCustomAutoAsync()
        {
            AutoRoutingTuningDto tuning;
            try { tuning = await ModelRoutingClient.Current.GetAutoTuningAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("Could not load Custom AUTO tuning: " + ex.Message); return false; }

            NumberBox minFloor = AutoNumber("Minimum relative capability floor", tuning.MinimumCapabilityRatio, 0.35, 0.98, 0.01);
            NumberBox floorBoost = AutoNumber("Complexity → capability-floor boost", tuning.ComplexityFloorBoost, 0, 0.50, 0.01);
            NumberBox capability = AutoNumber("Base capability weight", tuning.BaseCapabilityWeight, 0.05, 0.90, 0.01);
            NumberBox performance = AutoNumber("Base performance / tok-s weight", tuning.BasePerformanceWeight, 0, 0.80, 0.01);
            NumberBox compatibility = AutoNumber("Hardware compatibility weight", tuning.CompatibilityWeight, 0.05, 0.40, 0.01);
            NumberBox reuse = AutoNumber("Already-loaded model reuse weight", tuning.LoadedReuseWeight, 0, 0.30, 0.01);
            NumberBox capabilityBoost = AutoNumber("Complexity → capability-weight boost", tuning.ComplexityCapabilityBoost, 0, 0.50, 0.01);
            NumberBox performancePenalty = AutoNumber("Complexity → performance-weight reduction", tuning.ComplexityPerformancePenalty, 0, 0.50, 0.01);

            var stack = new StackPanel { Spacing = 10, MaxWidth = 560 };
            stack.Children.Add(new TextBlock
            {
                Text = "Custom AUTO still estimates task difficulty first. These controls decide how aggressively PaRuX escalates toward stronger models versus keeping a sufficiently capable faster model. Hardware safety and exact Manual locks cannot be bypassed.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });
            foreach (NumberBox box in new[] { minFloor, floorBoost, capability, performance, compatibility, reuse, capabilityBoost, performancePenalty })
                stack.Children.Add(box);
            var reset = new Button { Content = "Reset fields to Balanced-like defaults", HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (_, _) =>
            {
                var d = new AutoRoutingTuningDto();
                minFloor.Value = d.MinimumCapabilityRatio; floorBoost.Value = d.ComplexityFloorBoost;
                capability.Value = d.BaseCapabilityWeight; performance.Value = d.BasePerformanceWeight;
                compatibility.Value = d.CompatibilityWeight; reuse.Value = d.LoadedReuseWeight;
                capabilityBoost.Value = d.ComplexityCapabilityBoost; performancePenalty.Value = d.ComplexityPerformancePenalty;
            };
            stack.Children.Add(reset);

            var dialog = new ContentDialog
            {
                Title = "Auto · Custom intelligence",
                Content = new ScrollViewer { Content = stack, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Save & use Custom",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            await ModelRoutingClient.Current.SetAutoTuningAsync(new SetAutoRoutingTuningRequest
            {
                MinimumCapabilityRatio = minFloor.Value,
                ComplexityFloorBoost = floorBoost.Value,
                BaseCapabilityWeight = capability.Value,
                BasePerformanceWeight = performance.Value,
                CompatibilityWeight = compatibility.Value,
                LoadedReuseWeight = reuse.Value,
                ComplexityCapabilityBoost = capabilityBoost.Value,
                ComplexityPerformancePenalty = performancePenalty.Value
            }, CancellationToken.None);
            return true;
        }

        private static NumberBox AutoNumber(string header, double value, double min, double max, double step) => new()
        {
            Header = header, Value = value, Minimum = min, Maximum = max, SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        private static SetChatRoutingPreferenceRequest RequestForSelector(string selector)
        {
            if (selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase))
                return new SetChatRoutingPreferenceRequest
                {
                    Mode = RouteMode.Manual,
                    ManualModelFileName = selector.Substring("parux-manual:".Length)
                };
            string profile = selector.StartsWith("parux-auto:", StringComparison.OrdinalIgnoreCase)
                ? selector.Substring("parux-auto:".Length) : AutoIntelligenceProfile.Balanced;
            return new SetChatRoutingPreferenceRequest { Mode = RouteMode.Auto, AutoProfile = profile };
        }

        private static string NormalizeSavedModelSelection(string value)
        {
            string selector = (value ?? string.Empty).Trim();
            if (selector.Equals("parux-auto:responsive", StringComparison.OrdinalIgnoreCase)) return "parux-auto:responsive";
            if (selector.Equals("parux-auto:max_intelligence", StringComparison.OrdinalIgnoreCase)) return "parux-auto:max_intelligence";
            if (selector.Equals("parux-auto:custom", StringComparison.OrdinalIgnoreCase)) return "parux-auto:custom";
            if (selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase)
                && selector.Length > "parux-manual:".Length)
                return "parux-manual:" + selector.Substring("parux-manual:".Length);
            return "parux-auto:balanced";
        }
    }
}
