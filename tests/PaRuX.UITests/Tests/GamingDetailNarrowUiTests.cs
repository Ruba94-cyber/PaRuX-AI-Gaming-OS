using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class GamingDetailNarrowUiTests
{
    [Theory, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    [InlineData(1100)]
    [InlineData(900)]
    [InlineData(800)]
    [InlineData(700)]
    [InlineData(560)]
    public void Game_detail_survives_the_window_being_narrowed_and_widened(int narrowWidth)
    {
        const string name = nameof(Game_detail_survives_the_window_being_narrowed_and_widened);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            app.ResizeMainWindow(1280, 800);
                        DateTime lastClick = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null) return true;
                if (DateTime.UtcNow - lastClick > TimeSpan.FromSeconds(2))
                {
                    lastClick = DateTime.UtcNow;
                    try { page.Invoke(UiWait.ById(app.MainWindow, "GameHome_Hero_Details")); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException) { }
                }
                return false;
            }, "detail panel opens");
            foreach (int width in new[] { narrowWidth, 1280 })
            {
                app.ResizeMainWindow(width, 800);
                UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null, "detail still shown at width " + width);
            }
            string log = File.Exists(app.LogPath) ? SharedFile.ReadAllText(app.LogPath) : "";
            Assert.DoesNotContain("UNHANDLED", log, StringComparison.Ordinal);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
