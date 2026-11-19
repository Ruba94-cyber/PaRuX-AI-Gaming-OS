using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class GamingLibraryToolsUiTests
{
    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static string StatusText(UiTestApplication app) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameTitles_Status_Text"))?.Name ?? "";

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Pick_for_me_opens_the_card_of_a_game_that_can_start_without_launching_it()
    {
        const string name = nameof(Pick_for_me_opens_the_card_of_a_game_that_can_start_without_launching_it);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            app.ResizeMainWindow(1280, 800);
            DateTime last = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null) return true;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(2))
                {
                    last = DateTime.UtcNow;
                    try { Press(app, "GameHome_PickRandom_Button"); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException) { }
                }
                return false;
            }, "the picked game's detail card opens");
            Assert.Contains("How about", StatusText(app), StringComparison.Ordinal);
            // Picking only opens the card: the Gaming-active strip (shown once a game starts) stays off.
            AutomationElement? strip = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Strip_Status_Text"));
            Assert.True(strip is null || strip.Properties.IsOffscreen.ValueOrDefault, "nothing was launched");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Library_insights_dialog_summarizes_the_library()
    {
        const string name = nameof(Library_insights_dialog_summarizes_the_library);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            Press(app, "GameHome_Insights_Button");
            AutomationElement dialog = UiWait.ById(app.MainWindow, "GameHome_Insights_Dialog");
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e =>
            {
                try { return e.Name.Contains("24 games", StringComparison.Ordinal); } catch { return false; }
            }), "the dialog states the number of games");
            Assert.NotNull(dialog);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Game_notes_are_saved_when_the_box_loses_focus_and_survive_in_the_title_file()
    {
        const string name = nameof(Game_notes_are_saved_when_the_box_loses_focus_and_survive_in_the_title_file);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            app.ResizeMainWindow(1280, 800);
            DateTime last = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null) return true;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(2))
                {
                    last = DateTime.UtcNow;
                    try { Press(app, "GameHome_Hero_Details"); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException) { }
                }
                return false;
            }, "detail card opens");
            AutomationElement notes = null!;
            UiWait.For(() => (notes = app.MainWindow.FindAllDescendants()
                .FirstOrDefault(e => { try { return e.AutomationId.StartsWith("GameDetail_Notes_", StringComparison.Ordinal); } catch { return false; } })!) != null,
                "notes box present");
            notes.AsTextBox().Text = "second boss needs fire damage";
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            string titles = Path.Combine(root, "server", "game-titles.json");
            UiWait.For(() => File.Exists(titles) && SharedFile.ReadAllText(titles).Contains("second boss needs fire damage", StringComparison.Ordinal),
                "note written to the title file");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
