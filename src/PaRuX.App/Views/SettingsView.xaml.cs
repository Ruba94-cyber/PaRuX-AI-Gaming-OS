using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Models;
using PaRuX.Contracts;
using PaRuX.Services;
using QRCoder;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Media.Core;
using PaRuX.Intelligence;

namespace PaRuX.Views
{
    public sealed partial class SettingsView : UserControl
    {
        private readonly DispatcherQueue _dispatcher;
        private DispatcherQueueTimer _diagTimer;
        private bool _loading;

        /// <summary>Switches being set back to the server's position; their Toggled handlers ignore that change.</summary>
        private readonly HashSet<ToggleSwitch> _switchesBeingReverted = new();
        private bool _remoteAccessEnabled;
        private bool _benchmarkPollActive;

        private static string T(string en, string it) => KT(en, it);

        public SettingsView()
        {
            InitializeComponent();
            ApplyStoreEditionRestrictions();
            InitializeCorporateSettingsLayout();
            ApplyProfileSettingsSections();
            LocalizeVideoPresets();

            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _diagTimer = _dispatcher.CreateTimer();
            _diagTimer.Interval = TimeSpan.FromSeconds(3);
            _diagTimer.Tick += OnDiagTick;

            DiagEndpoint.Text = T("Endpoint unavailable", "Endpoint non disponibile")
                              + T("   ·   settings at ", "   ·   impostazioni in ") + AppPaths.SettingsFile;

            AboutVersion.Text = "PaRuX · " + typeof(SettingsView).Assembly.GetName().Version;

            BuildSettingsCategoryRail();

            if (McpPresetCombo != null)
                foreach (McpServerPreset preset in McpServerPresetCatalog.All)
                    McpPresetCombo.Items.Add(new ComboBoxItem { Content = preset.DisplayName, Tag = preset });

            Loaded += (s, e) =>
            {
                LoadFromSettings();
                UpdateSettingsLayout(SettingsLayoutGrid.ActualWidth);
            };
        }

        // ------------------------------------------------------------------
        // Category rail - this page grew to ~30 sections over the course of the
        // session, past the point where one long scroll was usable. Sections keep
        // their existing markup and x:Name references untouched; each top-level
        // section's Tag carries its category, and switching categories just
        // toggles Visibility on SettingsContentPanel's direct children rather than
        // reparenting anything, so nothing here can go stale relative to the
        // sections themselves.
        // ------------------------------------------------------------------

        private static readonly string[] SettingsCategories =
        {
            "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation", "Automation", "Agents", "Integrations", "Gaming & AI Sharing", "PaRuX Platform", "Privacy & Network", "Data", "Advanced"
        };

        private static string CategoryLabel(string category) => category switch
        {
            "General" => T("General", "Generale"),
            "Models" => T("Models", "Modelli"),
            "Chat Behavior" => T("Chat Behavior", "Comportamento della chat"),
            "Your PC apps" => T("Your PC apps", "App del tuo PC"),
            "Media Generation" => T("Media Generation", "Generazione multimediale"),
            "Automation" => T("Automation", "Automazione"),
            "Agents" => T("Agents", "Agenti"),
            "Integrations" => T("Integrations", "Integrazioni"),
            "Gaming & AI Sharing" => T("Gaming & AI Sharing", "Gaming e condivisione IA"),
            "PaRuX Platform" => T("PaRuX Platform", "Piattaforma PaRuX"),
            "Privacy & Network" => T("Privacy & Network", "Privacy e rete"),
            "Data" => T("Data", "Dati"),
            "Advanced" => T("Advanced", "Avanzate"),
            _ => category
        };

        private readonly Dictionary<string, Button> _categoryButtons = new Dictionary<string, Button>();
        private string _selectedSettingsCategory = "General";
        private bool _syncingCompactSettingsCategory;
        private bool _settingsCategoryRefreshEnabled;

        private IReadOnlyList<string> AvailableSettingsCategories => SettingsCategoryPolicy.VisibleCategories(
            SettingsCategories, SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId),
            CorporateMode.IsActive);

        private void RefreshSettingsCategoriesForProfile()
        {
            // The search index includes labels from category sections. Rebuild it
            // after every profile change; otherwise a hidden Gaming/Media setting
            // can remain searchable in an otherwise allowed category.
            _settingsSearchIndex.Clear();
            IReadOnlyList<string> categories = AvailableSettingsCategories;
            if (_categoryButtons.Keys.SequenceEqual(categories, StringComparer.Ordinal)) return;
            bool wasRefreshing = _settingsCategoryRefreshEnabled;
            _settingsCategoryRefreshEnabled = false;
            try { BuildSettingsCategoryRail(); }
            finally { _settingsCategoryRefreshEnabled = wasRefreshing; }
        }

        private void BuildSettingsCategoryRail()
        {
            if (SettingsCategoryRail == null) return;
            SettingsCategoryRail.Children.Clear();
            _categoryButtons.Clear();
            SettingsCompactCategoryPicker.Items.Clear();

            IReadOnlyList<string> categories = AvailableSettingsCategories;
            if (!categories.Contains(_selectedSettingsCategory, StringComparer.Ordinal))
                _selectedSettingsCategory = categories.FirstOrDefault() ?? "General";
            foreach (string category in categories)
            {
                var button = new Button
                {
                    Content = CategoryLabel(category),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, 2)
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    button,
                    "Settings_Category_" + string.Concat(category.Where(char.IsLetterOrDigit)) + "_Button");
                string capturedCategory = category;
                button.Click += (s, e) => SelectSettingsCategory(capturedCategory);
                _categoryButtons[category] = button;
                SettingsCategoryRail.Children.Add(button);
                SettingsCompactCategoryPicker.Items.Add(new ComboBoxItem { Content = CategoryLabel(category), Tag = category });
            }

            SelectSettingsCategory(_selectedSettingsCategory);
        }

        // Search the labels that the existing settings UI actually displays. Keeping the
        // index derived from XAML avoids a second, stale list of setting names or paths.
        private readonly Dictionary<string, string> _settingsSearchIndex = new(StringComparer.Ordinal);

        private IReadOnlyList<string> MatchSettingsCategories(string? query)
        {
            string[] terms = (query ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length == 0) return AvailableSettingsCategories;
            if (_settingsSearchIndex.Count == 0 && SettingsContentPanel != null)
            {
                foreach (object child in SettingsContentPanel.Children)
                {
                    if (child is not FrameworkElement section || section.Tag is not string category
                        || IsSettingsSectionHidden(section)
                        || !AvailableSettingsCategories.Contains(category, StringComparer.Ordinal))
                        continue;
                    var labels = new List<string> { category };
                    AppendSearchLabels(section, labels, 0);
                    if (_settingsSearchIndex.TryGetValue(category, out string? existing))
                        _settingsSearchIndex[category] = existing + " " + string.Join(" ", labels);
                    else
                        _settingsSearchIndex[category] = string.Join(" ", labels);
                }
            }

            return AvailableSettingsCategories
                .Where(category =>
                {
                    string searchable = category + " " + CategoryLabel(category) + " "
                        + (_settingsSearchIndex.TryGetValue(category, out string? labels) ? labels : string.Empty);
                    return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();
        }

        private static void AppendSearchLabels(UIElement element, List<string> labels, int depth)
        {
            if (depth > 24) return;
            switch (element)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    labels.Add(text.Text);
                    break;
                case ToggleSwitch toggle when toggle.Header is string toggleHeader:
                    labels.Add(toggleHeader);
                    break;
                case TextBox box when box.Header is string boxHeader:
                    labels.Add(boxHeader);
                    break;
                case ComboBox combo when combo.Header is string comboHeader:
                    labels.Add(comboHeader);
                    break;
                case NumberBox number when number.Header is string numberHeader:
                    labels.Add(numberHeader);
                    break;
                case Expander expander when expander.Header is string expanderHeader:
                    labels.Add(expanderHeader);
                    break;
                case Button button when button.Content is string name:
                    labels.Add(name);
                    break;
            }
            if (element is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                    AppendSearchLabels(child, labels, depth + 1);
            }
            else if (element is Border border && border.Child is UIElement borderChild)
                AppendSearchLabels(borderChild, labels, depth + 1);
            else if (element is ContentControl content && content.Content is UIElement child)
                AppendSearchLabels(child, labels, depth + 1);
            else if (element is ScrollViewer scroll && scroll.Content is UIElement scrollChild)
                AppendSearchLabels(scrollChild, labels, depth + 1);
        }

        private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            string query = sender.Text?.Trim() ?? string.Empty;
            IReadOnlyList<string> matches = MatchSettingsCategories(query);
            sender.ItemsSource = query.Length == 0 ? null : matches;
            SettingsSearchFeedback.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            SettingsSearchFeedback.Text = matches.Count == 0
                ? T("No matching settings. Try a shorter term.", "Nessuna impostazione corrispondente. Prova un termine più breve.")
                : matches.Count + (matches.Count == 1 ? T(" matching category.", " categoria corrispondente.") : T(" matching categories.", " categorie corrispondenti."));
            foreach (KeyValuePair<string, Button> entry in _categoryButtons)
                entry.Value.Visibility = query.Length == 0 || matches.Contains(entry.Key)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string? selected = args.ChosenSuggestion as string
                ?? MatchSettingsCategories(sender.Text).FirstOrDefault();
            if (selected != null) OpenSettingsSearchResult(selected);
        }

        private void SettingsSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            // Keyboard arrows only preview suggestions. Navigate on QuerySubmitted,
            // otherwise merely highlighting a result unexpectedly changes pages.
            if (args.SelectedItem is string selected)
                sender.Text = selected;
        }

        private void OpenSettingsSearchResult(string category)
        {
            if (!_categoryButtons.ContainsKey(category)) return;
            SettingsSearchBox.Text = string.Empty;
            SettingsSearchBox.ItemsSource = null;
            foreach (Button button in _categoryButtons.Values)
                button.Visibility = Visibility.Visible;
            SelectSettingsCategory(category);
        }

        private void SelectSettingsCategory(string category)
        {
            if (!_categoryButtons.ContainsKey(category)) return;
            _selectedSettingsCategory = category;

            if (SettingsCompactCategoryPicker != null)
            {
                int selectedIndex = _categoryButtons.Keys.ToList().IndexOf(category);
                if (SettingsCompactCategoryPicker.SelectedIndex != selectedIndex)
                {
                    _syncingCompactSettingsCategory = true;
                    try { SettingsCompactCategoryPicker.SelectedIndex = selectedIndex; }
                    finally { _syncingCompactSettingsCategory = false; }
                }
            }

            foreach (KeyValuePair<string, Button> kvp in _categoryButtons)
            {
                bool selected = kvp.Key == category;
                kvp.Value.Background = selected
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 124, 108, 240))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                kvp.Value.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }

            if (SettingsContentPanel == null) return;
            foreach (object child in SettingsContentPanel.Children)
            {
                if (child is not FrameworkElement element) continue;
                string? tag = element.Tag as string;
                element.Visibility = !IsSettingsSectionHidden(element)
                    && (tag == null || tag == category)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            // A new category starts at its top; otherwise it inherits the previous category's scroll offset
            // and can open scrolled past its own heading.
            SettingsContentScroll?.ChangeView(null, 0, null, true);
            if (_settingsCategoryRefreshEnabled)
                RefreshVisibleSettingsCategory();
        }

        private void SettingsCompactCategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingCompactSettingsCategory) return;
            if (SettingsCompactCategoryPicker.SelectedItem is ComboBoxItem { Tag: string category })
                SelectSettingsCategory(category);
        }

        private void SettingsLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSettingsLayout(e.NewSize.Width);
        }

        private void UpdateSettingsLayout(double width)
        {
            // Preserve the existing category rail for normal windows and keyboard/UIA tests.
            // At narrow widths, a compact picker frees that column for actual settings controls.
            if (SettingsRailColumn == null || SettingsRailScroll == null || SettingsCompactCategoryPicker == null
                || SettingsContentScroll == null || width <= 0) return;
            bool compact = width < 900;
            SettingsRailColumn.Width = new GridLength(compact ? 0 : 192);
            SettingsRailScroll.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SettingsCompactCategoryPicker.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            SettingsContentScroll.Padding = compact
                ? new Thickness(12, 8, 12, 16)
                : new Thickness(24, 20, 24, 32);
        }

        /// <summary>Called by MainWindow each time the settings page is shown.</summary>
        public void OnShown()
        {
            // Category layout may change after setup switches, but its selection must
            // not trigger a second round of remote Core refreshes on every Settings open.
            _settingsCategoryRefreshEnabled = false;
            ApplyProfileSettingsSections();
            RefreshSettingsCategoriesForProfile();
            SelectSettingsCategory(_selectedSettingsCategory);
            LoadFromSettings();
            _settingsCategoryRefreshEnabled = true;
            // Only the selected category queries Core. The previous implementation launched
            // nearly forty unrelated network calls whenever Settings opened.
            RefreshVisibleSettingsCategory();
            if (!_diagTimer.IsRunning) _diagTimer.Start();
        }

        private void RefreshVisibleSettingsCategory()
        {
            // All refreshes use the existing Core clients and read-only permission boundaries.
            // Switching a category always reloads its state so hidden panels cannot become stale.
            switch (_selectedSettingsCategory)
            {
                case "General":
                    _ = RefreshDiagnosticsAsync();
                    break;
                case "Models":
                    if (CorporateMode.IsActive) break; // Already-bundled CPU; only compact model library entry is shown.
                    // The local AI root and runtime components panels live in this category (not "PaRuX Platform").
                    _ = RefreshLocalAiRootAsync();
                    _ = RefreshRuntimeComponentsAsync();
                    _ = RefreshCloudFallbackAsync(); // the cloud-fallback panel also lives in Models
                    _ = RefreshDetectedModelsAsync();
                    _ = RefreshDiscoveryPolicyAsync();
                    _ = RefreshAutoCheckAsync();
                    _ = RefreshExternalBackendAsync();
                    _ = RefreshModelBudgetAsync();
                    _ = RefreshRecommendedModelsAsync();
                    _ = RefreshManagedModelsAsync();
                    _ = RefreshModelBenchmarksAsync();
                    _ = RefreshCompareModelsAsync();
                    break;
                case "Chat Behavior":
                    _ = RefreshAutoFallbackAsync();
                    _ = RefreshPersonasAsync();
                    if (!CorporateMode.IsActive) _ = RefreshGroupChatModelsAsync();
                    _ = RefreshRagSourcesAsync();           // the RAG card lives in this category
                    _ = RefreshEmbeddingEngineStatusAsync();
                    break;
                case "Your PC apps":
                    _ = RefreshDesktopIntegrationsAsync();
                    break;
                case "Media Generation":
                    _ = RefreshDocumentOutputAsync();
                    _ = RefreshMediaLaneAsync();
                    _ = RefreshMediaCheckpointsAsync();
                    _ = RefreshRecommendedCheckpointsAsync();
                    _ = RefreshSpeechEngineStatusAsync();
                    _ = RefreshAvatarEngineStatusAsync();
                    _ = RefreshTrainingEngineStatusAsync();
                    _ = RefreshThreedEngineStatusAsync();
                    _ = RefreshDiffusersEngineStatusAsync();
                    break;
                case "Automation":
                    _ = RefreshCustomWorkflowsAsync();
                    _ = RefreshMcpServersAsync();
                    _ = RefreshSkillsAsync();
                    break;
                case "Agents":
                    _ = RefreshAgentTasksAsync();
                    // Waiting Autopilot runs must be actionable (Approve/Cancel) as soon as the category opens, not only
                    // after the owner presses "Refresh waiting runs".
                    _ = RefreshPendingAutopilotRunsAsync();
                    break;
                case "Integrations":
                    _ = RefreshApiKeysAsync();
                    _ = RefreshIntegrationsAsync();
                    break;
                case "Gaming & AI Sharing":
                    _ = RefreshGamingStatusAsync();
                    _ = RefreshConsoleDisplaysAsync();
                    break;
                case "PaRuX Platform":
                    break;
                case "Privacy & Network":
                    if (CorporateMode.IsActive) break; // Compact privacy information only; no network controllers.
                    _ = RefreshRemoteAccessAsync();
                    _ = RefreshInternetStatusAsync();
                    _ = RefreshWebAccessAsync();
                    break;
                case "Data":
                    _ = RefreshMemoryListAsync();
                    _ = RefreshRagSourcesAsync();
                    _ = RefreshEmbeddingEngineStatusAsync();
                    break;
                case "Advanced":
                    _ = RefreshDiagnosticsAsync();
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Personas (server-owned - see PersonaClient)
        // ------------------------------------------------------------------

        private async Task RefreshPersonasAsync()
        {
            if (PersonasList == null) return;
            PersonaListResponse response;
            try { response = await PersonaClient.Current.GetAllAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("RefreshPersonasAsync failed: " + ex.Message); return; }

            PersonasList.Children.Clear();
            foreach (Persona persona in response.Personas)
            {
                bool isActive = persona.Id == response.ActivePersonaId;

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                var name = new TextBlock { Text = persona.Name + (persona.IsBuiltIn ? "" : T("  ·  custom", "  ·  personalizzato")) };
                ApplyBodyStrongStyle(name);
                text.Children.Add(name);
                if (!string.IsNullOrEmpty(persona.SystemPrompt))
                {
                    var prompt = new TextBlock { Text = persona.SystemPrompt, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 };
                    ApplyCaptionStyle(prompt);
                    text.Children.Add(prompt);
                }
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                if (isActive)
                {
                    var badge = new TextBlock { Text = T("Active", "Attivo"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                    ApplyCaptionStyle(badge);
                    if (Application.Current.Resources.TryGetValue("PaRuXSuccessColor", out object okColor) && okColor is Windows.UI.Color okC)
                        badge.Foreground = new SolidColorBrush(okC);
                    Grid.SetColumn(badge, 1);
                    row.Children.Add(badge);
                }
                else
                {
                    string capturedId = persona.Id;
                    var setActive = new Button { Content = T("Set active", "Imposta come attivo"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    setActive.Click += async (s, e2) => await SetActivePersonaAsync(capturedId);
                    Grid.SetColumn(setActive, 1);
                    row.Children.Add(setActive);
                }

                if (!persona.IsBuiltIn)
                {
                    string capturedId = persona.Id;
                    var delete = new Button { Content = T("Delete", "Elimina"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    delete.Click += async (s, e2) => await DeletePersonaAsync(capturedId);
                    Grid.SetColumn(delete, 2);
                    row.Children.Add(delete);
                }

                PersonasList.Children.Add(row);
            }
        }

        private async Task SetActivePersonaAsync(string id)
        {
            try { await PersonaClient.Current.SetActiveAsync(id, CancellationToken.None); }
            catch (Exception ex) { Log.Write("SetActivePersonaAsync failed: " + ex.Message); }
            await RefreshPersonasAsync();
        }

        private async Task DeletePersonaAsync(string id)
        {
            try { await PersonaClient.Current.DeleteAsync(id, CancellationToken.None); }
            catch (Exception ex) { Log.Write("DeletePersonaAsync failed: " + ex.Message); }
            await RefreshPersonasAsync();
        }

        /// <summary>Imports a SillyTavern/Chub character card (.json or .png with an embedded card) as a persona.</summary>
        private async void ImportPersonaCard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null) return;
                byte[] data = await System.IO.File.ReadAllBytesAsync(file.Path);
                Persona imported = await PersonaClient.Current.ImportCardAsync(file.Name, data, CancellationToken.None);
                if (PersonaStatus != null) PersonaStatus.Text = T("Imported \"", "Importato \"") + imported.Name + T("\" from ", "\" da ") + file.Name + T(". Set it active to use it in Chat.", ". Impostalo come attivo per usarlo in Chat.");
                await RefreshPersonasAsync();
            }
            catch (Exception ex)
            {
                Log.Write("ImportPersonaCard failed: " + ex.Message);
                if (PersonaStatus != null) PersonaStatus.Text = T("Couldn't import the card: ", "Impossibile importare la scheda: ") + ex.Message;
            }
        }

        private async void CreatePersona_Click(object sender, RoutedEventArgs e)
        {
            string? name = (NewPersonaNameBox?.Text ?? string.Empty).Trim();
            string? prompt = (NewPersonaPromptBox?.Text ?? string.Empty).Trim();
            if (name.Length == 0 || prompt.Length == 0)
            {
                if (PersonaStatus != null) PersonaStatus.Text = T("Both a name and a system-prompt instruction are required.", "Sono necessari sia un nome che un'istruzione di prompt di sistema.");
                return;
            }
            try
            {
                await PersonaClient.Current.CreateAsync(name, prompt, CancellationToken.None);
                if (NewPersonaNameBox != null) NewPersonaNameBox.Text = string.Empty;
                if (NewPersonaPromptBox != null) NewPersonaPromptBox.Text = string.Empty;
                if (PersonaStatus != null) PersonaStatus.Text = T("Created \"", "Creato \"") + name + "\".";
                await RefreshPersonasAsync();
            }
            catch (Exception ex)
            {
                Log.Write("CreatePersona failed: " + ex.Message);
                if (PersonaStatus != null) PersonaStatus.Text = T("Couldn't create persona: ", "Impossibile creare la persona: ") + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // MCP servers (server-owned - see McpServersClient)
        // ------------------------------------------------------------------

        private async Task RefreshMcpServersAsync()
        {
            if (McpServersList == null) return;
            List<McpServerStatus> statuses;
            try { statuses = await McpServersClient.Current.GetAllAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("RefreshMcpServersAsync failed: " + ex.Message); return; }

            McpServersList.Children.Clear();
            if (statuses.Count == 0)
            {
                var empty = new TextBlock { Text = T("No MCP servers configured yet.", "Nessun server MCP ancora configurato.") };
                ApplyCaptionStyle(empty);
                McpServersList.Children.Add(empty);
                return;
            }

            foreach (McpServerStatus status in statuses)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                var name = new TextBlock { Text = status.Name };
                ApplyBodyStrongStyle(name);
                text.Children.Add(name);
                var command = new TextBlock { Text = status.Command + " " + status.Args, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(command);
                text.Children.Add(command);

                string state;
                if (!status.Enabled) state = "Disabled";
                else if (status.Connected) state = "Connected · " + status.ToolCount + " tool(s)" + (status.ToolNames.Count > 0 ? ": " + string.Join(", ", status.ToolNames) : string.Empty);
                else state = "Not reachable" + (string.IsNullOrEmpty(status.LastError) ? string.Empty : ": " + status.LastError);
                var stateText = new TextBlock { Text = state, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(stateText);
                if (status.Enabled && status.Connected
                    && Application.Current.Resources.TryGetValue("PaRuXSuccessColor", out object okColor) && okColor is Windows.UI.Color okC)
                    stateText.Foreground = new SolidColorBrush(okC);
                text.Children.Add(stateText);

                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                string capturedId = status.Id;
                bool capturedEnabled = status.Enabled;
                var toggle = new ToggleSwitch { IsOn = status.Enabled, OnContent = T("On", "Attivo"), OffContent = T("Off", "Disattivo") };
                toggle.Toggled += async (s, e2) =>
                {
                    if (_switchesBeingReverted.Contains(toggle)) return;
                    bool requested = toggle.IsOn;
                    try { await McpServersClient.Current.SetEnabledAsync(capturedId, requested, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        Log.Write("Mcp SetEnabled failed: " + ex.Message);
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(toggle, requested, _switchesBeingReverted,
                            async ct => (await McpServersClient.Current.GetAllAsync(ct)).FirstOrDefault(server => server.Id == capturedId)?.Enabled);
                        ShowNotice(McpStatus, SwitchRevert.Explain("this MCP server", requested, ex.Message, recovered));
                    }
                    await RefreshMcpServersAsync();
                };
                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);

                var remove = new Button { Content = T("Remove", "Rimuovi"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                remove.Click += async (s, e2) =>
                {
                    try { await McpServersClient.Current.RemoveAsync(capturedId, CancellationToken.None); }
                    catch (Exception ex) { Log.Write("Mcp Remove failed: " + ex.Message); }
                    await RefreshMcpServersAsync();
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                McpServersList.Children.Add(row);
            }
        }

        private void McpPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (McpPresetCombo?.SelectedItem is not ComboBoxItem { Tag: McpServerPreset preset }) return;
            if (NewMcpNameBox != null) NewMcpNameBox.Text = preset.Name;
            if (NewMcpCommandBox != null) NewMcpCommandBox.Text = preset.Command;
            if (NewMcpArgsBox != null) NewMcpArgsBox.Text = preset.Args;
        }

        private async void AddMcpServer_Click(object sender, RoutedEventArgs e)
        {
            string? name = (NewMcpNameBox?.Text ?? string.Empty).Trim();
            string? command = (NewMcpCommandBox?.Text ?? string.Empty).Trim();
            string? args = (NewMcpArgsBox?.Text ?? string.Empty).Trim();
            if (name.Length == 0 || command.Length == 0)
            {
                if (McpStatus != null) McpStatus.Text = T("Both a name and a launch command are required.", "Servono sia un nome sia un comando di avvio.");
                return;
            }
            try
            {
                await McpServersClient.Current.AddAsync(name, command, args, CancellationToken.None);
                if (NewMcpNameBox != null) NewMcpNameBox.Text = string.Empty;
                if (NewMcpCommandBox != null) NewMcpCommandBox.Text = string.Empty;
                if (NewMcpArgsBox != null) NewMcpArgsBox.Text = string.Empty;
                if (McpPresetCombo != null) McpPresetCombo.SelectedItem = null;
                if (McpStatus != null) McpStatus.Text = T("Added \"", "Aggiunto \"") + name + T("\" - checking connection…", "\" - controllo della connessione…");
                await RefreshMcpServersAsync();
                if (McpStatus != null) McpStatus.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("AddMcpServer failed: " + ex.Message);
                if (McpStatus != null) McpStatus.Text = T("Couldn't add server: ", "Impossibile aggiungere il server: ") + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // RAG (server-owned - see RagClient)
        // ------------------------------------------------------------------

        private async Task RefreshRagSourcesAsync()
        {
            if (RagSourcesList == null) return;
            List<RagSourceDto> sources;
            try { sources = await RagClient.Current.ListSourcesAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("RefreshRagSourcesAsync failed: " + ex.Message); return; }

            RagSourcesList.Children.Clear();
            _knowledgeRows.Clear();
            _ = EnsureKnowledgeSettingsPanelAsync();
            if (sources.Count == 0)
            {
                var empty = new TextBlock { Text = T("No folders indexed yet.", "Nessuna cartella ancora indicizzata.") };
                ApplyCaptionStyle(empty);
                RagSourcesList.Children.Add(empty);
                return;
            }

            foreach (RagSourceDto source in sources)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                var path = new TextBlock { Text = source.FolderPath, TextWrapping = TextWrapping.Wrap };
                text.Children.Add(path);
                var meta = new TextBlock { Text = source.DocumentCount + T(" documents · ", " documenti · ") + source.ChunkCount + T(" chunks", " frammenti") };
                ApplyCaptionStyle(meta);
                text.Children.Add(meta);
                AddKnowledgeStatusRows(text, source);
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                var enabledSwitch = new ToggleSwitch { IsOn = source.Enabled, OnContent = "On", OffContent = T("Off", "No") };
                enabledSwitch.Toggled += async (s, e) =>
                {
                    if (_switchesBeingReverted.Contains(enabledSwitch)) return;
                    bool requested = enabledSwitch.IsOn;
                    try { await RagClient.Current.SetEnabledAsync(source.Id, requested, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        Log.Write("RAG SetEnabled failed: " + ex.Message);
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(enabledSwitch, requested, _switchesBeingReverted,
                            async ct => (await RagClient.Current.ListSourcesAsync(ct)).FirstOrDefault(item => item.Id == source.Id)?.Enabled);
                        ShowNotice(RagIndexStatus, SwitchRevert.Explain("this folder", requested, ex.Message, recovered));
                    }
                };
                Grid.SetColumn(enabledSwitch, 1);
                row.Children.Add(enabledSwitch);

                var reindex = new Button { Content = T("Reindex", "Reindicizza") };
                reindex.Click += async (s, e) => await ReindexRagSourceAsync(source);
                Grid.SetColumn(reindex, 2);
                row.Children.Add(reindex);

                var remove = new Button { Content = T("Remove", "Rimuovi") };
                remove.Click += async (s, e) =>
                {
                    try { await RagClient.Current.RemoveSourceAsync(source.Id, CancellationToken.None); }
                    catch (Exception ex) { Log.Write("RAG RemoveSource failed: " + ex.Message); }
                    await RefreshRagSourcesAsync();
                };
                Grid.SetColumn(remove, 3);
                row.Children.Add(remove);

                RagSourcesList.Children.Add(row);
            }
        }

        /// <summary>Shared by Memory + RAG (both retrieve through the same server-side
        /// EmbeddingService) - installing it here upgrades both at once, hence the explanation
        /// mentioning both rather than living solely under either card.</summary>
        private async Task RefreshEmbeddingEngineStatusAsync()
        {
            if (EmbeddingEngineStatusText == null) return;
            try
            {
                EmbeddingEngineStatus status = await EmbeddingClient.Current.GetStatusAsync(CancellationToken.None);
                if (status.Loaded)
                {
                    EmbeddingEngineStatusText.Text = T("Installed and active (", "Installato e attivo (") + status.Dimensions + T("-dim) - Memory and RAG now rank by real semantic similarity, not just keyword overlap.", "-dim) - Memoria e RAG ora ordinano per reale somiglianza semantica, non solo per parole chiave.");
                    if (InstallEmbeddingEngineButton != null) InstallEmbeddingEngineButton.Visibility = Visibility.Collapsed;
                }
                else if (status.Installed)
                {
                    EmbeddingEngineStatusText.Text = T("Installed but failed to load", "Installato ma caricamento non riuscito") + (string.IsNullOrEmpty(status.LastError) ? "." : ": " + status.LastError);
                    if (InstallEmbeddingEngineButton != null) InstallEmbeddingEngineButton.Visibility = Visibility.Visible;
                }
                else
                {
                    EmbeddingEngineStatusText.Text = T("Not installed - Memory and RAG currently rank by keyword overlap only. Installing adds real semantic search for both, entirely local (sentence-transformers/all-MiniLM-L6-v2, ~90 MB, one-time download, verified against a pinned SHA-256).", "Non installato - Memoria e RAG attualmente ordinano solo per sovrapposizione di parole chiave. L'installazione aggiunge la vera ricerca semantica per entrambi, interamente locale (sentence-transformers/all-MiniLM-L6-v2, ~90 MB, download singolo, verificato tramite SHA-256 bloccato).")
                        + (_embeddingInstallFailure == null ? string.Empty : (T(" Last install attempt failed: ", " Ultimo tentativo di installazione non riuscito: ") + _embeddingInstallFailure));
                    if (InstallEmbeddingEngineButton != null) InstallEmbeddingEngineButton.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshEmbeddingEngineStatusAsync failed: " + ex.Message);
                EmbeddingEngineStatusText.Text = T("Could not reach the embedding engine: ", "Impossibile raggiungere il motore di embedding: ") + ex.Message;
            }
        }

        private string? _embeddingInstallFailure;

        /// <summary>Microsoft Store edition: PaRuX does not download or install programs, so the engine installers and the
        /// runtime updater are hidden (the Core refuses them too). Locating software the user installed stays available.</summary>
        private void ApplyStoreEditionRestrictions()
        {
            if (PaRuX.Contracts.StoreEditionPolicy.AllowsExecutableDownloads) return;
            foreach (Button b in new[] { InstallEmbeddingEngineButton, AvatarInstallButton, ThreedInstallButton, Settings_AutoCheck_RuntimeUpdate_Button })
            {
                if (b == null) continue;
                b.Visibility = Visibility.Collapsed;
                b.IsEnabled = false;
            }
        }

        private async void InstallEmbeddingEngine_Click(object sender, RoutedEventArgs e)
        {
            _embeddingInstallFailure = null;
            if (InstallEmbeddingEngineButton != null) InstallEmbeddingEngineButton.IsEnabled = false;
            if (EmbeddingEngineStatusText != null) EmbeddingEngineStatusText.Text = T("Downloading (~90 MB)…", "Download in corso (~90 MB)…");
            try
            {
                await EmbeddingClient.Current.InstallEngineAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("InstallEmbeddingEngine failed: " + ex.Message);
                _embeddingInstallFailure = ex.Message;
            }
            finally
            {
                if (InstallEmbeddingEngineButton != null) InstallEmbeddingEngineButton.IsEnabled = true;
                await RefreshEmbeddingEngineStatusAsync();
            }
        }

        private async Task ReindexRagSourceAsync(RagSourceDto source)
        {
            if (RagIndexStatus != null)
                RagIndexStatus.Text = KT("Indexing ", "Indicizzazione di ") + source.FolderPath
                    + KT("… large technical archives can take time. Follow progress below.",
                        "… archivi tecnici molto grandi possono richiedere tempo. Segui l'avanzamento qui sotto.");
            try
            {
                (int documents, int chunks, bool runningInBackground) =
                    await RagClient.Current.ReindexAsync(source.Id, CancellationToken.None);
                if (RagIndexStatus != null)
                    RagIndexStatus.Text = runningInBackground
                        ? KT("Corporate indexing started. Already searchable: ",
                             "Indicizzazione Corporate avviata. Già ricercabili: ") + documents
                            + KT(" document(s), ", " documenti, ") + chunks
                            + KT(" passages. Open Document Settings to view PDF pages, pause or resume.",
                                " passaggi. Apri le impostazioni documenti per vedere le pagine PDF, mettere in pausa o riprendere.")
                        : KT("Finished indexing ", "Indicizzazione completata: ") + source.FolderPath
                            + ": " + documents + KT(" documents, ", " documenti, ")
                            + chunks + KT(" passages.", " passaggi.");
            }
            catch (Exception ex)
            {
                Log.Write("RAG reindex failed: " + ex.Message);
                if (RagIndexStatus != null) RagIndexStatus.Text = T("Indexing failed: ", "Indicizzazione non riuscita: ") + ex.Message;
            }
            await RefreshRagSourcesAsync();
        }

        private async void AddRagSource_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                RagSourceDto source;
                try { source = await RagClient.Current.AddSourceAsync(folder.Path, CancellationToken.None); }
                catch (SensitiveFolderException consent) when (consent.Code == "consent_required")
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = T("Index this broad location?", "Indicizzare questa posizione ampia?"),
                        Content = new TextBlock { Text = consent.Message + "\n\n" + folder.Path, TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = T("Index it", "Indicizza"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "Settings_Rag_Consent_Dialog");
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    source = await RagClient.Current.AddSourceAsync(folder.Path, CancellationToken.None, consentSensitive: true);
                }
                await RefreshRagSourcesAsync();
                await ReindexRagSourceAsync(source);
            }
            catch (SensitiveFolderException blocked)
            {
                if (RagIndexStatus != null) RagIndexStatus.Text = T("This folder is not indexed: ", "Questa cartella non è indicizzata: ") + blocked.Message;
            }
            catch (Exception ex)
            {
                Log.Write("AddRagSource failed: " + ex.Message);
                if (RagIndexStatus != null) RagIndexStatus.Text = T("Couldn't add the folder: ", "Impossibile aggiungere la cartella: ") + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // Local AI runtime location (LocalAiPaths.Root, via LocalAiRootClient)
        // ------------------------------------------------------------------

        private async Task RefreshLocalAiRootAsync()
        {
            if (LocalAiRootText == null) return;
            try
            {
                LocalAiRootStatus status = await LocalAiRootClient.Current.GetAsync(CancellationToken.None);
                LocalAiRootBox.Text = status.Root;
                LocalAiRootText.Text = (status.Configured ? T("Explicitly configured. ", "Configurata esplicitamente. ") : T("Auto-detected. ", "Rilevata automaticamente. "))
                    + (status.LooksConfigured ? T("A runtime looks installed there.", "Sembra che lì sia installato un runtime.") : T("No runtime found there yet.", "Nessun runtime trovato lì per ora."));
            }
            catch (Exception ex)
            {
                Log.Write("RefreshLocalAiRootAsync failed: " + ex.Message);
                LocalAiRootText.Text = T("Couldn't reach PaRuX Core to read this.", "Impossibile raggiungere PaRuX Core per leggerlo.");
            }
        }

        private async void LocalAiRootBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                LocalAiRootBox.Text = folder.Path;
            }
            catch (Exception ex)
            {
                Log.Write("LocalAiRootBrowse failed: " + ex.Message);
            }
        }

        private async void LocalAiRootSave_Click(object sender, RoutedEventArgs e)
        {
            string? path = (LocalAiRootBox?.Text ?? string.Empty).Trim();
            if (path.Length == 0) return;
            try
            {
                LocalAiRootStatus status = await LocalAiRootClient.Current.SetAsync(path, CancellationToken.None);
                SettingsStore.Current.Data.LocalAiRootPath = status.Root;
                SettingsStore.Current.Save();
                await RefreshLocalAiRootAsync();
            }
            catch (Exception ex)
            {
                Log.Write("LocalAiRootSave failed: " + ex.Message);
                LocalAiRootText.Text = T("Couldn't save: ", "Impossibile salvare: ") + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // Memory (server-owned - see MemoryClient)
        // ------------------------------------------------------------------

        private async Task RefreshMemoryListAsync(string? filter = null)
        {
            if (MemoryList == null) return;
            List<MemoryEntryDto> entries;
            try { entries = await MemoryClient.Current.ListAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("RefreshMemoryListAsync failed: " + ex.Message); return; }

            if (!string.IsNullOrWhiteSpace(filter))
                entries = entries.FindAll(m => m.Content.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            MemoryList.Children.Clear();
            if (entries.Count == 0)
            {
                var empty = new TextBlock { Text = filter == null ? T("No memory saved yet.", "Nessun ricordo salvato per ora.") : T("No matches.", "Nessuna corrispondenza.") };
                ApplyCaptionStyle(empty);
                MemoryList.Children.Add(empty);
                return;
            }

            foreach (MemoryEntryDto entry in entries)
            {
                var card = new Border { Padding = new Thickness(0, 4, 0, 4) };
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                var content = new TextBlock { Text = entry.Content, TextWrapping = TextWrapping.Wrap };
                text.Children.Add(content);
                var meta = new TextBlock
                {
                    Text = entry.Scope + (string.IsNullOrWhiteSpace(entry.ScopeKey) ? string.Empty : " · " + entry.ScopeKey)
                         + " · " + entry.UpdatedUtc.ToLocalTime().ToString("g")
                };
                ApplyCaptionStyle(meta);
                text.Children.Add(meta);
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                var enabledSwitch = new ToggleSwitch { IsOn = entry.Enabled, OnContent = "On", OffContent = T("Off", "No") };
                enabledSwitch.Toggled += async (s, e) =>
                {
                    if (_switchesBeingReverted.Contains(enabledSwitch)) return;
                    bool requested = enabledSwitch.IsOn;
                    try { await MemoryClient.Current.SetEnabledAsync(entry.Id, requested, CancellationToken.None); ShowNotice(MemoryStatusText, null); }
                    catch (Exception ex)
                    {
                        Log.Write("Memory SetEnabled failed: " + ex.Message);
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(enabledSwitch, requested, _switchesBeingReverted,
                            async ct => (await MemoryClient.Current.ListAsync(ct)).FirstOrDefault(item => item.Id == entry.Id)?.Enabled);
                        ShowNotice(MemoryStatusText, SwitchRevert.Explain("this memory", requested, ex.Message, recovered));
                    }
                };
                Grid.SetColumn(enabledSwitch, 1);
                row.Children.Add(enabledSwitch);

                var edit = new Button { Content = T("Edit", "Modifica") };
                edit.Click += async (s, e) => await EditMemoryAsync(entry);
                Grid.SetColumn(edit, 2);
                row.Children.Add(edit);

                var delete = new Button { Content = T("Delete", "Elimina") };
                delete.Click += async (s, e) =>
                {
                    try { await MemoryClient.Current.DeleteAsync(entry.Id, CancellationToken.None); }
                    catch (Exception ex) { Log.Write("Memory Delete failed: " + ex.Message); }
                    await RefreshMemoryListAsync(MemorySearchBox?.Text);
                };
                Grid.SetColumn(delete, 3);
                row.Children.Add(delete);

                card.Child = row;
                MemoryList.Children.Add(card);
            }
        }

        private async Task EditMemoryAsync(MemoryEntryDto entry)
        {
            var box = new TextBox { Text = entry.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var dialog = new ContentDialog
            {
                Title = T("Edit memory", "Modifica il ricordo"),
                Content = box,
                PrimaryButtonText = T("Save", "Salva"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string content = (box.Text ?? string.Empty).Trim();
            if (content.Length == 0) return;
            try { await MemoryClient.Current.UpdateAsync(entry.Id, content, CancellationToken.None); }
            catch (Exception ex) { Log.Write("Memory Update failed: " + ex.Message); }
            await RefreshMemoryListAsync(MemorySearchBox?.Text);
        }

        private async void AddMemory_Click(object sender, RoutedEventArgs e)
        {
            string content = (MemoryNewContentBox.Text ?? string.Empty).Trim();
            if (content.Length == 0) return;

            var item = MemoryScopeCombo.SelectedItem as ComboBoxItem;
            string? scope = item?.Tag as string ?? "User";

            // The box is cleared only once the server has stored the entry; a refused save keeps the text for a retry.
            DraftSubmitResult saved = await DraftSubmission.SubmitAsync(async token =>
            {
                await MemoryClient.Current.AddAsync(scope, null, content, token);
            }, CancellationToken.None);
            if (saved.Accepted)
            {
                MemoryNewContentBox.Text = string.Empty;
                ShowNotice(MemoryStatusText, null);
            }
            else
            {
                Log.Write("Memory Add failed: " + saved.Error);
                ShowNotice(MemoryStatusText, "Couldn't save the memory: " + saved.Error + " Your text is still in the box.");
            }
            await RefreshMemoryListAsync(MemorySearchBox?.Text);
        }

        private async void MemorySearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            await RefreshMemoryListAsync(MemorySearchBox.Text);

        private async void ExportMemory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "parux-memory" };
                picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return;
                List<MemoryEntryDto> entries = await MemoryClient.Current.ListAsync(CancellationToken.None);
                string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
                ShowMemoryStatus("Exported to " + file.Path);
            }
            catch (Exception ex)
            {
                Log.Write("ExportMemory failed: " + ex.Message);
                ShowMemoryStatus("Export failed: " + ex.Message);
            }
        }

        private async void ImportMemory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                List<MemoryEntryDto> entries = JsonSerializer.Deserialize<List<MemoryEntryDto>>(json) ?? new List<MemoryEntryDto>();
                int imported = await MemoryClient.Current.ImportAsync(entries, CancellationToken.None);
                await RefreshMemoryListAsync(MemorySearchBox?.Text);
                ShowMemoryStatus(imported + " memory entries imported.");
            }
            catch (Exception ex)
            {
                Log.Write("ImportMemory failed: " + ex.Message);
                ShowMemoryStatus("Import failed: " + ex.Message);
            }
        }

        private async void ClearMemory_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = T("Clear all memory?", "Cancellare tutta la memoria?"),
                Content = T("This permanently deletes every saved memory entry. This cannot be undone.", "Questo elimina definitivamente ogni voce di memoria salvata. L'operazione non può essere annullata."),
                PrimaryButtonText = T("Clear all", "Cancella tutto"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            int removed;
            try { removed = await MemoryClient.Current.DeleteAllAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("Memory DeleteAll failed: " + ex.Message); removed = 0; }
            await RefreshMemoryListAsync();
            ShowMemoryStatus(removed + " memory entries deleted.");
        }

        private void ShowMemoryStatus(string message)
        {
            Log.Write("Memory: " + message);
        }

        // ------------------------------------------------------------------
        // Backup & Restore (server-owned - see BackupClient)
        // ------------------------------------------------------------------

        private async Task<ContentDialogResult> ShowBackupDialogAsync(BackupRestoreDecisionPresenter.BackupDialogDecision decision)
        {
            var titleBlock = new TextBlock
            {
                Text = decision.Title
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleBlock, BackupRestoreDecisionPresenter.DialogTitleAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(titleBlock, decision.Title);

            var bodyBlock = new TextBlock
            {
                Text = decision.Body,
                TextWrapping = TextWrapping.Wrap
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(bodyBlock, BackupRestoreDecisionPresenter.DialogBodyAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bodyBlock, decision.Title);

            var dialog = new ContentDialog
            {
                Title = titleBlock,
                Content = bodyBlock,
                XamlRoot = XamlRoot
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, decision.Title);

            if (!string.IsNullOrEmpty(decision.PrimaryButtonText))
            {
                dialog.PrimaryButtonText = decision.PrimaryButtonText;
            }

            if (!string.IsNullOrEmpty(decision.SecondaryButtonText))
            {
                dialog.CloseButtonText = decision.SecondaryButtonText;
                dialog.DefaultButton = ContentDialogButton.Close;
            }
            else
            {
                dialog.DefaultButton = ContentDialogButton.Primary;
            }

            return await dialog.ShowAsync();
        }

        private async void ClearSafetySnapshots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var confirm = BackupRestoreDecisionPresenter.ConfirmClearSafetySnapshots();
                if (await ShowBackupDialogAsync(confirm) != ContentDialogResult.Primary) return;

                int removed = await BackupClient.Current.ClearSafetySnapshotsAsync(CancellationToken.None);
                var outcome = BackupRestoreDecisionPresenter.SafetySnapshotDeletionSucceeded(removed);
                ShowBackupStatus(outcome.Body);
                await ShowBackupDialogAsync(outcome);
            }
            catch (Exception ex)
            {
                Log.Write("ClearSafetySnapshots failed: " + ex.Message);
                var failure = BackupRestoreDecisionPresenter.SafetySnapshotDeletionFailed(ex.Message);
                ShowBackupStatus(failure.Body);
                await ShowBackupDialogAsync(failure);
            }
        }

        private async void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "parux-backup-" + DateTime.Now.ToString("yyyy-MM-dd")
                };
                picker.FileTypeChoices.Add("PaRuX backup", new List<string> { ".zip" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return;

                ShowBackupStatus("Exporting…");
                byte[] zip = await BackupClient.Current.ExportAsync(CancellationToken.None);
                await Windows.Storage.FileIO.WriteBytesAsync(file, zip);
                ShowBackupStatus("Exported " + FormatBytes(zip.Length) + " to " + file.Path);
            }
            catch (Exception ex)
            {
                Log.Write("ExportBackup failed: " + ex.Message);
                ShowBackupStatus("Export failed: " + ex.Message);
            }
        }

        private async void ImportBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".zip");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                var confirm = BackupRestoreDecisionPresenter.ConfirmRestore(file.Name);
                if (await ShowBackupDialogAsync(confirm) != ContentDialogResult.Primary) return;

                ShowBackupStatus("Restoring…");
                byte[] zip = (await Windows.Storage.FileIO.ReadBufferAsync(file)).ToArray();
                BackupImportResult result;
                try
                {
                    result = await BackupClient.Current.ImportAsync(zip, CancellationToken.None);
                }
                catch (IncompleteRollbackSnapshotException refusal)
                {
                    // Nothing was changed. Some live stores are missing or corrupt, so a failed restore could
                    // not return them to their previous state. Proceed only on explicit confirmation.
                    var risk = BackupRestoreDecisionPresenter.RestoreRefusedIncompleteSnapshot(refusal.UnsnapshottableStores);
                    if (await ShowBackupDialogAsync(risk) != ContentDialogResult.Primary)
                    {
                        ShowBackupStatus("Restore cancelled. Nothing was changed.");
                        return;
                    }
                    ShowBackupStatus("Restoring…");
                    result = await BackupClient.Current.ImportAsync(zip, CancellationToken.None, allowIncompleteRollbackSnapshot: true);
                }

                var successOutcome = BackupRestoreDecisionPresenter.RestoreSucceeded(result);
                ShowBackupStatus(successOutcome.Body);
                await ShowBackupDialogAsync(successOutcome);

                // Conversation history and settings were just overwritten on disk out from under
                // this running process - refresh what we can live rather than leaving stale state
                // silently displayed until the next restart.
                SettingsStore.Current.Load();
                LoadFromSettings();
                await RefreshMemoryListAsync();
                await RefreshRagSourcesAsync();
            }
            catch (Exception ex)
            {
                Log.Write("ImportBackup failed: " + ex.Message);
                var failOutcome = BackupRestoreDecisionPresenter.ForRestoreFailure(ex.Message);
                ShowBackupStatus(failOutcome.Body);
                await ShowBackupDialogAsync(failOutcome);
            }
        }

        /// <summary>Sent to the local server importer, then pulled into local history; the sidebar refreshes live.</summary>
        private async void ImportChatHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                picker.FileTypeFilter.Add(".zip");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                if (ChatImportStatus != null) ChatImportStatus.Text = T("Reading ", "Lettura di ") + file.Name + "…";
                byte[] bytes = (await Windows.Storage.FileIO.ReadBufferAsync(file)).ToArray();

                // The server parses the export (ChatGPT, Claude or Gemini Takeout), skips duplicates and reports
                // imported/skipped counts; the imported chats then sync into local history.
                ConversationImportOutcome outcome = await ConversationImportClient.ImportAsync(bytes, file.Name, CancellationToken.None);
                if (ChatImportStatus != null) ChatImportStatus.Text = outcome.Summary;
                if (!outcome.Ok) return;

                App.MainWin?.RefreshHistory();
            }
            catch (Exception ex)
            {
                Log.Write("ImportChatHistory failed: " + ex.Message);
                if (ChatImportStatus != null) ChatImportStatus.Text = T("Import failed: ", "Importazione non riuscita: ") + ex.Message;
            }
        }

        private async void FindModelsFromOtherApps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ExternalModelImportPicker.ShowAsync(this.XamlRoot,
                    message => { if (ExternalModelsStatus != null) ExternalModelsStatus.Text = message; },
                    () => Task.CompletedTask);
            }
            catch (Exception ex)
            {
                Log.Write("FindModelsFromOtherApps failed: " + ex.Message);
                if (ExternalModelsStatus != null) ExternalModelsStatus.Text = T("Could not search other apps: ", "Impossibile cercare in altre app: ") + ex.Message;
            }
        }

        private void ShowBackupStatus(string message)
        {
            Log.Write("Backup: " + message);
            if (BackupStatus != null) BackupStatus.Text = message;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes + " B";
        }

        private async void ExportTrainingDataset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "parux-training-train" };
                picker.FileTypeChoices.Add("JSONL", new List<string> { ".jsonl" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile trainFile = await picker.PickSaveFileAsync();
                if (trainFile == null) return;

                string trainPath = trainFile.Path;
                string validationPath = Path.Combine(
                    Path.GetDirectoryName(trainPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(trainPath).Replace("-train", string.Empty) + "-val.jsonl");

                DatasetExportStats stats = await TrainingDatasetService.ExportJsonlAsync(trainPath, validationPath);

                if (TrainingExportStatus != null)
                {
                    TrainingExportStatus.Text = stats.TotalCandidates == 0
                        ? T("No rated or saved replies found yet — thumbs-up or flag a reply first.", "Nessuna risposta valutata o salvata finora: metti prima un pollice in su o una bandiera a una risposta.")
                        : stats.TrainCount + T(" train / ", " training / ") + stats.ValidationCount + T(" validation examples written ", " esempi di validazione scritti ")
                          + "(" + stats.DuplicatesSkipped + T(" duplicates, ", " duplicati, ") + stats.MalformedSkipped + T(" malformed skipped).\n", " malformati saltati).\n")
                          + trainPath + "\n" + validationPath;
                }
            }
            catch (Exception ex)
            {
                Log.Write("ExportTrainingDataset failed: " + ex.Message);
                if (TrainingExportStatus != null) TrainingExportStatus.Text = T("Export failed: ", "Esportazione non riuscita: ") + ex.Message;
            }
        }

        private async Task RefreshExternalBackendAsync()
        {
            if (ExternalBackendStatusText == null) return;
            try
            {
                ExternalTextProviderStatus status = await ExternalBackendClient.Current.GetStatusAsync(CancellationToken.None);
                if (status.Configured)
                {
                    ExternalBackendStatusText.Text = T($"Using {status.ProviderName} at {status.Endpoint}", $"Uso di {status.ProviderName} su {status.Endpoint}")
                        + (status.Reachable ? T(" - reachable.", " - raggiungibile.") : T(" - not reachable right now.", " - al momento non raggiungibile."));
                    if (ClearExternalBackendButton != null) ClearExternalBackendButton.Visibility = Visibility.Visible;
                }
                else
                {
                    ExternalBackendStatusText.Text = T("Using PaRuX's own local model.", "Uso del modello locale di PaRuX.");
                    if (ClearExternalBackendButton != null) ClearExternalBackendButton.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshExternalBackendAsync failed: " + ex.Message);
                ExternalBackendStatusText.Text = T("Couldn't load backend status: ", "Impossibile caricare lo stato del backend: ") + ex.Message;
            }
        }

        private async void DetectBackends_Click(object sender, RoutedEventArgs e)
        {
            if (DetectedBackendsList == null) return;
            try
            {
                if (DetectBackendsButton != null) DetectBackendsButton.IsEnabled = false;
                DetectedBackendsList.Children.Clear();
                var scanning = new TextBlock { Text = T("Scanning well-known ports…", "Scansione delle porte più comuni…") };
                ApplyCaptionStyle(scanning);
                DetectedBackendsList.Children.Add(scanning);

                List<DetectedTextBackend> found = await ExternalBackendClient.Current.DetectAsync(CancellationToken.None);
                DetectedBackendsList.Children.Clear();
                if (found.Count == 0)
                {
                    var none = new TextBlock { Text = T("Nothing found answering on Ollama/LM Studio/koboldcpp/text-generation-webui's default ports.", "Nessuna risposta trovata sulle porte predefinite di Ollama/LM Studio/koboldcpp/text-generation-webui.") };
                    ApplyCaptionStyle(none);
                    DetectedBackendsList.Children.Add(none);
                    return;
                }
                foreach (DetectedTextBackend backend in found)
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new TextBlock { Text = backend.Name + " · " + backend.Endpoint, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(label, 0);
                    row.Children.Add(label);
                    var useButton = new Button { Content = T("Use", "Usa"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    Grid.SetColumn(useButton, 1);
                    string capturedName = backend.Name, capturedEndpoint = backend.Endpoint;
                    useButton.Click += async (s, e2) => await SetExternalBackendAsync(capturedName, capturedEndpoint);
                    row.Children.Add(useButton);
                    DetectedBackendsList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                Log.Write("DetectBackends_Click failed: " + ex.Message);
                DetectedBackendsList.Children.Clear();
                var error = new TextBlock { Text = T("Detection failed: ", "Rilevamento non riuscito: ") + ex.Message };
                ApplyCaptionStyle(error);
                DetectedBackendsList.Children.Add(error);
            }
            finally
            {
                if (DetectBackendsButton != null) DetectBackendsButton.IsEnabled = true;
            }
        }

        private async void SetManualBackend_Click(object sender, RoutedEventArgs e)
        {
            string? endpoint = ManualBackendEndpointBox?.Text?.Trim() ?? string.Empty;
            string? name = ManualBackendNameBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                if (ExternalBackendStatusText != null) ExternalBackendStatusText.Text = T("Enter an endpoint first.", "Inserisci prima un endpoint.");
                return;
            }
            await SetExternalBackendAsync(string.IsNullOrWhiteSpace(name) ? "custom" : name, endpoint);
        }

        private async Task SetExternalBackendAsync(string name, string endpoint)
        {
            try
            {
                await ExternalBackendClient.Current.SetAsync(name, endpoint, CancellationToken.None);
                await RefreshExternalBackendAsync();
            }
            catch (Exception ex)
            {
                Log.Write("SetExternalBackendAsync failed: " + ex.Message);
                if (ExternalBackendStatusText != null) ExternalBackendStatusText.Text = T("Couldn't set that backend: ", "Impossibile impostare quel backend: ") + ex.Message;
            }
        }

        private async void ClearExternalBackend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ExternalBackendClient.Current.ClearAsync(CancellationToken.None);
                await RefreshExternalBackendAsync();
            }
            catch (Exception ex)
            {
                Log.Write("ClearExternalBackend_Click failed: " + ex.Message);
                if (ExternalBackendStatusText != null) ExternalBackendStatusText.Text = T("Couldn't clear that backend: ", "Impossibile rimuovere quel backend: ") + ex.Message;
            }
        }

        private void RescanModels_Click(object sender, RoutedEventArgs e) => _ = RefreshDetectedModelsAsync();

        private async Task RefreshDetectedModelsAsync()
        {
            if (DetectedModelsList == null) return;
#if DEBUG
            bool assignmentFixture = Environment.GetEnvironmentVariable("PARUX_UI_TESTS") == "1"
                && Environment.GetEnvironmentVariable("PARUX_UI_TEST_MODEL_ROLE_ASSIGNMENT") == "1";
            if (assignmentFixture)
            {
                const string fixturePath = @"C:\PaRuX-UiTest\assigned-model.gguf";
                RenderDetectedModels(
                    new List<GgufModelInfo>
                    {
                        new GgufModelInfo { FileName = Path.GetFileName(fixturePath), FilePath = fixturePath, Name = "Assigned fixture", SizeBytes = 1024 }
                    },
                    new ResourceSnapshot(),
                    new List<ModelRoleStatus>
                    {
                        new ModelRoleStatus { Role = "standard", ModelPath = fixturePath, FileExists = true },
                        new ModelRoleStatus { Role = "uncensored", ModelPath = string.Empty }
                    });
                return;
            }
#endif

            // Same source as the Model Hub: the managed library served by PaRuX Core, plus the external Local AI folder.
            List<ManagedModelInfo> managedModels;
            try { managedModels = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("Managed library unavailable for the Settings model list: " + ex.Message); managedModels = new List<ManagedModelInfo>(); }
            List<GgufModelInfo> models = await Task.Run(() => ModelManagerService.ListInstalledModels(managedModels));
            ResourceSnapshot resources = await ResourceMonitorService.GetSnapshotAsync(CancellationToken.None);

            List<ModelRoleStatus> roles;
            try { roles = await ModelRolesClient.Current.GetRolesAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("GetRolesAsync failed: " + ex.Message); roles = new List<ModelRoleStatus>(); }

            RenderDetectedModels(models, resources, roles);
        }

        private void RenderDetectedModels(
            List<GgufModelInfo> models, ResourceSnapshot resources, List<ModelRoleStatus> roles)
        {
            DetectedModelsList.Children.Clear();
            if (models.Count == 0)
            {
                var empty = new TextBlock { Text = T("No .gguf models found in the PaRuX model library or the Local AI model folder.", "Nessun modello .gguf trovato nella libreria modelli di PaRuX o nella cartella dei modelli di Local AI.") };
                ApplyCaptionStyle(empty);
                DetectedModelsList.Children.Add(empty);
                return;
            }

            foreach (GgufModelInfo model in models)
            {
                var card = new StackPanel { Spacing = 1 };

                var titleRow = new Grid { ColumnSpacing = 8 };
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name, Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] };
                Grid.SetColumn(title, 0);
                titleRow.Children.Add(title);
                var size = new TextBlock { Text = StorageQuotaService.FormatBytes(model.SizeBytes) };
                ApplyCaptionStyle(size);
                Grid.SetColumn(size, 1);
                titleRow.Children.Add(size);
                card.Children.Add(titleRow);

                string detail = model.FileName + "  ·  " + (model.ParsedSuccessfully ? model.Architecture : "unparsed header")
                    + "  ·  " + model.Quantization
                    + (model.ContextLength.HasValue ? "  ·  " + model.ContextLength.Value.ToString("N0") + " ctx" : string.Empty)
                    + (model.ParameterCount.HasValue ? "  ·  " + FormatParams(model.ParameterCount.Value) : string.Empty);
                var detailText = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(detailText);
                card.Children.Add(detailText);

                ModelFit fit = ModelFitClassifier.Classify(model, resources);
                var fitText = new TextBlock { Text = ModelFitClassifier.Describe(fit) };
                ApplyCaptionStyle(fitText);
                string? fitBrushKey = fit switch
                {
                    ModelFit.ExcellentFit => "PaRuXSuccessColor",
                    ModelFit.WorksWithOffload => "PaRuXCautionTextBrush",
                    ModelFit.CpuRamOnly => "PaRuXCautionTextBrush",
                    ModelFit.NotRecommended => "PaRuXDangerTextBrush",
                    _ => null
                };
                if (fitBrushKey != null && Application.Current.Resources.TryGetValue(fitBrushKey, out object fitResource))
                {
                    if (fitResource is Brush fitBrush) fitText.Foreground = fitBrush;
                    else if (fitResource is Windows.UI.Color fitColor) fitText.Foreground = new SolidColorBrush(fitColor);
                }
                card.Children.Add(fitText);

                string tradeoff = DescribeModelTradeoff(model);
                if (!string.IsNullOrEmpty(tradeoff))
                {
                    var tradeoffText = new TextBlock { Text = tradeoff, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                    ApplyCaptionStyle(tradeoffText);
                    card.Children.Add(tradeoffText);
                }

                ModelRoleStatus? standardRole = roles.FirstOrDefault(r => r.Role == "standard");
                ModelRoleStatus? uncensoredRole = roles.FirstOrDefault(r => r.Role == "uncensored");
                bool isStandard = standardRole != null && PathsMatch(standardRole.ModelPath, model.FilePath);
                bool isUncensored = uncensoredRole != null && PathsMatch(uncensoredRole.ModelPath, model.FilePath);
                bool isFallbackOnly = IsFallbackOnlyModel(model.FilePath);
                string modelAutomationKey = string.Concat(model.FileName.Select(character =>
                    char.IsLetterOrDigit(character) ? character : '_')) + "_"
                    + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(model.FilePath))))[..8];

                var roleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
                if (isStandard)
                {
                    var badge = new TextBlock
                    {
                        Text = T("Currently: Standard", "Attuale: Standard"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(badge,
                        "Settings_ModelRole_CurrentStandard_" + modelAutomationKey + "_Text");
                    ApplyCaptionStyle(badge);
                    if (Application.Current.Resources.TryGetValue("PaRuXSuccessColor", out object okColor) && okColor is Windows.UI.Color okC)
                        badge.Foreground = new SolidColorBrush(okC);
                    roleRow.Children.Add(badge);
                }
                if (isUncensored)
                {
                    var badge = new TextBlock
                    {
                        Text = T("Currently: Uncensored", "Attuale: Senza censura"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(badge,
                        "Settings_ModelRole_CurrentUncensored_" + modelAutomationKey + "_Text");
                    ApplyCaptionStyle(badge);
                    roleRow.Children.Add(badge);
                }
                if (isFallbackOnly && !isStandard && !isUncensored)
                {
                    var badge = new TextBlock
                    {
                        Text = T("Fallback only · never assigned as Standard", "Solo di ripiego · mai assegnato come Standard"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    };
                    ApplyCaptionStyle(badge);
                    roleRow.Children.Add(badge);
                }
                else if (!isFallbackOnly)
                {
                    string capturedPath = model.FilePath;
                    if (!isStandard)
                    {
                        var setStandard = new Button { Content = T("Set as Standard", "Imposta come Standard"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(setStandard,
                            "Settings_ModelRole_SetStandard_" + modelAutomationKey + "_Button");
                        setStandard.Click += async (s, e2) => await SetModelRoleAsync("standard", capturedPath);
                        roleRow.Children.Add(setStandard);
                    }

                    if (!isUncensored)
                    {
                        var setUncensored = new Button { Content = T("Set as Uncensored", "Imposta come Senza censura"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(setUncensored,
                            "Settings_ModelRole_SetUncensored_" + modelAutomationKey + "_Button");
                        setUncensored.Click += async (s, e2) => await SetModelRoleAsync("uncensored", capturedPath);
                        roleRow.Children.Add(setUncensored);
                    }
                }
                card.Children.Add(roleRow);

                DetectedModelsList.Children.Add(card);
            }
        }

        private async Task RefreshMediaCheckpointsAsync()
        {
            if (MediaCheckpointsList == null) return;
            List<MediaCheckpointStatus> checkpoints;
            try { checkpoints = await MediaCheckpointsClient.Current.GetCheckpointsAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshMediaCheckpointsAsync failed: " + ex.Message);
                MediaCheckpointsList.Children.Clear();
                var error = new TextBlock { Text = T("Couldn't load checkpoint info: ", "Impossibile caricare le informazioni sul checkpoint: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(error);
                MediaCheckpointsList.Children.Add(error);
                return;
            }

            MediaCheckpointsList.Children.Clear();
            foreach (MediaCheckpointStatus status in checkpoints)
            {
                var card = new StackPanel { Spacing = 4 };

                string typeLabel = status.Type == "image" ? "Image" : (status.Type == "video" ? "Video" : "Edit");
                var titleRow = new Grid { ColumnSpacing = 8 };
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = typeLabel, Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] };
                Grid.SetColumn(title, 0);
                titleRow.Children.Add(title);
                if (status.IsCheckpointPinned || status.RoutingMode is "Force" or "Prefer")
                {
                    var badge = new TextBlock { Text = MediaSelectionPresenter.SettingsBadge(status) };
                    ApplyCaptionStyle(badge);
                    if (Application.Current.Resources.TryGetValue("PaRuXSuccessColor", out object okColor) && okColor is Windows.UI.Color okC)
                        badge.Foreground = new SolidColorBrush(okC);
                    Grid.SetColumn(badge, 1);
                    titleRow.Children.Add(badge);
                }
                card.Children.Add(titleRow);

                var current = new TextBlock { Text = status.CurrentFile, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(current);
                card.Children.Add(current);

                var currentHint = new TextBlock { Text = DescribeCheckpointFile(status.CurrentFile), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                ApplyCaptionStyle(currentHint);
                card.Children.Add(currentHint);

                if (MediaSelectionPresenter.SettingsRoutingHint(status) is { } routingHint)
                {
                    var route = new TextBlock { Text = routingHint, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                    ApplyCaptionStyle(route);
                    card.Children.Add(route);
                }

                List<string> alternatives = status.AvailableFiles
                    .Where(f => !string.Equals(f, status.CurrentFile, StringComparison.OrdinalIgnoreCase)).ToList();
                if (alternatives.Count > 0 || status.IsCheckpointPinned)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    foreach (string file in alternatives)
                    {
                        string capturedType = status.Type, capturedFile = file;
                        var button = new Button { Content = T("Use ", "Usa ") + file, FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                        button.Click += async (s, e2) => await SetMediaCheckpointAsync(capturedType, capturedFile);
                        row.Children.Add(button);
                    }
                    if (status.IsCheckpointPinned)
                    {
                        string capturedType = status.Type;
                        var resetButton = new Button { Content = T("Clear pinned model", "Cancella il modello fissato"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                        resetButton.Click += async (s, e2) => await ResetMediaCheckpointAsync(capturedType);
                        row.Children.Add(resetButton);
                    }
                    card.Children.Add(row);

                    if (alternatives.Count > 0)
                    {
                        var altHintsPanel = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
                        foreach (string file in alternatives)
                        {
                            var altHint = new TextBlock { Text = file + " - " + DescribeCheckpointFile(file), TextWrapping = TextWrapping.Wrap, Opacity = 0.6 };
                            ApplyCaptionStyle(altHint);
                            altHintsPanel.Children.Add(altHint);
                        }
                        card.Children.Add(altHintsPanel);
                    }
                }
                else if (status.AvailableFiles.Count <= 1)
                {
                    var note = new TextBlock { Text = T("Only this checkpoint was found - nothing else to switch to yet.", "È stato trovato solo questo checkpoint: per ora non c'è altro a cui passare.") };
                    ApplyCaptionStyle(note);
                    card.Children.Add(note);
                }

                MediaCheckpointsList.Children.Add(card);
            }
        }

        private async Task RefreshCustomWorkflowsAsync()
        {
            if (CustomWorkflowsList == null) return;
            List<CustomWorkflowInfo> workflows;
            try { workflows = await CustomWorkflowClient.Current.GetAllAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshCustomWorkflowsAsync failed: " + ex.Message);
                CustomWorkflowsList.Children.Clear();
                var error = new TextBlock { Text = T("Couldn't load custom workflows: ", "Impossibile caricare i workflow personalizzati: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(error);
                CustomWorkflowsList.Children.Add(error);
                return;
            }

            CustomWorkflowsList.Children.Clear();
            if (workflows.Count == 0)
            {
                var empty = new TextBlock { Text = T("No custom workflows imported yet.", "Nessun workflow personalizzato importato per ora.") };
                ApplyCaptionStyle(empty);
                CustomWorkflowsList.Children.Add(empty);
                return;
            }

            foreach (CustomWorkflowInfo workflow in workflows)
            {
                var card = new StackPanel { Spacing = 4 };

                var titleRow = new Grid { ColumnSpacing = 8 };
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = workflow.Name, Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] };
                Grid.SetColumn(title, 0);
                titleRow.Children.Add(title);
                var removeButton = new Button { Content = T("Remove", "Rimuovi"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                Grid.SetColumn(removeButton, 1);
                string capturedId = workflow.Id;
                removeButton.Click += async (s, e2) => await DeleteCustomWorkflowAsync(capturedId);
                titleRow.Children.Add(removeButton);
                card.Children.Add(titleRow);

                var capabilities = new List<string>();
                capabilities.Add(workflow.HasPromptControl ? "prompt overridable" : "fixed prompt");
                capabilities.Add(workflow.HasSeedControl ? "seed overridable" : "fixed seed");
                if (workflow.RequiresSourceImage) capabilities.Add("needs a source image");
                var detail = new TextBlock { Text = string.Join(" · ", capabilities), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                ApplyCaptionStyle(detail);
                card.Children.Add(detail);

                CustomWorkflowsList.Children.Add(card);
            }
        }

        private async void ImportWorkflow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                string name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                if (ImportWorkflowButton != null) ImportWorkflowButton.IsEnabled = false;
                await CustomWorkflowClient.Current.ImportAsync(name, json, CancellationToken.None);
                await RefreshCustomWorkflowsAsync();
                if (ImportWorkflowStatus != null) ImportWorkflowStatus.Text = T("Imported \"", "Importato \"") + name + "\".";
            }
            catch (Exception ex)
            {
                Log.Write("ImportWorkflow_Click failed: " + ex.Message);
                if (ImportWorkflowStatus != null) ImportWorkflowStatus.Text = T("Couldn't import that workflow: ", "Impossibile importare quel workflow: ") + ex.Message;
            }
            finally
            {
                if (ImportWorkflowButton != null) ImportWorkflowButton.IsEnabled = true;
            }
        }

        private async Task DeleteCustomWorkflowAsync(string id)
        {
            try
            {
                await CustomWorkflowClient.Current.RemoveAsync(id, CancellationToken.None);
                await RefreshCustomWorkflowsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("DeleteCustomWorkflowAsync failed: " + ex.Message);
                if (ImportWorkflowStatus != null) ImportWorkflowStatus.Text = T("Couldn't remove that workflow: ", "Impossibile rimuovere quel workflow: ") + ex.Message;
            }
        }

        private async Task SetMediaCheckpointAsync(string type, string fileName)
        {
            try
            {
                await MediaCheckpointsClient.Current.SetCheckpointAsync(type, fileName, CancellationToken.None);
                await RefreshMediaCheckpointsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("SetMediaCheckpointAsync failed: " + ex.Message);
            }
        }

        private async Task ResetMediaCheckpointAsync(string type)
        {
            try
            {
                await MediaCheckpointsClient.Current.ResetCheckpointAsync(type, CancellationToken.None);
                await RefreshMediaCheckpointsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("ResetMediaCheckpointAsync failed: " + ex.Message);
            }
        }

        /// <summary>Renders RecommendedCheckpointsService's curated list, grouped implicitly by
        /// falling in Type order (image/video/edit) since the server returns it that way. Each
        /// entry's "Download" button reuses the exact same ModelDownloadClient/download-progress
        /// row as the GGUF recommended-models list, then activates it (moves into ComfyUI's
        /// diffusion_models folder + assigns it to the type) once the transfer completes.</summary>
        private async Task RefreshRecommendedCheckpointsAsync()
        {
            if (RecommendedCheckpointsList == null) return;
            RecommendedCheckpointsList.Children.Clear();
            try
            {
                List<RecommendedCheckpoint> recommended = await MediaCheckpointsClient.Current.GetRecommendedAsync(CancellationToken.None);
                if (recommended.Count == 0)
                {
                    var empty = new TextBlock { Text = T("No verified alternatives to recommend yet.", "Nessuna alternativa verificata da consigliare per ora.") };
                    ApplyCaptionStyle(empty);
                    RecommendedCheckpointsList.Children.Add(empty);
                    return;
                }

                foreach (RecommendedCheckpoint rec in recommended)
                {
                    var card = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
                    var titleRow = new Grid { ColumnSpacing = 8 };
                    titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    string typeLabel = rec.Type == "image" ? "Image" : (rec.Type == "video" ? "Video" : "Edit");
                    var title = new TextBlock { Text = typeLabel + "  ·  " + rec.Title + "  ·  " + StorageQuotaService.FormatBytes(rec.EstimatedSizeBytes) };
                    ApplyBodyStrongStyle(title);
                    Grid.SetColumn(title, 0);
                    titleRow.Children.Add(title);

                    string capturedType = rec.Type, capturedFile = rec.FileName, capturedUrl = rec.DownloadUrl, capturedRepo = rec.HuggingFaceRepoId;
                    long capturedSize = rec.EstimatedSizeBytes;
                    var downloadButton = new Button { Content = T("Download", "Download") };
                    downloadButton.Click += async (s, e2) =>
                    {
                        downloadButton.IsEnabled = false;
                        try
                        {
                            ModelDownloadStatus? started = null;
                            if (!await ModelLicenseConsentPrompt.ConfirmAndStartRepositoryDownloadAsync(
                                XamlRoot, capturedRepo, capturedFile, async (revision, licenseEvidence) =>
                                {
                                    var download = new ModelDownloadRequest
                                    {
                                        RepoId = capturedRepo, FileName = capturedFile, DownloadUrl = capturedUrl, SizeBytes = capturedSize,
                                        MediaCheckpointType = capturedType,
                                        LicenseEvidence = licenseEvidence
                                    };
                                    ModelDownloadClient.PinRequestToRevision(download, revision);
                                    started = await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                                }))
                            {
                                downloadButton.IsEnabled = true;
                                return;
                            }
                            if (started != null) _ = PollAndActivateCheckpointDownloadAsync(started.Id, capturedType, capturedFile);
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Checkpoint download start failed: " + ex.Message);
                            downloadButton.IsEnabled = true;
                        }
                    };
                    Grid.SetColumn(downloadButton, 1);
                    titleRow.Children.Add(downloadButton);
                    card.Children.Add(titleRow);

                    var description = new TextBlock { Text = rec.Description, TextWrapping = TextWrapping.Wrap };
                    ApplyCaptionStyle(description);
                    card.Children.Add(description);

                    RecommendedCheckpointsList.Children.Add(card);
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshRecommendedCheckpointsAsync failed: " + ex.Message);
                var error = new TextBlock { Text = T("Couldn't load recommended checkpoints: ", "Impossibile caricare i checkpoint consigliati: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(error);
                RecommendedCheckpointsList.Children.Add(error);
            }
        }

        /// <summary>Same progress-row shape as PollModelDownloadAsync, plus the extra activate step
        /// once the transfer finishes - moving the file into ComfyUI's diffusion_models folder and
        /// assigning it to Type is what actually makes it usable, matching what SetMediaCheckpointAsync
        /// does for a file that was already sitting in that folder.</summary>
        private async Task PollAndActivateCheckpointDownloadAsync(string id, string type, string fileName)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = T("Starting…", "Avvio…") };
            ApplyCaptionStyle(label);
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
            var cancel = new Button { Content = T("Cancel", "Annulla") };
            cancel.Click += async (s, e) => await CancelDownloadRowAsync(id, label, cancel);
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
            ModelDownloadsList?.Children.Add(row);

            ModelDownloadStatus? status = await FollowDownloadRowAsync(id, label, cancel);
            if (status == null) return;

            label.Text = DownloadLine(status, null);
            if (status.ErrorDetail is MediaDependencyError refusal)
                label.Text += "  (" + refusal.Message + " Next step: " + refusal.NextAction + ")";
            else if (status.Status == "failed" && !string.IsNullOrEmpty(status.Error))
                label.Text += "  (" + status.Error + ")";
            if (status.Status == "completed" && status.VerificationStatus == "failed")
                label.Text += "  ⚠ Checksum mismatch - this file may be corrupt, re-download it";
            _ = RefreshModelBudgetAsync();

            if (status.Status == "completed" && status.VerificationStatus != "failed")
            {
                label.Text += "  ·  activating…";
                try
                {
                    await MediaCheckpointsClient.Current.ActivateAsync(type, fileName, CancellationToken.None);
                    label.Text = fileName + T("  ·  activated for ", "  ·  attivato per ") + type;
                    await RefreshMediaCheckpointsAsync();
                }
                catch (Exception ex)
                {
                    Log.Write("ActivateAsync failed: " + ex.Message);
                    label.Text += "  (activation failed: " + ex.Message + ")";
                }
            }
        }

        private static bool PathsMatch(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool IsFallbackOnlyModel(string path)
        {
            string file = Path.GetFileName(path);
            return string.Equals(file, "Qwen3.5-4B-Q4_K_M.gguf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "Qwen3.5-4B-Uncensored-Q4_K_M.gguf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Persists the role -> file assignment, then reuses
        /// LocalAiController.RunActionAsync - the exact same reload path the chat header's
        /// Standard/Uncensored toggle already goes through - so the newly-assigned file is
        /// actually loaded, not just recorded. Without this the change would silently do nothing
        /// until the next unrelated profile switch happened to reload it.</summary>
        private async Task SetModelRoleAsync(string role, string modelPath)
        {
            string label = role == "uncensored" ? "Uncensored" : "Standard";
            ShowModelRoleStatus("Setting as " + label + " - the model is reloading, this takes a moment…", false);
            try
            {
                await ModelRolesClient.Current.SetRoleAsync(role, modelPath, CancellationToken.None);
                LocalAiController.ActionResult result =
                    await Task.Run(() => LocalAiController.Current.RunActionAsync(label, CancellationToken.None));
                await RefreshDetectedModelsAsync();
                ShowModelRoleStatus(result.Success
                    ? "Now running as " + label + "."
                    : "Saved, but couldn't reload yet: " + LocalAiController.FriendlyScriptError(result), !result.Success);
            }
            catch (Exception ex)
            {
                Log.Write("SetModelRoleAsync failed: " + ex.Message);
                ShowModelRoleStatus("Couldn't set that model as " + label + ": " + ex.Message, true);
            }
        }

        private void ShowModelRoleStatus(string message, bool isError)
        {
            if (ModelRoleStatusText == null) return;
            ModelRoleStatusText.Text = message;
            ModelRoleStatusText.Visibility = Visibility.Visible;
            if (isError && Application.Current.Resources.TryGetValue("PaRuXDangerTextBrush", out object brush) && brush is Brush dangerBrush)
                ModelRoleStatusText.Foreground = dangerBrush;
            else
                ModelRoleStatusText.ClearValue(TextBlock.ForegroundProperty);
        }

        private static string FormatParams(long count)
        {
            if (count >= 1_000_000_000L) return (count / 1_000_000_000.0).ToString("0.#") + "B params";
            if (count >= 1_000_000L) return (count / 1_000_000.0).ToString("0.#") + "M params";
            return count.ToString("N0") + " params";
        }

        /// <summary>Short "why pick this one" hint built only from real, already-detected metadata
        /// (parameter count, quant string) rather than per-model guesses - so it stays true for any
        /// .gguf the user drops in, not just the two models shipped by default.</summary>
        private static string DescribeModelTradeoff(GgufModelInfo model)
        {
            var parts = new List<string>();
            if (model.ParameterCount.HasValue)
            {
                double billions = model.ParameterCount.Value / 1_000_000_000.0;
                parts.Add(billions >= 15
                    ? "More parameters: stronger reasoning and detail, but slower and heavier on VRAM."
                    : "Fewer parameters: faster and lighter, less nuanced on complex requests.");
            }
            // Strip the leading "I" off importance-matrix quants (IQ4_XS etc.) so they compare on
            // the same bit-depth digit as a plain Q-quant - both are the same "how many bits per
            // weight" tier as far as this size/speed/precision hint is concerned.
            string quant = model.Quantization ?? string.Empty;
            string quantForCompare = quant.StartsWith("IQ", StringComparison.OrdinalIgnoreCase) ? quant.Substring(1) : quant;
            if (quantForCompare.StartsWith("Q2", StringComparison.OrdinalIgnoreCase) || quantForCompare.StartsWith("Q3", StringComparison.OrdinalIgnoreCase) || quantForCompare.StartsWith("Q4", StringComparison.OrdinalIgnoreCase))
                parts.Add("Aggressively quantized: smallest file and fastest load, at some precision cost.");
            else if (quantForCompare.StartsWith("Q5", StringComparison.OrdinalIgnoreCase) || quantForCompare.StartsWith("Q6", StringComparison.OrdinalIgnoreCase))
                parts.Add("Mid-range quantization: a balance of size and precision.");
            else if (quantForCompare.StartsWith("Q8", StringComparison.OrdinalIgnoreCase) || quantForCompare.StartsWith("F16", StringComparison.OrdinalIgnoreCase) || quantForCompare.StartsWith("FP16", StringComparison.OrdinalIgnoreCase))
                parts.Add("Near-full precision: best fidelity, but the largest file and slowest.");
            return string.Join(" ", parts);
        }

        /// <summary>Same idea as DescribeModelTradeoff but for image/video/edit checkpoints, read
        /// purely from filename tokens (turbo/klein/distill, nvfp4/fp16/fp8) - standard naming
        /// conventions in the diffusion-model ecosystem, so it generalizes to files dropped in later
        /// rather than hardcoding facts about the three checkpoints bundled today.</summary>
        private static string DescribeCheckpointFile(string fileName)
        {
            string n = fileName.ToLowerInvariant();
            var parts = new List<string>();
            if (n.Contains("turbo") || n.Contains("klein") || n.Contains("distill") || n.Contains("lightning"))
                parts.Add("Distilled/turbo variant: faster generation, some quality trade-off.");
            if (n.Contains("nvfp4") || n.Contains("fp4") || n.Contains("int4"))
                parts.Add("4-bit compressed: smallest file, lowest VRAM use.");
            else if (n.Contains("fp8") || n.Contains("int8"))
                parts.Add("8-bit precision: a balance of speed and fidelity.");
            else if (n.Contains("fp16") || n.Contains("f16") || n.Contains("bf16"))
                parts.Add("Full 16-bit precision: highest fidelity, largest file and slowest.");
            return parts.Count > 0 ? string.Join(" ", parts) : "No naming hints to go on - check the model's page where you downloaded it.";
        }

        private async Task RefreshAutoFallbackAsync()
        {
            if (AutoFallbackSwitch == null) return;
            try
            {
                AiProfileStatus status = await AiProfileClient.Current.GetStatusAsync(CancellationToken.None);
                _loading = true;
                AutoFallbackSwitch.IsOn = status.AutoUncensoredFallback;
                _loading = false;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshAutoFallbackAsync failed: " + ex.Message);
            }
        }

        private async Task RefreshInternetStatusAsync()
        {
            if (InternetSwitch == null) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    InternetStatus status = await InternetClient.Current.GetStatusAsync(CancellationToken.None);
                    _loading = true;
                    InternetSwitch.IsOn = status.Enabled;
                    _loading = false;
                    InternetSwitch.IsEnabled = true;
                    InternetSwitch.OffContent = T("Off", "No");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("RefreshInternetStatusAsync failed: " + ex.Message);
                    if (attempt < 2) await Task.Delay(1500);
                }
            }

            // Never let a privacy control assert "Off" on the strength of a read that failed.
            // Observed on the physical PC: opening Settings while the Core was still starting left
            // this switch at its XAML default, so it showed Web search Off while the Core's own
            // internet.json had been {"Enabled":true} for days - the user could believe web access
            // was disabled while it was live. Usually the Core is simply not up yet, hence the
            // retries; if it stays unreachable the control says so instead of guessing, and is
            // disabled because a toggle could not be persisted anyway.
            _loading = true;
            InternetSwitch.IsOn = false;
            _loading = false;
            InternetSwitch.IsEnabled = false;
            InternetSwitch.OffContent = T("Unavailable — PaRuX Core did not answer", "Non disponibile: PaRuX Core non ha risposto");
        }

        private async void InternetSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _switchesBeingReverted.Contains(InternetSwitch)) return;
            bool wanted = InternetSwitch.IsOn;
            try
            {
                await InternetClient.Current.SetEnabledAsync(wanted, CancellationToken.None);
                ShowNotice(InternetStatusText, null);
            }
            catch (Exception ex)
            {
                Log.Write("InternetSwitch_Toggled failed: " + ex.Message);
                ToggleRecovery recovered = await SwitchRevert.ApplyAsync(InternetSwitch, wanted, _switchesBeingReverted,
                    async ct => (await InternetClient.Current.GetStatusAsync(ct)).Enabled);
                ShowNotice(InternetStatusText, SwitchRevert.Explain("web search", wanted, ex.Message, recovered));
            }
        }

        private async void AutoFallbackSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _switchesBeingReverted.Contains(AutoFallbackSwitch)) return;
            bool wanted = AutoFallbackSwitch.IsOn;
            try
            {
                await AiProfileClient.Current.SetAutoFallbackAsync(wanted, CancellationToken.None);
                ShowNotice(AutoFallbackStatusText, null);
            }
            catch (Exception ex)
            {
                Log.Write("AutoFallbackSwitch_Toggled failed: " + ex.Message);
                ToggleRecovery recovered = await SwitchRevert.ApplyAsync(AutoFallbackSwitch, wanted, _switchesBeingReverted,
                    async ct => (await AiProfileClient.Current.GetStatusAsync(ct)).AutoUncensoredFallback);
                ShowNotice(AutoFallbackStatusText, SwitchRevert.Explain("auto-escalation to Uncensored", wanted, ex.Message, recovered));
            }
        }

        private void OnDiagTick(DispatcherQueueTimer sender, object args)
        {
            if (this.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
            {
                _diagTimer.Stop();
                return;
            }
            _ = RefreshDiagnosticsAsync();
            if (_selectedSettingsCategory == "Agents")
            {
                _ = RefreshAgentTasksAsync();
                _ = RefreshPendingAutopilotRunsAsync(); // runs started from a paired phone appear without a manual refresh
            }
        }

        // ------------------------------------------------------------------
        // Load / persist
        // ------------------------------------------------------------------

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                AppSettings s = SettingsStore.Current.Data;

                SelectByTag(ThemeCombo, s.Theme);
                SelectByTag(LanguageCombo, PaRuX.Services.UiLanguage.Normalize(s.UiLanguage));
                LoadSetupProfile();
                SelectByTag(BubbleCombo, s.BubbleMode);
                SelectByTag(ProfileCombo, s.DefaultProfile);

                HotkeyBox.Text = s.Hotkey;
                // Startup registration happens with nobody watching, and the combo is often already
                // owned by another app. Showing the saved value alone would present a shortcut that
                // does nothing as if it were working.
                HotkeyHint.Text = App.Hotkey.IsRegistered
                    ? T("Active: press ", "Attiva: premi ") + s.Hotkey + T(" anywhere to open Quick Chat.", " ovunque per aprire la Chat rapida.")
                    : T("Not active: Windows refused ", "Non attiva: Windows ha rifiutato ") + s.Hotkey + T(" — another app already owns it. ", " — un'altra app la usa già. ")
                      + T("Pick a different combination and press Apply.", "Scegli una combinazione diversa e premi Applica.");
                ImageWidthBox.Value = s.GeneratedImageWidth;
                ImageHeightBox.Value = s.GeneratedImageHeight;
                SelectImagePreset(s.GeneratedImageWidth, s.GeneratedImageHeight);
                MaxImagesBox.Value = s.MaxImagesPerMessage;
                MaxImageMegabytesBox.Value = s.MaxImageMegabytes;
                MaxDocumentsBox.Value = s.MaxDocumentsPerMessage;
                MaxDocumentMegabytesBox.Value = s.MaxDocumentMegabytes;
                UpdateImageFolderText();
                VideoWidthBox.Value = s.GeneratedVideoWidth;
                VideoHeightBox.Value = s.GeneratedVideoHeight;
                VideoDurationBox.Value = s.GeneratedVideoDurationSeconds;
                VideoFpsBox.Value = s.GeneratedVideoFps;
                VideoStepsBox.Value = s.GeneratedVideoSteps;
                MaxVideoMegabytesBox.Value = s.MaxVideoMegabytes;
                LocalizeVideoPresets();
                SelectVideoPreset(s.GeneratedVideoWidth, s.GeneratedVideoHeight, s.GeneratedVideoSteps);
                UpdateVideoFolderText();
                VsCodeCommandBox.Text = s.VsCodeCommand;
                OpenCodeCommandBox.Text = s.OpenCodeCommand;
                if (AgentWorkspaceRootBox != null) AgentWorkspaceRootBox.Text = s.AgentCodingWorkspaceRoot;
                StorageQuotaBox.Value = Math.Clamp(s.StorageQuotaGigabytes, 10, StorageQuotaService.MaximumQuotaGigabytes);
                ComputerUseAutomationSwitch.IsOn = s.ComputerUseFullAutomationEnabled;
                VoiceAutoSendSwitch.IsOn = s.SendVoiceMessagesAutomatically;
                RefreshComputerApps();
                RefreshGamingStatus();
                PopulateLanAddresses();
                _ = RefreshStorageUsageAsync();
                _ = RefreshDiscoveryPolicyAsync();

                // Trust the registry over the settings file: the user may have removed it externally.
                bool startupActual = StartupService.IsEnabled();
                StartupSwitch.IsOn = startupActual;
                NotificationsGenerationSwitch.IsOn = s.NotifyOnGeneration;
                NotificationsDownloadsSwitch.IsOn = s.NotifyOnDownloads;
                NotificationsInstallsSwitch.IsOn = s.NotifyOnInstalls;
                if (startupActual != s.StartWithWindows)
                {
                    s.StartWithWindows = startupActual;
                    SettingsStore.Current.Save();
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as ComboBoxItem;
                if (item != null && (item.Tag as string) == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private static string? SelectedTag(ComboBox combo)
        {
            var item = combo.SelectedItem as ComboBoxItem;
            return item == null ? null : item.Tag as string;
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(LanguageCombo);
            if (tag == null) return;
            string language = PaRuX.Services.UiLanguage.Normalize(tag);
            if (language == PaRuX.Services.UiLanguage.Normalize(SettingsStore.Current.Data.UiLanguage)) return;

            SettingsStore.Current.Data.UiLanguage = language;
            SettingsStore.Current.Save();
            PaRuX.Services.UiLanguage.Apply(language);
            LanguageHint.Text = language == PaRuX.Services.UiLanguage.Italian
                ? "Riavvia PaRuX per applicare la lingua a tutte le schermate."
                : "Restart PaRuX to apply the language to every screen.";
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(ThemeCombo);
            if (tag == null) return;

            SettingsStore.Current.Data.Theme = tag;
            SettingsStore.Current.Save();
            App.ApplyTheme();
        }

        private void VoiceAutoSendSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Current.Data.SendVoiceMessagesAutomatically = VoiceAutoSendSwitch.IsOn;
            SettingsStore.Current.Save();
        }

        private void ComputerUseAutomationSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Current.Data.ComputerUseFullAutomationEnabled = ComputerUseAutomationSwitch.IsOn;
            SettingsStore.Current.Save();
        }

        private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            bool wanted = StartupSwitch.IsOn;
            bool ok = StartupService.SetEnabled(wanted);

            if (!ok)
            {
                StartupHint.Text = T("Couldn't update the Windows startup entry — see parux.log.", "Impossibile aggiornare la voce di avvio di Windows — vedi parux.log.");
                _loading = true;
                StartupSwitch.IsOn = StartupService.IsEnabled();
                _loading = false;
                return;
            }

            StartupHint.Text = wanted
                ? T("PaRuX will start automatically when you sign in.", "PaRuX si avvierà automaticamente quando accedi.")
                : T("Adds PaRuX to the Windows startup list", "Aggiunge PaRuX all'elenco di avvio di Windows");

            SettingsStore.Current.Data.StartWithWindows = wanted;
            SettingsStore.Current.Save();
        }

        private void NotificationsGenerationSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Current.Data.NotifyOnGeneration = NotificationsGenerationSwitch.IsOn;
            SettingsStore.Current.Save();
        }

        private void NotificationsDownloadsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Current.Data.NotifyOnDownloads = NotificationsDownloadsSwitch.IsOn;
            SettingsStore.Current.Save();
        }

        private void NotificationsInstallsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Current.Data.NotifyOnInstalls = NotificationsInstallsSwitch.IsOn;
            SettingsStore.Current.Save();
        }

        private void BubbleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(BubbleCombo);
            if (tag == null) return;

            SettingsStore.Current.Data.BubbleMode = tag;
            SettingsStore.Current.Save();

            if (App.BubbleWin != null) App.BubbleWin.ApplyVisibilityMode();
        }

        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(ProfileCombo);
            if (tag == null) return;

            SettingsStore.Current.Data.DefaultProfile = tag;
            SettingsStore.Current.Save();
        }

        private void ImageNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(sender.Value)) return;
            AppSettings settings = SettingsStore.Current.Data;
            if (!double.IsNaN(ImageWidthBox.Value)) settings.GeneratedImageWidth = NormalizeImageDimension(ImageWidthBox.Value);
            if (!double.IsNaN(ImageHeightBox.Value)) settings.GeneratedImageHeight = NormalizeImageDimension(ImageHeightBox.Value);
            if (!double.IsNaN(MaxImagesBox.Value)) settings.MaxImagesPerMessage = Math.Clamp((int)MaxImagesBox.Value, 1, 8);
            if (!double.IsNaN(MaxImageMegabytesBox.Value)) settings.MaxImageMegabytes = Math.Clamp((int)MaxImageMegabytesBox.Value, 1, 100);
            SettingsStore.Current.Save();
            SelectImagePreset(settings.GeneratedImageWidth, settings.GeneratedImageHeight);
        }

        private void ImagePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(ImagePresetCombo);
            if (string.IsNullOrWhiteSpace(tag) || tag == "custom") return;
            string[] parts = tag.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)) return;
            _loading = true;
            ImageWidthBox.Value = width;
            ImageHeightBox.Value = height;
            _loading = false;
            SettingsStore.Current.Data.GeneratedImageWidth = width;
            SettingsStore.Current.Data.GeneratedImageHeight = height;
            SettingsStore.Current.Save();
        }

        private void SelectImagePreset(int width, int height)
        {
            string wanted = width + "x" + height;
            _loading = true;
            SelectByTag(ImagePresetCombo, wanted);
            if (SelectedTag(ImagePresetCombo) != wanted) SelectByTag(ImagePresetCombo, "custom");
            _loading = false;
        }

        private void DocumentNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(sender.Value)) return;
            AppSettings settings = SettingsStore.Current.Data;
            settings.MaxDocumentsPerMessage = Math.Clamp((int)MaxDocumentsBox.Value, 1, 8);
            settings.MaxDocumentMegabytes = Math.Clamp((int)MaxDocumentMegabytesBox.Value, 1, 1024);
            SettingsStore.Current.Save();
        }

        private static int NormalizeImageDimension(double value)
        {
            int bounded = Math.Clamp((int)value, 512, 2048);
            return (int)Math.Round(bounded / 64.0) * 64;
        }

        private async void ChooseImageFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                SettingsStore.Current.Data.GeneratedImagesFolder = folder.Path;
                SettingsStore.Current.Save();
                UpdateImageFolderText();
            }
            catch (Exception ex)
            {
                Log.Write("Choose image folder failed: " + ex.Message);
                ImageFolderPath.Text = T("Couldn't use that folder.", "Impossibile usare quella cartella.");
            }
        }

        private void ResetImageFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.Data.GeneratedImagesFolder = string.Empty;
            SettingsStore.Current.Save();
            UpdateImageFolderText();
        }

        private void UpdateImageFolderText()
        {
            string configured = SettingsStore.Current.Data.GeneratedImagesFolder;
            ImageFolderPath.Text = string.IsNullOrWhiteSpace(configured)
                ? T("PaRuX managed local folder", "Cartella locale gestita da PaRuX")
                : configured;
        }

        private void VideoNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            // NumberBox can fire ValueChanged for a still-unloaded sibling box (its own
            // Value/Text coercion is not always synchronous with a programmatic .Value
            // set), so every box this handler reads is guarded individually rather than
            // trusting the _loading flag plus sender.Value alone - otherwise a NaN read
            // here becomes a non-finite double that SettingsStore.Save cannot serialize.
            if (_loading || double.IsNaN(sender.Value)) return;
            AppSettings settings = SettingsStore.Current.Data;
            if (!double.IsNaN(VideoWidthBox.Value)) settings.GeneratedVideoWidth = NormalizeVideoDimension(VideoWidthBox.Value);
            if (!double.IsNaN(VideoHeightBox.Value)) settings.GeneratedVideoHeight = NormalizeVideoDimension(VideoHeightBox.Value);
            if (!double.IsNaN(VideoDurationBox.Value)) settings.GeneratedVideoDurationSeconds = Math.Clamp(VideoDurationBox.Value, 1, 60);
            if (!double.IsNaN(VideoFpsBox.Value)) settings.GeneratedVideoFps = Math.Clamp((int)VideoFpsBox.Value, 8, 60);
            if (!double.IsNaN(VideoStepsBox.Value)) settings.GeneratedVideoSteps = Math.Clamp((int)VideoStepsBox.Value, 8, 60);
            if (!double.IsNaN(MaxVideoMegabytesBox.Value)) settings.MaxVideoMegabytes = Math.Clamp((int)MaxVideoMegabytesBox.Value, 10, 4096);
            SettingsStore.Current.Save();
            SelectVideoPreset(settings.GeneratedVideoWidth, settings.GeneratedVideoHeight, settings.GeneratedVideoSteps);
        }

        private static int NormalizeVideoDimension(double value)
        {
            int bounded = Math.Clamp((int)value, 256, 3840);
            return (int)Math.Round(bounded / 8.0) * 8;
        }

        private void VideoPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(VideoPresetCombo);
            if (string.IsNullOrWhiteSpace(tag) || tag == "custom") return;
            string[] parts = tag.Split('x');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int width)
                || !int.TryParse(parts[1], out int height)
                || !int.TryParse(parts[2], out int steps)) return;
            _loading = true;
            VideoWidthBox.Value = width;
            VideoHeightBox.Value = height;
            VideoStepsBox.Value = steps;
            _loading = false;
            SettingsStore.Current.Data.GeneratedVideoWidth = width;
            SettingsStore.Current.Data.GeneratedVideoHeight = height;
            SettingsStore.Current.Data.GeneratedVideoSteps = steps;
            SettingsStore.Current.Save();
        }

        private void SelectVideoPreset(int width, int height, int steps)
        {
            string wanted = width + "x" + height + "x" + steps;
            _loading = true;
            SelectByTag(VideoPresetCombo, wanted);
            if (SelectedTag(VideoPresetCombo) != wanted) SelectByTag(VideoPresetCombo, "custom");
            _loading = false;
        }

        private void LocalizeVideoPresets()
        {
            if (VideoPresetCombo == null) return;
            foreach (object entry in VideoPresetCombo.Items)
            {
                if (entry is ComboBoxItem item && item.Tag is string tag)
                {
                    item.Content = tag switch
                    {
                        "640x360x12" => T("Fast preview · 640×360 · 12 steps", "Anteprima rapida · 640×360 · 12 passaggi"),
                        "832x448x16" => T("Draft · 832×448 · 16 steps", "Bozza · 832×448 · 16 passaggi"),
                        "1280x720x30" => T("Standard · 1280×720 · 30 steps", "Standard · 1280×720 · 30 passaggi"),
                        "1920x1080x40" => T("High · 1920×1080 · 40 steps", "Alta · 1920×1080 · 40 passaggi"),
                        "2560x1440x50" => T("Ultra · 2560×1440 · 50 steps", "Ultra · 2560×1440 · 50 passaggi"),
                        "3840x2160x60" => T("4K · 3840×2160 · 60 steps", "4K · 3840×2160 · 60 passaggi"),
                        "custom" => T("Custom", "Personalizzato"),
                        _ => item.Content
                    };
                }
            }
        }

        private async void ChooseVideoFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                SettingsStore.Current.Data.GeneratedVideosFolder = folder.Path;
                SettingsStore.Current.Save();
                UpdateVideoFolderText();
            }
            catch (Exception ex)
            {
                Log.Write("Choose video folder failed: " + ex.Message);
                VideoFolderPath.Text = T("Couldn't use that folder.", "Impossibile usare quella cartella.");
            }
        }

        private void ResetVideoFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.Data.GeneratedVideosFolder = string.Empty;
            SettingsStore.Current.Save();
            UpdateVideoFolderText();
        }

        private void UpdateVideoFolderText()
        {
            string configured = SettingsStore.Current.Data.GeneratedVideosFolder;
            VideoFolderPath.Text = string.IsNullOrWhiteSpace(configured) ? T("PaRuX managed local folder", "Cartella locale gestita da PaRuX") : configured;
        }

        private async void ChooseRunningComputerApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // The user initiates this read-only scan in Settings.
                // No agent-invoked discovery is treated as authorization.
                IReadOnlyList<ComputerUseRunningAppDiscovery.DiscoveredApp> found =
                    await Task.Run(ComputerUseRunningAppDiscovery.ScanOpenApplications);

                if (found.Count == 0)
                {
                    await new ContentDialog
                    {
                        Title = T("No eligible open apps found", "Nessuna app aperta idonea trovata"),
                        Content = T("Open a desktop app and try again, or select Browse EXE. Protected and packaged app hosts may be unavailable.", "Apri un'app desktop e riprova, oppure scegli Sfoglia EXE. Gli host di app protetti e in pacchetto potrebbero non essere disponibili."),
                        CloseButtonText = "OK",
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                    return;
                }

                var chooser = new ComboBox
                {
                    Header = T("Open desktop application", "Apri applicazione desktop"),
                    PlaceholderText = T("Choose the application to allow", "Scegli l'applicazione da consentire"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                foreach (ComputerUseRunningAppDiscovery.DiscoveredApp app in found)
                    chooser.Items.Add(new ComboBoxItem { Content = app.Name, Tag = app });

                var executable = new TextBlock
                {
                    Text = T("Select an application to review its executable path.", "Seleziona un'applicazione per vedere il percorso del suo eseguibile."),
                    TextWrapping = TextWrapping.Wrap
                };
                chooser.SelectionChanged += (s, args) =>
                {
                    executable.Text = (chooser.SelectedItem as ComboBoxItem)?.Tag is
                        ComputerUseRunningAppDiscovery.DiscoveredApp selected
                        ? T("Executable: ", "Eseguibile: ") + selected.ExecutablePath
                        : T("Select an application to review its executable path.", "Seleziona un'applicazione per vedere il percorso del suo eseguibile.");
                };

                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(new TextBlock
                {
                    Text = T("Only the executable you approve is added to your Computer Use allowlist. No application is started or controlled during discovery.", "Solo l'eseguibile che approvi viene aggiunto all'elenco consentito di Uso del computer. Durante il rilevamento nessuna applicazione viene avviata o controllata."),
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(chooser);
                panel.Children.Add(executable);
                var dialog = new ContentDialog
                {
                    Title = T("Approve desktop app", "Approva applicazione desktop"),
                    Content = panel,
                    PrimaryButtonText = T("Allow selected app", "Consenti l'app selezionata"),
                    CloseButtonText = T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary
                    || (chooser.SelectedItem as ComboBoxItem)?.Tag is not
                        ComputerUseRunningAppDiscovery.DiscoveredApp chosen)
                    return;

                if (!ComputerUseRunningAppDiscovery.IsEligibleCandidate(
                        chosen.Name, chosen.ExecutablePath, true)
                    || !File.Exists(chosen.ExecutablePath))
                    return;

                List<ComputerAppShortcut> allowed =
                    SettingsStore.Current.Data.ComputerApps ??= new List<ComputerAppShortcut>();
                // Avoid duplicate executable grants or ambiguous display names.
                if (allowed.Any(a =>
                        string.Equals(a.ExecutablePath, chosen.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Name, chosen.Name,
                            StringComparison.OrdinalIgnoreCase))
                    || ComputerUseService.GetAllowedAppNames().Any(n =>
                        string.Equals(n, chosen.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await new ContentDialog
                    {
                        Title = T("App already approved", "App già approvata"),
                        Content = T("An app with this name or executable is already in the allowlist. Remove that entry first if you want to replace it.", "Un'app con questo nome o eseguibile è già nell'elenco consentito. Rimuovi prima quella voce se vuoi sostituirla."),
                        CloseButtonText = "OK",
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                    return;
                }

                allowed.Add(new ComputerAppShortcut
                {
                    Name = chosen.Name,
                    ExecutablePath = chosen.ExecutablePath,
                    Arguments = string.Empty
                });
                SettingsStore.Current.Save();
                RefreshComputerApps();
            }
            catch (Exception ex)
            {
                Log.Write("Computer Use app inventory failed: " + ex.GetType().Name);
            }
        }

        private async void ChooseComputerApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                var name = new TextBox { Header = T("Display name", "Nome visualizzato"), Text = Path.GetFileNameWithoutExtension(file.Name) };
                var aliases = new TextBox { Header = T("Aliases", "Alias"), PlaceholderText = T("Comma-separated names used in chat", "Nomi separati da virgola usati in chat") };
                var arguments = new TextBox { Header = T("Optional launch arguments", "Argomenti di avvio facoltativi"), PlaceholderText = T("Stored locally; not inferred from chat", "Salvati in locale; non dedotti dalla chat") };
                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(name); content.Children.Add(aliases); content.Children.Add(arguments);
                var dialog = new ContentDialog
                {
                    Title = T("Allow a Computer Use app", "Consenti un'app per Uso del computer"),
                    Content = content,
                    PrimaryButtonText = T("Add", "Aggiungi"),
                    CloseButtonText = T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                string displayName = string.IsNullOrWhiteSpace(name.Text)
                    ? Path.GetFileNameWithoutExtension(file.Name) : name.Text.Trim();
                // Manual selection obeys the same executable policy as open_app;
                // an EXE picker result is not sufficient to authorize a shell runner.
                if (!ComputerUseAppSelectionPolicy.IsValidName(displayName)
                    || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(file.Path)
                    || ComputerUseService.GetAllowedAppNames().Any(n =>
                        string.Equals(n, displayName, StringComparison.OrdinalIgnoreCase))
                    || (SettingsStore.Current.Data.ComputerApps?.Any(a =>
                        string.Equals(a.ExecutablePath, file.Path, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Name, displayName, StringComparison.OrdinalIgnoreCase)) ?? false))
                {
                    await new ContentDialog
                    {
                        Title = T("Cannot approve this app", "Impossibile approvare questa app"),
                        Content = T("Select a local GUI executable with a unique name. Shell interpreters, scripts and already approved executables are not permitted.", "Seleziona un eseguibile GUI locale con un nome univoco. Interpreti di shell, script ed eseguibili già approvati non sono consentiti."),
                        CloseButtonText = "OK",
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                    return;
                }
                SettingsStore.Current.Data.ComputerApps ??= new List<ComputerAppShortcut>();
                SettingsStore.Current.Data.ComputerApps.Add(new ComputerAppShortcut
                {
                    Name = displayName,
                    Aliases = (aliases.Text ?? string.Empty).Trim(),
                    ExecutablePath = file.Path,
                    Arguments = (arguments.Text ?? string.Empty).Trim()
                });
                SettingsStore.Current.Save();
                RefreshComputerApps();
            }
            catch (Exception ex)
            {
                Log.Write("Add Computer Use app failed: " + ex);
            }
        }

        private async void EnterGamingMode_Click(object sender, RoutedEventArgs e)
        {
            EnterGamingButton.IsEnabled = false;
            string? notice = null;
            try
            {
                // EnterAsync reports a refused start in its result rather than throwing, so both outcomes are shown.
                LocalAiController.ActionResult result = await GamingManager.Current.EnterAsync(CancellationToken.None);
                if (!result.Success)
                    notice = GamingActionNotice.Refused("Enter Gaming Mode", result.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Enter Gaming Mode failed: " + ex.Message);
                notice = "Couldn't enter Gaming Mode: " + ex.Message;
            }
            finally { EnterGamingButton.IsEnabled = true; RefreshGamingStatus(notice); }
        }

        private async void ExitGamingMode_Click(object sender, RoutedEventArgs e)
        {
            ExitGamingButton.IsEnabled = false;
            string? notice = null;
            try
            {
                LocalAiController.ActionResult result = await GamingManager.Current.ExitAsync(CancellationToken.None);
                if (!result.Success)
                    notice = GamingActionNotice.Refused("Exit Gaming Mode", result.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Exit Gaming Mode failed: " + ex.Message);
                notice = "Couldn't exit Gaming Mode: " + ex.Message;
            }
            finally { ExitGamingButton.IsEnabled = true; RefreshGamingStatus(notice); }
        }

        private void RefreshGamingStatus(string? notice = null)
        {
            if (GamingStatusText == null) return;
            bool active = GamingManager.Current.IsActive;
            string state = active
                ? "ACTIVE — AI jobs, coding agents, Computer Use, and the bubble are suspended."
                : "Ready — heavy AI stays unloaded until requested. Game exit never reloads a model.";
            GamingStatusText.Text = notice == null ? state : notice + "\n" + state;
            EnterGamingButton.IsEnabled = !active;
            ExitGamingButton.IsEnabled = active;
        }

        private void PopulateLanAddresses(string? selected = null)
        {
            if (LanAddressCombo == null) return;
            string? current = selected ?? (LanAddressCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            LanAddressCombo.Items.Clear();
            foreach (IPAddress address in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(a))
                .Distinct()
                .OrderBy(a => a.ToString()))
            {
                string value = address.ToString();
                var item = new ComboBoxItem { Content = value, Tag = value };
                LanAddressCombo.Items.Add(item);
                if (value == current) LanAddressCombo.SelectedItem = item;
            }
            if (LanAddressCombo.SelectedItem == null && LanAddressCombo.Items.Count > 0)
                LanAddressCombo.SelectedIndex = 0;
        }

        private static bool IsPrivateIpv4(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168));
        }

        /// <summary>
        /// Lists the provider keys. The plaintext is never shown here - only when a key is minted,
        /// because that is the only moment PaRuX has it.
        /// </summary>
        private async Task RefreshApiKeysAsync()
        {
            if (ApiKeyList == null) return;
            try
            {
                List<ApiKeyInfo> keys = await RemoteAccessClient.Current.ListApiKeysAsync(CancellationToken.None);
                ApiKeyList.Children.Clear();

                ApiKeyStatusText.Text = keys.Count == 0
                    ? T("No API keys yet. Create one to let another app use this PC's models.", "Nessuna chiave API per ora. Creane una per permettere a un'altra app di usare i modelli di questo PC.")
                    : (keys.Count == 1 ? T("1 key", "1 chiave") : keys.Count + T(" keys", " chiavi")) + T(" · revoking a key stops it working at once.", " · revocare una chiave la ferma subito.");

                foreach (ApiKeyInfo key in keys)
                {
                    var row = new Grid { ColumnSpacing = 12 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var text = new StackPanel { Spacing = 2 };
                    text.Children.Add(new TextBlock
                    {
                        Text = key.Name,
                        Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"]
                    });
                    text.Children.Add(new TextBlock
                    {
                        Text = T("Created ", "Creata il ") + key.CreatedUtc.ToLocalTime().ToString("d MMM yyyy") + " · "
                            + (key.LastUsedUtc.HasValue
                                ? T("last used ", "ultimo utilizzo ") + key.LastUsedUtc.Value.ToLocalTime().ToString("d MMM yyyy HH:mm")
                                : T("never used", "mai usata")),
                        Style = (Style)FindResource("PaRuXCaptionTextStyle"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    Grid.SetColumn(text, 0);

                    var revoke = new Button { Content = T("Revoke", "Revoca"), VerticalAlignment = VerticalAlignment.Center };
                    string id = key.Id;
                    revoke.Click += async (_, _) =>
                    {
                        revoke.IsEnabled = false;
                        try
                        {
                            await RemoteAccessClient.Current.RevokeApiKeyAsync(id, CancellationToken.None);
                            await RefreshApiKeysAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Revoke API key failed: " + ex);
                            ApiKeyStatusText.Text = T("Couldn't revoke that key: ", "Impossibile revocare quella chiave: ") + ex.Message;
                            revoke.IsEnabled = true;
                        }
                    };
                    Grid.SetColumn(revoke, 1);

                    row.Children.Add(text);
                    row.Children.Add(revoke);
                    ApiKeyList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Refresh API keys failed: " + ex);
                ApiKeyStatusText.Text = T("API key list unavailable — PaRuX Core did not answer.", "Elenco delle chiavi API non disponibile: PaRuX Core non ha risposto.");
            }
        }

        private async void CreateApiKey_Click(object sender, RoutedEventArgs e)
        {
            CreateApiKeyButton.IsEnabled = false;
            try
            {
                ApiKeyCreated created = await RemoteAccessClient.Current.CreateApiKeyAsync(
                    ApiKeyNameBox.Text ?? string.Empty, CancellationToken.None);
                ApiKeyNameBox.Text = string.Empty;
                NewApiKeyText.Text = created.Key;
                NewApiKeyPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                await RefreshApiKeysAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Create API key failed: " + ex);
                ApiKeyStatusText.Text = T("Couldn't create a key: ", "Impossibile creare una chiave: ") + ex.Message;
            }
            finally { CreateApiKeyButton.IsEnabled = true; }
        }

        private void CopyApiKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(NewApiKeyText.Text ?? string.Empty);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                CopyApiKeyButton.Content = T("Copied", "Copiata");
            }
            catch (Exception ex)
            {
                Log.Write("Copy API key failed: " + ex);
                ApiKeyStatusText.Text = T("Couldn't copy to the clipboard: ", "Impossibile copiare negli appunti: ") + ex.Message;
            }
        }

        private bool _webAccessLoading;

        /// <summary>
        /// Reflects the browser door. The toggle is disabled until a passphrase exists, because
        /// turning it on without one is refused by the Core anyway and a switch that flips back is
        /// worse than one that is visibly not ready yet.
        /// </summary>
        private async Task RefreshWebAccessAsync()
        {
            if (WebAccessSwitch == null) return;
            try
            {
                WebAccessStatus status = await RemoteAccessClient.Current.GetWebAccessAsync(CancellationToken.None);
                _webAccessLoading = true;
                WebAccessSwitch.IsOn = status.Enabled;
                _webAccessLoading = false;
                WebAccessSwitch.IsEnabled = status.PassphraseSet;
                SignOutWebSessionsButton.IsEnabled = status.ActiveSessions > 0;
                OpenWebClientButton.IsEnabled = status.Enabled;

                if (!status.PassphraseSet)
                {
                    WebAccessStatusText.Text = T("Off · set a passphrase first. It is stored only as a hash and never leaves this PC.", "Disattivato · imposta prima una passphrase. Viene conservata solo come hash e non lascia mai questo PC.");
                    return;
                }

                WebAccessStatusText.Text = status.Enabled
                    ? "On · " + status.LocalUrl + " · "
                        + (status.ActiveSessions == 1 ? T("1 browser signed in", "1 browser connesso") : status.ActiveSessions + T(" browsers signed in", " browser connessi"))
                        + T(" · a browser session can chat, load models and change settings, but never change this passphrase.", " · una sessione del browser può chattare, caricare modelli e cambiare impostazioni, ma non può mai cambiare questa passphrase.")
                    : T("Off · PaRuX serves the web interface only to this PC's own desktop app.", "Disattivato · PaRuX serve l'interfaccia web solo all'app desktop di questo PC.");
            }
            catch (Exception ex)
            {
                Log.Write("Refresh web access failed: " + ex);
                WebAccessSwitch.IsEnabled = false;
                WebAccessStatusText.Text = T("Browser-access status unavailable — PaRuX Core did not answer.", "Stato dell'accesso dal browser non disponibile: PaRuX Core non ha risposto.");
            }
        }

        private async void SetWebPassphrase_Click(object sender, RoutedEventArgs e)
        {
            SetWebPassphraseButton.IsEnabled = false;
            try
            {
                string passphrase = WebAccessPassphraseBox.Password ?? string.Empty;
                await RemoteAccessClient.Current.SaveWebAccessAsync(
                    new WebAccessConfigRequest { Passphrase = passphrase }, CancellationToken.None);
                WebAccessPassphraseBox.Password = string.Empty;
                WebAccessStatusText.Text = T("Passphrase set. Any browser already signed in has been signed out.", "Passphrase impostata. Ogni browser già connesso è stato disconnesso.");
                await RefreshWebAccessAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Set web passphrase failed: " + ex);
                WebAccessStatusText.Text = T("Couldn't set the passphrase: ", "Impossibile impostare la passphrase: ") + ex.Message;
            }
            finally { SetWebPassphraseButton.IsEnabled = true; }
        }

        private async void WebAccessSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_webAccessLoading || WebAccessSwitch == null) return;
            bool turningOn = WebAccessSwitch.IsOn;
            try
            {
                if (turningOn)
                {
                    var confirmation = new ContentDialog
                    {
                        Title = T("Serve PaRuX to a browser?", "Servire PaRuX a un browser?"),
                        Content = T("PaRuX will serve its web interface on this PC's loopback address so a reverse ", "PaRuX servirà la sua interfaccia web sull'indirizzo di loopback di questo PC in modo che un reverse ")
                            + T("proxy can publish it. Anyone who reaches that proxy and knows the passphrase can ", "proxy possa pubblicarla. Chiunque raggiunga quel proxy e conosca la passphrase può ")
                            + T("chat, read your saved conversations, load models and change settings on this PC. ", "chattare, leggere le tue conversazioni salvate, caricare modelli e cambiare impostazioni su questo PC. ")
                            + T("PaRuX does not open a firewall port or configure the proxy for you.", "PaRuX non apre una porta del firewall né configura il proxy al posto tuo."),
                        PrimaryButtonText = T("Turn on", "Attiva"),
                        CloseButtonText = T("Cancel", "Annulla"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                    {
                        _webAccessLoading = true;
                        WebAccessSwitch.IsOn = false;
                        _webAccessLoading = false;
                        return;
                    }
                }

                await RemoteAccessClient.Current.SaveWebAccessAsync(
                    new WebAccessConfigRequest { Enabled = turningOn }, CancellationToken.None);
                await RefreshWebAccessAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Toggle web access failed: " + ex);
                WebAccessStatusText.Text = T("Couldn't change browser access: ", "Impossibile cambiare l'accesso dal browser: ") + ex.Message;
                await RefreshWebAccessAsync();
            }
        }

        private async void SignOutWebSessions_Click(object sender, RoutedEventArgs e)
        {
            SignOutWebSessionsButton.IsEnabled = false;
            try
            {
                await RemoteAccessClient.Current.SignOutAllBrowsersAsync(CancellationToken.None);
                WebAccessStatusText.Text = T("Every browser has been signed out.", "Ogni browser è stato disconnesso.");
                await RefreshWebAccessAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Sign out web sessions failed: " + ex);
                WebAccessStatusText.Text = T("Couldn't sign browsers out: ", "Impossibile disconnettere i browser: ") + ex.Message;
            }
        }

        private async void OpenWebClient_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WebAccessStatus status = await RemoteAccessClient.Current.GetWebAccessAsync(CancellationToken.None);
                await Windows.System.Launcher.LaunchUriAsync(new Uri(status.LocalUrl));
            }
            catch (Exception ex)
            {
                Log.Write("Open web client failed: " + ex);
                WebAccessStatusText.Text = T("Couldn't open the browser: ", "Impossibile aprire il browser: ") + ex.Message;
            }
        }

        private async void ApplyRemoteAccess_Click(object sender, RoutedEventArgs e)
        {
            ApplyLanButton.IsEnabled = false;
            try
            {
                string? address = (LanAddressCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                if (LanEnabledSwitch.IsOn && string.IsNullOrWhiteSpace(address))
                {
                    LanStatusText.Text = T("No private IPv4 interface is available. Connect this PC to the local network first.", "Nessuna interfaccia IPv4 privata disponibile. Collega prima questo PC alla rete locale.");
                    return;
                }
                var request = new RemoteAccessConfigRequest
                {
                    Enabled = LanEnabledSwitch.IsOn,
                    Address = address,
                    Port = Math.Clamp((int)LanPortBox.Value, 1024, 65535)
                };
                if (request.Enabled && !_remoteAccessEnabled)
                {
                    var confirmation = new ContentDialog
                    {
                        Title = T("Enable local-network access?", "Attivare l'accesso alla rete locale?"),
                        Content = T("PaRuX will listen only on ", "PaRuX resterà in ascolto solo su ") + address + T(" over HTTPS. It will never open your router or accept public Internet addresses. If Windows Firewall asks, allow Private networks only — never Public networks.", " tramite HTTPS. Non aprirà mai il tuo router né accetterà indirizzi Internet pubblici. Se Windows Firewall lo chiede, consenti solo le reti Private, mai le reti Pubbliche."),
                        PrimaryButtonText = T("Enable LAN only", "Attiva solo LAN"),
                        CloseButtonText = T("Cancel", "Annulla"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
                }
                RemoteAccessStatus saved = await RemoteAccessClient.Current.SaveConfigurationAsync(request, CancellationToken.None);
                if (saved.RestartRequired)
                {
                    PairingCodeText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    PairingCodeText.Text = string.Empty;
                    PairingQrBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    PairingQrImage.Source = null;
                    LanStatusText.Text = T("Restarting PaRuX Core with the new local-network boundary…", "Riavvio di PaRuX Core con il nuovo confine di rete locale…");
                    await PaRuXServerController.Current.RestartAsync(null, CancellationToken.None);
                }
                await RefreshRemoteAccessAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Apply remote access failed: " + ex);
                LanStatusText.Text = T("Couldn't apply local-network access: ", "Impossibile applicare l'accesso alla rete locale: ") + ex.Message;
            }
            finally { ApplyLanButton.IsEnabled = true; }
        }

        private async void PairDevice_Click(object sender, RoutedEventArgs e)
        {
            PairDeviceButton.IsEnabled = false;
            try
            {
                PairingStartResponse pairing = await RemoteAccessClient.Current.StartPairingAsync(CancellationToken.None);
                PairingCodeText.Text = T("Code ", "Codice ") + pairing.Code + T("  ·  expires ", "  ·  scade ")
                    + pairing.ExpiresUtc.ToLocalTime().ToString("t") + "\n"
                    + pairing.Endpoint + T("\nCertificate SHA-256: ", "\nSHA-256 del certificato: ") + FormatFingerprint(pairing.CertificateFingerprint);
                PairingCodeText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                PairingQrImage.Source = await BuildPairingQrImageAsync(pairing);
                PairingQrBorder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Write("Create pairing code failed: " + ex.Message);
                PairingCodeText.Text = T("Couldn't create a pairing code: ", "Impossibile creare un codice di associazione: ") + ex.Message;
                PairingCodeText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                PairingQrBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            finally { PairDeviceButton.IsEnabled = LanEnabledSwitch.IsOn; }
        }

        /// <summary>
        /// Renders the pairing code, endpoint, and certificate fingerprint as one QR payload
        /// so PaRuX Android can pair in one scan instead of the user typing all three by hand.
        /// No permanent credential is in the QR: the code is single-use and expires in five
        /// minutes, exactly like the text fallback already shown next to it.
        /// </summary>
        private static async Task<BitmapImage> BuildPairingQrImageAsync(PairingStartResponse pairing)
        {
            string payload = JsonSerializer.Serialize(new
            {
                v = 1,
                endpoint = pairing.Endpoint,
                code = pairing.Code,
                fp = pairing.CertificateFingerprint
            });

            using var generator = new QRCodeGenerator();
            using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            byte[] png = new PngByteQRCode(data).GetGraphic(8);

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        private async Task RefreshRemoteAccessAsync()
        {
            if (LanStatusText == null) return;
            try
            {
                RemoteAccessStatus status = await RemoteAccessClient.Current.GetStatusAsync(CancellationToken.None);
                _loading = true;
                LanEnabledSwitch.IsOn = status.Enabled;
                _remoteAccessEnabled = status.Enabled;
                LanPortBox.Value = status.Port;
                PopulateLanAddresses(status.Address);
                _loading = false;
                PairDeviceButton.IsEnabled = status.Enabled;
                if (!status.Enabled)
                {
                    PairingCodeText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    PairingCodeText.Text = string.Empty;
                    PairingQrBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    PairingQrImage.Source = null;
                }
                LanStatusText.Text = status.Enabled
                    ? T("LAN only · HTTPS · ", "Solo LAN · HTTPS · ") + status.Endpoint + T(" · certificate SHA-256 ", " · SHA-256 del certificato ")
                        + FormatFingerprint(status.CertificateFingerprint) + " · " + DescribeFirewallNeed(status.NetworkCategory)
                    : T("Off · PaRuX accepts connections only from this PC (127.0.0.1).", "Disattivato · PaRuX accetta connessioni solo da questo PC (127.0.0.1).");
                RenderPairedDevices(status.Devices);
            }
            catch (Exception ex)
            {
                _loading = false;
                PairDeviceButton.IsEnabled = false;
                LanStatusText.Text = T("Local-network status unavailable: ", "Stato della rete locale non disponibile: ") + ex.Message;
            }
        }

        private void RenderPairedDevices(List<PairedDeviceInfo> devices)
        {
            PairedDevicesList.Children.Clear();
            if (devices == null || devices.Count == 0)
            {
                PairedDevicesList.Children.Add(new TextBlock
                {
                    Text = T("No paired devices.", "Nessun dispositivo associato."),
                    Opacity = 0.7
                });
                return;
            }
            foreach (PairedDeviceInfo device in devices)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string lastSeen = device.LastSeenUtc.HasValue
                    ? "Last seen " + device.LastSeenUtc.Value.ToLocalTime().ToString("g")
                    : "Never connected";
                row.Children.Add(new TextBlock { Text = device.Name + " · " + lastSeen, TextWrapping = TextWrapping.Wrap });
                var revoke = new Button { Content = T("Revoke", "Revoca"), Tag = device.Id };
                revoke.Click += async (s, e) =>
                {
                    try
                    {
                        await RemoteAccessClient.Current.RevokeAsync((string)((Button)s).Tag, CancellationToken.None);
                        await RefreshRemoteAccessAsync();
                    }
                    catch (Exception ex) { LanStatusText.Text = T("Couldn't revoke device: ", "Impossibile revocare il dispositivo: ") + ex.Message; }
                };
                Grid.SetColumn(revoke, 1);
                row.Children.Add(revoke);
                PairedDevicesList.Children.Add(row);
            }
        }

    /// <summary>
    /// Says what Windows Firewall actually needs on <em>this</em> machine, rather than assuming the
    /// network is Private.
    ///
    /// The old text always read "must allow PaRuX Core on Private networks only". A private IPv4
    /// address is not a private network: Windows Firewall applies rules by the category the user
    /// assigned, and this PC was measured on [redacted private LAN address] - unmistakably RFC1918 - attached to a
    /// home router that Windows classes as **Public**. PaRuX enabled LAN access, issued a pairing
    /// code, and printed advice that could not be followed, because the prompt Windows raises there
    /// is for a Public network. A phone on the same Wi-Fi then failed with "Couldn't reach PaRuX
    /// PC" and nothing on the PC explained why. Now it does.
    /// </summary>
    private static string DescribeFirewallNeed(string? networkCategory) => networkCategory switch
    {
        "Private" or "Domain" =>
            "Windows classifies this network as " + networkCategory
            + ", so Windows Firewall needs to allow PaRuX Core on that profile.",
        "Public" =>
            "Windows classifies this network as Public. Devices cannot reach PaRuX until you either "
            + "set this network to Private in Windows settings, or allow PaRuX Core on the Public "
            + "profile - PaRuX never changes firewall rules or network categories for you.",
        _ =>
            "Windows Firewall must allow PaRuX Core on this PC's network profile. PaRuX could not "
            + "read which profile Windows assigned, so check it in Windows settings if pairing fails."
    };

        private static string FormatFingerprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unavailable";
            return string.Join(":", Enumerable.Range(0, value.Length / 2).Select(i => value.Substring(i * 2, 2)));
        }

        private void RefreshComputerApps()
        {
            if (ComputerAppsList == null) return;
            ComputerAppsList.Children.Clear();
            List<ComputerAppShortcut> apps = SettingsStore.Current.Data.ComputerApps ??= new List<ComputerAppShortcut>();
            if (apps.Count == 0)
            {
                ComputerAppsList.Children.Add(new TextBlock
                {
                    Text = T("No additional apps. Notepad, Calculator, File Explorer, and Windows Settings are built in.", "Nessuna app aggiuntiva. Blocco note, Calcolatrice, Esplora file e Impostazioni di Windows sono integrati."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7
                });
                return;
            }
            foreach (ComputerAppShortcut app in apps)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Spacing = 2 };
                info.Children.Add(new TextBlock { Text = app.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                info.Children.Add(new TextBlock { Text = app.ExecutablePath, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.68 });
                row.Children.Add(info);
                var remove = new Button { Content = T("Remove", "Rimuovi"), Tag = app.Id };
                Grid.SetColumn(remove, 1);
                remove.Click += (s, e) =>
                {
                    string? id = (s as Button)?.Tag as string;
                    SettingsStore.Current.Data.ComputerApps.RemoveAll(a => a.Id == id);
                    SettingsStore.Current.Save();
                    RefreshComputerApps();
                };
                row.Children.Add(remove);
                ComputerAppsList.Children.Add(row);
            }
        }

        private void ApplyCodingCommands_Click(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.Data.VsCodeCommand = string.IsNullOrWhiteSpace(VsCodeCommandBox.Text) ? "code" : VsCodeCommandBox.Text.Trim();
            SettingsStore.Current.Data.OpenCodeCommand = string.IsNullOrWhiteSpace(OpenCodeCommandBox.Text) ? "opencode" : OpenCodeCommandBox.Text.Trim();
            SettingsStore.Current.Save();
        }

        private async void BrowseAgentWorkspaceRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder selected = await picker.PickSingleFolderAsync();
                if (selected == null) return;
                AgentWorkspaceRootBox.Text = selected.Path;
                SaveAgentWorkspaceRoot();
            }
            catch (Exception ex)
            {
                Log.Write("Agent workspace folder pick failed: " + ex.Message);
            }
        }

        private void AgentWorkspaceRootBox_LostFocus(object sender, RoutedEventArgs e) => SaveAgentWorkspaceRoot();

        private void SaveAgentWorkspaceRoot()
        {
            if (_loading) return;
            SettingsStore.Current.Data.AgentCodingWorkspaceRoot = AgentWorkspaceRootBox.Text?.Trim() ?? string.Empty;
            SettingsStore.Current.Save();
        }

        private void StorageQuotaNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(sender.Value)) return;
            SettingsStore.Current.Data.StorageQuotaGigabytes = Math.Clamp(
                (int)sender.Value, 10, StorageQuotaService.MaximumQuotaGigabytes);
            SettingsStore.Current.Save();
            _ = RefreshStorageUsageAsync();
        }

        private void RefreshStorage_Click(object sender, RoutedEventArgs e) => _ = RefreshStorageUsageAsync();

        private async Task RefreshStorageUsageAsync()
        {
            if (StorageUsageText == null) return;
            StorageBreakdown breakdown = await Task.Run(StorageQuotaService.GetBreakdown);
            StorageUsageText.Text = StorageQuotaService.FormatBytes(breakdown.ManagedBytes) + T(" used of ", " usati su ")
                + SettingsStore.Current.Data.StorageQuotaGigabytes + T(" GB. Custom output folders are outside this managed quota.", " GB. Le cartelle di output personalizzate sono fuori da questa quota gestita.");

            if (StorageBreakdownList == null) return;
            StorageBreakdownList.Children.Clear();
            foreach (StorageCategory category in breakdown.Categories)
            {
                if (category.Bytes == 0 && !category.PaRuXManaged) continue; // no external stack found: skip silently

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock { Text = category.Name + (category.PaRuXManaged ? string.Empty : T("  ·  not counted toward the quota", "  ·  non conteggiato nella quota")) };
                ApplyCaptionStyle(label);
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var size = new TextBlock { Text = StorageQuotaService.FormatBytes(category.Bytes) };
                ApplyCaptionStyle(size);
                Grid.SetColumn(size, 1);
                row.Children.Add(size);

                StorageBreakdownList.Children.Add(row);
            }

            if (AiStorageBudgetText != null)
            {
                AppSettings s = SettingsStore.Current.Data;
                long totalGb = (breakdown.ManagedBytes + breakdown.ExternalBytes) / 1024 / 1024 / 1024;
                AiStorageBudgetText.Text = T("AI storage: ", "Archiviazione IA: ") + StorageQuotaService.FormatBytes(breakdown.ManagedBytes + breakdown.ExternalBytes)
                    + " / " + s.AiStorageBudgetGigabytes + " GB";

                string? brushKey = totalGb >= s.AiStorageWarningGigabytes ? "PaRuXDangerTextBrush"
                    : totalGb >= s.AiStorageBudgetGigabytes ? "PaRuXCautionTextBrush"
                    : null;
                if (brushKey != null && Application.Current.Resources.TryGetValue(brushKey, out object brushResource) && brushResource is Brush brush)
                    AiStorageBudgetText.Foreground = brush;
            }
        }

        private static void ApplyCaptionStyle(TextBlock block)
        {
            object resource;
            if (Application.Current.Resources.TryGetValue("PaRuXCaptionTextStyle", out resource) && resource is Style style)
                block.Style = style;
        }

        private static void ApplyBodyStrongStyle(TextBlock block)
        {
            object resource;
            if (Application.Current.Resources.TryGetValue("PaRuXBodyStrongTextStyle", out resource) && resource is Style style)
                block.Style = style;
        }

        private static void AddDetailRow(StackPanel panel, string label, string value)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var labelBlock = new TextBlock { Text = label };
            ApplyCaptionStyle(labelBlock);
            var valueBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(labelBlock);
            row.Children.Add(valueBlock);
            panel.Children.Add(row);
        }

        // ------------------------------------------------------------------
        // Skills (Skill Inspector - server-owned registry, see SkillsClient)
        // ------------------------------------------------------------------

        private async Task RefreshSkillsAsync()
        {
            if (SkillsList == null) return;
            List<SkillManifest> skills;
            try { skills = await SkillsClient.Current.ListAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshSkillsAsync failed: " + ex.Message);
                if (SkillsCountLabel != null) SkillsCountLabel.Text = T("Failed to load skills: ", "Impossibile caricare le skill: ") + ex.Message;
                return;
            }

            if (SkillsCountLabel != null) SkillsCountLabel.Text = skills.Count + T(" skills", " skill");
            SkillsList.Children.Clear();
            foreach (SkillManifest skill in skills.OrderBy(s => s.Category).ThenBy(s => s.DisplayName))
                SkillsList.Children.Add(BuildSkillCard(skill));
        }

        private void RefreshSkills_Click(object sender, RoutedEventArgs e) => _ = RefreshSkillsAsync();

        private async void SkillsGuide_Click(object sender, RoutedEventArgs e)
        {
            var guide = new ContentDialog
            {
                Title = T("What are Skills?", "Cosa sono le skill?"),
                CloseButtonText = T("Close", "Chiudi"),
                XamlRoot = XamlRoot,
                Content = new ScrollViewer
                {
                    MaxHeight = 480,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text =
                            T("A Skill is a declarative capability, not a saved prompt. Each one declares its ", "Una skill è una capacità dichiarativa, non un prompt salvato. Ognuna dichiara il ")
                            + T("preferred model role, which tools it can use, its Internet/Memory/RAG policy, its ", "ruolo di modello preferito, quali strumenti può usare, i criteri per Internet/Memoria/RAG, i ")
                            + T("file/shell/Computer-Use permissions, a step-by-step workflow, and a timeout — all ", "permessi su file/shell/Uso del computer, un flusso di lavoro passo per passo e un timeout, tutto ")
                            + T("visible right here, nothing hidden.\n\n", "visibile proprio qui, senza nulla di nascosto.\n\n")
                            + T("AUTO: by default, PaRuX matches your message against each skill's trigger phrases ", "AUTO: per impostazione predefinita PaRuX confronta il tuo messaggio con le frasi di attivazione di ogni skill ")
                            + T("and picks the best match (or General, for open-ended chat). This happens for every ", "e sceglie la migliore (oppure Generale, per le chat aperte). Succede per ogni ")
                            + T("message, automatically.\n\n", "messaggio, in automatico.\n\n")
                            + T("FORCED: pick a skill from the composer's \"Skill: Auto\" selector to force every ", "FORZATA: scegli una skill dal selettore \"Skill: Auto\" del compositore per forzare ogni ")
                            + T("message to that skill until you switch back to Auto.\n\n", "messaggio su quella skill finché non torni ad Auto.\n\n")
                            + T("SLASH: type a recognized slash command (e.g. \"/research …\", \"/grill …\") to force ", "SLASH: digita un comando slash riconosciuto (ad es. \"/research …\", \"/grill …\") per forzare ")
                            + T("that one message to the matching skill — it strips itself from what's actually sent.\n\n", "quel singolo messaggio sulla skill corrispondente; il comando viene tolto da ciò che viene realmente inviato.\n\n")
                            + T("Built-in skills are read-only here (Enable/Disable only). \"New skill…\" creates a ", "Le skill integrate qui sono di sola lettura (solo Abilita/Disabilita). \"Nuova skill…\" crea una ")
                            + T("custom skill of your own, stored server-side so it's available from any paired ", "skill personalizzata tua, salvata lato server così è disponibile anche da qualsiasi dispositivo ")
                            + T("device too. An imported skill is treated as untrusted until you review its ", "associato. Una skill importata è considerata non attendibile finché non ne controlli i ")
                            + T("permissions and explicitly enable it.", "permessi e non la abiliti esplicitamente.")
                    }
                }
            };
            await guide.ShowAsync();
        }

        private async void CreateSkill_Click(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox { PlaceholderText = T("Display name, e.g. Meeting Notes", "Nome visualizzato, ad es. Note di riunione") };
            var descriptionBox = new TextBox { PlaceholderText = T("What does it do?", "Cosa fa?"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
            var slashBox = new TextBox { PlaceholderText = T("Slash command, e.g. /notes (optional)", "Comando slash, ad es. /notes (facoltativo)") };
            var roleCombo = new ComboBox { PlaceholderText = T("Preferred model role", "Ruolo di modello preferito"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string role in new[] { "MAIN_LLM", "FAST_LLM", "UNCENSORED_LLM", "CODING_LLM", "VISION_LLM" })
                roleCombo.Items.Add(role);
            roleCombo.SelectedIndex = 0;
            var internetToggle = new ToggleSwitch { Header = T("Needs Internet", "Richiede Internet"), OnContent = T("Required", "Richiesto"), OffContent = T("Off", "No") };

            var form = new StackPanel { Spacing = 10, Width = 380 };
            form.Children.Add(nameBox);
            form.Children.Add(descriptionBox);
            form.Children.Add(slashBox);
            form.Children.Add(roleCombo);
            form.Children.Add(internetToggle);

            var dialog = new ContentDialog
            {
                Title = T("New custom skill", "Nuova skill personalizzata"),
                PrimaryButtonText = T("Create", "Crea"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                Content = form
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

            var manifest = new SkillManifest
            {
                DisplayName = nameBox.Text.Trim(),
                Name = nameBox.Text.Trim().ToLowerInvariant().Replace(' ', '-'),
                Description = descriptionBox.Text?.Trim() ?? string.Empty,
                SlashCommand = string.IsNullOrWhiteSpace(slashBox.Text) ? string.Empty : slashBox.Text.Trim(),
                Category = "Custom",
                Source = "user",
                Enabled = true,
                Platforms = new List<string> { "Windows", "Android" },
                PreferredModelRole = roleCombo.SelectedItem as string ?? "MAIN_LLM",
                InternetPolicy = internetToggle.IsOn ? "required" : "off"
            };
            try
            {
                await SkillsClient.Current.CreateOrUpdateAsync(manifest, CancellationToken.None);
                await RefreshSkillsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("CreateSkill failed: " + ex.Message);
                var error = new ContentDialog { Title = T("Could not create skill", "Impossibile creare la skill"), Content = ex.Message, CloseButtonText = "OK", XamlRoot = XamlRoot };
                await error.ShowAsync();
            }
        }

        private FrameworkElement BuildSkillCard(SkillManifest skill)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
            };
            var stack = new StackPanel { Spacing = 6 };

            bool deletable = skill.Source == "user" || skill.Source == "imported";
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (deletable) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 1 };
            string title = skill.DisplayName + (string.IsNullOrEmpty(skill.SlashCommand) ? string.Empty : "   " + skill.SlashCommand);
            var titleBlock = new TextBlock { Text = title };
            ApplyBodyStrongStyle(titleBlock);
            titleStack.Children.Add(titleBlock);
            var descBlock = new TextBlock { Text = skill.Description, TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(descBlock);
            titleStack.Children.Add(descBlock);
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var enabledSwitch = new ToggleSwitch { IsOn = skill.Enabled, OnContent = "On", OffContent = T("Off", "No") };
            enabledSwitch.Toggled += async (s, e) =>
            {
                if (_switchesBeingReverted.Contains(enabledSwitch)) return;
                bool requested = enabledSwitch.IsOn;
                try { await SkillsClient.Current.SetEnabledAsync(skill.Id, requested, CancellationToken.None); ShowNotice(SkillsStatusText, null); }
                catch (Exception ex)
                {
                    Log.Write("Skill SetEnabled failed: " + ex.Message);
                    ToggleRecovery recovered = await SwitchRevert.ApplyAsync(enabledSwitch, requested, _switchesBeingReverted,
                        async ct => (await SkillsClient.Current.ListAsync(ct)).FirstOrDefault(item => item.Id == skill.Id)?.Enabled);
                    ShowNotice(SkillsStatusText, SwitchRevert.Explain("this skill", requested, ex.Message, recovered));
                }
            };
            Grid.SetColumn(enabledSwitch, 1);
            header.Children.Add(enabledSwitch);

            var detailsButton = new Button { Content = T("Details", "Dettagli") };
            Grid.SetColumn(detailsButton, 2);
            header.Children.Add(detailsButton);

            if (deletable)
            {
                var deleteButton = new Button { Content = T("Delete", "Elimina") };
                deleteButton.Click += async (s, e) =>
                {
                    try { await SkillsClient.Current.DeleteAsync(skill.Id, CancellationToken.None); }
                    catch (Exception ex) { Log.Write("Skill delete failed: " + ex.Message); }
                    await RefreshSkillsAsync();
                };
                Grid.SetColumn(deleteButton, 3);
                header.Children.Add(deleteButton);
            }

            stack.Children.Add(header);

            var details = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            AddDetailRow(details, "Version", skill.Version);
            AddDetailRow(details, "Source", skill.Source + (string.IsNullOrEmpty(skill.License) ? string.Empty : " · " + skill.License));
            AddDetailRow(details, "Category", skill.Category);
            AddDetailRow(details, "Platforms", string.Join(", ", skill.Platforms));
            AddDetailRow(details, "Preferred model", skill.PreferredModelRole
                + (skill.AllowedModelRoles.Count > 0 ? "  (allowed: " + string.Join(", ", skill.AllowedModelRoles) + ")" : string.Empty));
            AddDetailRow(details, "Tools", skill.AllowedTools.Count > 0 ? string.Join(", ", skill.AllowedTools) : "none");
            AddDetailRow(details, "Internet / Memory / RAG", skill.InternetPolicy + " / " + skill.MemoryPolicy + " / " + skill.RagPolicy);
            AddDetailRow(details, "Permissions",
                "Read files: " + skill.Permissions.ReadFiles
                + "   Write files: " + skill.Permissions.WriteFiles
                + "   Shell: " + skill.Permissions.Shell
                + "   Computer Use: " + skill.Permissions.ComputerUse);
            AddDetailRow(details, "Confirmation", skill.ConfirmationPolicy);
            AddDetailRow(details, "Timeout", skill.TimeoutSeconds + "s");
            if (skill.Workflow.Count > 0) AddDetailRow(details, "Workflow", string.Join("  →  ", skill.Workflow));
            if (skill.Dependencies.Count > 0) AddDetailRow(details, "Dependencies", string.Join(", ", skill.Dependencies));
            if (skill.SubSkillIds.Count > 0) AddDetailRow(details, "Sub-skills", string.Join(", ", skill.SubSkillIds));
            if (skill.Parameters.Count > 0)
                AddDetailRow(details, "Parameters", string.Join(", ", skill.Parameters.Select(p => p.Label + " (" + p.Type + ")")));
            stack.Children.Add(details);

            detailsButton.Click += (s, e) =>
            {
                bool show = details.Visibility != Visibility.Visible;
                details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                detailsButton.Content = show ? T("Hide", "Nascondi") : T("Details", "Dettagli");
            };

            card.Child = stack;
            return card;
        }

        // ------------------------------------------------------------------
        // Research
        // ------------------------------------------------------------------

        private async void ResearchAsk_Click(object sender, RoutedEventArgs e)
        {
            string? question = (ResearchQuestionBox?.Text ?? string.Empty).Trim();
            if (question.Length == 0) return;

            string? mode = (ResearchModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "balanced";
            ResearchAskButton.IsEnabled = false;
            ResearchProgress.IsActive = true;
            ResearchProgress.Visibility = Visibility.Visible;
            ResearchAnswer.Visibility = Visibility.Collapsed;
            ResearchSourcesList.Children.Clear();
            try
            {
                ResearchResult result = await ResearchClient.Current.AskAsync(question, mode, CancellationToken.None);
                ResearchAnswer.Text = result.Answer;
                ResearchAnswer.Visibility = Visibility.Visible;
                foreach (ResearchSource source in result.Sources)
                {
                    var row = new HyperlinkButton
                    {
                        Content = source.Title + "  —  " + source.Domain,
                        NavigateUri = Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? uri) ? uri : null
                    };
                    ResearchSourcesList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Research failed: " + ex.Message);
                ResearchAnswer.Text = T("Research failed: ", "Ricerca non riuscita: ") + ex.Message;
                ResearchAnswer.Visibility = Visibility.Visible;
            }
            finally
            {
                ResearchAskButton.IsEnabled = true;
                ResearchProgress.IsActive = false;
                ResearchProgress.Visibility = Visibility.Collapsed;
            }
        }

        // ------------------------------------------------------------------
        // Presentations
        // ------------------------------------------------------------------

        private byte[]? _lastPresentationBytes;
        private string _lastPresentationFileName = "presentation.pptx";

        private async void PresentationGenerate_Click(object sender, RoutedEventArgs e)
        {
            string? topic = (PresentationTopicBox?.Text ?? string.Empty).Trim();
            if (topic.Length == 0) return;
            int slideCount = (int)(PresentationSlideCount?.Value ?? 6);

            PresentationGenerateButton.IsEnabled = false;
            PresentationProgress.IsActive = true;
            PresentationProgress.Visibility = Visibility.Visible;
            PresentationSlidesList.Children.Clear();
            PresentationSaveButton.Visibility = Visibility.Collapsed;
            try
            {
                PresentationResponse result = await PresentationClient.Current.GenerateAsync(topic, slideCount, CancellationToken.None);
                _lastPresentationBytes = Convert.FromBase64String(result.Base64Data);
                _lastPresentationFileName = result.FileName;
                foreach (PresentationSlideDto slide in result.Slides)
                {
                    var slideStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
                    var titleBlock = new TextBlock { Text = slide.Title, TextWrapping = TextWrapping.Wrap };
                    ApplyBodyStrongStyle(titleBlock);
                    slideStack.Children.Add(titleBlock);
                    foreach (string bullet in slide.Bullets)
                    {
                        var bulletBlock = new TextBlock { Text = "•  " + bullet, TextWrapping = TextWrapping.Wrap };
                        ApplyCaptionStyle(bulletBlock);
                        slideStack.Children.Add(bulletBlock);
                    }
                    PresentationSlidesList.Children.Add(slideStack);
                }
                PresentationSaveButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Write("Presentation generation failed: " + ex.Message);
                var errorBlock = new TextBlock { Text = T("Generation failed: ", "Generazione non riuscita: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(errorBlock);
                PresentationSlidesList.Children.Add(errorBlock);
            }
            finally
            {
                PresentationGenerateButton.IsEnabled = true;
                PresentationProgress.IsActive = false;
                PresentationProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void PresentationSave_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPresentationBytes == null) return;
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(_lastPresentationFileName) };
                picker.FileTypeChoices.Add("PowerPoint", new List<string> { ".pptx" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await Windows.Storage.FileIO.WriteBytesAsync(file, _lastPresentationBytes);
            }
            catch (Exception ex)
            {
                Log.Write("PresentationSave failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Speech (STT/TTS)
        // ------------------------------------------------------------------

        private async Task RefreshSpeechEngineStatusAsync()
        {
            if (SpeechEngineStatusText == null) return;
            try
            {
                SpeechEngineStatus status = await SpeechClient.Current.GetStatusAsync(CancellationToken.None);
                SpeechEngineStatusText.Text = T("STT (Parakeet): ", "STT (Parakeet): ") + (status.SttInstalled ? T("installed", "installato") : T("not installed", "non installato"))
                    + T("    ·    TTS (Kokoro): ", "    ·    TTS (Kokoro): ") + (status.TtsInstalled ? T("installed", "installato") : T("not installed", "non installato"));
            }
            catch (Exception ex)
            {
                Log.Write("RefreshSpeechEngineStatusAsync failed: " + ex.Message);
                SpeechEngineStatusText.Text = T("Could not reach the speech engine: ", "Impossibile raggiungere il motore vocale: ") + ex.Message;
            }
        }

        private async void TtsSpeak_Click(object sender, RoutedEventArgs e)
        {
            string? text = (TtsTextBox?.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            TtsSpeakButton.IsEnabled = false;
            TtsStatus.Text = T("Synthesizing locally…", "Sintesi in locale…");
            try
            {
                SynthesizeResponse result = await SpeechClient.Current.SynthesizeAsync(text, null, CancellationToken.None);
                byte[] bytes = Convert.FromBase64String(result.Base64Data);
                string extension = result.MediaType.Contains("mpeg") ? ".mp3" : ".wav";
                string path = Path.Combine(Path.GetTempPath(), "parux-tts-" + Guid.NewGuid().ToString("N") + extension);
                await File.WriteAllBytesAsync(path, bytes);
                TtsPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                TtsPlayer.MediaPlayer.Play();
                TtsStatus.Text = T("Spoken locally · ", "Pronunciato in locale · ") + result.DurationSeconds.ToString("0.0") + "s";
            }
            catch (Exception ex)
            {
                Log.Write("TtsSpeak failed: " + ex.Message);
                TtsStatus.Text = T("Speech failed: ", "Sintesi vocale non riuscita: ") + ex.Message;
            }
            finally
            {
                TtsSpeakButton.IsEnabled = true;
            }
        }

        private async void SttPick_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                SttPickButton.IsEnabled = false;
                SttResult.Visibility = Visibility.Visible;
                SttResult.Text = T("Transcribing locally…", "Trascrizione in locale…");
                byte[] bytes = await File.ReadAllBytesAsync(file.Path);
                TranscribeResponse result = await SpeechClient.Current.TranscribeAsync(Convert.ToBase64String(bytes), file.Name, CancellationToken.None);
                SttResult.Text = result.Text + (string.IsNullOrEmpty(result.LanguageDetected) ? string.Empty : T("\n\n(detected: ", "\n\n(rilevato: ") + result.LanguageDetected + ")");
            }
            catch (Exception ex)
            {
                Log.Write("SttPick failed: " + ex.Message);
                SttResult.Visibility = Visibility.Visible;
                SttResult.Text = T("Transcription failed: ", "Trascrizione non riuscita: ") + ex.Message;
            }
            finally
            {
                SttPickButton.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------
        // Model Downloads (in-app Model Manager: HF search + budgeted download)
        // ------------------------------------------------------------------

        private async Task RefreshModelBudgetAsync()
        {
            if (ModelBudgetText == null) return;
            try
            {
                AiStorageBudgetStatus budget = await ModelDownloadClient.Current.GetBudgetAsync(CancellationToken.None);
                ModelBudgetText.Text = T("Downloaded models: ", "Modelli scaricati: ") + StorageQuotaService.FormatBytes(budget.UsedBytes)
                    + " / " + StorageQuotaService.FormatBytes(budget.LimitBytes)
                    + (budget.WithinBudget ? string.Empty : T("  — over budget", "  — oltre il budget"));
            }
            catch (Exception ex)
            {
                Log.Write("RefreshModelBudgetAsync failed: " + ex.Message);
                ModelBudgetText.Text = string.Empty;
            }
        }

        private void RefreshManagedModels_Click(object sender, RoutedEventArgs e) => _ = RefreshManagedModelsAsync();

        /// <summary>Roles a Managed Model Library file can actually be loaded into - the text/GGUF
        /// role slots (see LocalAiPaths.KnownTextRoles server-side). Media/fixed-engine/
        /// not-implemented roles are not offered here since a GGUF load has no meaning for them.</summary>
        private static readonly (string Role, string Label)[] LoadableRoles =
        {
            ("standard", "Main"), ("uncensored", "Uncensored"), ("fast", "Fast"), ("reasoning", "Reasoning"),
            ("planner", "Planner"), ("coding", "Coding"), ("reviewer", "Reviewer"), ("vision", "Vision")
        };

        private async Task RefreshManagedModelsAsync()
        {
            if (ManagedModelsList == null) return;
            ManagedModelsList.Children.Clear();
            await RefreshRuntimeManagerStatusAsync();
            try
            {
                List<ManagedModelInfo> models = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None);
                if (models.Count == 0)
                {
                    var empty = new TextBlock { Text = T("No managed GGUF models yet.", "Nessun modello GGUF gestito per ora.") };
                    ApplyCaptionStyle(empty);
                    ManagedModelsList.Children.Add(empty);
                    return;
                }
                foreach (ManagedModelInfo model in models)
                {
                    var card = new StackPanel { Spacing = 4 };

                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    string title = string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias + "  (" + model.FileName + ")";
                    string benchmarkSuffix = model.LastBenchmarkTokensPerSecond.HasValue
                        ? "  ·  " + model.LastBenchmarkTokensPerSecond.Value.ToString("F1") + " tok/s"
                        : string.Empty;
                    var label = new TextBlock
                    {
                        Text = title + "  ·  " + StorageQuotaService.FormatBytes(model.SizeBytes)
                            + "  ·  " + model.Quantization + benchmarkSuffix,
                        TextWrapping = TextWrapping.Wrap
                    };
                    ApplyCaptionStyle(label);
                    row.Children.Add(label);
                    string capturedName = model.FileName;
                    long capturedSize = model.SizeBytes;
                    var delete =new Button { Content = T("Delete", "Elimina"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    delete.Click += async (s, e2) =>
                    {
                        delete.IsEnabled = false;
                        (bool deleted, string message) = await ManagedModelDeleteFlow.RunAsync(XamlRoot, capturedName, capturedSize);
                        if (deleted)
                        {
                            await RefreshManagedModelsAsync();
                            await RefreshModelBudgetAsync();
                            await RefreshDetectedModelsAsync();
                        }
                        else
                        {
                            delete.IsEnabled = true;
                            Log.Write("Settings delete: " + message);
                        }
                    };
                    Grid.SetColumn(delete, 1);
                    row.Children.Add(delete);
                    card.Children.Add(row);

                    var aliasRow = new Grid { ColumnSpacing = 8 };
                    aliasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    aliasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var aliasBox = new TextBox { PlaceholderText = T("Alias (display name)…", "Alias (nome visualizzato)…"), Text = model.Alias ?? string.Empty };
                    Grid.SetColumn(aliasBox, 0);
                    aliasRow.Children.Add(aliasBox);
                    var saveAlias = new Button { Content = T("Save alias", "Salva l'alias"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    saveAlias.Click += async (s, e2) =>
                    {
                        saveAlias.IsEnabled = false;
                        try
                        {
                            await ModelDownloadClient.Current.SetAliasAsync(capturedName, aliasBox.Text, CancellationToken.None);
                            await RefreshManagedModelsAsync();
                        }
                        catch (Exception ex) { Log.Write("Set alias failed: " + ex.Message); }
                        finally { saveAlias.IsEnabled = true; }
                    };
                    Grid.SetColumn(saveAlias, 1);
                    aliasRow.Children.Add(saveAlias);
                    card.Children.Add(aliasRow);

                    var loadRow = new Grid { ColumnSpacing = 8 };
                    loadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    loadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var roleBox = new ComboBox { PlaceholderText = T("Role…", "Ruolo…") };
                    foreach ((string role, string roleLabel) in LoadableRoles)
                        roleBox.Items.Add(new ComboBoxItem { Content = roleLabel, Tag = role });
                    roleBox.SelectedIndex = 0;
                    Grid.SetColumn(roleBox, 0);
                    loadRow.Children.Add(roleBox);
                    var load = new Button { Content = T("Load", "Carica"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    load.Click += async (s, e2) =>
                    {
                        if (roleBox.SelectedItem is not ComboBoxItem selected || selected.Tag is not string role) return;
                        load.IsEnabled = false;
                        try
                        {
                            // This control reads "Load <model> as <role>", so the user is choosing
                            // that role's model on purpose and the assignment is persisted. Callers
                            // that only need the model resident for one run - benchmarks, probes,
                            // comparisons - leave PersistRoleAssignment off and no longer rewrite
                            // the user's default profile as a side effect.
                            RuntimeLoadResult result = await ModelRolesClient.Current.LoadManagedModelAsync(
                                capturedName, role, CancellationToken.None, persistRoleAssignment: true);
                            if (RuntimeManagerStatusText != null)
                                RuntimeManagerStatusText.Text = result.Success
                                    ? T($"Loaded {capturedName} as {role}.", $"Caricato {capturedName} come {role}.")
                                    : T($"Couldn't load {capturedName} as {role}: ", $"Impossibile caricare {capturedName} come {role}: ") + result.Message;
                            await RefreshRuntimeManagerStatusAsync();
                        }
                        catch (Exception ex) { Log.Write("Load managed model failed: " + ex.Message); }
                        finally { load.IsEnabled = true; }
                    };
                    Grid.SetColumn(load, 1);
                    loadRow.Children.Add(load);
                    card.Children.Add(loadRow);

                    ManagedModelsList.Children.Add(card);
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshManagedModelsAsync failed: " + ex.Message);
                var error = new TextBlock { Text = T("Couldn't load managed models: ", "Impossibile caricare i modelli gestiti: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(error);
                ManagedModelsList.Children.Add(error);
            }
        }

        private async Task RefreshRuntimeManagerStatusAsync()
        {
            if (RuntimeManagerStatusText == null) return;
            try
            {
                RuntimeManagerStatus status = await ModelRolesClient.Current.GetRuntimeStatusAsync(CancellationToken.None);
                string gpu = status.GpuDetected ? status.VramTotalMib / 1024 + " GB VRAM detected" : "no capable GPU detected";
                string active = status.ActiveRole != null
                    ? "role \"" + status.ActiveRole + "\" loaded (" + (status.ActiveProfile ?? "unknown profile") + ")"
                    : "no role currently loaded";
                string text = "Runtime Manager: " + gpu + "; " + active
                    + (status.GamingActive ? "; Gaming Mode active (loads blocked)" : string.Empty) + ".";

                try
                {
                    RuntimeDiagnosticsReport diagnostics = await ModelRolesClient.Current.GetRuntimeDiagnosticsAsync(CancellationToken.None);
                    if (diagnostics.Issues.Count > 0)
                        text += "\n" + string.Join("\n", diagnostics.Issues.Select(i => "• " + i.Message));
                }
                catch (Exception ex) { Log.Write("GetRuntimeDiagnosticsAsync failed: " + ex.Message); }

                RuntimeManagerStatusText.Text = text;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshRuntimeManagerStatusAsync failed: " + ex.Message);
                RuntimeManagerStatusText.Text = T("Runtime Manager status unavailable: ", "Stato del Runtime Manager non disponibile: ") + ex.Message;
            }
        }

        private async void UnloadManagedModel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RuntimeLoadResult result = await ModelRolesClient.Current.UnloadManagedModelAsync(CancellationToken.None);
                if (RuntimeManagerStatusText != null)
                    RuntimeManagerStatusText.Text = result.Success ? T("Stopped.", "Fermato.") : T("Couldn't stop: ", "Impossibile fermare: ") + result.Message;
                await RefreshRuntimeManagerStatusAsync();
            }
            catch (Exception ex) { Log.Write("UnloadManagedModel_Click failed: " + ex.Message); }
        }

        /// <summary>Curated "which model should I get" picks, rated against this machine's own
        /// hardware (ModelFitClassifier) - the counterpart to Odysseus's model-discovery/
        /// hardware-fit catalog. Picking one just re-runs the same real HF search below by repo id;
        /// nothing here is a separate download path.</summary>
        private async Task RefreshRecommendedModelsAsync()
        {
            if (RecommendedModelsList == null) return;
            RecommendedModelsList.Children.Clear();
            try
            {
                List<RecommendedModel> recommended = await ModelDownloadClient.Current.GetRecommendedAsync(CancellationToken.None);
                ResourceSnapshot resources = await ResourceMonitorService.GetSnapshotAsync(CancellationToken.None);

                foreach (RecommendedModel rec in recommended)
                {
                    ModelFit fit = ModelFitClassifier.Classify(new GgufModelInfo { SizeBytes = rec.EstimatedSizeBytes }, resources);

                    var card = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
                    var titleRow = new Grid { ColumnSpacing = 8 };
                    titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var title = new TextBlock { Text = rec.Title + "  ·  " + rec.Category };
                    ApplyBodyStrongStyle(title);
                    Grid.SetColumn(title, 0);
                    titleRow.Children.Add(title);
                    var findButton = new Button { Content = T("Find on Hugging Face", "Trova su Hugging Face") };
                    findButton.Click += (s, e2) =>
                    {
                        ModelSearchBox.Text = rec.HuggingFaceRepoId;
                        _ = RunModelSearchAsync(rec.HuggingFaceRepoId);
                    };
                    Grid.SetColumn(findButton, 1);
                    titleRow.Children.Add(findButton);
                    card.Children.Add(titleRow);

                    var fitLabel = new TextBlock { Text = rec.ApproxParams + "  ·  " + rec.QuantHint + "  ·  " + ModelFitClassifier.Describe(fit) };
                    ApplyCaptionStyle(fitLabel);
                    card.Children.Add(fitLabel);

                    var description = new TextBlock { Text = rec.Description, TextWrapping = TextWrapping.Wrap };
                    ApplyCaptionStyle(description);
                    card.Children.Add(description);

                    RecommendedModelsList.Children.Add(card);
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshRecommendedModelsAsync failed: " + ex.Message);
            }
        }

        private async void ModelSearch_Click(object sender, RoutedEventArgs e)
        {
            string? query = (ModelSearchBox?.Text ?? string.Empty).Trim();
            if (query.Length == 0) return;
            await RunModelSearchAsync(query);
        }

        private async Task RunModelSearchAsync(string query)
        {
            if (query.Length == 0) return;

            ModelSearchProgress.IsActive = true;
            ModelSearchProgress.Visibility = Visibility.Visible;
            ModelSearchResults.Children.Clear();
            try
            {
                List<ModelSearchResult> results = await ModelDownloadClient.Current.SearchAsync(query, 10, CancellationToken.None);
                foreach (ModelSearchResult repo in results)
                {
                    var repoStack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
                    var repoTitleRow = new Grid { ColumnSpacing = 8 };
                    repoTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    repoTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var repoTitle = new TextBlock { Text = repo.RepoId + "   (" + repo.Downloads + T(" downloads, ", " download, ") + repo.Likes + T(" likes)", " mi piace)"), TextWrapping = TextWrapping.Wrap };
                    ApplyBodyStrongStyle(repoTitle);
                    repoTitleRow.Children.Add(repoTitle);
                    var detailsButton = new Button { Content = T("Details", "Dettagli"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    detailsButton.Click += async (s, e2) => await ShowModelDetailsAsync(repo.RepoId, repoStack, detailsButton);
                    Grid.SetColumn(detailsButton, 1);
                    repoTitleRow.Children.Add(detailsButton);
                    repoStack.Children.Add(repoTitleRow);
                    foreach (ModelFileOption file in repo.GgufFiles)
                    {
                        var row = new Grid { ColumnSpacing = 8 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var label = new TextBlock { Text = file.FileName + "  ·  " + StorageQuotaService.FormatBytes(file.SizeBytes) };
                        ApplyCaptionStyle(label);
                        Grid.SetColumn(label, 0);
                        row.Children.Add(label);
                        var download = new Button { Content = T("Download", "Download") };
                        download.Click += async (s, e2) =>
                        {
                            download.IsEnabled = false;
                            try
                            {
                                ModelDownloadStatus? started = null;
                                if (!await ModelLicenseConsentPrompt.ConfirmAndStartRepositoryDownloadAsync(
                                    XamlRoot, repo.RepoId, file.FileName, async (revision, licenseEvidence) =>
                                    {
                                        var download = new ModelDownloadRequest
                                        {
                                            RepoId = repo.RepoId, FileName = file.FileName, DownloadUrl = file.DownloadUrl, SizeBytes = file.SizeBytes,
                                            LicenseEvidence = licenseEvidence
                                        };
                                        ModelDownloadClient.PinRequestToRevision(download, revision);
                                        started = await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                                    }))
                                {
                                    download.IsEnabled = true;
                                    return;
                                }
                                if (started != null) _ = PollModelDownloadAsync(started.Id);
                            }
                            catch (Exception ex)
                            {
                                Log.Write("Model download start failed: " + ex.Message);
                                download.IsEnabled = true;
                            }
                        };
                        Grid.SetColumn(download, 1);
                        row.Children.Add(download);
                        repoStack.Children.Add(row);
                    }
                    ModelSearchResults.Children.Add(repoStack);
                }
                if (results.Count == 0)
                {
                    var empty = new TextBlock { Text = T("No GGUF models found for that search.", "Nessun modello GGUF trovato per questa ricerca.") };
                    ApplyCaptionStyle(empty);
                    ModelSearchResults.Children.Add(empty);
                }
            }
            catch (Exception ex)
            {
                Log.Write("ModelSearch failed: " + ex.Message);
                var errorBlock = new TextBlock { Text = T("Search failed: ", "Ricerca non riuscita: ") + ex.Message, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(errorBlock);
                ModelSearchResults.Children.Add(errorBlock);
            }
            finally
            {
                ModelSearchProgress.IsActive = false;
                ModelSearchProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async Task ShowModelDetailsAsync(string repoId, StackPanel host, Button button)
        {
            button.IsEnabled = false;
            try
            {
                ModelRepoDetails details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, CancellationToken.None);
                var panel = new StackPanel { Spacing = 3, Margin = new Thickness(8, 4, 0, 8) };
                string summary = (details.PipelineTag ?? "unknown family") + "  ·  "
                    + (string.IsNullOrWhiteSpace(details.License) ? "license not declared" : details.License)
                    + "  ·  " + details.Files.Count + " GGUF variant(s)";
                var summaryText = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(summaryText);
                panel.Children.Add(summaryText);

                if (details.Tags != null && details.Tags.Count > 0)
                {
                    // Real repo-declared tags (already fetched by GetDetailsAsync, previously
                    // never rendered anywhere - a genuine UI-only gap, not a data gap) - bounded
                    // to a readable count, same "+N more" convention the variants list below uses.
                    const int maxTagsShown = 15;
                    string tagsLine = "Tags: " + string.Join(", ", details.Tags.Take(maxTagsShown))
                        + (details.Tags.Count > maxTagsShown ? $"  (+{details.Tags.Count - maxTagsShown} more)" : string.Empty);
                    var tagsText = new TextBlock { Text = tagsLine, TextWrapping = TextWrapping.Wrap };
                    ApplyCaptionStyle(tagsText);
                    panel.Children.Add(tagsText);
                }

                ResourceSnapshot resources = await ResourceMonitorService.GetSnapshotAsync(CancellationToken.None);
                string hardware = "Hardware: " + ResourceHardwareSnapshotMapper.DescribeGpu(resources)
                    + (resources.RamAvailable
                        ? " · " + resources.RamTotalMb.ToString("N0") + " MB RAM"
                        : " · RAM total unavailable");
                var hardwareText = new TextBlock { Text = hardware, TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(hardwareText);
                panel.Children.Add(hardwareText);

                foreach (ModelFileDetails file in details.Files.Take(12))
                {
                    string variant = file.FileName + "  ·  " + (file.Quantization ?? "unknown quantization");
                    if (file.SizeBytes.HasValue)
                    {
                        variant += "  ·  " + StorageQuotaService.FormatBytes(file.SizeBytes.Value);
                        variant += "  ·  " + ModelFitClassifier.Describe(ModelFitClassifier.Classify(
                            new GgufModelInfo { SizeBytes = file.SizeBytes.Value }, resources));
                    }
                    var variantText = new TextBlock { Text = variant, TextWrapping = TextWrapping.Wrap };
                    ApplyCaptionStyle(variantText);
                    panel.Children.Add(variantText);
                }
                if (details.Files.Count > 12)
                {
                    var more = new TextBlock { Text = "+" + (details.Files.Count - 12) + T(" more variants", " altre varianti") };
                    ApplyCaptionStyle(more);
                    panel.Children.Add(more);
                }
                if (!string.IsNullOrWhiteSpace(details.Readme))
                {
                    string excerpt = details.Readme.Trim();
                    if (excerpt.Length > 800) excerpt = excerpt[..800] + "…";
                    var readme = new TextBlock { Text = excerpt, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
                    ApplyCaptionStyle(readme);
                    panel.Children.Add(readme);
                }
                host.Children.Insert(1, panel);
            }
            catch (Exception ex)
            {
                Log.Write("ShowModelDetailsAsync failed: " + ex.Message);
            }
            finally { button.IsEnabled = true; }
        }

        private async Task PollModelDownloadAsync(string id)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = T("Starting…", "Avvio…") };
            ApplyCaptionStyle(label);
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
            var cancel = new Button { Content = T("Cancel", "Annulla") };
            cancel.Click += async (s, e) => await CancelDownloadRowAsync(id, label, cancel);
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
            ModelDownloadsList?.Children.Add(row);

            ModelDownloadStatus? status = await FollowDownloadRowAsync(id, label, cancel);
            if (status == null) return;

            label.Text = DownloadLine(status, null);
            if (status.Status == "failed" && !string.IsNullOrEmpty(status.Error)) label.Text += "  (" + status.Error + ")";
            // A completed download whose checksum didn't match Hugging Face's own hash is
            // still saved (see ModelDownloadService) rather than silently discarded, so the
            // user needs a visible warning here - the whole point of verifying is lost if a
            // corrupt file otherwise looks identical to a good one in this list.
            if (status.Status == "completed" && status.VerificationStatus == "failed")
                label.Text += "  ⚠ Checksum mismatch - this file may be corrupt, re-download it";
            _ = RefreshModelBudgetAsync();
            if (status.Status == "completed" && status.VerificationStatus != "failed"
                && status.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                _ = PollAutomaticBenchmarksAsync();
        }

        private static async Task<ModelDownloadStatus?> ReadDownloadAsync(string id, CancellationToken ct) =>
            (await ModelDownloadClient.Current.ListDownloadsAsync(ct)).FirstOrDefault(d => d.Id == id);

        private static string DownloadLine(ModelDownloadStatus status, string? note)
        {
            double percent = status.TotalBytes > 0 ? 100.0 * status.BytesDownloaded / status.TotalBytes : 0;
            string line = status.FileName + "  ·  " + status.Status + "  ·  " + percent.ToString("0") + "%";
            return note == null ? line : line + "  ·  " + note;
        }

        /// <summary>
        /// Keeps a download row current until the download reaches a final state. Returns that final status. Returns
        /// null when the page stopped following it, after saying so on the row. Cancel stays visible when the page lost
        /// track after failed reads or the read limit, so the user can still ask the server to stop the transfer; it is
        /// hidden when the server no longer lists the download.
        /// </summary>
        private async Task<ModelDownloadStatus?> FollowDownloadRowAsync(string id, TextBlock label, Button cancel)
        {
            StatusPollResult<ModelDownloadStatus> outcome = await BoundedStatusPoller.RunAsync<ModelDownloadStatus>(
                ct => ReadDownloadAsync(id, ct),
                status => status.Status is "completed" or "failed" or "cancelled",
                ModelDownloadStatusPolicy.Follow,
                status => label.Text = DownloadLine(status, cancel.Tag as string),
                onFailure: (error, failures) => label.Text = T("Status unavailable, retrying (", "Stato non disponibile, nuovo tentativo (") + failures + "): " + error,
                delay: null,
                CancellationToken.None);

            if (outcome.End == StatusPollEnd.Terminal)
            {
                cancel.Visibility = Visibility.Collapsed;
                return outcome.Last;
            }
            if (outcome.End == StatusPollEnd.NotListed)
            {
                cancel.Visibility = Visibility.Collapsed;
                label.Text = T("No longer listed by the server.", "Non più elencato dal server.");
                return null;
            }
            label.Text = T("Lost track of this download: ", "Perso il filo di questo download: ") + (outcome.LastError ?? T("no final state was reported", "nessuno stato finale è stato segnalato"))
                + T(". It may still be running; check the Model Hub.", ". Potrebbe essere ancora in corso; controlla il Model Hub.");
            return null;
        }

        /// <summary>
        /// Asks the server to stop a download and writes the outcome on the row. The next status read keeps the note on the
        /// line; the write here shows it when the page has already stopped following. A failure re-enables Cancel for a retry.
        /// </summary>
        private async Task CancelDownloadRowAsync(string id, TextBlock label, Button cancel)
        {
            cancel.IsEnabled = false;
            string note;
            try
            {
                await ModelDownloadClient.Current.CancelDownloadAsync(id, CancellationToken.None);
                note = "cancel requested";
            }
            catch (Exception ex)
            {
                Log.Write("CancelDownload failed: " + ex.Message);
                note = "cancel failed: " + ex.Message;
                cancel.IsEnabled = true;
            }
            cancel.Tag = note;
            string suffix = "  (" + note + ")";
            if (!label.Text.EndsWith(suffix, StringComparison.Ordinal)) label.Text += suffix;
        }

        private void RefreshModelBenchmarks_Click(object sender, RoutedEventArgs e) => _ = RefreshModelBenchmarksAsync();

        private async Task RefreshModelBenchmarksAsync()
        {
            if (ModelBenchmarksList == null) return;
            try
            {
                List<ModelBenchmarkSummary> models = await ModelDownloadClient.Current.ListBenchmarkModelsAsync(CancellationToken.None);
                ModelBenchmarksList.Children.Clear();
                if (models.Count == 0)
                {
                    var empty = new TextBlock { Text = T("No installed GGUF text models found.", "Nessun modello di testo GGUF installato trovato.") };
                    ApplyCaptionStyle(empty);
                    ModelBenchmarksList.Children.Add(empty);
                    return;
                }

                foreach (ModelBenchmarkSummary model in models)
                {
                    var card = new StackPanel { Spacing = 4 };
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    string usage = model.Usage switch
                    {
                        "standard" => " · Standard (flagship)",
                        "uncensored" => " · Uncensored",
                        "fallback" => " · Fallback only",
                        _ => string.Empty
                    };
                    var title = new TextBlock { Text = model.Label + usage, TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] };
                    row.Children.Add(title);
                    var button = new Button { Content = T("Benchmark", "Benchmark"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                    bool active = model.LastRun?.Status is "queued" or "running";
                    button.IsEnabled = !active;
                    Grid.SetColumn(button, 1);
                    row.Children.Add(button);
                    card.Children.Add(row);

                    string result = model.AverageTokensPerSecond.HasValue
                        ? "Average: " + model.AverageTokensPerSecond.Value.ToString("0.00") + " token/s · "
                          + model.CompletedRunCount + " completed run(s) · " + (model.HardwareKey ?? model.Backend ?? "unknown hardware")
                        : "Not benchmarked yet.";
                    if (active && model.LastRun != null) result += "  ·  " + model.LastRun.Stage;
                    else if (model.LastRun?.Status == "failed") result += "  ·  Last run failed: " + model.LastRun.Error;
                    var status = new TextBlock { Text = result, TextWrapping = TextWrapping.Wrap };
                    ApplyCaptionStyle(status);
                    card.Children.Add(status);

                    // Shown only while this model's benchmark runs; the run id it cancels is kept in its Tag.
                    var cancelRun = new Button { Content = T("Cancel benchmark", "Annulla il benchmark"), FontSize = 12, Padding = new Thickness(8, 2, 8, 2), Visibility = Visibility.Collapsed };
                    card.Children.Add(cancelRun);
                    cancelRun.Click += async (s, e) => await CancelModelBenchmarkAsync(cancelRun, status);

                    string capturedPath = model.ModelPath, capturedLabel = model.Label;
                    button.Click += async (s, e) => await RunModelBenchmarkAsync(capturedPath, capturedLabel, button, cancelRun, status);
                    ModelBenchmarksList.Children.Add(card);
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshModelBenchmarksAsync failed: " + ex.Message);
            }
        }

        private async Task RunModelBenchmarkAsync(string path, string label, Button button, Button cancelRun, TextBlock statusText)
        {
            button.IsEnabled = false;
            try
            {
                ModelBenchmarkRun started = await ModelDownloadClient.Current.StartBenchmarkAsync(
                    new ModelBenchmarkRequest { ModelPath = path, Label = label }, CancellationToken.None);
                string runId = started.Id;
                cancelRun.Tag = runId;
                cancelRun.IsEnabled = true;
                cancelRun.Visibility = Visibility.Visible;

                StatusPollResult<ModelBenchmarkRun> outcome = await BoundedStatusPoller.RunAsync<ModelBenchmarkRun>(
                    async ct => await ModelDownloadClient.Current.GetBenchmarkAsync(runId, ct),
                    run => run.Status is "completed" or "failed" or "cancelled",
                    RunStatusPolicy,
                    run => statusText.Text = run.Stage,
                    onFailure: null,
                    delay: null,
                    CancellationToken.None);

                ModelBenchmarkRun? final = outcome.Last;
                statusText.Text = outcome.End switch
                {
                    StatusPollEnd.Terminal when final is { Status: "failed" } failed => T("Benchmark failed: ", "Benchmark non riuscito: ") + failed.Error,
                    StatusPollEnd.Terminal when final is { Status: "cancelled" } => T("Benchmark cancelled.", "Benchmark annullato."),
                    StatusPollEnd.Terminal => final?.Stage ?? T("Benchmark completed.", "Benchmark completato."),
                    StatusPollEnd.NotListed => LostTrackOfRun(T("the server no longer lists this benchmark", "il server non elenca più questo benchmark")),
                    StatusPollEnd.GaveUp => LostTrackOfRun(outcome.LastError ?? T("the status request failed", "la richiesta di stato non è riuscita")),
                    _ => LostTrackOfRun(T("no final result after about ", "nessun risultato finale dopo circa ") + RunPollMinutes + T(" minutes; it may still be running on the server", " minuti; potrebbe essere ancora in esecuzione sul server")),
                };
                await RefreshModelBenchmarksAsync();
            }
            catch (Exception ex)
            {
                Log.Write("RunModelBenchmarkAsync failed: " + ex.Message);
                statusText.Text = T("Benchmark failed: ", "Benchmark non riuscito: ") + ex.Message;
                button.IsEnabled = true;
            }
            finally
            {
                cancelRun.Tag = null;
                cancelRun.Visibility = Visibility.Collapsed;
            }
        }

        private async Task CancelModelBenchmarkAsync(Button cancelRun, TextBlock statusText)
        {
            if (cancelRun.Tag is not string runId) return;
            cancelRun.IsEnabled = false;
            try
            {
                await ModelDownloadClient.Current.CancelBenchmarkAsync(runId, CancellationToken.None);
                statusText.Text = T("Cancelling benchmark…", "Annullamento del benchmark…");
            }
            catch (Exception ex)
            {
                Log.Write("CancelModelBenchmark failed: " + ex.Message);
                statusText.Text = T("Couldn't cancel the benchmark: ", "Impossibile annullare il benchmark: ") + ex.Message;
                cancelRun.IsEnabled = true;
            }
        }

        private async Task PollAutomaticBenchmarksAsync()
        {
            if (_benchmarkPollActive) return;
            _benchmarkPollActive = true;
            try
            {
                for (int i = 0; i < 800; i++)
                {
                    await Task.Delay(1500);
                    List<ModelBenchmarkSummary> models = await ModelDownloadClient.Current.ListBenchmarkModelsAsync(CancellationToken.None);
                    await RefreshModelBenchmarksAsync();
                    if (!models.Any(m => m.LastRun?.Status is "queued" or "running")) break;
                }
            }
            catch (Exception ex) { Log.Write("PollAutomaticBenchmarksAsync failed: " + ex.Message); }
            finally { _benchmarkPollActive = false; }
        }

        // ------------------------------------------------------------------
        // Model Compare (blind side-by-side test - see ModelCompareClient / ModelCompareService)
        // ------------------------------------------------------------------

        private async Task RefreshCompareModelsAsync()
        {
            if (CompareModelACombo == null || CompareModelBCombo == null) return;
            try
            {
                List<ModelCompareAvailableModel> models = await ModelCompareClient.Current.GetAvailableModelsAsync(CancellationToken.None);

                CompareModelACombo.Items.Clear();
                CompareModelBCombo.Items.Clear();
                foreach (ModelCompareAvailableModel model in models)
                {
                    string content = model.Label + "  ·  " + StorageQuotaService.FormatBytes(model.SizeBytes);
                    CompareModelACombo.Items.Add(new ComboBoxItem { Content = content, Tag = model });
                    CompareModelBCombo.Items.Add(new ComboBoxItem { Content = content, Tag = model });
                }
                if (CompareModelACombo.Items.Count > 0) CompareModelACombo.SelectedIndex = 0;
                if (CompareModelBCombo.Items.Count > 1) CompareModelBCombo.SelectedIndex = 1;
                else if (CompareModelBCombo.Items.Count > 0) CompareModelBCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshCompareModelsAsync failed: " + ex.Message);
            }
        }

        /// <summary>Minutes a comparison, group chat or benchmark is followed before the page stops waiting for it.</summary>
        private const int RunPollMinutes = 30;

        /// <summary>
        /// Polling bounds for comparison, group chat and benchmark runs: one read every 1.5 s, at most <see cref="RunPollMinutes"/>
        /// of reads, and a failed read ends the follow-up at once with the reason shown.
        /// </summary>
        private static readonly StatusPollPolicy RunStatusPolicy = new(
            Interval: TimeSpan.FromMilliseconds(1500),
            MaxPolls: RunPollMinutes * 60 * 1000 / 1500,
            MaxConsecutiveFailures: 1,
            MaxFailureBackoff: TimeSpan.FromMilliseconds(1500));

        private string? _compareRunId;
        private string? _groupChatRunId;

        /// <summary>The line a run page shows when it can no longer follow a run.</summary>
        private static string LostTrackOfRun(string reason) => "Lost track of the run: " + reason + ".";

        private async void CompareRun_Click(object sender, RoutedEventArgs e)
        {
            string? prompt = (ComparePromptBox?.Text ?? string.Empty).Trim();
            var modelA = (CompareModelACombo?.SelectedItem as ComboBoxItem)?.Tag as ModelCompareAvailableModel;
            var modelB = (CompareModelBCombo?.SelectedItem as ComboBoxItem)?.Tag as ModelCompareAvailableModel;
            if (prompt.Length == 0 || modelA == null || modelB == null)
            {
                CompareStageText.Text = T("Pick a prompt and two models first.", "Scegli prima un prompt e due modelli.");
                return;
            }

            CompareRunButton.IsEnabled = false;
            CompareProgress.IsActive = true;
            CompareProgress.Visibility = Visibility.Visible;
            CompareResultsGrid.Visibility = Visibility.Collapsed;
            CompareRevealButton.Visibility = Visibility.Collapsed;
            CompareALabel.Text = T("Response A", "Risposta A");
            CompareBLabel.Text = T("Response B", "Risposta B");
            CompareStageText.Text = T("Starting…", "Avvio…");

            try
            {
                ModelCompareStatus status = await ModelCompareClient.Current.StartAsync(new ModelCompareRequest
                {
                    Prompt = prompt,
                    ModelA = new ModelCompareCandidate { Kind = modelA.Kind, ModelPath = modelA.ModelPath, Label = modelA.Label },
                    ModelB = new ModelCompareCandidate { Kind = modelB.Kind, ModelPath = modelB.ModelPath, Label = modelB.Label }
                }, CancellationToken.None);

                _compareRunId = status.Id;
                CompareCancelButton.IsEnabled = true;
                CompareCancelButton.Visibility = Visibility.Visible;
                await PollCompareAsync(status.Id);
            }
            catch (Exception ex)
            {
                Log.Write("CompareRun failed: " + ex.Message);
                CompareStageText.Text = T("Failed to start: ", "Avvio non riuscito: ") + ex.Message;
            }
            finally
            {
                _compareRunId = null;
                CompareCancelButton.Visibility = Visibility.Collapsed;
                CompareRunButton.IsEnabled = true;
                CompareProgress.IsActive = false;
                CompareProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async Task PollCompareAsync(string id)
        {
            StatusPollResult<ModelCompareStatus> outcome = await BoundedStatusPoller.RunAsync<ModelCompareStatus>(
                async ct => await ModelCompareClient.Current.GetStatusAsync(id, ct),
                status => status.Status is "completed" or "failed" or "cancelled",
                RunStatusPolicy,
                status => CompareStageText.Text = status.Stage,
                onFailure: null,
                delay: null,
                CancellationToken.None);

            ModelCompareStatus? final = outcome.Last;
            if (outcome.End == StatusPollEnd.Terminal && final is { Status: "completed" } done)
            {
                CompareAText.Text = done.ResponseA?.Response ?? string.Empty;
                CompareBText.Text = done.ResponseB?.Response ?? string.Empty;
                CompareResultsGrid.Visibility = Visibility.Visible;
                CompareRevealButton.Visibility = Visibility.Visible;
                CompareRevealButton.Tag = done;
                CompareStageText.Text = T("Done. Compare the two replies, then Reveal.", "Fatto. Confronta le due risposte, poi premi Rivela.");
                return;
            }

            CompareStageText.Text = outcome.End switch
            {
                StatusPollEnd.Terminal when final is { Status: "failed" } failed => T("Failed: ", "Non riuscito: ") + failed.Error,
                StatusPollEnd.Terminal => T("Cancelled.", "Annullato."),
                StatusPollEnd.NotListed => LostTrackOfRun(T("the server no longer lists it", "il server non lo elenca più")),
                StatusPollEnd.GaveUp => LostTrackOfRun(outcome.LastError ?? T("the status request failed", "la richiesta di stato non è riuscita")),
                _ => LostTrackOfRun(T("no final result after about ", "nessun risultato finale dopo circa ") + RunPollMinutes + T(" minutes; it may still be running on the server", " minuti; potrebbe essere ancora in esecuzione sul server")),
            };
        }

        private async void CompareCancel_Click(object sender, RoutedEventArgs e)
        {
            string? id = _compareRunId;
            if (id == null) return;
            CompareCancelButton.IsEnabled = false;
            try
            {
                await ModelCompareClient.Current.CancelAsync(id, CancellationToken.None);
                CompareStageText.Text = T("Cancelling…", "Annullamento…");
            }
            catch (Exception ex)
            {
                Log.Write("CompareCancel failed: " + ex.Message);
                CompareStageText.Text = T("Couldn't cancel the comparison: ", "Impossibile annullare il confronto: ") + ex.Message;
                CompareCancelButton.IsEnabled = true;
            }
        }

        private void CompareReveal_Click(object sender, RoutedEventArgs e)
        {
            if (CompareRevealButton.Tag is ModelCompareStatus status)
            {
                CompareALabel.Text = T("Response A — ", "Risposta A — ") + (status.ModelALabel ?? T("unknown", "sconosciuto"));
                CompareBLabel.Text = T("Response B — ", "Risposta B — ") + (status.ModelBLabel ?? T("unknown", "sconosciuto"));
                CompareRevealButton.Visibility = Visibility.Collapsed;
            }
        }

        // ------------------------------------------------------------------
        // Group Chat (2-4 local models take turns - see GroupChatClient / GroupChatService)
        // ------------------------------------------------------------------

        private async Task RefreshGroupChatModelsAsync()
        {
            bool it = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
            GroupChatSharedContextBox.Header = it ? "Note o testo di riferimento condiviso (facoltativo)"
                : "Shared notes or reference text (optional)";
            GroupChatSharedContextBox.PlaceholderText = it
                ? "Incolla solo il contesto che desideri mostrare a ogni modello locale"
                : "Paste only the relevant context you want each local model to see";
            GroupChatSharedContextHint.Text = it
                ? "Solo il testo inserito qui viene condiviso con i modelli locali selezionati. Altre conversazioni, memoria e file non vengono inclusi automaticamente."
                : "Only the text you paste above is shared with all selected local models. Other conversations, memory and files are not included automatically.";
            ComboBox[] combos = { GroupChatModel1Combo, GroupChatModel2Combo, GroupChatModel3Combo, GroupChatModel4Combo };
            if (combos.Any(c => c == null)) return;
            try
            {
                List<ModelCompareAvailableModel> models = await GroupChatClient.Current.GetAvailableModelsAsync(CancellationToken.None);
                foreach (ComboBox combo in combos)
                {
                    combo.Items.Clear();
                    if (combo != GroupChatModel1Combo && combo != GroupChatModel2Combo)
                        combo.Items.Add(new ComboBoxItem { Content = T("(none)", "(nessuno)"), Tag = null });
                    foreach (ModelCompareAvailableModel model in models)
                        combo.Items.Add(new ComboBoxItem { Content = model.Label + "  ·  " + StorageQuotaService.FormatBytes(model.SizeBytes), Tag = model });
                }
                if (GroupChatModel1Combo.Items.Count > 0) GroupChatModel1Combo.SelectedIndex = 0;
                if (GroupChatModel2Combo.Items.Count > 1) GroupChatModel2Combo.SelectedIndex = 1;
                else if (GroupChatModel2Combo.Items.Count > 0) GroupChatModel2Combo.SelectedIndex = 0;
                GroupChatModel3Combo.SelectedIndex = 0;
                GroupChatModel4Combo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshGroupChatModelsAsync failed: " + ex.Message);
            }
        }

        private async void GroupChatRun_Click(object sender, RoutedEventArgs e)
        {
            string? prompt = (GroupChatPromptBox?.Text ?? string.Empty).Trim();
            var participants = new List<ModelCompareAvailableModel>();
            foreach (ComboBox combo in new[] { GroupChatModel1Combo, GroupChatModel2Combo, GroupChatModel3Combo, GroupChatModel4Combo })
            {
                if ((combo?.SelectedItem as ComboBoxItem)?.Tag is ModelCompareAvailableModel model) participants.Add(model);
            }
            bool italian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
            if (prompt.Length == 0 || participants.Count < 2)
            {
                GroupChatStageText.Text = italian
                    ? "Inserisci una domanda e seleziona almeno due modelli."
                    : "Enter a question and choose at least two models.";
                return;
            }
            if (participants.Select(model => (model.Kind ?? string.Empty) + "|" + (model.ModelPath ?? string.Empty))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != participants.Count)
            {
                GroupChatStageText.Text = italian
                    ? "Scegli modelli diversi: ripetere lo stesso modello non aggiunge un secondo parere."
                    : "Choose different models: repeating the same model does not add a second opinion.";
                return;
            }

            GroupChatRunButton.IsEnabled = false;
            GroupChatProgress.IsActive = true;
            GroupChatProgress.Visibility = Visibility.Visible;
            GroupChatResultsList.Children.Clear();
            GroupChatStageText.Text = T("Starting…", "Avvio…");

            try
            {
                GroupChatStatus status = await GroupChatClient.Current.StartAsync(new GroupChatRequest
                {
                    Prompt = prompt,
                    SharedContext = GroupChatSharedContextBox?.Text?.Trim() ?? string.Empty,
                    Participants = participants.Select(m => new ModelCompareCandidate { Kind = m.Kind, ModelPath = m.ModelPath, Label = m.Label }).ToList()
                }, CancellationToken.None);

                _groupChatRunId = status.Id;
                GroupChatCancelButton.IsEnabled = true;
                GroupChatCancelButton.Visibility = Visibility.Visible;
                await PollGroupChatAsync(status.Id);
            }
            catch (Exception ex)
            {
                Log.Write("GroupChatRun failed: " + ex.Message);
                GroupChatStageText.Text = T("Failed to start: ", "Avvio non riuscito: ") + ex.Message;
            }
            finally
            {
                _groupChatRunId = null;
                GroupChatCancelButton.Visibility = Visibility.Collapsed;
                GroupChatRunButton.IsEnabled = true;
                GroupChatProgress.IsActive = false;
                GroupChatProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async Task PollGroupChatAsync(string id)
        {
            int renderedTurns = 0;
            StatusPollResult<GroupChatStatus> outcome = await BoundedStatusPoller.RunAsync<GroupChatStatus>(
                async ct => await GroupChatClient.Current.GetStatusAsync(id, ct),
                status => status.Status is "completed" or "failed" or "cancelled",
                RunStatusPolicy,
                status =>
                {
                    GroupChatStageText.Text = status.Stage;
                    for (; renderedTurns < status.Turns.Count; renderedTurns++)
                    {
                        GroupChatTurn turn = status.Turns[renderedTurns];
                        var block = new StackPanel { Spacing = 4 };
                        var label = new TextBlock { Text = turn.Label, Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] };
                        var text = new TextBlock { Text = turn.Response, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["PaRuXBodyTextStyle"] };
                        block.Children.Add(label);
                        block.Children.Add(text);
                        GroupChatResultsList.Children.Add(block);
                    }
                },
                onFailure: null,
                delay: null,
                CancellationToken.None);

            GroupChatStatus? final = outcome.Last;
            GroupChatStageText.Text = outcome.End switch
            {
                StatusPollEnd.Terminal when final is { Status: "completed" } => T("Done.", "Fatto."),
                StatusPollEnd.Terminal when final is { Status: "failed" } failed => T("Failed: ", "Non riuscito: ") + failed.Error,
                StatusPollEnd.Terminal => T("Cancelled.", "Annullato."),
                StatusPollEnd.NotListed => LostTrackOfRun(T("the server no longer lists it", "il server non lo elenca più")),
                StatusPollEnd.GaveUp => LostTrackOfRun(outcome.LastError ?? T("the status request failed", "la richiesta di stato non è riuscita")),
                _ => LostTrackOfRun(T("no final result after about ", "nessun risultato finale dopo circa ") + RunPollMinutes + T(" minutes; it may still be running on the server", " minuti; potrebbe essere ancora in esecuzione sul server")),
            };
        }

        private async void GroupChatCancel_Click(object sender, RoutedEventArgs e)
        {
            string? id = _groupChatRunId;
            if (id == null) return;
            GroupChatCancelButton.IsEnabled = false;
            try
            {
                await GroupChatClient.Current.CancelAsync(id, CancellationToken.None);
                GroupChatStageText.Text = T("Cancelling…", "Annullamento…");
            }
            catch (Exception ex)
            {
                Log.Write("GroupChatCancel failed: " + ex.Message);
                GroupChatStageText.Text = T("Couldn't cancel the group chat: ", "Impossibile annullare la chat di gruppo: ") + ex.Message;
                GroupChatCancelButton.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------
        // Avatar Studio
        // ------------------------------------------------------------------

        private async Task RefreshAvatarStatusAsync()
        {
            if (AvatarStatusText == null) return;
            try
            {
                AvatarProviderStatus status = await AvatarClient.Current.GetStatusAsync(CancellationToken.None);
                if (status.Configured && status.Reachable)
                {
                    AvatarStatusText.Text = T("Provider: ", "Provider: ") + status.ProviderName + T(" · reachable.", " · raggiungibile.");
                    AvatarTextBox.IsEnabled = true;
                    AvatarGenerateButton.IsEnabled = true;
                }
                else if (status.Configured)
                {
                    AvatarStatusText.Text = T("Provider ", "Provider ") + status.ProviderName + T(" is configured but not reachable right now.", " è configurato ma al momento non è raggiungibile.");
                }
                else
                {
                    AvatarStatusText.Text = T("No avatar engine ready yet - install the local engine above (or configure an external ", "Nessun motore avatar pronto: installa qui sopra il motore locale (oppure configura ")
                        + T("provider like Duix outside the repository) to enable Avatar Studio.", "un provider esterno come Duix fuori dal repository) per abilitare Avatar Studio.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshAvatarStatusAsync failed: " + ex.Message);
                AvatarStatusText.Text = T("Could not reach the avatar provider: ", "Impossibile raggiungere il provider avatar: ") + ex.Message;
            }
        }

        private async Task RefreshAvatarEngineStatusAsync()
        {
            if (AvatarEngineStatusText == null) return;
            try
            {
                AvatarEngineStatus status = await AvatarClient.Current.GetEngineStatusAsync(CancellationToken.None);
                if (status.Installed)
                {
                    AvatarEngineStatusText.Text = T("Engine installed", "Motore installato") + (status.HasDefaultPhoto ? T(" · reference photo set.", " · foto di riferimento impostata.") : T(" · no reference photo set yet - use \"Set reference photo…\" below.", " · nessuna foto di riferimento impostata: usa \"Imposta la foto di riferimento…\" qui sotto."));
                    AvatarInstallButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    AvatarEngineStatusText.Text = string.IsNullOrEmpty(status.LastError)
                        ? T("Local engine not installed yet.", "Motore locale non ancora installato.")
                        : status.LastErrorCode == "build_tools_required"
                            ? AvatarBuildToolsRequiredText(T)
                            : status.LastErrorCode == "data_path_too_long"
                                ? AvatarDataPathTooLongText(T)
                                : T("Last install attempt failed: ", "Ultimo tentativo di installazione non riuscito: ") + status.LastError;
                    AvatarInstallButton.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshAvatarEngineStatusAsync failed: " + ex.Message);
                AvatarEngineStatusText.Text = T("Could not reach the avatar engine: ", "Impossibile raggiungere il motore avatar: ") + ex.Message;
            }
            await RefreshAvatarStatusAsync();
        }

        /// <summary>The avatar engine needs a package that is only published as source (insightface), so a C++ compiler must exist on this PC.</summary>
        internal static string AvatarBuildToolsRequiredText(Func<string, string, string> t) =>
            t("C++ build tools are required to install the avatar engine: one of its packages must be compiled on this PC. Install \"Visual Studio Build Tools\" with the \"Desktop development with C++\" workload, then press Install again. PaRuX does not install it for you.",
              "Per installare il motore avatar servono gli strumenti di compilazione C++: uno dei suoi pacchetti va compilato su questo PC. Installa \"Visual Studio Build Tools\" con il carico di lavoro \"Sviluppo di applicazioni desktop con C++\", poi premi di nuovo Installa. PaRuX non lo installa al posto tuo.");

        /// <summary>The data folder is too deep for the C++ linker to build a package of the avatar engine (path limit of 260 characters).</summary>
        internal static string AvatarDataPathTooLongText(Func<string, string, string> t) =>
            t("The avatar engine cannot be installed here: the PaRuX data folder path is too long for Windows and the C++ tools (file paths are limited to 260 characters) and no shorter PaRuX location can be used. Move the PaRuX data to a shorter folder, for example C:\\PaRuX, then press Install again.",
              "Il motore avatar non può essere installato qui: il percorso della cartella dati di PaRuX è troppo lungo per Windows e per gli strumenti C++ (i percorsi dei file sono limitati a 260 caratteri) e non è possibile usare una posizione PaRuX più corta. Sposta i dati di PaRuX in una cartella più corta, ad esempio C:\\PaRuX, poi premi di nuovo Installa.");

        private async void AvatarInstall_Click(object sender, RoutedEventArgs e)
        {
            AvatarInstallButton.IsEnabled = false;
            AvatarInstallProgress.IsActive = true;
            AvatarInstallProgress.Visibility = Visibility.Visible;
            AvatarEngineStatusText.Text = T("Installing (this clones the engine, installs its dependencies, and downloads a ~5 GB checkpoint - can take a long while)…", "Installazione in corso (clona il motore, installa le sue dipendenze e scarica un checkpoint da circa 5 GB: può richiedere parecchio tempo)…");
            try
            {
                await AvatarClient.Current.InstallEngineAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("AvatarInstall failed: " + ex.Message);
            }
            finally
            {
                AvatarInstallProgress.IsActive = false;
                AvatarInstallProgress.Visibility = Visibility.Collapsed;
                AvatarInstallButton.IsEnabled = true;
                await RefreshAvatarEngineStatusAsync();
            }
        }

        private async void AvatarPhoto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;

                byte[] bytes = (await Windows.Storage.FileIO.ReadBufferAsync(file)).ToArray();
                AvatarEngineStatusText.Text = T("Setting reference photo…", "Impostazione della foto di riferimento…");
                await AvatarClient.Current.SetPhotoAsync(bytes, file.Name, CancellationToken.None);
                await RefreshAvatarEngineStatusAsync();
            }
            catch (Exception ex)
            {
                Log.Write("AvatarPhoto failed: " + ex.Message);
                AvatarEngineStatusText.Text = T("Couldn't set the reference photo: ", "Impossibile impostare la foto di riferimento: ") + ex.Message;
            }
        }

        private async void AvatarGenerate_Click(object sender, RoutedEventArgs e)
        {
            string? text = (AvatarTextBox?.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            AvatarGenerateButton.IsEnabled = false;
            AvatarProgress.IsActive = true;
            AvatarProgress.Visibility = Visibility.Visible;
            try
            {
                AvatarGenerateResponse result = await AvatarClient.Current.GenerateAsync(text, CancellationToken.None);
                byte[] bytes = Convert.FromBase64String(result.Base64Data);
                string path = Path.Combine(AppPaths.GeneratedVideosDir, Guid.NewGuid().ToString("N") + ".mp4");
                Directory.CreateDirectory(AppPaths.GeneratedVideosDir);
                await File.WriteAllBytesAsync(path, bytes);
                ChatSession.Current.AppendAssistantNotice("Avatar video generated locally: " + path);
            }
            catch (Exception ex)
            {
                Log.Write("AvatarGenerate failed: " + ex.Message);
                AvatarStatusText.Text = T("Avatar generation failed: ", "Generazione dell'avatar non riuscita: ") + ex.Message;
            }
            finally
            {
                AvatarGenerateButton.IsEnabled = true;
                AvatarProgress.IsActive = false;
                AvatarProgress.Visibility = Visibility.Collapsed;
            }
        }

        // ------------------------------------------------------------------
        // Training Lab (job creation/status - dataset export is the section above)
        // ------------------------------------------------------------------

        private string? _trainingJsonlPath;

        // ------------------------------------------------------------------
        // 3D Studio (TRELLIS image-to-3D - see ThreedClient/ThreedService)
        // ------------------------------------------------------------------

        private string? _threedSourceImagePath;
        private byte[]? _threedLastGlbBytes;
        private string _threedLastGlbFileName = "model.glb";

        private async Task RefreshThreedEngineStatusAsync()
        {
            if (ThreedEngineStatusText == null) return;
            try
            {
                ThreedEngineStatus status = await ThreedClient.Current.GetStatusAsync(CancellationToken.None);
                if (status.Installed)
                {
                    ThreedEngineStatusText.Text = T("Engine installed", "Motore installato") + (string.IsNullOrEmpty(status.TorchVersion) ? string.Empty : T(" · torch ", " · torch ") + status.TorchVersion)
                        + (status.CudaVerified ? T(" · CUDA verified", " · CUDA verificato") : T(" · CUDA not verified", " · CUDA non verificato"));
                    ThreedInstallButton.Visibility = Visibility.Collapsed;
                    ThreedGenerateButton.IsEnabled = _threedSourceImagePath != null;
                }
                else
                {
                    ThreedEngineStatusText.Text = string.IsNullOrEmpty(status.LastError)
                        ? T("3D engine not installed yet. TRELLIS needs custom CUDA extensions - install can genuinely fail on this machine.", "Motore 3D non ancora installato. TRELLIS richiede estensioni CUDA personalizzate: l'installazione può davvero fallire su questa macchina.")
                        : T("Last install attempt failed: ", "Ultimo tentativo di installazione non riuscito: ") + status.LastError;
                    ThreedInstallButton.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshThreedEngineStatusAsync failed: " + ex.Message);
                ThreedEngineStatusText.Text = T("Could not reach the 3D engine: ", "Impossibile raggiungere il motore 3D: ") + ex.Message;
            }
        }

        private async Task RefreshDiffusersEngineStatusAsync()
        {
            if (DiffusersEngineStatusText == null) return;
            try
            {
                ManagedMediaEngineStatus status = await DiffusersEngineClient.Current.GetStatusAsync(CancellationToken.None);
                DiffusersEngineStatusText.Text = DiffusersEngineStatusPresenter.Describe(status, T);
                DiffusersInstallButton.Visibility = DiffusersEngineStatusPresenter.CanInstall(status) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshDiffusersEngineStatusAsync failed: " + ex.Message);
                DiffusersEngineStatusText.Text = T("Could not reach the Diffusers engine.", "Impossibile raggiungere il motore Diffusers.");
            }
        }

        private async void DiffusersInstall_Click(object sender, RoutedEventArgs e)
        {
            DiffusersInstallButton.IsEnabled = false;
            DiffusersInstallProgress.IsActive = true;
            DiffusersInstallProgress.Visibility = Visibility.Visible;
            DiffusersEngineStatusText.Text = T("Installing (this downloads several GB and can take a while)…", "Installazione in corso (scarica diversi GB e può richiedere un po' di tempo)…");
            try { await DiffusersEngineClient.Current.InstallAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("DiffusersInstall failed: " + ex.Message); }
            finally
            {
                DiffusersInstallProgress.IsActive = false;
                DiffusersInstallProgress.Visibility = Visibility.Collapsed;
                DiffusersInstallButton.IsEnabled = true;
                await RefreshDiffusersEngineStatusAsync(); // the status code carries the true reason for a refusal
            }
        }

        private async void ThreedInstall_Click(object sender, RoutedEventArgs e)
        {
            ThreedInstallButton.IsEnabled = false;
            ThreedInstallProgress.IsActive = true;
            ThreedInstallProgress.Visibility = Visibility.Visible;
            ThreedEngineStatusText.Text = T("Installing (this downloads several GB and can take a while)…", "Installazione in corso (scarica diversi GB e può richiedere un po' di tempo)…");
            try
            {
                await ThreedClient.Current.InstallEngineAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("ThreedInstall failed: " + ex.Message);
            }
            finally
            {
                ThreedInstallProgress.IsActive = false;
                ThreedInstallProgress.Visibility = Visibility.Collapsed;
                ThreedInstallButton.IsEnabled = true;
                await RefreshThreedEngineStatusAsync();
            }
        }

        private async void ThreedPickImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                _threedSourceImagePath = file.Path;
                ThreedSourceImageText.Text = file.Name;
                ThreedGenerateButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Write("ThreedPickImage failed: " + ex.Message);
            }
        }

        private async void ThreedGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_threedSourceImagePath)) return;
            ThreedGenerateButton.IsEnabled = false;
            ThreedGenerateProgress.IsActive = true;
            ThreedGenerateProgress.Visibility = Visibility.Visible;
            ThreedResultText.Text = T("Generating locally (can take a few minutes)…", "Generazione in locale (può richiedere qualche minuto)…");
            ThreedActionsPanel.Visibility = Visibility.Collapsed;
            try
            {
                ThreedGenerateResponse result = await ThreedClient.Current.GenerateAsync(_threedSourceImagePath, CancellationToken.None);
                _threedLastGlbBytes = Convert.FromBase64String(result.Base64Data);
                _threedLastGlbFileName = result.FileName;
                ThreedResultText.Text = T("Generated locally · ", "Generato in locale · ") + result.DurationSeconds.ToString("0.0") + "s · " + _threedLastGlbBytes.Length / 1024 + " KB";
                ThreedActionsPanel.Visibility = Visibility.Visible;
                await ShowThreedPreviewAsync(_threedLastGlbBytes);
            }
            catch (Exception ex)
            {
                Log.Write("ThreedGenerate failed: " + ex.Message);
                ThreedResultText.Text = T("3D generation failed: ", "Generazione 3D non riuscita: ") + ex.Message;
            }
            finally
            {
                ThreedGenerateProgress.IsActive = false;
                ThreedGenerateProgress.Visibility = Visibility.Collapsed;
                ThreedGenerateButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Best-effort inline preview via WebView2 + the &lt;model-viewer&gt; web component, whose
        /// script is fetched from a public CDN.
        ///
        /// That fetch is now gated on PaRuX's own Internet toggle. It previously ran
        /// unconditionally: generating a 3D model reached out to unpkg.com even with Internet
        /// switched off, which is exactly the consent gate InternetSettingsService exists to be -
        /// and it told a third party that this user was using the 3D feature. The generation
        /// itself is entirely local; only this optional preview ever needed the network.
        ///
        /// When the preview cannot be shown the reason is stated instead of the viewer silently
        /// collapsing, because "nothing appeared" is indistinguishable from a failed generation.
        /// Save and Open in Blender are unaffected and work fully offline either way.
        /// </summary>
        private async Task ShowThreedPreviewAsync(byte[] glbBytes)
        {
            ThreedViewer.Visibility = Visibility.Collapsed;

            bool internetAllowed;
            try
            {
                InternetStatus status = await InternetClient.Current.GetStatusAsync(CancellationToken.None);
                internetAllowed = status.Enabled;
            }
            catch (Exception ex)
            {
                // Unable to read the toggle is not permission to ignore it.
                Log.Write("3D preview: could not read the Internet setting, assuming off: " + ex.Message);
                internetAllowed = false;
            }

            if (!internetAllowed)
            {
                ThreedResultText.Text = ThreedResultText.Text
                    + T("\nThe model was generated locally. The inline 3D preview needs a web component ", "\nIl modello è stato generato in locale. L'anteprima 3D incorporata richiede un componente web ")
                    + T("downloaded from the internet, which is currently switched off - use Save or ", "scaricato da Internet, che al momento è disattivato: usa Salva o ")
                    + T("Open in Blender, which work offline.", "Apri in Blender, che funzionano offline.");
                return;
            }

            try
            {
                await ThreedViewer.EnsureCoreWebView2Async();
                string base64 = Convert.ToBase64String(glbBytes);
                string html = "<!DOCTYPE html><html><head><meta charset='utf-8'>" +
                    "<script type='module' src='https://unpkg.com/@google/model-viewer/dist/model-viewer.min.js'></script>" +
                    "<style>body{margin:0;background:#1a1a1a;}model-viewer{width:100%;height:100vh;}</style></head><body>" +
                    "<model-viewer src='data:model/gltf-binary;base64," + base64 + "' camera-controls auto-rotate shadow-intensity='1'></model-viewer>" +
                    "</body></html>";
                ThreedViewer.NavigateToString(html);
                ThreedViewer.Height = 320;
                ThreedViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Write("3D preview failed (non-fatal, Save/Open in Blender still work): " + ex.Message);
                ThreedViewer.Visibility = Visibility.Collapsed;
                ThreedResultText.Text = ThreedResultText.Text
                    + T("\nThe model was generated locally, but the inline 3D preview could not load.", "\nIl modello è stato generato in locale, ma l'anteprima 3D incorporata non si è potuta caricare.");
            }
        }

        private async void ThreedSave_Click(object sender, RoutedEventArgs e)
        {
            if (_threedLastGlbBytes == null) return;
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(_threedLastGlbFileName) };
                picker.FileTypeChoices.Add("glTF Binary", new List<string> { ".glb" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await Windows.Storage.FileIO.WriteBytesAsync(file, _threedLastGlbBytes);
                _threedLastSavedPath = file.Path;
            }
            catch (Exception ex)
            {
                Log.Write("ThreedSave failed: " + ex.Message);
            }
        }

        private string? _threedLastSavedPath;

        private async void ThreedOpenBlender_Click(object sender, RoutedEventArgs e)
        {
            if (_threedLastGlbBytes == null) return;
            try
            {
                string? path = _threedLastSavedPath;
                if (path == null || !File.Exists(path))
                {
                    path = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(_threedLastGlbFileName) + ".glb");
                    await File.WriteAllBytesAsync(path, _threedLastGlbBytes);
                }

                string? blenderExe = FindBlenderExecutable();
                if (blenderExe == null)
                {
                    ThreedResultText.Text = T("Blender was not found on this PC. Use Save .glb and open it manually.", "Blender non è stato trovato su questo PC. Usa Salva .glb e aprilo a mano.");
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = blenderExe,
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Log.Write("ThreedOpenBlender failed: " + ex.Message);
                ThreedResultText.Text = T("Couldn't open Blender: ", "Impossibile aprire Blender: ") + ex.Message;
            }
        }

        private static string? FindBlenderExecutable()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string blenderRoot = Path.Combine(programFiles, "Blender Foundation");
            if (!Directory.Exists(blenderRoot)) return null;
            foreach (string dir in Directory.GetDirectories(blenderRoot).OrderByDescending(d => d))
            {
                string exe = Path.Combine(dir, "blender.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        private async Task RefreshTrainingEngineStatusAsync()
        {
            if (TrainingEngineStatusText == null) return;
            try
            {
                TrainingEngineStatus status = await TrainingLabClient.Current.GetEngineStatusAsync(CancellationToken.None);
                TrainingEngineStatusText.Text = status.Installed
                    ? T("Engine installed", "Motore installato") + (string.IsNullOrEmpty(status.TorchVersion) ? string.Empty : T(" · torch ", " · torch ") + status.TorchVersion)
                        + (status.CudaVerified ? T(" · CUDA verified", " · CUDA verificato") : T(" · CUDA not verified", " · CUDA non verificato"))
                    : T("Training engine not installed yet — starting a job will install it first.", "Motore di training non ancora installato: avviare un job lo installerà prima.");
            }
            catch (Exception ex)
            {
                Log.Write("RefreshTrainingEngineStatusAsync failed: " + ex.Message);
                TrainingEngineStatusText.Text = T("Could not reach the training engine: ", "Impossibile raggiungere il motore di training: ") + ex.Message;
            }
        }

        private async void TrainingPickJsonl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".jsonl");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                _trainingJsonlPath = file.Path;
                TrainingJsonlPathText.Text = _trainingJsonlPath;
                TrainingStartButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Write("TrainingPickJsonl failed: " + ex.Message);
            }
        }

        private async void TrainingStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_trainingJsonlPath)) return;
            string? baseModel = (TrainingBaseModelBox?.Text ?? string.Empty).Trim();
            if (baseModel.Length == 0) baseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";

            TrainingStartButton.IsEnabled = false;
            TrainingJobProgress.Visibility = Visibility.Visible;
            TrainingJobStatusText.Text = T("Starting…", "Avvio…");
            try
            {
                TrainingJobStatus job = await TrainingLabClient.Current.CreateJobAsync(new TrainingJobRequest
                {
                    TrainJsonlPath = _trainingJsonlPath, BaseModel = baseModel
                }, CancellationToken.None);
                await PollTrainingJobAsync(job.Id);
            }
            catch (Exception ex)
            {
                Log.Write("TrainingStart failed: " + ex.Message);
                TrainingJobStatusText.Text = T("Failed to start: ", "Avvio non riuscito: ") + ex.Message;
                TrainingStartButton.IsEnabled = true;
            }
        }

        private async Task PollTrainingJobAsync(string jobId)
        {
            while (true)
            {
                TrainingJobStatus job;
                try { job = await TrainingLabClient.Current.GetJobAsync(jobId, CancellationToken.None); }
                catch (Exception ex) { Log.Write("Training job poll failed: " + ex.Message); break; }

                double percent = job.TotalSteps > 0 ? 100.0 * job.CurrentStep / job.TotalSteps : 0;
                TrainingJobProgress.Value = percent;
                TrainingJobStatusText.Text = job.Stage + T("  ·  step ", "  ·  passo ") + job.CurrentStep + "/" + job.TotalSteps
                    + (job.LastLoss.HasValue ? T("  ·  loss ", "  ·  perdita ") + job.LastLoss.Value.ToString("0.000") : string.Empty);

                if (job.Status == "completed")
                {
                    TrainingJobStatusText.Text = T("Completed. Adapter: ", "Completato. Adapter: ") + job.AdapterPath;
                    break;
                }
                if (job.Status == "failed") { TrainingJobStatusText.Text = T("Failed: ", "Non riuscito: ") + job.Error; break; }
                if (job.Status == "cancelled") { TrainingJobStatusText.Text = T("Cancelled.", "Annullato."); break; }
                await Task.Delay(2000);
            }
            TrainingStartButton.IsEnabled = true;
        }

        private void ApplyHotkey_Click(object sender, RoutedEventArgs e)
        {
            string candidate = (HotkeyBox.Text ?? string.Empty).Trim();

            uint mods, key;
            if (!HotkeyService.TryParse(candidate, out mods, out key))
            {
                HotkeyHint.Text = T("That combination isn't valid. Use something like Ctrl+Alt+Space or Ctrl+Shift+N.", "Quella combinazione non è valida. Usa qualcosa come Ctrl+Alt+Spazio o Ctrl+Maiusc+N.");
                return;
            }

            // Live re-registration, so the new combo works without a restart.
            bool registered = App.Hotkey.Register(candidate);
            if (!registered)
            {
                HotkeyHint.Text = T("Windows refused that combination — another app probably owns it. Try a different one.", "Windows ha rifiutato quella combinazione: probabilmente un'altra app la usa. Provane un'altra.");
                // Put the previous, working hotkey back.
                App.Hotkey.Register(SettingsStore.Current.Data.Hotkey);
                HotkeyBox.Text = SettingsStore.Current.Data.Hotkey;
                return;
            }

            SettingsStore.Current.Data.Hotkey = candidate;
            SettingsStore.Current.Save();
            HotkeyHint.Text = T("Active: press ", "Attiva: premi ") + candidate + T(" anywhere to open Quick Chat.", " ovunque per aprire la Chat rapida.");
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshDiagnosticsAsync();
        }

        private async Task RefreshDiagnosticsAsync()
        {
            try
            {
                LocalAiState state = await Task.Run(() =>
                    LocalAiController.Current.GetStateAsync(CancellationToken.None));

                DiagHealth.Text = state.Healthy
                    ? T("healthy", "in salute")
                    : (state.ServerProcessPresent ? T("process running, not answering yet", "processo in esecuzione, non ancora in risposta") : T("not running", "non in esecuzione"));

                // The ring only spins until the first real answer arrives; after that
                // a coloured dot carries the verdict, so nothing sits on "checking…".
                SetHealthDot(state.Healthy
                    ? "PaRuXDotOkBrush"
                    : (state.ServerProcessPresent ? "PaRuXDotWarnBrush" : "PaRuXDotOffBrush"));

                DiagProfile.Text = string.IsNullOrWhiteSpace(state.ActiveProfile) ? T("none loaded", "nessuno caricato") : state.ActiveProfile;
                DiagMode.Text = state.Mode;
                DiagTextProfile.Text = state.TextProfile;
                DiagGame.Text = state.GameActive ? T("a game is active — AI stays unloaded", "un gioco è attivo: l'IA resta scaricata dalla memoria") : T("no game active", "nessun gioco attivo");
            }
            catch (Exception ex)
            {
                Log.Write("RefreshDiagnostics failed: " + ex.Message);
                DiagHealth.Text = T("unavailable", "non disponibile");
                SetHealthDot("PaRuXDotOffBrush");
            }

            try
            {
                string? endpoint = await LocalAiController.Current.GetManagedProviderEndpointAsync(CancellationToken.None);
                // SEC-F01: the runtime requires a per-launch key that only PaRuX Core holds, so its own built-in
                // web page cannot be used from a browser; chat and the Local AI Gateway reach it through Core.
                DiagEndpoint.Text = T("Endpoint ", "Endpoint ") + (endpoint ?? T("unavailable", "non disponibile"))
                                  + T(" (protected by a per-launch key; open chat or use the Local AI Gateway instead of a browser)",
                                      " (protetto da una chiave per avvio; usa la chat o il Local AI Gateway invece di un browser)")
                                  + T("   ·   settings at ", "   ·   impostazioni in ") + AppPaths.SettingsFile;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshManagedProviderEndpoint failed: " + ex.Message);
                DiagEndpoint.Text = T("Endpoint unavailable", "Endpoint non disponibile")
                                  + T("   ·   settings at ", "   ·   impostazioni in ") + AppPaths.SettingsFile;
            }

            try
            {
                ResourceSnapshot resources = await ResourceMonitorService.GetSnapshotAsync(CancellationToken.None);
                // Only claim usage when it was actually measured: the vendor-agnostic inventory
                // knows the adapter and its installed VRAM but not what is in use right now.
                string gpuDescription = ResourceHardwareSnapshotMapper.DescribeGpu(resources);
                DiagVram.Text = resources.GpuUsageMeasured && !resources.GpuMemoryIsShared
                    ? gpuDescription + " · " + resources.VramUsedMb.ToString("N0") + T(" MB used · ", " MB usati · ")
                        + resources.GpuUtilizationPercent + T("% util", "% di utilizzo")
                    : gpuDescription + (resources.GpuAvailable ? T(" · live usage unavailable", " · utilizzo in tempo reale non disponibile") : "");
                DiagRam.Text = resources.RamAvailable
                    ? resources.RamUsedMb + " / " + resources.RamTotalMb + " MB"
                    : T("unavailable", "non disponibile");
            }
            catch (Exception ex)
            {
                Log.Write("Resource snapshot failed: " + ex.Message);
                DiagVram.Text = T("unavailable", "non disponibile");
                DiagRam.Text = T("unavailable", "non disponibile");
            }
        }

        /// <summary>
        /// Visual only. Swaps the spinning ring for a status dot, using a brush
        /// from this control's own resource dictionary so it repaints with the theme.
        /// </summary>
        private void SetHealthDot(string brushKey)
        {
            try
            {
                DiagRing.IsActive = false;
                DiagRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                DiagHealthDot.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                object value;
                if (Resources.TryGetValue(brushKey, out value))
                {
                    var brush = value as Brush;
                    if (brush != null) DiagHealthDot.Background = brush;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Health dot update failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Agent Command Center (vNext Phase 8 - server-owned TaskStore/
        // AgentOrchestrator/AgentWorkerService/VerifierService, see AgentOrchestratorClient)
        // ------------------------------------------------------------------

        private async Task RefreshAgentTasksAsync()
        {
            if (AgentTasksList == null) return;
            AgentTaskListResponse response;
            try { response = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshAgentTasksAsync failed: " + ex.Message);
                if (AgentTasksCountLabel != null) AgentTasksCountLabel.Text = T("Failed to load tasks: ", "Impossibile caricare le attività: ") + ex.Message;
                return;
            }

            if (AgentTasksCountLabel != null) AgentTasksCountLabel.Text = response.Tasks.Count + T(" task(s)", " attività");
            AgentTasksList.Children.Clear();
            foreach (AgentTaskDto task in response.Tasks.OrderByDescending(t => t.CreatedUtc))
                AgentTasksList.Children.Add(BuildAgentTaskCard(task));
        }

        private void RefreshAgentTasks_Click(object sender, RoutedEventArgs e) => _ = RefreshAgentTasksAsync();

        private async void CreateAgentTask_Click(object sender, RoutedEventArgs e)
        {
            string? goal = AgentTaskGoalBox?.Text?.Trim();
            if (string.IsNullOrEmpty(goal))
            {
                if (AgentTasksStatus != null) AgentTasksStatus.Text = T("Enter a goal first.", "Inserisci prima un obiettivo.");
                return;
            }
            string? role = (AgentTaskRoleBox?.SelectedItem as ComboBoxItem)?.Tag as string;

            // A coding task carries its own repository authority; Core deliberately never borrows the
            // global Coding workspace for a task that did not ask for it. Creating one here without
            // that authority produced a task that sat in "Waiting for desktop repository authority"
            // and could never run, so the configured workspace is attached the same way the CLI's
            // --workspace does - and if there is none, we say so instead of queueing dead work.
            if (string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase))
            {
                string workspace = (SettingsStore.Current.Data.AgentCodingWorkspaceRoot ?? string.Empty).Trim();
                if (workspace.Length == 0)
                {
                    if (AgentTasksStatus != null)
                        AgentTasksStatus.Text = T("Set the Coding agent workspace above first: a coding task runs in its ", "Imposta prima l'area di lavoro dell'agente di programmazione qui sopra: un'attività di programmazione viene eseguita nel ")
                            + T("own worktree of a repository you name, and PaRuX will not pick one for it.", "proprio worktree di un repository che indichi tu, e PaRuX non ne sceglierà uno al posto tuo.");
                    return;
                }

                try
                {
                    await AgentOrchestratorClient.Current.CreateCodingTaskAsync(
                        goal,
                        workspace,
                        SettingsStore.Current.Data.CodingPermissionProfile ?? "offline",
                        2,
                        CancellationToken.None);
                    if (AgentTaskGoalBox != null) AgentTaskGoalBox.Text = string.Empty;
                    if (AgentTasksStatus != null) AgentTasksStatus.Text = string.Empty;
                }
                catch (Exception ex)
                {
                    Log.Write("CreateCodingTask failed: " + ex.Message);
                    if (AgentTasksStatus != null) AgentTasksStatus.Text = T("Failed to create coding task: ", "Impossibile creare l'attività di programmazione: ") + ex.Message;
                }
                await RefreshAgentTasksAsync();
                return;
            }

            try
            {
                await AgentOrchestratorClient.Current.CreateAsync(
                    new CreateAgentTaskRequest { Goal = goal, Role = string.IsNullOrEmpty(role) ? null : role },
                    CancellationToken.None);
                if (AgentTaskGoalBox != null) AgentTaskGoalBox.Text = string.Empty;
                if (AgentTasksStatus != null) AgentTasksStatus.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("CreateAgentTask failed: " + ex.Message);
                if (AgentTasksStatus != null) AgentTasksStatus.Text = T("Failed to create task: ", "Impossibile creare l'attività: ") + ex.Message;
            }
            await RefreshAgentTasksAsync();
        }

        // ==================== Project Manager + Autopilot (Phase 48) ====================

        private async void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            string? goal = ProjectGoalBox?.Text?.Trim();
            if (string.IsNullOrEmpty(goal))
            {
                if (ProjectStatus != null) ProjectStatus.Text = T("Enter a goal first.", "Inserisci prima un obiettivo.");
                return;
            }
            try
            {
                ProjectDto project = await ProjectManagerClient.Current.CreateAsync(goal, CancellationToken.None);
                if (ProjectGoalBox != null) ProjectGoalBox.Text = string.Empty;
                if (ProjectStatus != null) ProjectStatus.Text = T("Created project ", "Progetto creato: ") + project.Id + T(" with ", " con ") + project.TaskIds.Count + T(" task(s).", " attività.");
            }
            catch (Exception ex)
            {
                Log.Write("CreateProject failed: " + ex.Message);
                if (ProjectStatus != null) ProjectStatus.Text = T("Failed to create project: ", "Impossibile creare il progetto: ") + ex.Message;
            }
            await RefreshProjectsAsync();
        }

        private void RefreshProjects_Click(object sender, RoutedEventArgs e) => _ = RefreshProjectsAsync();

        private async Task RefreshProjectsAsync()
        {
            if (ProjectsList == null) return;
            _ = RefreshPendingAutopilotRunsAsync();
            ContextEntityListResponse response;
            try { response = await ProjectManagerClient.Current.ListAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshProjectsAsync failed: " + ex.Message);
                if (ProjectsCountLabel != null) ProjectsCountLabel.Text = T("Failed to load projects: ", "Impossibile caricare i progetti: ") + ex.Message;
                return;
            }

            if (ProjectsCountLabel != null) ProjectsCountLabel.Text = response.Entities.Count + T(" project(s)", " progetti");
            ProjectsList.Children.Clear();
            foreach (ContextEntityDto project in response.Entities.OrderByDescending(p => p.CreatedUtc))
            {
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
                };
                var stack = new StackPanel { Spacing = 4 };
                var nameBlock = new TextBlock { Text = project.Name, TextWrapping = TextWrapping.Wrap };
                ApplyBodyStrongStyle(nameBlock);
                stack.Children.Add(nameBlock);
                var metaBlock = new TextBlock { Text = project.CreatedUtc.ToLocalTime().ToString("g") + "  ·  " + project.Id };
                ApplyCaptionStyle(metaBlock);
                stack.Children.Add(metaBlock);
                card.Child = stack;
                ProjectsList.Children.Add(card);
            }
        }

        private async void StartAutopilotRun_Click(object sender, RoutedEventArgs e)
        {
            string? goal = AutopilotGoalBox?.Text?.Trim();
            if (string.IsNullOrEmpty(goal))
            {
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Enter a goal first.", "Inserisci prima un obiettivo.");
                return;
            }
            string? mode = (AutopilotModeBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? AutopilotPermissionMode.Balanced;

            try
            {
                AutopilotRunDto run = await AutopilotClient.Current.StartRunAsync(goal, mode, CancellationToken.None);
                if (AutopilotRunIdBox != null) AutopilotRunIdBox.Text = run.Id;
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Run ", "Esecuzione ") + run.Id + ": " + run.Status + " (" + run.TaskIds.Count + T(" task(s)).", " attività).");
                await RefreshPendingAutopilotRunsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("StartAutopilotRun failed: " + ex.Message);
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Failed to start run: ", "Impossibile avviare l'esecuzione: ") + ex.Message;
            }
        }

        private async void CheckAutopilotRun_Click(object sender, RoutedEventArgs e)
        {
            string? id = AutopilotRunIdBox?.Text?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Enter a run id first.", "Inserisci prima un ID di esecuzione.");
                return;
            }
            try
            {
                AutopilotRunDto run = await AutopilotClient.Current.GetRunAsync(id, CancellationToken.None);
                string report = run.FinalReportJson != null ? " Report: " + run.FinalReportJson : string.Empty;
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Run ", "Esecuzione ") + run.Id + ": " + run.Status + "." + report;
            }
            catch (Exception ex)
            {
                Log.Write("CheckAutopilotRun failed: " + ex.Message);
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Failed to check run: ", "Impossibile controllare l'esecuzione: ") + ex.Message;
            }
        }

        private string? _pendingRunsSignature;

        private void RefreshPendingAutopilotRuns_Click(object sender, RoutedEventArgs e) => _ = RefreshPendingAutopilotRunsAsync();

        /// <summary>Lists the Autopilot runs waiting for the desktop owner (started here in supervised mode or from a paired
        /// phone) with Approve and Cancel, instead of asking the owner to type a run id.</summary>
        private async Task RefreshPendingAutopilotRunsAsync()
        {
            if (AutopilotPendingList == null) return;
            List<AutopilotRunDto> pending;
            try { pending = await AutopilotClient.Current.ListPendingRunsAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("ListPendingAutopilotRuns failed: " + ex.Message);
                if (AutopilotStatus != null) AutopilotStatus.Text = T("Failed to load waiting runs: ", "Impossibile caricare le esecuzioni in attesa: ") + ex.Message;
                return;
            }

            // The periodic refresh must not rebuild (and so steal) the Approve/Cancel buttons while nothing changed.
            string signature = string.Join("|", pending.Select(r => r.Id));
            if (signature == _pendingRunsSignature && AutopilotPendingList.Children.Count == pending.Count) return;
            _pendingRunsSignature = signature;
            AutopilotPendingList.Children.Clear();
            if (AutopilotPendingEmpty != null) AutopilotPendingEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (AutopilotRunDto run in pending)
            {
                string runId = run.Id;
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
                };
                var stack = new StackPanel { Spacing = 6 };
                var goal = new TextBlock { Text = run.Goal, TextWrapping = TextWrapping.Wrap };
                ApplyBodyStrongStyle(goal);
                stack.Children.Add(goal);
                string origin = string.IsNullOrEmpty(run.StartedBy) ? T("this PC", "questo PC") : T("a paired device", "un dispositivo associato");
                string expires = run.ExpiresUtc.HasValue
                    ? T("  ·  expires ", "  ·  scade ") + run.ExpiresUtc.Value.ToLocalTime().ToString("g") : string.Empty;
                var meta = new TextBlock
                {
                    Text = T("Started from ", "Avviata da ") + origin + "  ·  " + run.TaskIds.Count + T(" task(s)", " attività") + expires,
                    TextWrapping = TextWrapping.Wrap
                };
                ApplyCaptionStyle(meta);
                stack.Children.Add(meta);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var approve = new Button { Content = T("Approve", "Approva") };
                approve.Click += async (_, _) =>
                {
                    try
                    {
                        await AutopilotClient.Current.ApproveRunAsync(runId, CancellationToken.None);
                        if (AutopilotStatus != null) AutopilotStatus.Text = T("Run ", "Esecuzione ") + runId + T(" approved.", " approvata.");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("ApproveAutopilotRun failed: " + ex.Message);
                        if (AutopilotStatus != null) AutopilotStatus.Text = T("Failed to approve run: ", "Impossibile approvare l'esecuzione: ") + ex.Message;
                    }
                    await RefreshPendingAutopilotRunsAsync();
                };
                var cancel = new Button { Content = T("Cancel", "Annulla") };
                cancel.Click += async (_, _) =>
                {
                    try
                    {
                        await AutopilotClient.Current.CancelRunAsync(runId, CancellationToken.None);
                        if (AutopilotStatus != null) AutopilotStatus.Text = T("Run ", "Esecuzione ") + runId + T(" cancelled.", " annullata.");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("CancelAutopilotRun failed: " + ex.Message);
                        if (AutopilotStatus != null) AutopilotStatus.Text = T("Failed to cancel run: ", "Impossibile annullare l'esecuzione: ") + ex.Message;
                    }
                    await RefreshPendingAutopilotRunsAsync();
                };
                buttons.Children.Add(approve);
                buttons.Children.Add(cancel);
                stack.Children.Add(buttons);
                card.Child = stack;
                AutopilotPendingList.Children.Add(card);
            }
        }

        private async void StopAllAgents_Click(object sender, RoutedEventArgs e)
        {
            // Universal STOP (roadmap P11/C11): the server-side StopAllService only ever reached
            // orchestrator-created tasks - a chat-invoked "Run PaRuX Coding Agent (local)" or
            // Computer Use session never flows through the orchestrator at all, so it was
            // previously unreachable by this button. Both loops run in this same process, so
            // cancelling them here, client-side, needs no server round trip.
            int localAgentsCancelled = LocalAgentCancellationRegistry.CancelAll();
            try
            {
                StopAllResult result = await AutopilotClient.Current.StopAllAsync(CancellationToken.None);
                if (StopAllStatusText != null)
                    StopAllStatusText.Text = T("Stopped ", "Fermate ") + result.TasksCancelled + T(" task(s), cancelled ", " attività, annullate ") + result.AutopilotRunsCancelled
                        + T(" Autopilot run(s), ", " esecuzioni Autopilot, ") + localAgentsCancelled + T(" local agent session(s), automations paused.", " sessioni dell'agente locale, automazioni sospese.");
            }
            catch (Exception ex)
            {
                Log.Write("StopAllAgents failed: " + ex.Message);
                if (StopAllStatusText != null) StopAllStatusText.Text = T("STOP ALL failed: ", "FERMA TUTTO non riuscito: ") + ex.Message;
            }
            await RefreshAgentTasksAsync();
        }

        private async void ResumeAutomations_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await AutopilotClient.Current.ResumeAutomationsAsync(CancellationToken.None);
                if (StopAllStatusText != null) StopAllStatusText.Text = T("Automations resumed.", "Automazioni riprese.");
            }
            catch (Exception ex)
            {
                Log.Write("ResumeAutomations failed: " + ex.Message);
                if (StopAllStatusText != null) StopAllStatusText.Text = T("Failed to resume: ", "Impossibile riprendere: ") + ex.Message;
            }
        }

        // ==================== Automations (Phase 16) ====================

        private async void BrowseAutomationFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder selected = await picker.PickSingleFolderAsync();
                if (selected == null) return;
                if (AutomationFolderPathBox != null) AutomationFolderPathBox.Text = selected.Path;
            }
            catch (Exception ex)
            {
                Log.Write("Automation folder pick failed: " + ex.Message);
            }
        }

        private async void CreateAutomation_Click(object sender, RoutedEventArgs e)
        {
            string? name = AutomationNameBox?.Text?.Trim();
            string? goalTemplate = AutomationGoalTemplateBox?.Text?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(goalTemplate))
            {
                if (AutomationStatus != null) AutomationStatus.Text = T("Enter a name and a task goal first.", "Inserisci prima un nome e un obiettivo dell'attività.");
                return;
            }
            string? triggerType = (AutomationTriggerTypeBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? AutomationTriggerType.Schedule;
            string? role = (AutomationRoleBox?.SelectedItem as ComboBoxItem)?.Tag as string;

            string triggerConfigJson;
            if (triggerType == AutomationTriggerType.FileCreated)
            {
                string? folderPath = AutomationFolderPathBox?.Text?.Trim();
                if (string.IsNullOrEmpty(folderPath))
                {
                    if (AutomationStatus != null) AutomationStatus.Text = T("Enter or browse to a folder for the File Created trigger.", "Inserisci o scegli una cartella per il trigger File creato.");
                    return;
                }
                string? filter = string.IsNullOrWhiteSpace(AutomationFilterBox?.Text) ? "*" : AutomationFilterBox.Text.Trim();
                triggerConfigJson = JsonSerializer.Serialize(new FileCreatedTriggerConfig { FolderPath = folderPath, Filter = filter });
            }
            else if (triggerType == AutomationTriggerType.GitCommit)
            {
                string? repositoryPath = AutomationFolderPathBox?.Text?.Trim();
                if (string.IsNullOrEmpty(repositoryPath))
                {
                    if (AutomationStatus != null) AutomationStatus.Text = T("Enter or browse to a git repository for the Git Commit trigger.", "Inserisci o scegli un repository git per il trigger Commit Git.");
                    return;
                }
                string? branch = AutomationGitBranchBox?.Text?.Trim() ?? string.Empty;
                triggerConfigJson = JsonSerializer.Serialize(new GitCommitTriggerConfig { RepositoryPath = repositoryPath, Branch = branch });
            }
            else
            {
                int intervalSeconds = AutomationIntervalSecondsBox != null && !double.IsNaN(AutomationIntervalSecondsBox.Value)
                    ? (int)AutomationIntervalSecondsBox.Value : 3600;
                triggerConfigJson = JsonSerializer.Serialize(new ScheduleTriggerConfig { IntervalSeconds = intervalSeconds });
            }
            string actionConfigJson = JsonSerializer.Serialize(new CreateAgentTaskActionConfig
            {
                GoalTemplate = goalTemplate,
                Role = string.IsNullOrEmpty(role) ? null : role
            });

            try
            {
                await AutomationClient.Current.CreateAsync(new CreateAutomationRequest
                {
                    Name = name,
                    TriggerType = triggerType,
                    TriggerConfigJson = triggerConfigJson,
                    ActionType = AutomationActionType.CreateAgentTask,
                    ActionConfigJson = actionConfigJson
                }, CancellationToken.None);
                if (AutomationNameBox != null) AutomationNameBox.Text = string.Empty;
                if (AutomationGoalTemplateBox != null) AutomationGoalTemplateBox.Text = string.Empty;
                if (AutomationStatus != null) AutomationStatus.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("CreateAutomation failed: " + ex.Message);
                if (AutomationStatus != null) AutomationStatus.Text = T("Failed to create automation: ", "Impossibile creare l'automazione: ") + ex.Message;
            }
            await RefreshAutomationsAsync();
        }

        private void RefreshAutomations_Click(object sender, RoutedEventArgs e) => _ = RefreshAutomationsAsync();

        private async Task RefreshAutomationsAsync()
        {
            if (AutomationsList == null) return;
            AutomationListResponse response;
            try { response = await AutomationClient.Current.ListAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshAutomationsAsync failed: " + ex.Message);
                if (AutomationsCountLabel != null) AutomationsCountLabel.Text = T("Failed to load automations: ", "Impossibile caricare le automazioni: ") + ex.Message;
                return;
            }

            if (AutomationsCountLabel != null) AutomationsCountLabel.Text = response.Automations.Count + T(" automation(s)", " automazioni");
            AutomationsList.Children.Clear();
            foreach (AutomationDto automation in response.Automations.OrderByDescending(a => a.CreatedUtc))
                AutomationsList.Children.Add(BuildAutomationCard(automation));
        }

        private FrameworkElement BuildAutomationCard(AutomationDto automation)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
            };
            var stack = new StackPanel { Spacing = 4 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock { Text = automation.Name, TextWrapping = TextWrapping.Wrap };
            ApplyBodyStrongStyle(nameBlock);
            Grid.SetColumn(nameBlock, 0);
            header.Children.Add(nameBlock);

            var enabledSwitch = new ToggleSwitch { IsOn = automation.Enabled, OnContent = "On", OffContent = T("Off", "No") };
            enabledSwitch.Toggled += async (s, e) =>
            {
                if (_switchesBeingReverted.Contains(enabledSwitch)) return;
                bool requested = enabledSwitch.IsOn;
                try { await AutomationClient.Current.SetEnabledAsync(automation.Id, requested, CancellationToken.None); ShowNotice(AutomationsStatusText, null); }
                catch (Exception ex)
                {
                    Log.Write("SetAutomationEnabled failed: " + ex.Message);
                    ToggleRecovery recovered = await SwitchRevert.ApplyAsync(enabledSwitch, requested, _switchesBeingReverted,
                        async ct => (await AutomationClient.Current.ListAsync(ct)).Automations.FirstOrDefault(item => item.Id == automation.Id)?.Enabled);
                    ShowNotice(AutomationsStatusText, SwitchRevert.Explain("this automation", requested, ex.Message, recovered));
                }
            };
            Grid.SetColumn(enabledSwitch, 1);
            header.Children.Add(enabledSwitch);

            var deleteButton = new Button { Content = T("Delete", "Elimina") };
            deleteButton.Click += async (s, e) =>
            {
                try { await AutomationClient.Current.DeleteAsync(automation.Id, CancellationToken.None); await RefreshAutomationsAsync(); }
                catch (Exception ex) { Log.Write("DeleteAutomation failed: " + ex.Message); }
            };
            Grid.SetColumn(deleteButton, 2);
            header.Children.Add(deleteButton);

            stack.Children.Add(header);

            string subtitle = automation.TriggerType + " -> " + automation.ActionType;
            if (automation.LastTriggeredUtc.HasValue)
                subtitle += "  ·  last fired " + automation.LastTriggeredUtc.Value.ToLocalTime().ToString("g");
            if (!string.IsNullOrEmpty(automation.LastResult))
                subtitle += "  ·  " + automation.LastResult;
            var subtitleBlock = new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(subtitleBlock);
            stack.Children.Add(subtitleBlock);

            card.Child = stack;
            return card;
        }

        // ==================== License / Trust (PaRuX Phase 37) ====================

        /// <summary>Starts a real profile run (server-side: 5 probes against whichever model is
        /// currently loaded and healthy), then polls GET .../runs/{id} until it leaves "running" -
        /// mirrors the same short-poll pattern already used elsewhere in this file for long-running
        /// server work (e.g. checkpoint activation below).</summary>
        private async void RunCapabilityProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CapabilityProfilerRunButton == null || CapabilityProfilerStatusText == null) return;
            CapabilityProfilerRunButton.IsEnabled = false;
            CapabilityProfilerStatusText.Text = T("Starting profile run…", "Avvio dell'esecuzione del profilo…");
            CapabilityProfilerResultsList?.Children.Clear();
            try
            {
                CapabilityProfileRunDto run = await CapabilityProfilerClient.Current.StartRunAsync(CancellationToken.None);
                if (run.Status == CapabilityProfileRunStatus.Error)
                {
                    CapabilityProfilerStatusText.Text = T("Couldn't run: ", "Impossibile eseguire: ") + run.Error;
                    return;
                }

                for (int i = 0; i < 200; i++) // up to ~10 minutes at 3s polling - a real 5-probe run is slower than a chat turn
                {
                    await Task.Delay(3000);
                    CapabilityProfileRunDetailDto detail = await CapabilityProfilerClient.Current.GetRunAsync(run.Id, CancellationToken.None);
                    if (detail.Run.Status == CapabilityProfileRunStatus.Running)
                    {
                        CapabilityProfilerStatusText.Text = T($"Running… {detail.Results.Count}/5 probes completed.", $"Esecuzione… {detail.Results.Count}/5 prove completate.");
                        continue;
                    }
                    RenderCapabilityProfileResult(detail);
                    return;
                }
                CapabilityProfilerStatusText.Text = T("Still running after 10 minutes - check back later by running again.", "Ancora in esecuzione dopo 10 minuti: riprova più tardi eseguendo di nuovo.");
            }
            catch (Exception ex)
            {
                Log.Write("RunCapabilityProfile_Click failed: " + ex.Message);
                CapabilityProfilerStatusText.Text = T("Couldn't run capability profile: ", "Impossibile eseguire il profilo delle capacità: ") + ex.Message;
            }
            finally
            {
                CapabilityProfilerRunButton.IsEnabled = true;
            }
        }

        private void RenderCapabilityProfileResult(CapabilityProfileRunDetailDto detail)
        {
            if (CapabilityProfilerStatusText != null)
            {
                string modelName = string.IsNullOrEmpty(detail.Run.ModelPath) ? "unknown model" : Path.GetFileName(detail.Run.ModelPath);
                CapabilityProfilerStatusText.Text = detail.Run.Status == CapabilityProfileRunStatus.Completed
                    ? T("Completed against ", "Completato su ") + modelName + (detail.Run.Role != null ? T("  ·  role \"", "  ·  ruolo \"") + detail.Run.Role + "\"" : string.Empty)
                    : T("Run failed: ", "Esecuzione non riuscita: ") + detail.Run.Error;
            }
            if (CapabilityProfilerResultsList == null) return;
            CapabilityProfilerResultsList.Children.Clear();
            foreach (CapabilityProbeResultDto result in detail.Results)
            {
                var row = new TextBlock
                {
                    Text = (result.Passed ? "✓ " : "✗ ") + result.Category + "  —  " + result.Detail,
                    TextWrapping = TextWrapping.Wrap
                };
                ApplyCaptionStyle(row);
                CapabilityProfilerResultsList.Children.Add(row);
            }
        }

        private async void InspectLicenseTrust_Click(object sender, RoutedEventArgs e)
        {
            if (LicenseTrustInspectButton == null || LicenseTrustStatusText == null || LicenseTrustResultText == null) return;

            string? repoId = LicenseTrustRepoIdBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(repoId) || !repoId.Contains('/'))
            {
                LicenseTrustStatusText.Text = T("Enter a repository id in owner/repository form.", "Inserisci un ID repository nella forma proprietario/repository.");
                LicenseTrustResultText.Text = string.Empty;
                return;
            }

            string? kind = (LicenseTrustKindBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "model";
            LicenseTrustInspectButton.IsEnabled = false;
            LicenseTrustStatusText.Text = T("Reading published Hugging Face metadata...", "Lettura dei metadati pubblicati di Hugging Face...");
            LicenseTrustResultText.Text = string.Empty;
            try
            {
                string? license;
                string? detail;
                if (kind == "dataset")
                {
                    DatasetRepoDetails result = await LicenseTrustClient.Current.GetDatasetAsync(repoId, CancellationToken.None);
                    license = result.License;
                    detail = result.Files.Count + " file(s) listed by the repository";
                }
                else
                {
                    ModelRepoDetails result = await LicenseTrustClient.Current.GetModelAsync(repoId, CancellationToken.None);
                    license = result.License;
                    detail = "model repository";
                }

                LicenseTrustStatusText.Text = repoId + "  ·  " + detail;
                LicenseTrustResultText.Text = string.IsNullOrWhiteSpace(license)
                    ? T("No license declared in the repository metadata. Do not treat this as permission to use or redistribute.", "Nessuna licenza dichiarata nei metadati del repository. Non considerarlo un permesso di usare o ridistribuire.")
                    : T("Declared license: ", "Licenza dichiarata: ") + license;
            }
            catch (Exception ex)
            {
                Log.Write("InspectLicenseTrust_Click failed: " + ex.Message);
                LicenseTrustStatusText.Text = T("Metadata lookup failed: ", "Ricerca dei metadati non riuscita: ") + ex.Message;
                LicenseTrustResultText.Text = string.Empty;
            }
            finally
            {
                LicenseTrustInspectButton.IsEnabled = true;
            }
        }

        // ==================== Observability (PaRuX Phase 35) ====================

        private void RefreshObservability_Click(object sender, RoutedEventArgs e) => _ = RefreshObservabilityAsync();

        private async Task RefreshObservabilityAsync()
        {
            if (ObservabilityTimelineList == null || ObservabilityRefreshButton == null || ObservabilityCountText == null) return;

            ObservabilityRefreshButton.IsEnabled = false;
            ObservabilityCountText.Text = T("Loading timeline...", "Caricamento della timeline...");
            try
            {
                TimelineResponse response = await ObservabilityClient.Current.GetTimelineAsync(50, CancellationToken.None);
                ObservabilityTimelineList.Children.Clear();
                foreach (TimelineEntry entry in response.Entries)
                    ObservabilityTimelineList.Children.Add(BuildObservabilityCard(entry));

                ObservabilityCountText.Text = response.Entries.Count == 0
                    ? T("No recorded activity", "Nessuna attività registrata")
                    : response.Entries.Count + T(" most recent event(s)", " eventi più recenti");
            }
            catch (Exception ex)
            {
                Log.Write("RefreshObservabilityAsync failed: " + ex.Message);
                ObservabilityCountText.Text = T("Failed to load timeline: ", "Impossibile caricare la timeline: ") + ex.Message;
            }
            finally
            {
                ObservabilityRefreshButton.IsEnabled = true;
            }
        }

        private FrameworkElement BuildObservabilityCard(TimelineEntry entry)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
            };
            var stack = new StackPanel { Spacing = 4 };

            var title = new TextBlock { Text = entry.Title, TextWrapping = TextWrapping.Wrap };
            ApplyBodyStrongStyle(title);
            stack.Children.Add(title);

            string metadata = entry.Timestamp.ToLocalTime().ToString("g") + "  ·  " + entry.Category;
            if (!string.IsNullOrWhiteSpace(entry.Detail)) metadata += "  ·  " + entry.Detail;
            var detail = new TextBlock { Text = metadata, TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(detail);
            stack.Children.Add(detail);

            card.Child = stack;
            return card;
        }

        // ==================== Universal Diff (PaRuX Phase 26) ====================

        private async void CompareUniversalDiff_Click(object sender, RoutedEventArgs e)
        {
            if (UniversalDiffCompareButton == null || UniversalDiffStatusText == null || UniversalDiffOutputBox == null) return;

            UniversalDiffCompareButton.IsEnabled = false;
            UniversalDiffStatusText.Text = T("Comparing locally...", "Confronto in locale...");
            try
            {
                DiffResult result = await DiffClient.Current.CompareAsync(
                    UniversalDiffOldTextBox?.Text ?? string.Empty,
                    UniversalDiffNewTextBox?.Text ?? string.Empty,
                    CancellationToken.None);

                UniversalDiffOutputBox.Text = UniversalDiffFormatter.Format(result);
                UniversalDiffStatusText.Text = "+" + result.AddedCount + T(" added  ·  -", " aggiunte  ·  -") + result.RemovedCount
                    + T(" removed  ·  ", " rimosse  ·  ") + result.UnchangedCount + T(" unchanged", " invariate");
            }
            catch (Exception ex)
            {
                Log.Write("CompareUniversalDiff_Click failed: " + ex.Message);
                UniversalDiffStatusText.Text = T("Comparison failed: ", "Confronto non riuscito: ") + ex.Message;
            }
            finally
            {
                UniversalDiffCompareButton.IsEnabled = true;
            }
        }

        private void ClearUniversalDiff_Click(object sender, RoutedEventArgs e)
        {
            if (UniversalDiffOldTextBox != null) UniversalDiffOldTextBox.Text = string.Empty;
            if (UniversalDiffNewTextBox != null) UniversalDiffNewTextBox.Text = string.Empty;
            if (UniversalDiffOutputBox != null) UniversalDiffOutputBox.Text = string.Empty;
            if (UniversalDiffStatusText != null) UniversalDiffStatusText.Text = string.Empty;
        }

        // ==================== Context Graph Inspector (Phase 23) ====================

        private void RefreshContextEntities_Click(object sender, RoutedEventArgs e) => _ = RefreshContextEntitiesAsync();

        private async Task RefreshContextEntitiesAsync()
        {
            if (ContextEntitiesList == null) return;
            string? type = ContextTypeFilterBox?.Text?.Trim();
            ContextEntityListResponse response;
            try { response = await ContextGraphClient.Current.ListEntitiesAsync(type, CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("RefreshContextEntitiesAsync failed: " + ex.Message);
                if (ContextEntitiesCountLabel != null) ContextEntitiesCountLabel.Text = T("Failed to load entities: ", "Impossibile caricare le entità: ") + ex.Message;
                return;
            }

            if (ContextEntitiesCountLabel != null) ContextEntitiesCountLabel.Text = response.Entities.Count + T(" entities", " entità");
            ContextEntitiesList.Children.Clear();
            foreach (ContextEntityDto entity in response.Entities.OrderByDescending(en => en.CreatedUtc).Take(20))
                ContextEntitiesList.Children.Add(BuildContextEntityCard(entity));
        }

        private FrameworkElement BuildContextEntityCard(ContextEntityDto entity)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
            };
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 1 };
            var nameBlock = new TextBlock { Text = entity.Name, TextWrapping = TextWrapping.Wrap };
            ApplyBodyStrongStyle(nameBlock);
            titleStack.Children.Add(nameBlock);
            var subtitleBlock = new TextBlock { Text = entity.EntityType + "  ·  " + entity.Id, TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(subtitleBlock);
            titleStack.Children.Add(subtitleBlock);
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var viewButton = new Button { Content = T("View neighborhood", "Vedi il vicinato") };
            viewButton.Click += async (s, e) => await ViewContextNeighborhoodAsync(entity.Id);
            Grid.SetColumn(viewButton, 1);
            header.Children.Add(viewButton);

            card.Child = header;
            return card;
        }

        private async Task ViewContextNeighborhoodAsync(string entityId)
        {
            if (ContextNeighborhoodText == null) return;
            try
            {
                ContextNeighborhoodResponse neighborhood = await ContextGraphClient.Current.GetNeighborhoodAsync(entityId, CancellationToken.None);
                var sb = new StringBuilder();
                sb.Append(neighborhood.Center.Name).Append(" (").Append(neighborhood.Center.EntityType).Append(")\n");
                sb.Append(neighborhood.OutgoingRelations.Count).Append(" outgoing, ")
                    .Append(neighborhood.IncomingRelations.Count).Append(" incoming, ")
                    .Append(neighborhood.NeighborEntities.Count).Append(" neighbor(s)\n");
                foreach (ContextRelationDto relation in neighborhood.OutgoingRelations)
                    sb.Append("  -> ").Append(relation.RelationType).Append(" -> ").Append(relation.ToEntityId).Append('\n');
                foreach (ContextRelationDto relation in neighborhood.IncomingRelations)
                    sb.Append("  <- ").Append(relation.RelationType).Append(" <- ").Append(relation.FromEntityId).Append('\n');
                ContextNeighborhoodText.Text = sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Log.Write("ViewContextNeighborhoodAsync failed: " + ex.Message);
                ContextNeighborhoodText.Text = T("Failed to load neighborhood: ", "Impossibile caricare il vicinato: ") + ex.Message;
            }
        }

        private async void CreateContextEntity_Click(object sender, RoutedEventArgs e)
        {
            string? type = ContextEntityTypeBox?.Text?.Trim();
            string? name = ContextEntityNameBox?.Text?.Trim();
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name))
            {
                if (ContextGraphStatus != null) ContextGraphStatus.Text = T("Enter both a type and a name first.", "Inserisci prima sia un tipo sia un nome.");
                return;
            }
            try
            {
                await ContextGraphClient.Current.CreateEntityAsync(new CreateContextEntityRequest { EntityType = type, Name = name }, CancellationToken.None);
                if (ContextEntityTypeBox != null) ContextEntityTypeBox.Text = string.Empty;
                if (ContextEntityNameBox != null) ContextEntityNameBox.Text = string.Empty;
                if (ContextGraphStatus != null) ContextGraphStatus.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("CreateContextEntity failed: " + ex.Message);
                if (ContextGraphStatus != null) ContextGraphStatus.Text = T("Failed to add entity: ", "Impossibile aggiungere l'entità: ") + ex.Message;
            }
            await RefreshContextEntitiesAsync();
        }

        private async void CreateContextRelation_Click(object sender, RoutedEventArgs e)
        {
            string? from = ContextRelationFromBox?.Text?.Trim();
            string? relationType = ContextRelationTypeBox?.Text?.Trim();
            string? to = ContextRelationToBox?.Text?.Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(relationType) || string.IsNullOrEmpty(to))
            {
                if (ContextGraphStatus != null) ContextGraphStatus.Text = T("Enter from id, relation type, and to id first.", "Inserisci prima ID di partenza, tipo di relazione e ID di arrivo.");
                return;
            }
            try
            {
                await ContextGraphClient.Current.CreateRelationAsync(
                    new CreateContextRelationRequest { FromEntityId = from, RelationType = relationType, ToEntityId = to }, CancellationToken.None);
                if (ContextRelationFromBox != null) ContextRelationFromBox.Text = string.Empty;
                if (ContextRelationTypeBox != null) ContextRelationTypeBox.Text = string.Empty;
                if (ContextRelationToBox != null) ContextRelationToBox.Text = string.Empty;
                if (ContextGraphStatus != null) ContextGraphStatus.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("CreateContextRelation failed: " + ex.Message);
                if (ContextGraphStatus != null) ContextGraphStatus.Text = T("Failed to add relation: ", "Impossibile aggiungere la relazione: ") + ex.Message;
            }
        }

        private static Brush? AgentStatusBrush(string status)
        {
            string? key = status switch
            {
                "completed" => "PaRuXSuccessColor",
                "failed" => "PaRuXWarnColor",
                "cancelled" => "PaRuXWarnColor",
                "blocked" => "PaRuXWarnColor",
                _ => null
            };
            if (key != null && Application.Current.Resources.TryGetValue(key, out object? color) && color is Windows.UI.Color c)
                return new SolidColorBrush(c);
            return null;
        }

        private FrameworkElement BuildAgentTaskCard(AgentTaskDto task)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = (Brush)Application.Current.Resources["PaRuXStrokeBrush"]
            };
            var stack = new StackPanel { Spacing = 6 };

            bool terminal = task.Status is "completed" or "failed" or "cancelled";

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (!terminal) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 1 };
            var goalBlock = new TextBlock { Text = task.Goal, TextWrapping = TextWrapping.Wrap };
            ApplyBodyStrongStyle(goalBlock);
            titleStack.Children.Add(goalBlock);
            string subtitle = (task.Role ?? "no role yet") + (string.IsNullOrEmpty(task.Stage) ? string.Empty : "  ·  " + task.Stage);
            var subtitleBlock = new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(subtitleBlock);
            titleStack.Children.Add(subtitleBlock);
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var statusBlock = new TextBlock
            {
                Text = task.Status.ToUpperInvariant() + (task.ProgressPercent > 0 && !terminal ? "  " + task.ProgressPercent + "%" : string.Empty),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplyCaptionStyle(statusBlock);
            Brush? statusBrush = AgentStatusBrush(task.Status);
            if (statusBrush != null) statusBlock.Foreground = statusBrush;
            Grid.SetColumn(statusBlock, 1);
            header.Children.Add(statusBlock);

            var logsButton = new Button { Content = T("Logs", "Log") };
            Grid.SetColumn(logsButton, 2);
            header.Children.Add(logsButton);

            if (!terminal)
            {
                string capturedId = task.Id;
                var cancelButton = new Button { Content = T("Cancel", "Annulla") };
                cancelButton.Click += async (s, e) =>
                {
                    try { await AgentOrchestratorClient.Current.CancelAsync(capturedId, CancellationToken.None); }
                    catch (Exception ex) { Log.Write("Cancel agent task failed: " + ex.Message); }
                    await RefreshAgentTasksAsync();
                };
                Grid.SetColumn(cancelButton, 3);
                header.Children.Add(cancelButton);
            }

            stack.Children.Add(header);

            // Never display model-authored app values or raw ResultJson. The
            // presenter accepts only a fixed, redacted worker receipt schema
            // and explicitly does NOT call the goal independently verified.
            string? guiReceiptLine = ComputerUseGuiReceiptPresenter.BuildLine(
                task.Role, task.ResultJson, task.Status, task.VerificationStatus);
            if (guiReceiptLine != null)
            {
                var evidenceLine = new TextBlock
                {
                    Text = guiReceiptLine,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                ApplyCaptionStyle(evidenceLine);
                stack.Children.Add(evidenceLine);
            }

            if (!string.IsNullOrEmpty(task.Error))
            {
                var errorBlock = new TextBlock { Text = task.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                ApplyCaptionStyle(errorBlock);
                if (Application.Current.Resources.TryGetValue("PaRuXWarnColor", out object warnColor) && warnColor is Windows.UI.Color wc)
                    errorBlock.Foreground = new SolidColorBrush(wc);
                stack.Children.Add(errorBlock);
            }

            var logsPanel = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(logsPanel);

            logsButton.Click += async (s, e) =>
            {
                bool show = logsPanel.Visibility != Visibility.Visible;
                if (show)
                {
                    logsPanel.Children.Clear();
                    AgentTaskLogListResponse logs;
                    try { logs = await AgentOrchestratorClient.Current.GetLogsAsync(task.Id, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        Log.Write("Get agent task logs failed: " + ex.Message);
                        return;
                    }
                    if (logs.Logs.Count == 0)
                    {
                        var none = new TextBlock { Text = T("No log entries yet.", "Nessuna voce di log per ora.") };
                        ApplyCaptionStyle(none);
                        logsPanel.Children.Add(none);
                    }
                    foreach (AgentTaskLogEntryDto entry in logs.Logs)
                    {
                        var line = new TextBlock
                        {
                            Text = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + entry.Message,
                            TextWrapping = TextWrapping.Wrap
                        };
                        ApplyCaptionStyle(line);
                        logsPanel.Children.Add(line);
                    }
                }
                logsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                logsButton.Content = show ? T("Hide logs", "Nascondi i log") : T("Logs", "Log");
            };

            card.Child = stack;
            return card;
        }

        // ---- Gaming & AI Resource Sharing Subsystem UI Handlers ----

        private async void GamingRefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            // An explicit refresh also reloads the editor from the server, discarding unsaved edits on purpose.
            _gamingEditorProfileId = null;
            await RefreshGamingStatusAsync();
        }

        private async void GamingEnterBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = GamingProfileSelector?.SelectedItem as ComboBoxItem;
            string? profileName = selectedItem?.Content?.ToString();
            await RunGamingActionAsync(GamingEnterBtn, "enter Gaming Mode", ct => GamingOrchestratorClient.Current.EnterGamingAsync(profileName, ct));
        }

        private async void GamingExitBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunGamingActionAsync(GamingExitBtn, "exit Gaming Mode", ct => GamingOrchestratorClient.Current.ExitGamingAsync(ct));
        }

        private async void GamingSuspendBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunGamingActionAsync(GamingSuspendBtn, "suspend the game", ct => GamingOrchestratorClient.Current.SuspendGameAsync(ct));
        }

        private async void GamingResumeBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunGamingActionAsync(GamingResumeBtn, "resume AI work", ct => GamingOrchestratorClient.Current.ResumeGameAsync(ct));
        }

        /// <summary>
        /// Runs one Gaming action with its button disabled, so a second click cannot start it again. A refusal or
        /// failure is shown with the server's reason, and the page is re-read either way.
        /// </summary>
        private async Task RunGamingActionAsync(Button button, string what, Func<CancellationToken, Task<GamingCallResult<GamingStatusDto>>> start)
        {
            button.IsEnabled = false;
            try
            {
                GamingCallResult<GamingStatusDto> result = await start(CancellationToken.None);
                ShowNotice(GamingActionText, result.Succeeded ? null : "Couldn't " + what + ": " + result.Error);
            }
            finally
            {
                button.IsEnabled = true;
            }
            await RefreshGamingStatusAsync();
        }

        private async void GamingSaveProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            // The save target is the profile picked in Active Profile, not the server's active profile.
            // Only the Quick Resume policy and grace period are replaced; the rest is the stored profile.
            string? pickedId = (GamingProfileSelector?.SelectedItem as ComboBoxItem)?.Tag is GamingProfileDto picked ? picked.Id : null;
            string? editedPolicy = (GamingQuickResumeCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            int? editedGrace = GamingGracePeriodBox == null || double.IsNaN(GamingGracePeriodBox.Value)
                ? null
                : (int)GamingGracePeriodBox.Value;
            // Only what the user changed since the editor loaded is sent, so a value stored elsewhere in the meantime is kept.
            (string? policy, int? grace) = GamingProfileEdit.ChangedFields(editedPolicy, _gamingLoadedPolicy, editedGrace, _gamingLoadedGrace);

            GamingSaveProfileBtn.IsEnabled = false;
            try
            {
                // Start from the stored profiles, not from the editor, so the save never writes stale fields back.
                GamingCallResult<List<GamingProfileDto>> loaded = await GamingOrchestratorClient.Current.ListProfilesResultAsync();
                if (!loaded.Succeeded)
                {
                    ShowNotice(GamingProfileNoticeText, "Couldn't load the gaming profiles: " + loaded.Error);
                    return;
                }

                GamingProfileDto? target = GamingProfileEdit.SelectTarget(loaded.Value ?? new List<GamingProfileDto>(), pickedId);
                if (target == null)
                {
                    ShowNotice(GamingProfileNoticeText, "Choose a profile in Active Profile, then save again.");
                    return;
                }

                GamingCallResult<GamingProfileDto> saved = await GamingOrchestratorClient.Current.SaveProfileAsync(
                    GamingProfileEdit.Apply(target, policy, grace));
                // A failed save leaves the editor as the user set it, so the values can be corrected and saved again.
                if (saved.Succeeded && string.Equals(_gamingEditorProfileId, target.Id, StringComparison.Ordinal))
                {
                    // The editor now shows what was saved, so the next save compares against these values.
                    _gamingLoadedPolicy = editedPolicy;
                    _gamingLoadedGrace = editedGrace;
                }
                ShowNotice(GamingProfileNoticeText, saved.Succeeded
                    ? "Saved the " + target.Name + " profile."
                    : "Couldn't save the " + target.Name + " profile: " + saved.Error);
            }
            finally
            {
                GamingSaveProfileBtn.IsEnabled = true;
            }
            await RefreshGamingStatusAsync();
        }

        /// <summary>Shows a notice line, or hides it when there is nothing to report.</summary>
        private static void ShowNotice(TextBlock? target, string? message)
        {
            if (target == null) return;
            target.Text = message ?? string.Empty;
            target.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Id of the profile whose Quick Resume policy and grace period the editor currently shows.</summary>
        private string? _gamingEditorProfileId;

        /// <summary>The Quick Resume policy and grace period the editor loaded for that profile; a save sends only what differs from them.</summary>
        private string? _gamingLoadedPolicy;
        private int? _gamingLoadedGrace;

        /// <summary>
        /// Loads a profile's Quick Resume policy and grace period into the editor, so a save changes only what is
        /// edited. Only a different profile reloads the editor: a page refresh never discards unsaved edits.
        /// </summary>
        private void ShowGamingProfileEditor(GamingProfileDto? profile)
        {
            if (profile == null || string.Equals(profile.Id, _gamingEditorProfileId, StringComparison.Ordinal)) return;
            _gamingEditorProfileId = profile.Id;
            _gamingLoadedPolicy = profile.QuickResumePolicy;
            _gamingLoadedGrace = profile.GracePeriodSeconds;
            if (GamingGracePeriodBox != null) GamingGracePeriodBox.Value = profile.GracePeriodSeconds;
            if (GamingQuickResumeCombo != null)
            {
                GamingQuickResumeCombo.SelectedItem = GamingQuickResumeCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, profile.QuickResumePolicy, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Page resources first, then application resources. The gaming provider list looked
        /// its app-level card/caption styles up on the page only; the lookup threw ("Unspecified
        /// error"), aborting the refresh before the provider list, Active Profile and Quick
        /// Resume selectors were filled, so the page showed them empty (live functional test).
        /// </summary>
        private object FindResource(string key) =>
            Resources.TryGetValue(key, out object? local) ? local : Application.Current.Resources[key];

        private void GamingProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Picking a profile loads its own policy and grace period, so Save edits the profile that was picked.
            ShowGamingProfileEditor((GamingProfileSelector?.SelectedItem as ComboBoxItem)?.Tag as GamingProfileDto);
        }

        private void GamingQuickResumeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private async Task RefreshGamingStatusAsync()
        {
            try
            {
                var status = await GamingOrchestratorClient.Current.GetStatusAsync();
                var providers = await GamingOrchestratorClient.Current.ListProvidersAsync();
                var profiles = await GamingOrchestratorClient.Current.ListProfilesAsync();

                if (status != null)
                {
                    if (GamingStatusBadge != null)
                    {
                        GamingStatusBadge.Text = status.State.ToUpperInvariant();
                    }
                    if (GamingStatusMessage != null)
                    {
                        GamingStatusMessage.Text = status.Message ?? string.Empty;
                    }
                    if (GamingActiveLauncherLabel != null)
                    {
                        GamingActiveLauncherLabel.Text = T("Active Launcher: ", "Launcher attivo: ") + (status.ActiveLauncher ?? T("None", "Nessuno"));
                    }
                    if (GamingActiveInputLabel != null)
                    {
                        GamingActiveInputLabel.Text = T("Active Input: ", "Input attivo: ") + (status.ActiveInput ?? T("Native", "Nativo"));
                    }
                    if (GamingVramLabel != null)
                    {
                        // Null means the driver could not report the game's GPU memory; show that, never a guessed number.
                        string gameVram = status.SuspendedVramMib is long usedMib
                            ? usedMib + " MB used by game"
                            : "game VRAM not reported by the driver";
                        GamingVramLabel.Text = T($"VRAM Allocation: {gameVram} / {status.SystemTotalVramMib} MB total", $"Allocazione VRAM: {gameVram} / {status.SystemTotalVramMib} MB in totale");
                    }
                    if (GamingSuspendedGameLabel != null)
                    {
                        GamingSuspendedGameLabel.Text = T("Suspended Game: ", "Gioco sospeso: ") + (status.SuspendedProcessName ?? T("None", "Nessuno"));
                    }
                    if (GamingWorkloadsLabel != null)
                    {
                        GamingWorkloadsLabel.Text = status.IsGamingActive
                            ? T("AI Workloads: Releasing GPU resources for gaming", "Carichi di lavoro IA: rilascio delle risorse GPU per il gaming")
                            : T("AI Workloads: Server active & ready", "Carichi di lavoro IA: server attivo e pronto");
                    }
                }

                if (GamingProvidersList != null)
                {
                    GamingProvidersList.Children.Clear();
                    foreach (var p in providers)
                    {
                        var border = new Border
                        {
                            Style = (Style)FindResource("PaRuXCardStyle"),
                            Padding = new Thickness(10)
                        };
                        var grid = new Grid();
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var infoStack = new StackPanel { Spacing = 2 };
                        var title = new TextBlock { Text = p.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                        var desc = new TextBlock { Text = p.Description, Style = (Style)FindResource("PaRuXCaptionTextStyle") };
                        infoStack.Children.Add(title);
                        infoStack.Children.Add(desc);
                        grid.Children.Add(infoStack);
                        Grid.SetColumn(infoStack, 0);

                        var badge = new TextBlock
                        {
                            Text = p.Installed ? T("[Detected]", "[Rilevato]") : T("[Not Installed]", "[Non installato]"),
                            Foreground = p.Installed ? (SolidColorBrush)FindResource("PaRuXDotOkBrush") : (SolidColorBrush)FindResource("PaRuXDotOffBrush"),
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold
                        };
                        grid.Children.Add(badge);
                        Grid.SetColumn(badge, 1);

                        border.Child = grid;
                        GamingProvidersList.Children.Add(border);
                    }
                }

                if (GamingProfileSelector != null && profiles.Count > 0)
                {
                    // Keep the profile the user picked when it still exists; otherwise select the active profile.
                    string? pickedId = (GamingProfileSelector.SelectedItem as ComboBoxItem)?.Tag is GamingProfileDto previous ? previous.Id : null;
                    GamingProfileSelector.Items.Clear();
                    var added = new List<ComboBoxItem>();
                    foreach (var prof in profiles)
                    {
                        var item = new ComboBoxItem { Content = prof.Name, Tag = prof };
                        GamingProfileSelector.Items.Add(item);
                        added.Add(item);
                    }
                    ComboBoxItem? keep = added.FirstOrDefault(item => ((GamingProfileDto)item.Tag!).Id == pickedId)
                        ?? added.FirstOrDefault(item => ((GamingProfileDto)item.Tag!).Name == status?.ActiveProfileName)
                        ?? added[0];
                    GamingProfileSelector.SelectedItem = keep;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshGamingStatusAsync error: " + ex.Message);
            }
        }
    }
}
