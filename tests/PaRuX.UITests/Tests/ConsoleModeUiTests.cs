using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Opt-in real WinUI smoke: toggles the SAME application window presenter.
/// Does not start a game, change display settings, or inspect personal library data.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConsoleModeUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Console_presenter_can_enter_and_return_to_existing_desktop_shell()
    {
        using var app = new UiTestApplication(nameof(Console_presenter_can_enter_and_return_to_existing_desktop_shell));
        try
        {
            string id = "MainWindow_ConsoleMode_Button";
            var button = UiWait.ById(app.MainWindow, id, ControlType.Button);
            string desktopName = button.Name;
            Assert.False(string.IsNullOrWhiteSpace(desktopName));

            button.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, id, ControlType.Button).Name != desktopName,
                "Console mode button changed to Return to desktop");
            Assert.NotNull(UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem));

            UiWait.ById(app.MainWindow, id, ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, id, ControlType.Button).Name == desktopName,
                "Console mode returned to normal desktop presenter");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Console_presenter_can_enter_and_return_to_existing_desktop_shell), ex);
            throw;
        }
    }
}
