using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class SettingsOwnerRecordsUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    /// <summary>The new sections sit at the end of long pages, so scroll until the named element exists.</summary>
    private static AutomationElement Reveal(AutomationElement e)
    {
        try { e.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
        Thread.Sleep(250);
        return e;
    }

    private static void ScrollTo(UiTestApplication app, string automationId, string what)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < until)
        {
            if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) != null) return;
            // The Settings page is the largest vertically scrollable area in the window.
            var scroller = app.MainWindow.FindAllDescendants()
                .Where(e => { try { return e.Patterns.Scroll.IsSupported && e.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault; } catch { return false; } })
                .OrderByDescending(e => { try { return e.BoundingRectangle.Height; } catch { return 0; } })
                .FirstOrDefault();
            try { scroller?.Patterns.Scroll.Pattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, FlaUI.Core.Definitions.ScrollAmount.LargeIncrement); }
            catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("Timed out scrolling to " + what + ".");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Privacy_and_network_shows_the_activity_log_and_adds_and_removes_a_local_webhook()
    {
        const string name = nameof(Privacy_and_network_shows_the_activity_log_and_adds_and_removes_a_local_webhook);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_PrivacyNetwork_Button");

            // Activity log: verify answers in plain words.
            ScrollTo(app, "Settings_AuditLog_Status_Text", "the activity log section");
            var verify = app.MainWindow.FindAllDescendants().First(e => Text(e) == "Check the log is untouched");
            Reveal(verify).Click();
            var auditStatus = UiWait.ById(app.MainWindow, "Settings_AuditLog_Status_Text", ControlType.Text);
            UiWait.For(() => Text(auditStatus).Contains("intact", StringComparison.OrdinalIgnoreCase)
                || Text(auditStatus).Contains("empty", StringComparison.OrdinalIgnoreCase), "the log check answers", TimeSpan.FromSeconds(60));

            ScrollTo(app, "Settings_Webhooks_Add_Button", "the webhooks section");
            // Webhooks: an address outside the network without the Internet switch is refused with a reason.
            var url = UiWait.ById(app.MainWindow, "Settings_Webhooks_Url_TextBox", ControlType.Edit);
            var events = UiWait.ById(app.MainWindow, "Settings_Webhooks_Events_TextBox", ControlType.Edit);
            url.AsTextBox().Text = "https://hooks.example.com/in";
            events.AsTextBox().Text = "plugin.";
            Reveal(UiWait.ById(app.MainWindow, "Settings_Webhooks_Add_Button", ControlType.Button)).AsButton().Invoke();
            var status = UiWait.ById(app.MainWindow, "Settings_Webhooks_Status_Text", ControlType.Text);
            UiWait.For(() => Text(status).Contains("Internet", StringComparison.OrdinalIgnoreCase), "a clear refusal for an outside address", TimeSpan.FromSeconds(60));

            // A local address is accepted; the signing secret is shown once.
            url.AsTextBox().Text = "http://127.0.0.1:9/parux";
            Reveal(UiWait.ById(app.MainWindow, "Settings_Webhooks_Add_Button", ControlType.Button)).AsButton().Invoke();
            var done = app.MainWindow.FindAllDescendants().FirstOrDefault(e => Text(e) == "I copied it");
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => Text(e) == "I copied it"), "the secret dialog appears", TimeSpan.FromSeconds(60));
            app.MainWindow.FindAllDescendants().First(e => Text(e) == "I copied it").Click();

            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("127.0.0.1:9/parux", StringComparison.Ordinal)), "the new webhook is listed", TimeSpan.FromSeconds(60));

            var delete = app.MainWindow.FindAllDescendants().First(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Settings_Webhooks_Delete_", StringComparison.Ordinal));
            delete.AsButton().Invoke();
            UiWait.For(() => !app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("127.0.0.1:9/parux", StringComparison.Ordinal)), "the webhook is removed", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Platform_lists_installed_plugins_and_chat_generation_has_a_preset_picker()
    {
        const string name = nameof(Platform_lists_installed_plugins_and_chat_generation_has_a_preset_picker);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_PaRuXPlatform_Button");
            ScrollTo(app, "Settings_InstalledPlugins_Status_Text", "the installed plugins section");
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("No plugins installed", StringComparison.Ordinal)), "an honest empty list", TimeSpan.FromSeconds(90));

            var chatItem = UiWait.ById(app.MainWindow, "Navigation_Chat_Item");
            chatItem.Focus();
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "Chat_Generation_Button"));
            UiWait.ById(app.MainWindow, "Chat_Generation_Preset_ComboBox");
            UiWait.ById(app.MainWindow, "Chat_Generation_SavePreset_Button");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
