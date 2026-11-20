using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class GamingCompactTopUiTests
{
    private static void Press(UiTestApplication app, string id) => new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static bool Shown(UiTestApplication app, string id)
    {
        var e = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
        return e != null && !e.Properties.IsOffscreen.ValueOrDefault && e.BoundingRectangle.Width > 1 && e.BoundingRectangle.Height > 1;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Compact_top_hides_the_last_played_card_and_shelf_remembers_the_choice_and_can_be_undone()
    {
        const string name = nameof(Compact_top_hides_the_last_played_card_and_shelf_remembers_the_choice_and_can_be_undone);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(40);
        UiTestApplication app = new(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            app.ResizeMainWindow(1280, 800);
            // By default the last-played card and the Continue shelf are there (the library has played games).
            UiWait.For(() => Shown(app, "GameHome_Continue_Heading"), "the Continue shelf is shown by default", TimeSpan.FromSeconds(60));

            Press(app, "GameHome_View_Button");
            Press(app, "GameHome_CompactTop_CheckBox");
            UiWait.For(() => !Shown(app, "GameHome_Continue_Heading") && !Shown(app, "GameHome_Continue_Shelf"), "the shelf is hidden", TimeSpan.FromSeconds(30));
            UiWait.For(() => File.ReadAllText(app.SettingsPath).Contains("\"GameLibraryCompactTop\": true", StringComparison.Ordinal)
                || File.ReadAllText(app.SettingsPath).Contains("\"GameLibraryCompactTop\":true", StringComparison.Ordinal), "the choice is saved");

            // It survives a restart.
            app = app.Restart(name + "-restart");
            Press(app, "MainWindow_ModeTab_Gaming");
            app.ResizeMainWindow(1280, 800);
            UiWait.ById(app.MainWindow, "GameHome_View_Button");
            Thread.Sleep(3000);
            Assert.False(Shown(app, "GameHome_Continue_Heading"));

            // And one click brings them back.
            Press(app, "GameHome_View_Button");
            Press(app, "GameHome_CompactTop_CheckBox");
            UiWait.For(() => Shown(app, "GameHome_Continue_Heading"), "the shelf is back", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        finally { app.Dispose(); }
    }
}
