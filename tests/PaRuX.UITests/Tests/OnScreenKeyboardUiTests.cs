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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>This test process is DPI-unaware, so FlaUI's Capture() grabs only the top-left part of a scaled window.
    /// A short DPI-aware PowerShell child takes the real physical-pixel picture of the window.</summary>
    private static void CaptureDpiAware(IntPtr hwnd, string output)
    {
        string script = Path.Combine(Repository.Root, "tools", "capture-window.ps1");
        if (!File.Exists(script)) return; // picture is evidence only; the bounds assertions still run
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Hwnd {hwnd.ToInt64()} -Out \"{output}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(20_000);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Cancel_restores_the_field_and_the_keyboard_fits_the_window_at_several_sizes_emulated()
    {
        const string name = nameof(Cancel_restores_the_field_and_the_keyboard_fits_the_window_at_several_sizes_emulated);
        using var app = new UiTestApplication(name);
        try
        {
            string modeId = "MainWindow_ConsoleMode_Button";
            var button = UiWait.ById(app.MainWindow, modeId, ControlType.Button);
            string desktopName = button.Name;
            button.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, modeId, ControlType.Button).Name != desktopName, "console entered");
            UiWait.For(() => UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button).IsEnabled,
                "console idle", TimeSpan.FromSeconds(60));

            string shots = Path.Combine(Repository.Root, ".ai", "artifacts", "kbd-layout");
            Directory.CreateDirectory(shots);
            var window = app.MainWindow;

            foreach (var (w, h) in new[] { (0, 0), (1280, 720), (3840, 2160) })
            {
                if (w > 0)
                {
                    var hwnd = new IntPtr(window.Properties.NativeWindowHandle.Value);
                    ShowWindow(hwnd, 1); // restore: a maximized window ignores SetWindowPos sizes
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x0004 | 0x0010);
                    Thread.Sleep(1500);
                }
                var rect = window.BoundingRectangle;
                string tag = $"{(int)rect.Width}x{(int)rect.Height}";

                var search = UiWait.ById(window, "GameTitles_Search_TextBox", ControlType.Edit);
                window.Focus();
                search.Focus();
                search.Patterns.Value.Pattern.SetValue("orig");
                UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.F4),
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is { } r
                          && !r.Properties.IsOffscreen.ValueOrDefault, "keyboard open at " + tag);
                UiWait.ById(window, "OnScreenKeyboard_Key_k_q", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
                UiWait.ById(window, "OnScreenKeyboard_Space", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(400);
                CaptureDpiAware(new IntPtr(window.Properties.NativeWindowHandle.Value), Path.Combine(shots, $"open-{tag}.png"));

                // Every action key must lie fully inside the window: no clipping at this size.
                foreach (string id in new[] { "OnScreenKeyboard_Preview", "OnScreenKeyboard_Key_d1", "OnScreenKeyboard_Key_k_q",
                    "OnScreenKeyboard_Shift", "OnScreenKeyboard_Cancel", "OnScreenKeyboard_Space", "OnScreenKeyboard_Done" })
                {
                    var r = UiWait.ById(window, id).BoundingRectangle;
                    Assert.True(r.Left >= rect.Left - 1 && r.Top >= rect.Top - 1 && r.Right <= rect.Right + 1 && r.Bottom <= rect.Bottom + 1,
                        $"{id} {r} clipped by window {rect} at {tag}");
                }

                UiWait.ById(window, "OnScreenKeyboard_Cancel", ControlType.Button).Patterns.Invoke.Pattern.Invoke();
                UiWait.For(() => window.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is not { } r
                                 || r.Properties.IsOffscreen.ValueOrDefault, "keyboard closed");
                Assert.Equal("orig", UiWait.ById(window, "GameTitles_Search_TextBox", ControlType.Edit)
                    .Patterns.Value.Pattern.Value.Value);
            }

            UiWait.ById(app.MainWindow, modeId, ControlType.Button).Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex)
        {
            app.CaptureFailure(name, ex);
            throw;
        }
    }

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
