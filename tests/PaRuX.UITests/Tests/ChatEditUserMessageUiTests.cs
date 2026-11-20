using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatEditUserMessageUiTests
{
    private static bool HasText(UiTestApplication app, string text) =>
        app.MainWindow.FindAllDescendants().Any(e => { try { return e.Name == text; } catch { return false; } });

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void An_own_message_can_be_edited_and_resent_and_the_original_is_replaced()
    {
        const string name = nameof(An_own_message_can_be_edited_and_resent_and_the_original_is_replaced);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox").AsTextBox().Text = "first wording";
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "Chat_Send_Button"));
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_UserMessage_Edit_Button")) != null, "edit button under the sent message");
            // The turn must be over (the reply row appears) or the edit is refused with a status message instead of a dialog.
            UiWait.For(() => HasText(app, "Regenerate") || HasText(app, "Edit and continue"), "the first turn has finished");

            app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_UserMessage_Edit_Button"))!.AsButton().Invoke();
            var box = UiWait.ById(app.MainWindow, "Chat_UserMessage_Edit_TextBox");
            box.AsTextBox().Text = "second wording";
            var resend = app.MainWindow.FindAllDescendants().First(e => { try { return e.Name == "Resend"; } catch { return false; } });
            resend.Click();

            UiWait.For(() => HasText(app, "second wording"), "the edited text is now the user message");
            // The sidebar may still carry the old chat title, so count messages instead: the edited message replaced the original.
            Assert.Single(app.MainWindow.FindAllDescendants(cf => cf.ByAutomationId("Chat_UserMessage_Edit_Button")));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
