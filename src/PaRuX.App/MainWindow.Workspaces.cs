using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>
    /// First-class workspace navigation and recent conversations share the NavigationView's native
    /// MenuItems scroll surface, so neither section can paint over or make the other unreachable.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _synchronizingStudioModeToggles;

        private bool _applyingNavigationMode;
        private bool _mediaEditSourceBridgeSubscribed;

        private enum StudioWorkspace
        {
            Chat,
            Create,
            Projects,
            ModelHub,
            ModelRouting,
            Lifecycle,
            Workflows,
            Tools,
            Observability,
            Updates,
            Runtime,
            Games,
            DocumentSearch
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            GameLibraryNavItem.Content = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "Giochi" : "Games";
            UpdateConsoleModeButton();
            _applyingNavigationMode = true;
            try
            {
                bool studioModeEnabled = SettingsStore.Current.Data.StudioModeEnabled;
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _applyingNavigationMode = false;
            }
            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
            ApplyNavigationMode(navigateHomeIfHidden: false);
            RestoreModeTabFromSettings();
            ApplySetupProfile();

            if (!_mediaEditSourceBridgeSubscribed)
            {
                _mediaEditSourceBridgeSubscribed = true;
                MediaEditSourceBridge.Current.EditSourceRequested += (_, _) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        MediaEditSourceBridge.TryApplyToCreateView(CreatePage);
                        ShowStudioWorkspace(StudioWorkspace.Create);
                    });
                };
            }
        }

        /// <summary>Opens the Model Hub (on its Start tab) from anywhere, e.g. the chat model picker's "Get more models".</summary>
        public void ShowModelHub()
        {
            ShowStudioWorkspace(StudioWorkspace.ModelHub);
            ModelHubPage.OnShown();
        }

        private void StudioModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingNavigationMode || _synchronizingStudioModeToggles) return;

            bool studioModeEnabled = sender is ToggleSwitch source
                ? source.IsOn
                : StudioModeToggle.IsOn;
            _synchronizingStudioModeToggles = true;
            try
            {
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _synchronizingStudioModeToggles = false;
            }

            SettingsStore.Current.Data.StudioModeEnabled = studioModeEnabled;
            SettingsStore.Current.Save();
            ApplyNavigationMode(navigateHomeIfHidden: true);
        }

        private const string AdvancedWorkspaceList =
            "Model routing, Runtime, Lifecycle Lab, Workflows, Tools, Observability and Updates";

        private void ApplyNavigationMode(bool navigateHomeIfHidden)
        {
            bool studio = StudioModeToggle.IsOn;
            Visibility advanced = studio ? Visibility.Visible : Visibility.Collapsed;

            ModelRoutingNavItem.Visibility = advanced;
            LifecycleNavItem.Visibility = advanced;
            WorkflowNavItem.Visibility = advanced;
            ToolsNavItem.Visibility = advanced;
            ObservabilityNavItem.Visibility = advanced;
            UpdatesNavItem.Visibility = advanced;
            RuntimeNavItem.Visibility = advanced;
            ChatPage.ApplyInterfaceMode(studio);
            ModelHubPage.ApplyInterfaceMode(studio);
            ApplyModeTabVisibility();

            if (!studio && navigateHomeIfHidden && !IsGamingModeTab)
            {
                ShowChat();
                RefreshHistory();
            }
        }

        private bool IsAdvancedWorkspaceVisible() =>
            ModelRoutingPage.Visibility == Visibility.Visible
            || LifecycleLabPage.Visibility == Visibility.Visible
            || WorkflowPage.Visibility == Visibility.Visible
            || ToolsAutomationPage.Visibility == Visibility.Visible
            || ObservabilityPage.Visibility == Visibility.Visible
            || UpdateManagerPage.Visibility == Visibility.Visible
            || RuntimeManagerPage.Visibility == Visibility.Visible;

        private void HideAllWorkspacePages()
        {
            CreatePage.Visibility = Visibility.Collapsed;
            ProjectsPage.Visibility = Visibility.Collapsed;
            if (ModelHubPage.Visibility == Visibility.Visible)
                ModelHubPage.OnHidden();
            ModelHubPage.Visibility = Visibility.Collapsed;
            if (DocumentSearchPage.Visibility == Visibility.Visible)
                DocumentSearchPage.OnHidden();
            DocumentSearchPage.Visibility = Visibility.Collapsed;
            ModelRoutingPage.Visibility = Visibility.Collapsed;
            LifecycleLabPage.Visibility = Visibility.Collapsed;
            WorkflowPage.Visibility = Visibility.Collapsed;
            ToolsAutomationPage.Visibility = Visibility.Collapsed;
            ObservabilityPage.Visibility = Visibility.Collapsed;
            UpdateManagerPage.Visibility = Visibility.Collapsed;
            RuntimeManagerPage.Visibility = Visibility.Collapsed;
            GameLibraryPage.CancelExpensiveWork();
            GameLibraryPage.Visibility = Visibility.Collapsed;
        }

        private bool _onboardingHandoffActive;

        private void OnFirstModelRequested(string repoId)
        {
            // The recommended first-model path hands off to Model Hub and MUST return to onboarding;
            // OnboardingCompleted is NOT marked true until the user finishes setup with working chat evidence.
            _onboardingHandoffActive = true;
            if (OnboardingReturnBanner != null) OnboardingReturnBanner.IsOpen = true;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;

            ShowStudioWorkspace(StudioWorkspace.ModelHub);
            if (CorporateMode.IsActive && string.IsNullOrWhiteSpace(repoId))
                ModelHubPage.OnShown(); // Corporate's local model management, not an HF search
            else
                ModelHubPage.OnShownSearching(repoId, OnboardingPage.SuggestedQuantHintForHandoff);
            GuidedTourDecision tourDecision = GuidedTourTriggerPolicy.EvaluateHandoff(
                SettingsStore.Current.Data.GuidedTourCompleted,
                "ModelHub");
            if (tourDecision.ShouldStart)
            {
                _dispatcher.TryEnqueue(MaybeStartGuidedTour);
            }
        }

        private void ReturnToOnboardingButton_Click(object sender, RoutedEventArgs e)
        {
            ShowOnboarding();
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (_onboardingHandoffActive)
            {
                ShowOnboarding();
            }
        }

        private bool TryNavigateWorkspace(string tag)
        {
            // A link, keyboard action or UIA invoke must obey the SAME profile as the
            // sidebar. Visibility is not a sufficient access boundary: a hidden item
            // or old deep link could otherwise open a different product surface.
            if (!IsWorkspaceAllowedForSetup(tag)) return true;
            switch (tag)
            {
                case "__games__":
                    ShowStudioWorkspace(StudioWorkspace.Games);
                    GameLibraryPage.OnShown();
                    return true;
                case "__chat__":
                    ShowChat();
                    return true;
                case "__create__":
                    ShowStudioWorkspace(StudioWorkspace.Create);
                    CreatePage.OnShown();
                    return true;
                case "__documents__":
                    ShowStudioWorkspace(StudioWorkspace.DocumentSearch);
                    DocumentSearchPage.OnShown();
                    return true;
                case "__projects__":
                    ShowStudioWorkspace(StudioWorkspace.Projects);
                    ProjectsPage.OnShown();
                    return true;
                case "__modelhub__":
                    ShowStudioWorkspace(StudioWorkspace.ModelHub);
                    ModelHubPage.OnShown();
                    return true;
                case "__modelrouting__":
                    ShowStudioWorkspace(StudioWorkspace.ModelRouting);
                    ModelRoutingPage.OnShown();
                    return true;
                case "__lifecycle__":
                    ShowStudioWorkspace(StudioWorkspace.Lifecycle);
                    LifecycleLabPage.OnShown();
                    return true;
                case "__workflows__":
                    ShowStudioWorkspace(StudioWorkspace.Workflows);
                    WorkflowPage.OnShown();
                    return true;
                case "__tools__":
                    ShowStudioWorkspace(StudioWorkspace.Tools);
                    ToolsAutomationPage.OnShown();
                    return true;
                case "__observability__":
                    ShowStudioWorkspace(StudioWorkspace.Observability);
                    ObservabilityPage.OnShown();
                    return true;
                case "__updates__":
                    ShowStudioWorkspace(StudioWorkspace.Updates);
                    UpdateManagerPage.OnShown();
                    return true;
                case "__runtime__":
                    ShowStudioWorkspace(StudioWorkspace.Runtime);
                    RuntimeManagerPage.OnShown();
                    return true;
                case "__help__":
                    _ = ShowInAppHelpDialogAsync();
                    return true;
                case "__settings__":
                    ShowSettings();
                    return true;
                default:
                    return false;
            }
        }

        private void ShowStudioWorkspace(StudioWorkspace workspace)
        {
            if (!IsWorkspaceAllowedForSetup(NavigationItemFor(workspace).Tag as string))
                return;
            if (workspace == StudioWorkspace.Chat)
            {
                ShowChat();
                return;
            }

            SyncModeTabToWorkspace(gaming: workspace == StudioWorkspace.Games);
            SetNavPaneVisible(true);
            ChatPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            OnboardingPage.Visibility = Visibility.Collapsed;
            HideAllWorkspacePages();
            SelectWorkspaceNavigationItem(NavigationItemFor(workspace));

            switch (workspace)
            {
                case StudioWorkspace.Create: CreatePage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Projects: ProjectsPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.ModelHub: ModelHubPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.ModelRouting: ModelRoutingPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Lifecycle: LifecycleLabPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Workflows: WorkflowPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Tools: ToolsAutomationPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Observability: ObservabilityPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Updates: UpdateManagerPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Runtime: RuntimeManagerPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Games: GameLibraryPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.DocumentSearch: DocumentSearchPage.Visibility = Visibility.Visible; break;
            }
        }

        private bool IsWorkspaceAllowedForSetup(string? tag) =>
            WorkspaceNavigationOrder.CanOpenWorkspace(tag,
                SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId),
                CorporateMode.IsActive);

        /// <summary>If the user switches from Both to AI-only or Gaming-only while on
        /// an incompatible page, leave that page immediately. Also repairs the active
        /// page after restart or restored deep links. Never switches away from first-run setup.</summary>
        private void EnsureWorkspaceFitsSetupProfile()
        {
            if (OnboardingPage.Visibility == Visibility.Visible) return;
            string? activeTag = ActiveWorkspaceTag();
            if (activeTag == null || IsWorkspaceAllowedForSetup(activeTag)) return;

            if (CurrentSetupPlan.StartInGamingTab)
            {
                ShowStudioWorkspace(StudioWorkspace.Games);
                GameLibraryPage.OnShown();
            }
            else
            {
                ShowChat();
            }
        }

        private string? ActiveWorkspaceTag()
        {
            if (GameLibraryPage.Visibility == Visibility.Visible) return "__games__";
            if (CreatePage.Visibility == Visibility.Visible) return "__create__";
            if (ProjectsPage.Visibility == Visibility.Visible) return "__projects__";
            if (ModelHubPage.Visibility == Visibility.Visible) return "__modelhub__";
            if (DocumentSearchPage.Visibility == Visibility.Visible) return "__documents__";
            if (ModelRoutingPage.Visibility == Visibility.Visible) return "__modelrouting__";
            if (LifecycleLabPage.Visibility == Visibility.Visible) return "__lifecycle__";
            if (WorkflowPage.Visibility == Visibility.Visible) return "__workflows__";
            if (ToolsAutomationPage.Visibility == Visibility.Visible) return "__tools__";
            if (ObservabilityPage.Visibility == Visibility.Visible) return "__observability__";
            if (UpdateManagerPage.Visibility == Visibility.Visible) return "__updates__";
            if (RuntimeManagerPage.Visibility == Visibility.Visible) return "__runtime__";
            if (ChatPage.Visibility == Visibility.Visible) return "__chat__";
            if (SettingsPage.Visibility == Visibility.Visible) return "__settings__";
            return null;
        }

        private void SelectWorkspaceNavigationItem(NavigationViewItem? selected)
        {
            // Corporate Model Hub can open from Chat's model picker while absent
            // from the compact sidebar. Never focus a collapsed navigation item.
            if (selected != null && selected.Visibility != Visibility.Visible)
                selected = ChatNavItem.Visibility == Visibility.Visible ? ChatNavItem : null;
            NavView.SelectedItem = selected;
            selected?.Focus(FocusState.Programmatic);
        }

        private NavigationViewItem[] WorkspaceNavigationItems() => new[]
        {
            ChatNavItem, CreateNavItem, ProjectsNavItem, GameLibraryNavItem, DocumentSearchNavItem, ModelHubNavItem, ModelRoutingNavItem,
            LifecycleNavItem, WorkflowNavItem, ToolsNavItem, ObservabilityNavItem,
            UpdatesNavItem, RuntimeNavItem, SettingsNavItem, HelpNavItem
        };

        private NavigationViewItem NavigationItemFor(StudioWorkspace workspace) => workspace switch
        {
            StudioWorkspace.Chat => ChatNavItem,
            StudioWorkspace.Games => GameLibraryNavItem,
            StudioWorkspace.DocumentSearch => DocumentSearchNavItem,
            StudioWorkspace.Create => CreateNavItem,
            StudioWorkspace.Projects => ProjectsNavItem,
            StudioWorkspace.ModelHub => ModelHubNavItem,
            StudioWorkspace.ModelRouting => ModelRoutingNavItem,
            StudioWorkspace.Lifecycle => LifecycleNavItem,
            StudioWorkspace.Workflows => WorkflowNavItem,
            StudioWorkspace.Tools => ToolsNavItem,
            StudioWorkspace.Observability => ObservabilityNavItem,
            StudioWorkspace.Updates => UpdatesNavItem,
            StudioWorkspace.Runtime => RuntimeNavItem,
            _ => CreateNavItem
        };
    }
}
