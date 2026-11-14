using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// KEYBOARD-EMULATED, NOT a physical pad. A SendInput gamepad virtual key (0xC3) is NOT delivered to XAML, so F4 (the
/// physical-keyboard equivalent of pad A/Y on a focused field in Console mode) opens the keyboard and the on-screen keys
/// are driven through UIA invoke. Physical controller presses and gamepad-navigation auto-open are NOT verified here.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class OnScreenKeyboardUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Console_search_field_types_through_on_screen_keys_emulated()
    {
        using var app = new UiTestApplication(nameof(Console_search_field_types_through_on_screen_keys_emulated));
        try
        {
            string modeId = "MainWindow_ConsoleMode_Button";
            var button = UiWait.ById(app.MainWindow, modeId, ControlType.Button);
            string desktopName = button.Name;
            button.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, modeId, ControlType.Button).Name != desktopName, "console entered");
            UiWait.For(() => UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button).IsEnabled,
                "console idle", TimeSpan.FromSeconds(60));

            var search = UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit);
            app.MainWindow.Focus();
            search.Focus();
            // Emulated: F4 on the focused field in Console mode opens the on-screen keyboard.
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.F4),
                () => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is { } r
                      && !r.Properties.IsOffscreen.ValueOrDefault,
                "on-screen keyboard open");

            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Key_k_h", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Key_k_i", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Space", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Key_k_x", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Backspace", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit)
                .Patterns.Value.Pattern.Value.Value == "hi ", "typed text reached the field");

            UiWait.ById(app.MainWindow, "OnScreenKeyboard_Done", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is not { } r
                             || r.Properties.IsOffscreen.ValueOrDefault, "keyboard closed");
            Assert.Equal("hi ", UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit)
                .Patterns.Value.Pattern.Value.Value);
            Assert.True(UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit).Properties.HasKeyboardFocus.Value,
                "focus returned to the same field");

            UiWait.ById(app.MainWindow, modeId, ControlType.Button).Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Console_search_field_types_through_on_screen_keys_emulated), ex);
            throw;
        }
    }
}
