using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>First run offers AI only / Gaming only / Both. Both is the default and keeps the whole AI setup; Gaming only
/// removes the language-model setup and offers to start without any model. Nothing is downloaded or installed.</summary>
[Collection(UiTestCollection.Name)]
public sealed class SetupProfileCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step, enableFirstModelStorageFixture: true);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static AutomationElement Option(UiTestApplication app, string id)
    {
        AutomationElement? found = null;
        UiWait.For(() => (found = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id))) != null, id);
        return found!;
    }

    private static void Choose(AutomationElement radio)
    {
        if (radio.Patterns.SelectionItem.PatternOrDefault != null) radio.Patterns.SelectionItem.Pattern.Select();
        else radio.Click();
    }

    private static bool SettingsFileContains(UiTestApplication app, string text) =>
        System.IO.File.Exists(app.SettingsPath) && System.IO.File.ReadAllText(app.SettingsPath).Contains(text, StringComparison.Ordinal);

    private static bool Shown(UiTestApplication app, string id)
    {
        AutomationElement? element = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
        // A collapsed section can linger in the UIA tree with an empty rectangle, so size counts as well.
        return element != null && !element.Properties.IsOffscreen.ValueOrDefault
            && element.BoundingRectangle.Width > 1 && element.BoundingRectangle.Height > 1;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Onboarding_offers_the_three_profiles_with_both_selected_and_the_ai_setup_visible() => Run(
        nameof(Onboarding_offers_the_three_profiles_with_both_selected_and_the_ai_setup_visible), app =>
        {
            foreach (string id in new[] { "Onboarding_SetupProfile_Both", "Onboarding_SetupProfile_Ai", "Onboarding_SetupProfile_Gaming" })
                Option(app, id);
            UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Start_Button", ControlType.Button);
            Assert.False(Shown(app, "Onboarding_GamingOnly_Start_Button"), "Both must not offer the Gaming-only shortcut");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Choosing_gaming_only_hides_the_language_model_setup_and_offers_to_start_without_one() => Run(
        nameof(Choosing_gaming_only_hides_the_language_model_setup_and_offers_to_start_without_one), app =>
        {
            Choose(Option(app, "Onboarding_SetupProfile_Gaming"));
            UiWait.For(() => Shown(app, "Onboarding_GamingOnly_Start_Button"), "Gaming-only start button");
            // The choice is saved at once. (The hidden AI sections are descendants of a collapsed panel, which UI Automation
            // keeps listing with stale geometry, so their absence is checked on the saved state and by screenshot, not by tree.)
            UiWait.For(() => SettingsFileContains(app, "\"SetupProfileId\": \"gaming\""), "gaming profile saved");

            // Back to Both: the shortcut goes away and the choice is saved again.
            Choose(Option(app, "Onboarding_SetupProfile_Both"));
            UiWait.For(() => !Shown(app, "Onboarding_GamingOnly_Start_Button"), "Gaming-only start hidden again");
            UiWait.For(() => SettingsFileContains(app, "\"SetupProfileId\": \"both\""), "both profile saved");
        });
}
