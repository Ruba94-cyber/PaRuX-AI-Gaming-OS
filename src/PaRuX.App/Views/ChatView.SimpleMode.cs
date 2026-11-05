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
                foreach (ManagedModelInfo model in _chatModelCatalog.OrderBy(
                    m => ModelHubRowPresentation.CompactManualLabel(m.Alias, m.FileName), StringComparer.OrdinalIgnoreCase))
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
                    AutomationProperties.SetName(item, "Model " + ModelHubRowPresentation.CompactManualLabel(model.Alias, model.FileName)
                        + ". File " + model.FileName + " · " + model.Quantization);
                    item.Click += async (_, _) => await ApplyChatModelChoiceAsync(selector);
                    items.Add(item);
                }
            }

            items.Add(new MenuFlyoutSeparator());
            var more = new MenuFlyoutItem { Text = it ? "Scarica altri modelli…" : "Get more models…", Icon = new FontIcon { Glyph = "" } };
            AutomationProperties.SetAutomationId(more, "Chat_SimpleMode_ModelPicker_GetMore");
            more.Click += (_, _) => App.MainWin?.ShowModelHub();
            items.Add(more);
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
            _isSimpleMode = !advanced;
            CodeFolderButton.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            ProfileSelectorPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelSelector.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelRouteExplanationButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            QuickModelCompareButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            SimpleModeAnsweringModelPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            SimpleModeRuntimeHealthPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
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
                    string.Equals(badge, info.BadgeText, StringComparison.Ordinal) ? info.AccessibleName : "Answering model: " + badge);
            }
            if (SimpleModeAnsweringModelBadge != null)
            {
                ToolTipService.SetToolTip(SimpleModeAnsweringModelBadge, info.TooltipText);
            }
            if (SimpleModeRouteReasonButton != null)
            {
                ToolTipService.SetToolTip(SimpleModeRouteReasonButton, string.IsNullOrWhiteSpace(info.OneLineReason) ? "Why this model?" : info.OneLineReason);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRouteReasonButton, "Why this model? " + info.OneLineReason);
            }
        }

        private void RefreshCodeFolderLabel()
        {
            string? root = string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId)
                ? SettingsStore.Current.Data.AgentCodingWorkspaceRoot
                : ProjectWorkspaceSession.Current.RepositoryRoot;
            bool hasFolder = !string.IsNullOrWhiteSpace(root);
            string name = hasFolder ? Path.GetFileName(root!.TrimEnd(Path.DirectorySeparatorChar)) : string.Empty;
            CodeFolderLabel.Text = name.Length == 0 ? "Choose code folder" : name;
            ToolTipService.SetToolTip(CodeFolderButton,
                name.Length == 0 ? "Choose the repository PaRuX will work on" : root);
            // Icon-only button: without a name screen readers announced nothing at all.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CodeFolderButton,
                name.Length == 0 ? "Choose code folder" : "Code folder: " + name + ". Choose another");
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
            ShowTransientStatus("Code folder deselected.");
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
                    ShowTransientStatus("Code folder selected. Tell PaRuX what to change in this chat.");
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
            if (SimpleModeRuntimeHealthPanel == null) return;
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
