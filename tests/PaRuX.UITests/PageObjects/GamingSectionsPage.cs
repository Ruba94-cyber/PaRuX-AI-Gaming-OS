using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PaRuX.UITests.PageObjects;

/// <summary>
/// Page object for the Gaming tab's structure. The Gaming page is split into sections
/// (Library / Downloads / Add &amp; import / Devices &amp; session, selector <c>GameLibrary_Sections</c>) and the library toolbar keeps its
/// secondary controls in two menus (<c>Filters</c> and <c>More</c>). Controls in an unselected section or a closed menu are not
/// in the UIA tree, so a test that names such a control must first reveal it. <see cref="Reveal"/> is called by
/// <c>UiWait.ById</c> whenever a lookup misses, so tests keep asking for controls by AutomationId only.
/// </summary>
internal static class GamingSectionsPage
{
    internal const string SectionBar = "GameLibrary_Sections";
    internal const string FiltersMenuButton = "GameHome_MoreFilters_Button";
    internal const string MoreMenuButton = "GameHome_LibraryTools_Button";

    /// <summary>Controls hosted by the toolbar's Filters menu (see GamingHomeView.BuildToolbar).</summary>
    private static readonly HashSet<string> FiltersMenu = new(StringComparer.Ordinal)
    {
        "GameHome_PlatformFilter", "GameHome_InstalledFilter", "GameHome_TagFilter", "GameTitles_Collection_Filter",
        "GameTitles_Collection_Apply", "GameHome_ShowHidden", "GameHome_Presets", "GameHome_PresetSave", "GameHome_PresetDelete",
    };

    /// <summary>Controls hosted by the toolbar's More menu.</summary>
    private static readonly HashSet<string> MoreMenu = new(StringComparer.Ordinal)
    {
        "GameHome_Metadata_Toggle", "GameHome_SteamGridDb_Button", "GameHome_Igdb_Button", "GameHome_ExternalLibraryPreview_Button",
        "GameHome_ExternalLibrarySaved_Button", "GameHome_LocalGuide_Button", "GameHome_Wishlist_Button", "GameHome_SelectMode",
    };

    /// <summary>Home, tiles, shelf, detail panel, saves/hooks, advanced management and Quick Resume all live in the default Library section.</summary>
    private static readonly string[] LibraryPrefixes =
    {
        "GameHome_", "GameTitles_", "GameTitle_", "GameTile", "GameContinue", "GameDetail_", "GameSide_", "GameSaves_",
        "GameHooks_", "GameImport_", "QuickResume_", "AdvancedGameManagement_", "Emulators_",
    };

    /// <summary>The section that owns a Gaming control, or null when the id is not a section-hosted control (dialogs, shell, tiles).</summary>
    internal static string? SectionOf(string id)
    {
        if (id.StartsWith("GameDownloads_", StringComparison.Ordinal)) return "Downloads";
        if (id.StartsWith("GameDevices_", StringComparison.Ordinal)
            || id.StartsWith("GameLibrary_Gaming_", StringComparison.Ordinal)
            || id.StartsWith("GameLibrary_Displays_", StringComparison.Ordinal)
            || id.StartsWith("GameLibrary_Controllers_", StringComparison.Ordinal)
            || id == "GameLibrary_ReturnToAi_Button") return "Devices";
        // The Gaming-active strip sits above every section, so it needs no section selected.
        if (id == SectionBar || id.StartsWith("GameLibrary_Section_", StringComparison.Ordinal)
            || id.StartsWith("GameLibrary_Strip", StringComparison.Ordinal)) return null;
        // The first-visit "find my installed games" banner sits at the top of the Library section.
        if (id.StartsWith("GameLibrary_FirstRunScan_", StringComparison.Ordinal)) return "Library";
        if (id.StartsWith("GameLibrary_", StringComparison.Ordinal)) return "Add";
        foreach (string prefix in LibraryPrefixes)
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return "Library";
        return null;
    }

    /// <summary>Selects the section that owns <paramref name="id"/> and/or opens the toolbar menu that hosts it. Never throws:
    /// the caller keeps polling for the element, so a not-yet-reachable tab strip is simply retried.</summary>
    internal static void Reveal(AutomationElement root, string id)
    {
        try
        {
            string? section = SectionOf(id);
            if (section == null) return;
            SelectSection(root, section);
            string? menu = FiltersMenu.Contains(id) ? FiltersMenuButton : MoreMenu.Contains(id) ? MoreMenuButton : null;
            if (menu != null) OpenMenu(root, menu);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or FlaUI.Core.Exceptions.ElementNotAvailableException
                                       or FlaUI.Core.Exceptions.PatternNotSupportedException
                                       or InvalidOperationException) { }
    }

    /// <summary>Non-waiting lookup that still reveals the owning section/menu once; null when the control is genuinely absent.</summary>
    internal static AutomationElement? TryFind(AutomationElement root, string id)
    {
        AutomationElement? element = root.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (element != null) return element;
        Reveal(root, id);
        return root.FindFirstDescendant(cf => cf.ByAutomationId(id));
    }

    internal static void SelectSection(AutomationElement root, string section)
    {
        AutomationElement? item = root.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Section_" + section));
        if (item == null) return;
        var selection = item.Patterns.SelectionItem.PatternOrDefault;
        if (selection != null)
        {
            if (!selection.IsSelected.ValueOrDefault) selection.Select();
        }
        else item.Patterns.Invoke.PatternOrDefault?.Invoke();
    }

    private static DateTime _lastMenuInvoke = DateTime.MinValue;

    /// <summary>Opens a toolbar menu button once; never toggles an already-open menu closed.</summary>
    private static void OpenMenu(AutomationElement root, string buttonId)
    {
        AutomationElement? button = root.FindFirstDescendant(cf => cf.ByAutomationId(buttonId));
        if (button == null) return;
        var expand = button.Patterns.ExpandCollapse.PatternOrDefault;
        if (expand != null)
        {
            if (expand.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded
                && DateTime.UtcNow - _lastMenuInvoke > TimeSpan.FromSeconds(2))
            {
                _lastMenuInvoke = DateTime.UtcNow;
                expand.Expand();
            }
            return;
        }

        if (DateTime.UtcNow - _lastMenuInvoke > TimeSpan.FromSeconds(3))
        {
            _lastMenuInvoke = DateTime.UtcNow;
            button.Patterns.Invoke.PatternOrDefault?.Invoke();
        }
    }
}
