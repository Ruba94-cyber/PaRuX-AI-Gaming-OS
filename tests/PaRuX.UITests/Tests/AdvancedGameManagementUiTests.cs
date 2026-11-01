using System.Net.NetworkInformation;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Synthetic folder and fake program only: nothing is installed, launched or added to a real library. The test
/// opens the opt-in section, inspects a folder and checks that a ranked candidate is offered.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class AdvancedGameManagementUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Advanced_section_inspects_a_synthetic_game_folder_and_offers_the_main_program()
    {
        const string name = nameof(Advanced_section_inspects_a_synthetic_game_folder_and_offers_the_main_program);
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));
        using var app = new UiTestApplication(name);
        try
        {
            string gameFolder = Path.Combine(app.DataRoot, "Synthetic Quest v1.0");
            Directory.CreateDirectory(gameFolder);
            File.WriteAllBytes(Path.Combine(gameFolder, "SyntheticQuest.exe"), new byte[200_000]);
            File.WriteAllBytes(Path.Combine(gameFolder, "unins000.exe"), new byte[200_000]);

            var item = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(item);

            // The advanced section is collapsed by default; expanding it is the opt-in.
            AutomationElement expander = Find(() => app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("AdvancedGameManagement_Expander")), "advanced section");
            expander.Patterns.ExpandCollapse.Pattern.Expand();

            var folder = UiWait.ById(app.MainWindow, "GameImport_Folder_TextBox", ControlType.Edit).AsTextBox();
            folder.Text = gameFolder;
            var inspect = UiWait.ById(app.MainWindow, "GameImport_Inspect_Button", ControlType.Button);
            UiWait.For(() => inspect.Properties.IsEnabled.ValueOrDefault, "library idle");
            inspect.Patterns.Invoke.Pattern.Invoke();

            // Only the real game program is offered; the uninstaller is filtered out. Nothing is added or run.
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Candidate_SyntheticQuest.exe")),
                "main program candidate");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Candidate_unins000.exe")));
            Assert.Contains("Synthetic Quest", UiWait.ById(app.MainWindow, "GameImport_Status_Text", ControlType.Text).Name);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    private static AutomationElement Find(Func<AutomationElement?> find, string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = find()) != null, description);
        return element!;
    }
}
