using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatEmptyStateUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void An_empty_chat_shows_no_version_navigator()
    {
        const string name = nameof(An_empty_chat_shows_no_version_navigator);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox");
            // Nothing to navigate in a new chat: no "1/1" counter and no Versions button.
            Assert.DoesNotContain(app.MainWindow.FindAllDescendants(), e =>
            {
                try { return !e.IsOffscreen && (e.Name == "Versions" || e.Name == "1/1"); } catch { return false; }
            });
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
