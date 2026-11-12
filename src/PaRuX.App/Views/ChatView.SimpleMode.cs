using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private bool _simpleModeFolderEventsAttached;
        private bool _isSimpleMode;
        private List<ManagedModelInfo> _chatModelCatalog = new List<ManagedModelInfo>();
        private bool _allModelsBrowserOpen;

        private static bool ChatItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        // ---- Model picker (Simple mode) ------------------------------------------------------------------------------
        // The model badge opens this menu. It is a thin skin over the existing selector: choosing an entry selects the matching
        // ComboBox item, so persistence in Core, the manual lock and the way back to Auto are exactly what Advanced mode uses.

        private async void SimpleModeModelMenu_Opening(object? sender, object e)
        {
            bool loadedBefore = ModelSelector.Items.Count > 0;
            BuildSimpleModeModelMenu();
            if (loadedBefore) return;
            await LoadModelSelectorAsync();
            BuildSimpleModeModelMenu();
        }

        private void BuildSimpleModeModelMenu()
        {
            bool it = ChatItalian;
            IList<MenuFlyoutItemBase> items = SimpleModeModelMenu.Items;
            items.Clear();

            string current = NormalizeSavedModelSelection(SettingsStore.Current.Data.ChatModelSelection);
            bool auto = string.IsNullOrEmpty(current) || current.StartsWith("parux-auto:", StringComparison.OrdinalIgnoreCase);

            var autoItem = new RadioMenuFlyoutItem
            {
                Text = it ? "Auto · sceglie PaRuX (consigliato)" : "Auto · PaRuX picks (recommended)",
                GroupName = "ChatModel",
                IsChecked = auto
            };
            ToolTipService.SetToolTip(autoItem, it
                ? "PaRuX usa il modello più adatto a ogni domanda tra quelli installati."
                : "PaRuX uses the most suitable installed model for each question.");
            AutomationProperties.SetAutomationId(autoItem, "Chat_SimpleMode_ModelPicker_Auto");
            string autoSelector = auto && !string.IsNullOrEmpty(current) ? current : "parux-auto:balanced";
            autoItem.Click += async (_, _) => await ApplyChatModelChoiceAsync(autoSelector);
            items.Add(autoItem);

            items.Add(new MenuFlyoutSeparator());
            if (_chatModelCatalog.Count == 0)
            {
                items.Add(new MenuFlyoutItem { Text = it ? "Nessun modello installato" : "No models installed yet", IsEnabled = false });
            }
            else
            {
                items.Add(new MenuFlyoutItem { Text = it ? "I tuoi modelli (usa sempre quello scelto)" : "Your models (always uses the one you pick)", IsEnabled = false });
                // The default Chat picker must not become a 500-item flyout.
                // Keep the exact manually chosen model visible even when it
                // sorts outside the first twelve; the full list is searchable.
                string pinnedFile = current.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase)
                    ? current.Substring("parux-manual:".Length) : string.Empty;
                if (!string.IsNullOrEmpty(pinnedFile) && !_chatModelCatalog.Any(m =>
                        string.Equals(m.FileName, pinnedFile, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new MenuFlyoutItem
                    {
                        Text = it ? "Il modello fissato non è più disponibile" : "Pinned model is no longer available",
                        IsEnabled = false
                    });
                foreach (ManagedModelInfo model in ChatModelPickerPolicy.Quick(_chatModelCatalog, pinnedFile))
                {
                    string selector = "parux-manual:" + model.FileName;
                    string size = model.SizeBytes > 0 ? " · " + ModelHubStartPresenter.FormatSize(model.SizeBytes) : string.Empty;
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName) + size,
                        GroupName = "ChatModel",
                        IsChecked = string.Equals(current, selector, StringComparison.OrdinalIgnoreCase)
                    };
                    ToolTipService.SetToolTip(item, model.FileName + " · " + model.Quantization);
                    AutomationProperties.SetName(item, T("Model ", "Modello ") + ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName)
                        + ". File " + model.FileName + " · " + model.Quantization);
                    item.Click += async (_, _) => await ApplyChatModelChoiceAsync(selector);
                    items.Add(item);
                }
            }

            items.Add(new MenuFlyoutSeparator());
            if (_chatModelCatalog.Count > ChatModelPickerPolicy.QuickChoices)
            {
                var browse = new MenuFlyoutItem
                {
                    Text = it ? "Cerca tra tutti i modelli installati…" : "Find any installed model…"
                };
                AutomationProperties.SetAutomationId(browse, "Chat_SimpleMode_ModelPicker_BrowseAll");
                browse.Click += async (_, _) => await BrowseAllLocalModelsAsync();
                items.Add(browse);
            }
            var more = new MenuFlyoutItem { Text = it ? "Scarica altri modelli…" : "Get more models…", Icon = new FontIcon { Glyph = "" } };
            AutomationProperties.SetAutomationId(more, "Chat_SimpleMode_ModelPicker_GetMore");
            more.Click += (_, _) => App.MainWin?.ShowModelHub();
            items.Add(more);
        }

        /// <summary>
        /// Bounded, searchable local model browser. Selecting a row changes
        /// the same authoritative ModelSelector as the normal flyout;
        /// Cancel, filtering and changing page have no side effects.
        /// </summary>
        private async Task BrowseAllLocalModelsAsync()
        {
            if (_allModelsBrowserOpen) return;
            _allModelsBrowserOpen = true;
            string L(string en, string it) => ChatItalian ? it : en;
            try
            {
                var query = new TextBox
                {
                    PlaceholderText = L("Search name, filename or quantization",
                        "Cerca per nome, file o quantizzazione"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 44
                };
                AutomationProperties.SetAutomationId(query, "Chat_ModelBrowser_Filter_TextBox");
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
                    Content = L("Smallest download first (size only)", "File più piccolo prima (solo dimensione)"),
                    Tag = ChatModelBrowseSort.SmallestFileFirst
                });
                sortPicker.Items.Add(new ComboBoxItem
                {
                    Content = L("Previously measured speed first", "Velocità misurata in precedenza"),
                    Tag = ChatModelBrowseSort.MeasuredSpeedFirst
                });
                sortPicker.SelectedIndex = 0;
                AutomationProperties.SetAutomationId(sortPicker, "Chat_ModelBrowser_Sort_Combo");
                var choices = new ListView
                {
                    SelectionMode = ListViewSelectionMode.Single,
                    Height = 290, HorizontalAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetAutomationId(choices, "Chat_ModelBrowser_Choices_List");
                var previous = new Button { Content = L("Previous", "Precedente"), MinHeight = 40 };
                var next = new Button { Content = L("Next", "Successiva"), MinHeight = 40 };
                var position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                AutomationProperties.SetAutomationId(previous, "Chat_ModelBrowser_Previous_Button");
                AutomationProperties.SetAutomationId(next, "Chat_ModelBrowser_Next_Button");
                AutomationProperties.SetAutomationId(position, "Chat_ModelBrowser_Page_Text");
                int pageIndex = 0;
                ContentDialog? dialog = null;
                choices.SelectionChanged += (_, _) =>
                {
                    if (dialog != null)
                        dialog.IsPrimaryButtonEnabled = choices.SelectedItem is ListViewItem;
                };
                void Render()
                {
                    ChatModelBrowseSort sort = (sortPicker.SelectedItem as ComboBoxItem)?.Tag
                        is ChatModelBrowseSort chosen ? chosen : ChatModelBrowseSort.Name;
                    ChatModelPickerPolicy.Page page =
                        ChatModelPickerPolicy.Browse(_chatModelCatalog, query.Text, pageIndex, sort);
                    pageIndex = page.PageIndex;
                    choices.Items.Clear();
                    if (dialog != null) dialog.IsPrimaryButtonEnabled = false;
                    foreach (ManagedModelInfo model in page.Visible)
                    {
                        string display = ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName);
                        string size = model.SizeBytes > 0
                            ? ModelHubStartPresenter.FormatSize(model.SizeBytes) : "?";
                        var item = new ListViewItem
                        {
                            Content = new TextBlock
                            {
                                Text = display + " · " + model.Quantization + " · " + size
                                    + (ChatModelPickerPolicy.HasMeasuredSpeed(model)
                                        ? " · " + model.LastBenchmarkTokensPerSecond.Value.ToString("0.0")
                                            + L(" tok/s previously measured", " tok/s misurati in precedenza")
                                        : string.Empty),
                                TextWrapping = TextWrapping.Wrap
                            },
                            Tag = model.FileName, MinHeight = 44
                        };
                        ToolTipService.SetToolTip(item, model.FileName);
                        AutomationProperties.SetName(item,
                            L("Local model ", "Modello locale ") + display + " · " + model.Quantization);
                        choices.Items.Add(item);
                    }
                    previous.IsEnabled = page.PageIndex > 0;
                    next.IsEnabled = page.PageIndex + 1 < page.TotalPages;
                    position.Text = page.TotalMatches + L(" models · page ", " modelli · pagina ")
                        + (page.PageIndex + 1) + "/" + page.TotalPages;
                }
                query.TextChanged += (_, _) => { pageIndex = 0; Render(); };
                sortPicker.SelectionChanged += (_, _) => { pageIndex = 0; Render(); };
                previous.Click += (_, _) => { pageIndex--; Render(); };
                next.Click += (_, _) => { pageIndex++; Render(); };

                var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                pager.Children.Add(previous);
                pager.Children.Add(position);
                pager.Children.Add(next);

                var content = new StackPanel { Spacing = 12, MaxWidth = 570 };
                content.Children.Add(new TextBlock
                {
                    Text = L("Choose an installed local model. This does not download or start it until your next Chat turn.",
                        "Scegli un modello locale installato. Non viene scaricato né avviato fino al prossimo messaggio."),
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(query);
                content.Children.Add(sortPicker);
                content.Children.Add(choices);
                content.Children.Add(pager);
                Render();
                dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = L("Your installed models", "I tuoi modelli installati"),
                    IsPrimaryButtonEnabled = false,
                    Content = content,
                    PrimaryButtonText = L("Use this model", "Usa questo modello"),
                    CloseButtonText = L("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close
                };
                AutomationProperties.SetAutomationId(dialog, "Chat_ModelBrowser_Dialog");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                string? file = (choices.SelectedItem as ListViewItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(file))
                {
                    ShowTransientStatus(L("Select a model before confirming.",
                        "Seleziona un modello prima di confermare."));
                    return;
                }
                await ApplyChatModelChoiceAsync("parux-manual:" + file);
            }
            finally { _allModelsBrowserOpen = false; }
        }

        private async Task ApplyChatModelChoiceAsync(string selector)
        {
            ComboBoxItem? target = ModelSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, selector, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                ShowTransientStatus(ChatItalian ? "Modello non disponibile. Riapri il selettore." : "That model is not available. Reopen the picker.");
                return;
            }
            if (!ReferenceEquals(ModelSelector.SelectedItem, target)) ModelSelector.SelectedItem = target;
            await Task.CompletedTask;
        }

        /// <summary>"Model: Auto" until a manual choice is made; then the chosen model, marked as fixed.</summary>
        private string ApplyManualChoiceToBadge(string badgeText)
        {
            string selector = NormalizeSavedModelSelection(SettingsStore.Current.Data.ChatModelSelection);
            if (!selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase)) return badgeText;
            string file = selector.Substring("parux-manual:".Length);
            ManagedModelInfo? model = _chatModelCatalog.FirstOrDefault(m => string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase));
            string name = ModelHubRowPresentation.CompactManualLabel(model?.Alias, file);
            return (ChatItalian ? "Modello: " : "Model: ") + name + (ChatItalian ? " (fisso)" : " (fixed)");
        }

        public void ApplyInterfaceMode(bool advanced)
        {
            bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
            if (corporate) advanced = false; // Chat stays usable even with a legacy Advanced setting.
            _isSimpleMode = !advanced;
            CodeFolderButton.Visibility = corporate || advanced ? Visibility.Collapsed : Visibility.Visible;
            if (corporate)
            {
                HomeCodeSuggestion.Visibility = Visibility.Collapsed;
                HomeImageSuggestion.Visibility = Visibility.Collapsed;
                HomeHeadline.Text = ChatItalian ? "Come posso aiutarti?" : "How can I help?";
                HomeDescription.Text = ChatItalian
                    ? "Fai una domanda oppure usa i tuoi documenti. Tutto viene elaborato in locale sul PC."
                    : "Ask a question or work with your documents. Everything runs locally on this PC.";
                // Corporate ZIP ships text llama.cpp, not media/voice/coding engines.
                AttachButton.Visibility = Visibility.Collapsed;
                AttachAudioButton.Visibility = Visibility.Collapsed;
                MicButton.Visibility = Visibility.Collapsed;
                VoiceModeButton.Visibility = Visibility.Collapsed;
                TuningStatusHost.Visibility = Visibility.Collapsed;
                CodeFolderClearButton.Visibility = Visibility.Collapsed;
                ProfileSelectorScrollViewer.Visibility = Visibility.Collapsed;
            }
            ProfileSelectorPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelSelector.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelRouteExplanationButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            QuickModelCompareButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            SimpleModeAnsweringModelPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            // The Corporate CPU engine is shipped beside the executable and need not
            // appear as a Runtime Manager installation. Do not show a false
            // "runtime missing" badge derived from the standard runtime inventory.
            SimpleModeRuntimeHealthPanel.Visibility = corporate || advanced
                ? Visibility.Collapsed : Visibility.Visible;
            SkillSelector.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            if (!_simpleModeFolderEventsAttached)
            {
                _simpleModeFolderEventsAttached = true;
                ProjectWorkspaceSession.Current.Changed += (_, _) =>
                    _dispatcher.TryEnqueue(RefreshCodeFolderLabel);
            }
            RefreshCodeFolderLabel();
            RefreshSimpleModeAnsweringModel();
            RefreshSimpleModeRuntimeHealth();
        }

        public void RefreshSimpleModeAnsweringModel()
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

            AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
            if (SimpleModeAnsweringModelLabel != null)
            {
                string badge = ApplyManualChoiceToBadge(info.BadgeText);
                SimpleModeAnsweringModelLabel.Text = badge;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeAnsweringModelLabel,
                    string.Equals(badge, info.BadgeText, StringComparison.Ordinal) ? info.AccessibleName : T("Answering model: ", "Modello che risponde: ") + badge);
            }
            if (SimpleModeAnsweringModelBadge != null)
            {
                ToolTipService.SetToolTip(SimpleModeAnsweringModelBadge, info.TooltipText);
            }
            if (SimpleModeRouteReasonButton != null)
            {
                ToolTipService.SetToolTip(SimpleModeRouteReasonButton, string.IsNullOrWhiteSpace(info.OneLineReason) ? T("Why this model?", "Perché questo modello?") : info.OneLineReason);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRouteReasonButton, T("Why this model? ", "Perché questo modello? ") + info.OneLineReason);
            }
        }

        private void RefreshCodeFolderLabel()
        {
            string? root = string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId)
                ? SettingsStore.Current.Data.AgentCodingWorkspaceRoot
                : ProjectWorkspaceSession.Current.RepositoryRoot;
            bool hasFolder = !string.IsNullOrWhiteSpace(root);
            string name = hasFolder ? Path.GetFileName(root!.TrimEnd(Path.DirectorySeparatorChar)) : string.Empty;
            CodeFolderLabel.Text = name.Length == 0 ? T("Choose code folder", "Scegli la cartella di codice") : name;
            ToolTipService.SetToolTip(CodeFolderButton,
                name.Length == 0 ? T("Choose the repository PaRuX will work on", "Scegli il repository su cui lavorerà PaRuX") : root);
            // Icon-only button: without a name screen readers announced nothing at all.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CodeFolderButton,
                name.Length == 0 ? T("Choose code folder", "Scegli la cartella di codice") : T("Code folder: ", "Cartella di codice: ") + name + T(". Choose another", ". Sceglierne un'altra"));
            CodeFolderClearButton.Visibility = (CodeFolderButton.Visibility == Visibility.Visible && hasFolder)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void CodeFolderClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId))
            {
                ProjectWorkspaceSession.Current.SetRepositoryRoot(null);
            }
            SettingsStore.Current.Data.AgentCodingWorkspaceRoot = string.Empty;
            SettingsStore.Current.Save();
            RefreshCodeFolderLabel();
            ShowTransientStatus(T("Code folder deselected.", "Cartella di codice deselezionata."));
        }

        private async void CodeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            CodeFolderButton.IsEnabled = false;
            try
            {
                string? root = await CodeHandoffService.ChooseRepositoryAsync(ProjectWorkspaceSession.Current.ProjectId);
                if (root != null)
                {
                    CodingTaskSandboxWorkspacePolicy.TryBindConversationSandboxTasks(
                        ChatSession.Current.Conversation, root);
                    RefreshCodeFolderLabel();
                    ShowTransientStatus(T("Code folder selected. Tell PaRuX what to change in this chat.", "Cartella di codice selezionata. Dì a PaRuX cosa modificare in questa chat."));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Code folder selection failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapFolderSelection(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
            }
            finally { CodeFolderButton.IsEnabled = true; }
        }

        public async void RefreshSimpleModeRuntimeHealth()
        {
            if (PaRuX.Contracts.CorporateMode.IsActive
                || SimpleModeRuntimeHealthPanel == null) return;
            try
            {
                var components = await RuntimeManagerClient.Current.GetComponentsAsync(System.Threading.CancellationToken.None);
                _dispatcher.TryEnqueue(() => ApplySimpleModeRuntimeHealth(components));
            }
            catch (Exception ex)
            {
                Log.Write("ChatView RefreshSimpleModeRuntimeHealth failed: " + ex.Message);
                _dispatcher.TryEnqueue(() =>
                {
                    var fallback = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(null);
                    ApplySimpleModeRuntimeHealthInfo(fallback);
                });
            }
        }

        public void ApplySimpleModeRuntimeHealth(System.Collections.Generic.IReadOnlyList<PaRuX.Contracts.ComponentStatus>? components)
        {
            var info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);
            ApplySimpleModeRuntimeHealthInfo(info);
        }

        private void ApplySimpleModeRuntimeHealthInfo(SimpleModeRuntimeHealthInfo info)
        {
            if (SimpleModeRuntimeHealthLabel != null)
            {
                SimpleModeRuntimeHealthLabel.Text = info.BadgeText;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRuntimeHealthLabel, info.AccessibleName);
            }
            if (SimpleModeRuntimeHealthButton != null)
            {
                ToolTipService.SetToolTip(SimpleModeRuntimeHealthButton, info.TooltipText);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRuntimeHealthButton, info.AccessibleName);
            }
            if (SimpleModeRuntimeHealthIcon != null)
            {
                SimpleModeRuntimeHealthIcon.Glyph = info.IsHealthy ? "\uE73E" : (info.IsInstalled ? "\uE783" : "\uE711");
            }
        }

        private void SimpleModeRuntimeHealthButton_Click(object sender, RoutedEventArgs e)
        {
            App.MainWin?.ShowSettings();
        }
    }
}
