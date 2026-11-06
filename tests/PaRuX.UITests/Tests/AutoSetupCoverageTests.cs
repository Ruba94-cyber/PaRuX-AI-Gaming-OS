using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>First run offers one primary "Set up automatically" action. These tests only check that the
/// card and its controls exist with the right states; nothing is started, downloaded or installed, so
/// the first-model storage fixture (in-memory run state, no network) is enough.</summary>
[Collection(UiTestCollection.Name)]
public sealed class AutoSetupCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step, enableFirstModelStorageFixture: true);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Onboarding_exposes_the_automatic_setup_card_with_headline_detail_and_primary_action() => Run(
        nameof(Onboarding_exposes_the_automatic_setup_card_with_headline_detail_and_primary_action), app =>
        {
            // A Border's UIA control type varies by host; the id is the contract.
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Onboarding_AutoSetup_Card")) != null,
                "automatic setup card");
            AutomationElement headline = UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Headline_Text", ControlType.Text);
            Assert.False(string.IsNullOrWhiteSpace(headline.Name));
            AutomationElement detail = UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Detail_Text", ControlType.Text);
            Assert.False(string.IsNullOrWhiteSpace(detail.Name));

            AutomationElement start = UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Start_Button", ControlType.Button);
            Assert.Equal("Set up automatically", start.Name);
            Assert.True(start.Properties.IsEnabled.ValueOrDefault);
        });

    /// <summary>Regression: on a normal first launch the (hidden) navigation pane was re-opened by the width policy and
    /// painted as a dark 288 px slab over the left of the welcome page, clipping its text.</summary>
    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Onboarding_does_not_leave_the_side_pane_open_over_the_welcome_page() => Run(
        nameof(Onboarding_does_not_leave_the_side_pane_open_over_the_welcome_page), app =>
        {
            AutomationElement card = UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Start_Button", ControlType.Button);
            // Give the window time to receive its first size change, which is when the pane used to re-open.
            System.Threading.Thread.Sleep(2500);

            AutomationElement? pane = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PaneRoot"));
            Assert.True(pane == null || pane.Properties.IsOffscreen.ValueOrDefault || pane.BoundingRectangle.Width < 4,
                "the navigation pane must not be painted over the welcome page");
            Assert.True(card.BoundingRectangle.Left > 0 && !card.Properties.IsOffscreen.ValueOrDefault,
                "the primary setup action is on screen");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Onboarding_idle_state_hides_cancel_retry_skip_and_progress_but_keeps_the_manual_path() => Run(
        nameof(Onboarding_idle_state_hides_cancel_retry_skip_and_progress_but_keeps_the_manual_path), app =>
        {
            UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Start_Button", ControlType.Button);

            foreach (string hidden in new[]
            {
                "Onboarding_AutoSetup_Cancel_Button", "Onboarding_AutoSetup_Retry_Button",
                "Onboarding_AutoSetup_Skip_Button", "Onboarding_AutoSetup_Progress"
            })
            {
                AutomationElement? element = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(hidden));
                Assert.True(element == null || element.Properties.IsOffscreen.ValueOrDefault,
                    hidden + " must not be shown before a run starts.");
            }

            UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Manual_Text", ControlType.Text);
            // Manual controls are progressive disclosure: not shown until explicitly expanded.
            AutomationElement manual = UiWait.ById(app.MainWindow, "Onboarding_ManualSetup_Expander");
            var pattern = manual.Patterns.ExpandCollapse.PatternOrDefault;
            Assert.NotNull(pattern);
            Assert.Equal(ExpandCollapseState.Collapsed, pattern!.ExpandCollapseState.ValueOrDefault);
            UiWait.ExpandById(app.MainWindow, "Onboarding_ManualSetup_Expander");
            UiWait.ById(app.MainWindow, "FirstModelInstallButton", ControlType.Button);
            UiWait.ById(app.MainWindow, "Onboarding_VerifyLocalChat_Button", ControlType.Button);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding"), Trait("Category", "FlaUI")]
    public void Manual_setup_disclosure_does_not_hide_the_primary_ready_check() => Run(
        nameof(Manual_setup_disclosure_does_not_hide_the_primary_ready_check), app =>
        {
            UiWait.ById(app.MainWindow, "Onboarding_AutoSetup_Start_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "Onboarding_ManualSetup_Expander");
            // Readiness and the final action must remain accessible outside the technical panel.
            UiWait.ById(app.MainWindow, "Onboarding_VerifyLocalChat_Button", ControlType.Button);
            AutomationElement start = UiWait.ById(app.MainWindow, "Onboarding_GetStarted_Button", ControlType.Button);
            Assert.NotNull(start); // It may be temporarily disabled while Core discovery is in progress.
        });
}
