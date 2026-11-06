using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Services;
using Windows.System;

namespace PaRuX;

/// <summary>
/// Top-level shell mode switch (AI / Gaming). It only changes which sidebar items are
/// visible and which workspace is shown; no item is removed and no AutomationId changes.
/// AI: Chat, Create, Projects, Model Hub (+ advanced AI items in Advanced interface).
/// Gaming: Games (+ Console / TV button in the pane footer). Settings and Help are in both.
/// </summary>
public sealed partial class MainWindow
{
    internal const string ModeKeyAi = "AI";
    internal const string ModeKeyGaming = "Gaming";

    private string _modeTab = ModeKeyAi;
    private bool _applyingModeTab;
    // Until the saved tab has been restored (NavView_Loaded) implicit syncs must not overwrite it.
    private bool _modeTabInitialized;

    private bool IsGamingModeTab => string.Equals(_modeTab, ModeKeyGaming, StringComparison.Ordinal);

    private static string ModeTabText(string en, string it) => ConsoleText(en, it);

    private void InitializeModeTabs()
    {
        ApplyModeTabLocalization();

        AddModeTabAccelerator(VirtualKey.Number1, ModeKeyAi);
        AddModeTabAccelerator(VirtualKey.Number2, ModeKeyGaming);

        _applyingModeTab = true;
        try { ModeTabs.SelectedItem = ModeTabAiItem; }
        finally { _applyingModeTab = false; }
        ApplyModeTabVisibility();
    }

    private void AddModeTabAccelerator(VirtualKey key, string tab)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (App.IsShuttingDown || NavView.IsPaneVisible == false || !CurrentSetupPlan.ShowModeTabs) return;
            SelectModeTab(tab, navigate: true);
            (tab == ModeKeyAi ? ModeTabAiItem : ModeTabGamingItem).Focus(FocusState.Keyboard);
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void ApplyModeTabLocalization()
    {
        string groupName = ModeTabText("Workspace mode", "Modalità dell'area di lavoro");
        AutomationProperties.SetName(ModeTabs, groupName);
        AutomationProperties.SetName(ModeTabAiItem, ModeTabText("AI mode", "Modalità AI"));
        AutomationProperties.SetName(ModeTabGamingItem, ModeTabText("Gaming mode", "Modalità Gaming"));
        ToolTipService.SetToolTip(ModeTabAiItem, ModeTabText("AI (Ctrl+1)", "AI (Ctrl+1)"));
        ToolTipService.SetToolTip(ModeTabGamingItem, ModeTabText("Gaming (Ctrl+2)", "Gaming (Ctrl+2)"));
    }

    /// <summary>Called once from NavView_Loaded, after the existing startup page was chosen.</summary>
    private void RestoreModeTabFromSettings()
    {
        if (_modeTabInitialized) return;
        _modeTabInitialized = true;
        SetupProfilePlan setup = CurrentSetupPlan;
        // AI only / Gaming only have no tab to remember: the profile decides where the app starts.
        bool wantsGaming = setup.ShowModeTabs
            ? string.Equals(SettingsStore.Current.Data.LastModeTab, ModeKeyGaming, StringComparison.Ordinal)
            : setup.StartInGamingTab;
        // Only restore over the plain startup Chat page; first-run onboarding and test/deep-link
        // initial pages keep priority.
        if (wantsGaming && SettingsStore.Current.Data.OnboardingCompleted
            && NavView.IsPaneVisible && ChatPage.Visibility == Visibility.Visible)
        {
            SelectModeTab(ModeKeyGaming, navigate: true);
        }
    }

    private static SetupProfilePlan CurrentSetupPlan =>
        SetupProfilePolicy.Plan(SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId));

    /// <summary>
    /// Applies the setup profile (AI only / Gaming only / Both) to the shell: with a single-purpose profile the AI/Gaming
    /// switch is hidden and the app stays in that workspace. Nothing is deleted or uninstalled, and the choice can be changed
    /// again in Settings. Safe to call repeatedly.
    /// </summary>
    internal void ApplySetupProfile()
    {
        SetupProfilePlan setup = CurrentSetupPlan;
        ModeTabsHost.Visibility = setup.ShowModeTabs ? Visibility.Visible : Visibility.Collapsed;
        if (!setup.ShowModeTabs)
        {
            string forced = setup.StartInGamingTab ? ModeKeyGaming : ModeKeyAi;
            bool settledShell = SettingsStore.Current.Data.OnboardingCompleted && OnboardingPage.Visibility != Visibility.Visible;
            if (!string.Equals(_modeTab, forced, StringComparison.Ordinal))
                SelectModeTab(forced, navigate: settledShell);
        }
        ApplyModeTabVisibility();
        EnsureWorkspaceFitsSetupProfile();
    }

    /// <summary>Selects a tab; optionally opens its default page (AI = Chat, Gaming = Games library).</summary>
    private void SelectModeTab(string tab, bool navigate)
    {
        tab = string.Equals(tab, ModeKeyGaming, StringComparison.Ordinal) ? ModeKeyGaming : ModeKeyAi;
        SetupProfilePlan setup = CurrentSetupPlan;
        if (!setup.ShowModeTabs) tab = setup.StartInGamingTab ? ModeKeyGaming : ModeKeyAi;   // Ctrl+1/Ctrl+2 cannot leave the profile
        SetModeTabState(tab);
        if (!navigate) return;

        if (tab == ModeKeyGaming) TryNavigateWorkspace("__games__");
        else ShowChat();
    }

    /// <summary>
    /// Keeps the tab in step with the workspace that is really shown (Console mode, tray Quick Chat,
    /// deep links...). Never navigates.
    /// </summary>
    private void SyncModeTabToWorkspace(bool gaming) =>
        SetModeTabState(gaming ? ModeKeyGaming : ModeKeyAi);

    private void SetModeTabState(string tab)
    {
        SetupProfilePlan setup = CurrentSetupPlan;
        if (!setup.ShowModeTabs)
            tab = setup.StartInGamingTab ? ModeKeyGaming : ModeKeyAi;
        bool changed = !string.Equals(_modeTab, tab, StringComparison.Ordinal);
        _modeTab = tab;
        _applyingModeTab = true;
        try { ModeTabs.SelectedItem = tab == ModeKeyGaming ? ModeTabGamingItem : ModeTabAiItem; }
        finally { _applyingModeTab = false; }
        if (changed) ApplyModeTabVisibility();

        if (_modeTabInitialized && changed
            && !string.Equals(SettingsStore.Current.Data.LastModeTab, tab, StringComparison.Ordinal))
        {
            SettingsStore.Current.Data.LastModeTab = tab;
            SettingsStore.Current.Save();
        }
    }

    private void ModeTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_applyingModeTab || !CurrentSetupPlan.ShowModeTabs) return;
        string tab = ReferenceEquals(sender.SelectedItem, ModeTabGamingItem) ? ModeKeyGaming : ModeKeyAi;
        if (string.Equals(tab, _modeTab, StringComparison.Ordinal)) return;
        SelectModeTab(tab, navigate: true);
    }

    private void ModeTabs_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CurrentSetupPlan.ShowModeTabs || (e.Key != VirtualKey.Left && e.Key != VirtualKey.Right)) return;
        // Arrow keys inside the selector switch tabs; Home/End are not needed for two tabs.
        string target = e.Key == VirtualKey.Left ? ModeKeyAi : ModeKeyGaming;
        e.Handled = true;
        if (!string.Equals(target, _modeTab, StringComparison.Ordinal))
            SelectModeTab(target, navigate: true);
        // Page navigation may move focus into the workspace; keep it on the tab the user is driving.
        (target == ModeKeyAi ? ModeTabAiItem : ModeTabGamingItem).Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Applies visibility only. Collapsed items are neither rendered nor focusable, but still exist
    /// in the NavigationView (same x:Name, Tag and AutomationId).
    /// </summary>
    private void ApplyModeTabVisibility()
    {
        bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
        SetupProfile selected = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
        bool gaming = !corporate && (selected == SetupProfile.GamingOnly
            || (selected == SetupProfile.Both && IsGamingModeTab));
        bool ai = !gaming;
        bool studio = StudioModeToggle.IsOn;
        var visibleIds = WorkspaceNavigationOrder
            .GetOrderedAutomationIds(studio, gaming, selected, corporate)
            .ToHashSet(StringComparer.Ordinal);
        foreach (NavigationViewItem item in WorkspaceNavigationItems())
        {
            string id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item);
            item.Visibility = visibleIds.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        }

        // Corporate is intentionally Chat + documents + Settings/Help. Model Hub stays
        // reachable from Chat model selection/Settings, but not as sidebar clutter.
        WorkspaceSectionHeader.Content = corporate
            ? ModeTabText("Assistant", "Assistente")
            : gaming ? ModeTabText("Gaming", "Giochi")
                : ModeTabText("Workspaces", "Aree di lavoro");
        StudioModeToggle.Visibility = !corporate && !gaming
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        ConsoleModeButton.Visibility = _consoleModeActive || gaming
            ? Visibility.Visible : Visibility.Collapsed;

        // Only AI profiles have conversations. Gaming-only must not expose a chat
        // search box, new-chat action, history, nor a shortcut to those controls.
        SearchBox.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        if (NewChatButton.Parent is FrameworkElement chatButtons)
            chatButtons.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;

        var workspaceItems = WorkspaceNavigationItems();
        foreach (object menuItem in NavView.MenuItems)
        {
            if (ReferenceEquals(menuItem, WorkspaceSectionHeader)) continue;
            if (menuItem is NavigationViewItemBase element
                && !workspaceItems.Any(w => ReferenceEquals(w, menuItem)))
                element.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
