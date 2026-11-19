using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Resizing is a crash surface of its own (a layout that throws takes the whole layout pass down). Every AI workspace and every
/// Gaming section is shown while the window is narrowed to phone-like widths and widened again; the app log must never record an
/// UNHANDLED exception and the window must keep answering UI Automation.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ResizeStabilityUiTests
{
    private static readonly string[] AiWorkspaces =
    {
        "Navigation_Chat_Item", "Navigation_Create_Item", "Navigation_Projects_Item", "Navigation_ModelHub_Item", "Navigation_Settings_Item",
    };

    private static void Select(AutomationElementHolder app, string id)
    {
        var item = app.Window.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (item == null) return;
        var selection = item.Patterns.SelectionItem.PatternOrDefault;
        if (selection != null) selection.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke();
    }

    private sealed record AutomationElementHolder(FlaUI.Core.AutomationElements.Window Window);

    private static void AssertHealthy(UiTestApplication app, string where)
    {
        string log = File.Exists(app.LogPath) ? SharedFile.ReadAllText(app.LogPath) : "";
        Assert.False(log.Contains("UNHANDLED", StringComparison.Ordinal), "UNHANDLED exception recorded " + where + ":\n" + log);
        UiWait.For(() => !string.IsNullOrEmpty(app.MainWindow.Title), "window answers UIA " + where);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Every_ai_workspace_survives_narrowing_and_widening()
    {
        const string name = nameof(Every_ai_workspace_survives_narrowing_and_widening);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            app.ResizeMainWindow(1280, 800);
            foreach (string id in AiWorkspaces)
            {
                Select(new AutomationElementHolder(app.MainWindow), id);
                foreach (int width in new[] { 900, 700, 560, 420, 1280 })
                {
                    app.ResizeMainWindow(width, 800);
                    Thread.Sleep(350);
                    AssertHealthy(app, id + " at " + width);
                }
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Every_gaming_section_survives_narrowing_and_widening_with_a_populated_library()
    {
        const string name = nameof(Every_gaming_section_survives_narrowing_and_widening_with_a_populated_library);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(40, includeCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            app.ResizeMainWindow(1280, 800);
            foreach (string section in new[] { "Library", "Downloads", "Add", "Emulators", "Devices" })
            {
                GamingSectionsPage.SelectSection(app.MainWindow, section);
                foreach (int width in new[] { 900, 700, 560, 420, 1280 })
                {
                    app.ResizeMainWindow(width, 800);
                    Thread.Sleep(350);
                    AssertHealthy(app, section + " at " + width);
                }
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
