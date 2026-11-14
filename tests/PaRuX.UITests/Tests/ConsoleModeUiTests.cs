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
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button));
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_ReturnToAi_Button", ControlType.Button));
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit));
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameTitles_Collection_Filter", ControlType.Edit));
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameTitles_Refresh_Button", ControlType.Button));
            // Controller A/Y opens the existing on-screen keyboard for these fields.
            // Physical gamepad event forwarding is verified separately on Windows.

            UiWait.For(() =>
            {
                var editor = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("GameLibrary_Name_TextBox"));
                var inspector = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("GameLibrary_Installer_Inspect_Button"));
                return (editor == null || editor.Properties.IsOffscreen.ValueOrDefault)
                    && (inspector == null || inspector.Properties.IsOffscreen.ValueOrDefault);
            }, "Console hides filesystem and installer editing actions");

            UiWait.ById(app.MainWindow, id, ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, id, ControlType.Button).Name == desktopName,
                "Console mode returned to normal desktop presenter");
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_Name_TextBox", ControlType.Edit));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Console_presenter_can_enter_and_return_to_existing_desktop_shell), ex);
            throw;
        }
    }

    /// <summary>EMULATED: keyboard Escape is the same ConsoleNavigationPolicy action as GamepadB. Physical B is BLOCKED-HW.</summary>
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Escape_key_exits_console_presenter_like_gamepad_B_emulated()
    {
        using var app = new UiTestApplication(nameof(Escape_key_exits_console_presenter_like_gamepad_B_emulated));
        try
        {
            string id = "MainWindow_ConsoleMode_Button";
            var button = UiWait.ById(app.MainWindow, id, ControlType.Button);
            string desktopName = button.Name;
            button.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, id, ControlType.Button).Name != desktopName, "console entered");
            // B/Escape is deliberately inert while a library command is running (ConsoleNavigationPolicy), so wait for idle first.
            var idleWatch = System.Diagnostics.Stopwatch.StartNew();
            UiWait.For(() => UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button).IsEnabled, "console idle", TimeSpan.FromSeconds(60));
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parux-console-idle-seconds.txt"), idleWatch.Elapsed.TotalSeconds.ToString("F1"));
            app.MainWindow.Focus();
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            UiWait.For(() => UiWait.ById(app.MainWindow, id, ControlType.Button).Name == desktopName,
                "Escape (emulated B) returned to desktop presenter");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Escape_key_exits_console_presenter_like_gamepad_B_emulated), ex);
            throw;
        }
    }
}
