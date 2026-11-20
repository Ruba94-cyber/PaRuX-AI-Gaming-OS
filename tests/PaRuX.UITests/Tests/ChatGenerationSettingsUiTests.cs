using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatGenerationSettingsUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Generation_settings_are_off_by_default_and_saved_once_chosen()
    {
        const string name = nameof(Generation_settings_are_off_by_default_and_saved_once_chosen);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            string file = Path.Combine(app.DataRoot, "chat-sampling.json");
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "Chat_Generation_Button"));
            AutomationElement toggle = UiWait.ById(app.MainWindow, "Chat_Generation_Custom_Toggle");
            Assert.False(File.Exists(file));
            Assert.False(UiWait.ById(app.MainWindow, "Chat_Generation_MaxTokens_Box").IsEnabled);

            toggle.Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => UiWait.ById(app.MainWindow, "Chat_Generation_MaxTokens_Box").IsEnabled, "controls enabled once the owner opts in");
            UiWait.For(() => File.Exists(file) && SharedFile.ReadAllText(file).Contains("Temperature", StringComparison.Ordinal), "settings saved to disk");

            toggle.Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => !File.Exists(file), "turning it off returns to PaRuX defaults and removes the file");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
