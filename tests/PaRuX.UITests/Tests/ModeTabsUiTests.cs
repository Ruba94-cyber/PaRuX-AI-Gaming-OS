using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Top-level AI / Gaming mode switch. The tabs are SelectorBarItems; their UIA control type is not
/// pinned here (it is matched by the stable AutomationId only), while the sidebar items keep the
/// ListItem type used by every other UI test.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ModeTabsUiTests
{
    private const string AiTab = "MainWindow_ModeTab_AI";
    private const string GamingTab = "MainWindow_ModeTab_Gaming";
    private const string ChatItem = "Navigation_Chat_Item";
    private const string CreateItem = "Navigation_Create_Item";
    private const string GamesItem = "Navigation_GameLibrary_Item";
    private const string SettingsItem = "Navigation_Settings_Item";

    private static AutomationElement Tab(UiTestApplication app, string id)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element != null;
        }, "mode tab " + id);
        return element!;
    }

    private static bool IsShown(UiTestApplication app, string id)
    {
        var element = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
        return element != null && !element.Properties.IsOffscreen.ValueOrDefault;
    }

    private static bool IsSelected(AutomationElement tab) =>
        tab.Patterns.SelectionItem.IsSupported && tab.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Tabs_switch_sidebar_items_and_default_pages()
    {
        using var app = new UiTestApplication(nameof(Tabs_switch_sidebar_items_and_default_pages));
        try
        {
            var page = new InstallProgressPage(app);
            var ai = Tab(app, AiTab);
            var gaming = Tab(app, GamingTab);
            Assert.False(string.IsNullOrWhiteSpace(ai.Name));
            Assert.False(string.IsNullOrWhiteSpace(gaming.Name));
            Assert.True(ai.BoundingRectangle.Height >= 44, "tab touch target is at least 44px");

            // Default tab is AI: chat workspace items visible, Games hidden, Settings reachable.
            UiWait.For(() => IsShown(app, ChatItem) && IsShown(app, CreateItem), "AI items visible");
            Assert.False(IsShown(app, GamesItem));
            Assert.True(IsShown(app, SettingsItem));

            page.Invoke(gaming);
            UiWait.For(() => IsShown(app, GamesItem), "Games item visible in Gaming tab");
            UiWait.For(() => !IsShown(app, ChatItem) && !IsShown(app, CreateItem), "AI items hidden in Gaming tab");
            Assert.True(IsShown(app, SettingsItem));
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Sections")) != null,
                "Gaming opens on the Games library");

            page.Invoke(ai);
            UiWait.For(() => IsShown(app, ChatItem) && !IsShown(app, GamesItem), "AI items back, Games hidden");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Tabs_switch_sidebar_items_and_default_pages), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Keyboard_shortcuts_and_arrow_keys_switch_tabs()
    {
        using var app = new UiTestApplication(nameof(Keyboard_shortcuts_and_arrow_keys_switch_tabs));
        try
        {
            app.MainWindow.SetForeground();
            var ai = Tab(app, AiTab);
            var gaming = Tab(app, GamingTab);

            UiWait.InputUntil(() => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2),
                () => IsShown(app, GamesItem) && !IsShown(app, ChatItem), "Ctrl+2 opens the Gaming tab");
            UiWait.InputUntil(() => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1),
                () => IsShown(app, ChatItem) && !IsShown(app, GamesItem), "Ctrl+1 opens the AI tab");

            // Arrow keys inside the selector move between the two tabs.
            ai.Focus();
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.RIGHT),
                () => IsShown(app, GamesItem), "Right arrow selects Gaming");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.LEFT),
                () => IsShown(app, ChatItem) && !IsShown(app, GamesItem), "Left arrow selects AI");
            Assert.True(IsSelected(ai) || !IsSelected(gaming));
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Keyboard_shortcuts_and_arrow_keys_switch_tabs), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Last_tab_is_persisted_in_settings()
    {
        using var app = new UiTestApplication(nameof(Last_tab_is_persisted_in_settings));
        try
        {
            new InstallProgressPage(app).Invoke(Tab(app, GamingTab));
            UiWait.For(() =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(app.SettingsPath));
                return doc.RootElement.TryGetProperty("LastModeTab", out var v) && v.GetString() == "Gaming";
            }, "LastModeTab=Gaming persisted");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Last_tab_is_persisted_in_settings), ex); throw; }
    }
}
