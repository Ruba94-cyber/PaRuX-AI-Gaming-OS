using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatPromptLibraryUiTests
{
    private static AutomationElement? Find(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_starter_prompt_asks_for_its_fields_and_only_fills_the_composer()
    {
        const string name = nameof(A_starter_prompt_asks_for_its_fields_and_only_fills_the_composer);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            Press(app, "Chat_Prompts_Button");
            Press(app, "Chat_Prompt_starter-explain");
            AutomationElement field = UiWait.ById(app.MainWindow, "Chat_PromptField_topic");
            field.AsTextBox().Text = "quantization";
            var insert = app.MainWindow.FindAllDescendants().First(e =>
            {
                try { return e.Name == "Put in the box"; } catch { return false; }
            });
            insert.Click();
            UiWait.For(() =>
            {
                string text = Find(app, "Chat_Prompt_TextBox")?.AsTextBox().Text ?? "";
                return text.Contains("Explain quantization simply", StringComparison.Ordinal) && !text.Contains("{{", StringComparison.Ordinal);
            }, "the filled prompt is in the composer");
            // Nothing was sent: the empty-chat starter list is still the page content.
            Assert.NotNull(Find(app, "Chat_Home_Code_Button"));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_prompt_written_in_the_box_can_be_saved_listed_and_deleted()
    {
        const string name = nameof(A_prompt_written_in_the_box_can_be_saved_listed_and_deleted);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox").AsTextBox().Text = "Plan my week around {{goal}}";
            Press(app, "Chat_Prompts_Button");
            Press(app, "Chat_Prompts_SaveCurrent_Button");
            UiWait.ById(app.MainWindow, "Chat_PromptSave_Name").AsTextBox().Text = "Weekly plan";
            var save = app.MainWindow.FindAllDescendants().First(e => { try { return e.Name == "Save"; } catch { return false; } });
            save.Click();
            string file = Path.Combine(app.DataRoot, "prompt-library.json");
            UiWait.For(() => File.Exists(file) && SharedFile.ReadAllText(file).Contains("Weekly plan", StringComparison.Ordinal), "prompt saved to disk");

            Press(app, "Chat_Prompts_Button");
            var delete = app.MainWindow.FindAllDescendants().First(e =>
            {
                try { return e.AutomationId.StartsWith("Chat_PromptDelete_", StringComparison.Ordinal); } catch { return false; }
            });
            delete.Click();
            UiWait.For(() => !SharedFile.ReadAllText(file).Contains("Weekly plan", StringComparison.Ordinal), "prompt removed from disk");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
