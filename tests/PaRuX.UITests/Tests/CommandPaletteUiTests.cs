using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CommandPaletteUiTests
{
    private static void OpenPalette(UiTestApplication app)
    {
        app.MainWindow.SetForeground();
        app.MainWindow.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Ctrl_K_opens_the_palette_and_Enter_runs_the_first_match()
    {
        const string name = nameof(Ctrl_K_opens_the_palette_and_Enter_runs_the_first_match);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            DateTime last = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Search")) != null) return true;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(3)) { last = DateTime.UtcNow; OpenPalette(app); }
                return false;
            }, "the palette opens with Ctrl+K");
            UiWait.ById(app.MainWindow, "CommandPalette_Search").AsTextBox().Text = "model hub";
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Item_go-modelhub")) != null, "Model Hub is offered");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Item_go-chat")));
            Keyboard.Type(VirtualKeyShort.ENTER);
            UiWait.For(() => UiWait.ById(app.MainWindow, "Navigation_ModelHub_Item").Patterns.SelectionItem.PatternOrDefault
                ?.IsSelected.ValueOrDefault == true, "Model Hub is selected after Enter");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Search")));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Typing_something_unknown_shows_no_commands_and_Escape_closes_it()
    {
        const string name = nameof(Typing_something_unknown_shows_no_commands_and_Escape_closes_it);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            DateTime last = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Search")) != null) return true;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(3)) { last = DateTime.UtcNow; OpenPalette(app); }
                return false;
            }, "the palette opens");
            UiWait.ById(app.MainWindow, "CommandPalette_Search").AsTextBox().Text = "zzzzqq";
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => { try { return e.Name == "No matching command"; } catch { return false; } }), "empty state is shown");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPalette_Search")) == null, "the palette closes");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
