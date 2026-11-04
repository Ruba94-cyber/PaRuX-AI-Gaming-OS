using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Metadata descriptor for a workspace navigation entry in the sidebar.
    /// </summary>
    public sealed record WorkspaceNavigationEntry(
        string AutomationId,
        string Tag,
        string DisplayName,
        bool IsAdvancedOnly,
        bool IsHome = false,
        bool IsGamingOnly = false,
        bool IsCorporateOnly = false);

    /// <summary>
    /// Pure policy defining the canonical ordered list of workspace entries,
    /// their AutomationIds, Simple/Advanced visibility filtering, and deterministic
    /// Next/Previous traversal with defined ends for keyboard and focus navigation.
    /// </summary>
    public static class WorkspaceNavigationOrder
    {
        public const string ChatAutomationId = "Navigation_Chat_Item";
        public const string CreateAutomationId = "Navigation_Create_Item";
        public const string ProjectsAutomationId = "Navigation_Projects_Item";
        // Gaming-tab workspace (AI|Gaming top tabs). It sits after Projects in MainWindow.xaml, so it
        // is part of the canonical XAML order, but it is only reachable while the Gaming tab is
        // active: the AI tab hides it and the Gaming tab hides every AI workspace.
        public const string GameLibraryAutomationId = "Navigation_GameLibrary_Item";
        public const string GamesWorkspaceTag = "__games__";
        // Corporate Lite edition only (PARUX_CORPORATE): hidden everywhere else.
        public const string DocumentSearchAutomationId = "Navigation_DocumentSearch_Item";
        public const string DocumentSearchWorkspaceTag = "__documents__";
        public const string ModelHubAutomationId = "Navigation_ModelHub_Item";
        public const string ModelRoutingAutomationId = "Navigation_ModelRouting_Item";
        public const string LifecycleAutomationId = "Navigation_Lifecycle_Item";
        public const string WorkflowAutomationId = "Navigation_Workflows_Item";
        public const string ToolsAutomationId = "Navigation_Tools_Item";
        public const string ObservabilityAutomationId = "Navigation_Observability_Item";
        public const string UpdatesAutomationId = "Navigation_Updates_Item";
        public const string RuntimeAutomationId = "Navigation_Runtime_Item";
        public const string SettingsAutomationId = "Navigation_Settings_Item";
        public const string HelpAutomationId = "Navigation_Help_Item";

        private static readonly IReadOnlyList<WorkspaceNavigationEntry> AllEntries = new List<WorkspaceNavigationEntry>
        {
            new(ChatAutomationId, ChatHomeNavigationPolicy.ChatWorkspaceTag, "Chat", IsAdvancedOnly: false, IsHome: true),
            new(CreateAutomationId, ChatHomeNavigationPolicy.CreateWorkspaceTag, "Create", IsAdvancedOnly: false),
            new(ProjectsAutomationId, ChatHomeNavigationPolicy.ProjectsWorkspaceTag, "Projects", IsAdvancedOnly: false),
            new(GameLibraryAutomationId, GamesWorkspaceTag, "Games", IsAdvancedOnly: false, IsGamingOnly: true),
            new(DocumentSearchAutomationId, DocumentSearchWorkspaceTag, "Document search", IsAdvancedOnly: false, IsCorporateOnly: true),
            new(ModelHubAutomationId, ChatHomeNavigationPolicy.ModelHubWorkspaceTag, "Model Hub", IsAdvancedOnly: false),
            new(ModelRoutingAutomationId, ChatHomeNavigationPolicy.ModelRoutingWorkspaceTag, "Model Intelligence", IsAdvancedOnly: true),
            new(LifecycleAutomationId, ChatHomeNavigationPolicy.LifecycleWorkspaceTag, "Lifecycle Lab", IsAdvancedOnly: true),
            new(WorkflowAutomationId, ChatHomeNavigationPolicy.WorkflowWorkspaceTag, "Workflows", IsAdvancedOnly: true),
            new(ToolsAutomationId, ChatHomeNavigationPolicy.ToolsWorkspaceTag, "Tools & Automations", IsAdvancedOnly: true),
            new(ObservabilityAutomationId, ChatHomeNavigationPolicy.ObservabilityWorkspaceTag, "Observability", IsAdvancedOnly: true),
            new(UpdatesAutomationId, ChatHomeNavigationPolicy.UpdatesWorkspaceTag, "Updates", IsAdvancedOnly: true),
            new(RuntimeAutomationId, ChatHomeNavigationPolicy.RuntimeWorkspaceTag, "Runtime", IsAdvancedOnly: true),
            new(SettingsAutomationId, ChatHomeNavigationPolicy.SettingsWorkspaceTag, "Settings", IsAdvancedOnly: false),
            new(HelpAutomationId, ChatHomeNavigationPolicy.HelpWorkspaceTag, "Help", IsAdvancedOnly: false),
        }.AsReadOnly();

        /// <summary>
        /// All workspace entries in canonical visual order.
        /// </summary>
        public static IReadOnlyList<WorkspaceNavigationEntry> Entries => AllEntries;

        /// <summary>
        /// Canonical list of AutomationIds in visual order matching MainWindow.xaml.
        /// </summary>
        public static readonly IReadOnlyList<string> CanonicalOrder =
            AllEntries.Select(e => e.AutomationId).ToList().AsReadOnly();

        public static IReadOnlyList<string> CanonicalAutomationIds => CanonicalOrder;

        /// <summary>
        /// Gets the ordered workspace entries filtered by interface mode (Simple vs Advanced).
        /// </summary>
        public static IReadOnlyList<WorkspaceNavigationEntry> GetEntries(bool isAdvancedMode, bool isGamingMode = false)
        {
            bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
            return AllEntries
                .Where(e => (corporate || !e.IsCorporateOnly) && (isGamingMode
                    ? e.IsGamingOnly || IsSharedAcrossModes(e)
                    : !e.IsGamingOnly && (isAdvancedMode || !e.IsAdvancedOnly)))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the ordered AutomationIds filtered by interface mode.
        /// </summary>
        public static IReadOnlyList<string> GetOrderedAutomationIds(bool isAdvancedMode, bool isGamingMode = false)
        {
            return GetEntries(isAdvancedMode, isGamingMode)
                .Select(e => e.AutomationId)
                .ToList()
                .AsReadOnly();
        }

        // Settings and Help stay visible in both top tabs.
        private static bool IsSharedAcrossModes(WorkspaceNavigationEntry e) =>
            e.AutomationId == SettingsAutomationId || e.AutomationId == HelpAutomationId;

        public static IReadOnlyList<string> SimpleOrder => GetOrderedAutomationIds(isAdvancedMode: false);
        public static IReadOnlyList<string> AdvancedOrder => GetOrderedAutomationIds(isAdvancedMode: true);

        /// <summary>
        /// Resolves which workspace should anchor arrow navigation. A workspace selected
        /// after the current focus is authoritative; focus moved after selection remains
        /// authoritative so repeated arrows continue from the focused item.
        /// </summary>
        public static string? ResolveNavigationOrigin(
            string? focusedAutomationId,
            string? selectedAutomationId,
            bool keyboardFocusIsCurrent,
            bool isAdvancedMode,
            bool isGamingMode = false)
        {
            IReadOnlyList<string> visibleIds = GetOrderedAutomationIds(isAdvancedMode, isGamingMode);
            bool focusedIsVisible = !string.IsNullOrWhiteSpace(focusedAutomationId)
                && IndexOf(visibleIds, focusedAutomationId) >= 0;
            bool selectedIsVisible = !string.IsNullOrWhiteSpace(selectedAutomationId)
                && IndexOf(visibleIds, selectedAutomationId) >= 0;

            if (focusedIsVisible && keyboardFocusIsCurrent)
            {
                return focusedAutomationId;
            }

            if (selectedIsVisible)
            {
                return selectedAutomationId;
            }

            return focusedIsVisible ? focusedAutomationId : null;
        }

        /// <summary>
        /// Returns true if the automation ID belongs to a known workspace navigation item.
        /// </summary>
        public static bool IsWorkspaceAutomationId(string? automationId)
        {
            if (string.IsNullOrWhiteSpace(automationId)) return false;
            return AllEntries.Any(e => string.Equals(e.AutomationId, automationId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Resolves the next workspace AutomationId in visual order, with defined end (null at boundary).
        /// </summary>
        public static string? Next(string currentAutomationId, bool isAdvancedMode, bool isGamingMode = false) =>
            GetNext(currentAutomationId, isAdvancedMode, isGamingMode);

        /// <summary>
        /// Resolves the previous workspace AutomationId in visual order, with defined start (null at boundary).
        /// </summary>
        public static string? Previous(string currentAutomationId, bool isAdvancedMode, bool isGamingMode = false) =>
            GetPrevious(currentAutomationId, isAdvancedMode, isGamingMode);

        public static string? GetNext(string currentAutomationId, bool isAdvancedMode, bool isGamingMode = false)
        {
            if (string.IsNullOrWhiteSpace(currentAutomationId)) return null;
            var list = GetOrderedAutomationIds(isAdvancedMode, isGamingMode);
            int index = IndexOf(list, currentAutomationId);
            if (index < 0 || index >= list.Count - 1)
            {
                return null;
            }
            return list[index + 1];
        }

        public static string? GetPrevious(string currentAutomationId, bool isAdvancedMode, bool isGamingMode = false)
        {
            if (string.IsNullOrWhiteSpace(currentAutomationId)) return null;
            var list = GetOrderedAutomationIds(isAdvancedMode, isGamingMode);
            int index = IndexOf(list, currentAutomationId);
            if (index <= 0)
            {
                return null;
            }
            return list[index - 1];
        }

        public static WorkspaceNavigationEntry? FindByAutomationId(string? automationId)
        {
            if (string.IsNullOrWhiteSpace(automationId)) return null;
            return AllEntries.FirstOrDefault(e => string.Equals(e.AutomationId, automationId, StringComparison.Ordinal));
        }

        public static WorkspaceNavigationEntry? FindByTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            return AllEntries.FirstOrDefault(e => string.Equals(e.Tag, tag, StringComparison.Ordinal));
        }

        private static int IndexOf(IReadOnlyList<string> list, string automationId)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], automationId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
