using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

/// <summary>Settings &gt; General "What PaRuX is for" row (AI only / Gaming only / Both) and the shell it drives
/// (mode tabs, sidebar workspaces). Lookup is by AutomationId only; nothing here depends on the UI language.</summary>
internal sealed class SetupProfilePage
{
    internal const string ComboId = "Settings_SetupProfile_ComboBox";
    internal const string BothItem = "Settings_SetupProfile_Both_Item";
    internal const string AiItem = "Settings_SetupProfile_Ai_Item";
    internal const string GamingItem = "Settings_SetupProfile_Gaming_Item";
    internal const string HintId = "Settings_SetupProfile_Hint";
    internal const string AiTab = "MainWindow_ModeTab_AI";
    internal const string GamingTab = "MainWindow_ModeTab_Gaming";
    internal const string ChatNav = "Navigation_Chat_Item";
    internal const string GamesNav = "Navigation_GameLibrary_Item";

    private readonly UiTestApplication _app;
    internal SetupProfilePage(UiTestApplication app) => _app = app;

    /// <summary>True when the element exists, is on screen and has a real size. A collapsed WinUI element can linger in the UIA
    /// tree with an empty rectangle, so size counts as well as the off-screen flag.</summary>
    internal bool IsShown(string id)
    {
        AutomationElement? element = _app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
        return element != null && !element.Properties.IsOffscreen.ValueOrDefault
            && element.BoundingRectangle.Width > 1 && element.BoundingRectangle.Height > 1;
    }

    /// <summary>True when the element is in the UIA tree at all (a page marker that may sit below the fold of a long page).</summary>
    internal bool Exists(string id) => _app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)) != null;

    internal bool ModeTabsShown => IsShown(AiTab) && IsShown(GamingTab);
    internal bool ModeTabsHidden => !IsShown(AiTab) && !IsShown(GamingTab);

    /// <summary>Opens Settings on General, where the row lives, and waits for the row's hint text. The combo box itself is not
    /// awaited: Corporate deliberately collapses it (the profile is fixed), so callers that need it wait for it explicitly.</summary>
    internal AutomationElement OpenSettingsGeneral()
    {
        var main = new MainWindowPage(_app.MainWindow);
        main.OpenSettings();
        new SettingsCategoriesPage(_app.MainWindow).SelectCategory("General");
        return UiWait.ById(_app.MainWindow, HintId, ControlType.Text);
    }

    internal string? SelectedItemId => UiWait.SelectedItemId(
        UiWait.ById(_app.MainWindow, ComboId, ControlType.ComboBox));

    internal string HintText => UiWait.ById(_app.MainWindow, HintId, ControlType.Text).Name ?? string.Empty;

    /// <summary>Picks a profile the way a person does: open the drop-down, choose the item.</summary>
    internal void Choose(string itemId)
    {
        _app.MainWindow.SetForeground();
        AutomationElement combo = UiWait.ById(_app.MainWindow, ComboId, ControlType.ComboBox);
        UiWait.For(() => combo.Properties.IsEnabled.ValueOrDefault, "profile combo enabled");
        UiWait.For(() =>
        {
            var expand = combo.Patterns.ExpandCollapse.Pattern;
            if (expand.ExpandCollapseState.Value != ExpandCollapseState.Expanded) expand.Expand();
            AutomationElement? item = _app.FindRawDescendant(_app.MainWindow,
                element => element.Properties.AutomationId.ValueOrDefault == itemId);
            if (item == null) return false;
            try { item.Patterns.SelectionItem.Pattern.Select(); }
            catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
            {
                // The selection is accepted and the row disables its own combo box while it applies the change; UI Automation then
                // reports the item as not enabled from inside the same call. The caller waits for the observable result.
            }
            return true;
        }, "profile item " + itemId);
        try
        {
            var expand = combo.Patterns.ExpandCollapse.Pattern;
            if (expand.ExpandCollapseState.Value == ExpandCollapseState.Expanded) expand.Collapse();
        }
        catch { /* selecting an item already closed the drop-down */ }
    }

    /// <summary>The persisted <c>SetupProfileId</c> ("both" / "ai" / "gaming"), or null while the setting was never written.</summary>
    internal string? SavedProfile()
    {
        if (!File.Exists(_app.SettingsPath)) return null;
        using JsonDocument doc = JsonDocument.Parse(SharedFile.ReadAllText(_app.SettingsPath));
        return doc.RootElement.TryGetProperty("SetupProfileId", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    internal bool LogContains(string text) => LogCount(text) > 0;

    /// <summary>How many log lines contain every fragment (each profile switch writes exactly one "Setup profile switch" line).</summary>
    internal int LogCount(params string[] fragments) => !File.Exists(_app.LogPath) ? 0
        : SharedFile.ReadAllText(_app.LogPath).Split('\n')
            .Count(line => fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));

    /// <summary>Selects one of the AI | Gaming tabs the way the existing mode-tab tests do (the control type is not pinned).</summary>
    internal void SelectTab(string tabId)
    {
        AutomationElement? tab = null;
        UiWait.For(() => (tab = _app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(tabId))) != null, "mode tab " + tabId);
        new InstallProgressPage(_app).Invoke(tab!);
    }
}
