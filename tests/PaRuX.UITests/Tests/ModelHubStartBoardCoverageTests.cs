using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>The Model Hub opens on a Start board: one tile per capability (chat, images, editing, video, voice, music, 3D)
/// that names what is ready and what is missing. Observes controls only; no engine is installed and nothing is downloaded.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelHubStartBoardCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Start_board_lists_every_capability_with_a_status_and_one_next_action() => Run(
        nameof(Start_board_lists_every_capability_with_a_status_and_one_next_action), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenModelHub();
            main.SelectModelHubTab("ModelHub_Tab_Start");

            // A StackPanel is not an accessibility element; the summary line is the board's own, stable text.
            AutomationElement summary = UiWait.ById(app.MainWindow, "ModelHub_Start_Summary", ControlType.Text);
            Assert.False(string.IsNullOrWhiteSpace(summary.Name));

            foreach (string capability in new[] { "chat", "image", "edit", "video", "speech", "music", "3d" })
            {
                string id = "ModelHub_Start_" + capability;
                UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)) != null, "tile " + id);
                AutomationElement tile = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id))!;
                Assert.Contains(":", tile.Name);   // "<title>: <status>" - the state is part of the accessible name
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Start_board_can_reach_the_detailed_catalogues() => Run(
        nameof(Start_board_can_reach_the_detailed_catalogues), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenModelHub();
            main.SelectModelHubTab("ModelHub_Tab_Start");

            foreach (string id in new[] { "ModelHub_Start_Browse_Text", "ModelHub_Start_Browse_Media", "ModelHub_Start_Browse_Downloads" })
                UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)) != null, id);
        });
}
