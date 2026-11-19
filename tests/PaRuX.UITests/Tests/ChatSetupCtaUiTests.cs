using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatSetupCtaUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Empty_chat_without_a_local_engine_offers_one_click_setup_in_Model_Hub()
    {
        const string name = nameof(Empty_chat_without_a_local_engine_offers_one_click_setup_in_Model_Hub);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            var button = UiWait.ById(app.MainWindow, "Chat_Home_Setup_Button");
            UiWait.For(() => !button.Properties.IsOffscreen.ValueOrDefault, "setup action visible on the empty chat");
            new InstallProgressPage(app).Invoke(button);
            UiWait.For(() => UiWait.ById(app.MainWindow, "Navigation_ModelHub_Item").Patterns.SelectionItem.PatternOrDefault
                ?.IsSelected.ValueOrDefault == true, "Model Hub selected after the setup action");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
