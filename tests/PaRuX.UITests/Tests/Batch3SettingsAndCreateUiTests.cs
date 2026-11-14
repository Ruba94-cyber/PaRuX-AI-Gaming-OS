using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Visible behaviour added in batch 3: the Diffusers engine card in Settings, waiting Autopilot runs loading when Settings > Agents
/// opens, the Create setup line being limited to the Image tab, and framework strings (the ComboBox light-dismiss "Close")
/// following the app language. All of it runs against the real Core; no model, GPU or network is needed.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class Batch3SettingsAndCreateUiTests
{
    private static readonly string[] DiffusersStatusTexts =
    {
        "Diffusers engine ready (offline, local only).",
        "The Diffusers engine is not installed yet. Press Install to add the PaRuX-managed runtime and the pinned model.",
        "The Diffusers runtime is installed, but the pinned model snapshot has not been downloaded yet. Press Install again to finish it.",
        "The Diffusers engine cannot run on this PC: PaRuX-managed PyTorch cannot use a CUDA GPU here. It needs a supported NVIDIA GPU; nothing was generated.",
        "The Diffusers engine install is incomplete or failed. Press Install to run it again.",
        "The Diffusers engine is not ready."
    };

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "FlaUI")]
    public void Settings_media_generation_shows_the_diffusers_engine_status_and_an_install_action_when_one_applies()
    {
        const string testName = nameof(Settings_media_generation_shows_the_diffusers_engine_status_and_an_install_action_when_one_applies);
        using var app = new UiTestApplication(testName);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_MediaGeneration_Button");

            AutomationElement status = UiWait.ById(app.MainWindow, "Settings_Diffusers_Status", ControlType.Text);
            string text = string.Empty;
            UiWait.For(() =>
            {
                text = status.Name;
                return text.Length > 0 && !text.StartsWith("Checking", StringComparison.Ordinal);
            }, "the Diffusers card to report the engine state from the Core", TimeSpan.FromSeconds(60));

            Assert.Contains(text, DiffusersStatusTexts);
            // Install is offered for every state a reinstall can change: not when ready and not for the CUDA hardware refusal.
            bool installApplies = !text.StartsWith("Diffusers engine ready", StringComparison.Ordinal)
                && !text.Contains("cannot run on this PC", StringComparison.Ordinal);
            if (installApplies)
            {
                AutomationElement install = UiWait.ById(app.MainWindow, "Settings_Diffusers_Install", ControlType.Button);
                Assert.Equal("Install engine", install.Name);
            }
            else
            {
                Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Diffusers_Install")));
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "FlaUI")]
    public void Settings_agents_lists_waiting_autopilot_runs_on_open_without_pressing_refresh()
    {
        const string testName = nameof(Settings_agents_lists_waiting_autopilot_runs_on_open_without_pressing_refresh);
        const string goal = "Synthetic batch 3 waiting Autopilot goal";
        using var app = new UiTestApplication(testName);
        try
        {
            using var core = new DocumentSearchUiTests.Core(app);
            core.StartSupervisedAutopilotRun(goal);

            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_Agents_Button");

            // Nothing presses "Refresh waiting runs": the category loads the list itself.
            AutomationElement? card = null;
            UiWait.For(() => (card = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && string.Equals(el.Properties.Name.ValueOrDefault, goal, StringComparison.Ordinal))) != null,
                "the waiting Autopilot run to be listed when Settings > Agents opens", TimeSpan.FromSeconds(30));
            Assert.NotNull(card);
            Assert.NotNull(app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Button
                && string.Equals(el.Properties.Name.ValueOrDefault, "Approve", StringComparison.Ordinal)));
            Assert.NotNull(app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Button
                && string.Equals(el.Properties.Name.ValueOrDefault, "Cancel", StringComparison.Ordinal)));
            // The "nothing waiting" line must not contradict the list.
            Assert.Null(app.FindRawDescendant(app.MainWindow, el =>
                string.Equals(el.Properties.Name.ValueOrDefault, "No Autopilot run is waiting for approval.", StringComparison.Ordinal)
                && !el.Properties.IsOffscreen.ValueOrDefault));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_setup_line_belongs_to_the_image_tab_and_is_absent_on_gallery_and_speech()
    {
        const string testName = nameof(Create_setup_line_belongs_to_the_image_tab_and_is_absent_on_gallery_and_speech);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new CreateWorkspaceModesPage(app.MainWindow, app);
            page.OpenCreateWorkspace();
            page.SelectMode("Image");
            string imageLine = page.WaitForSettledSetupStatus();
            Assert.False(string.IsNullOrWhiteSpace(imageLine));

            foreach (string mode in new[] { "Gallery", "Speech" })
            {
                page.SelectMode(mode);
                UiWait.For(() => !SetupLineVisible(app), "the image setup line to leave the " + mode + " tab");
                // Not merely hidden once: it stays absent after the layout has settled.
                Thread.Sleep(500);
                Assert.False(SetupLineVisible(app), "the image setup line reappeared on the " + mode + " tab");
            }

            page.SelectMode("Image");
            UiWait.For(() => SetupLineVisible(app), "the image setup line to return on the Image tab");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    private static bool SetupLineVisible(UiTestApplication app)
    {
        string[] lines = { CreateWorkspaceModesPage.CheckingStatus, CreateWorkspaceModesPage.ReadyStatus, CreateWorkspaceModesPage.NeedsSetupStatus };
        return app.FindRawDescendant(app.MainWindow, el =>
            lines.Contains(el.Properties.Name.ValueOrDefault) && !el.Properties.IsOffscreen.ValueOrDefault) != null;
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "FlaUI")]
    [InlineData("en", "Close", "Chiudi")]
    [InlineData("it", "Chiudi", "Close")]
    public void Combo_box_light_dismiss_name_follows_the_app_language(string language, string expected, string other)
    {
        string testName = nameof(Combo_box_light_dismiss_name_follows_the_app_language) + "_" + language;
        using var app = new UiTestApplication(testName, uiLanguage: language);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_General_Button");
            AutomationElement combo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            new SettingsCategoriesPage(app.MainWindow).ScrollSettingsIntoView(combo);
            combo.Patterns.ExpandCollapse.Pattern.Expand();

            UiWait.For(() => HasNamed(app, expected), "the open ComboBox to expose a '" + expected + "' light-dismiss element");
            Assert.False(HasNamed(app, other), "the ComboBox popup exposes '" + other + "' in the '" + language + "' language");
            string? screenshot = app.CaptureSnapshot("combo-dismiss-" + language);
            Assert.NotNull(screenshot);
            combo.Patterns.ExpandCollapse.Pattern.Collapse();
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    private static bool HasNamed(UiTestApplication app, string name)
    {
        // Only the framework's light-dismiss layer counts: the window caption and system-menu buttons are owned by Windows
        // and follow the OS display language, which the app cannot and must not override.
        bool Match(AutomationElement el) => string.Equals(el.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal)
            && string.Equals(el.Properties.ClassName.ValueOrDefault, "ComboBoxLightDismiss", StringComparison.Ordinal);
        if (app.FindRawDescendant(app.MainWindow, Match) != null) return true;
        // The popup can be hosted in its own top-level window of the same process.
        int pid = app.MainWindow.Properties.ProcessId.Value;
        foreach (AutomationElement top in app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(pid)))
            if (app.FindRawDescendant(top, Match) != null) return true;
        return false;
    }
}
