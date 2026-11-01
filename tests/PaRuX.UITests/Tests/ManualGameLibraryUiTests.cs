using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ManualGameLibraryUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Manual_library_adds_and_removes_entry_without_removing_game_file()
    {
        // The previous test's Core needs a moment to exit after its window closes. Wait for the port instead of failing
        // on that teardown lag; a Core that never exits (a real leftover) still fails the assertion below.
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), endpoint => endpoint.Port == 51123);
        using var app = new UiTestApplication(nameof(Manual_library_adds_and_removes_entry_without_removing_game_file));
        try
        {
            string marker = Path.Combine(app.DataRoot, "fixture-game.exe");
            File.WriteAllText(marker, "fixture, not executable");
            var item = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(item);
            var name = UiWait.ById(app.MainWindow, "GameLibrary_Name_TextBox", ControlType.Edit).AsTextBox();
            var path = UiWait.ById(app.MainWindow, "GameLibrary_Path_TextBox", ControlType.Edit).AsTextBox();
            UiWait.For(() => File.Exists(Path.Combine(app.DataRoot, "server", "bootstrap.json")), "isolated core bootstrap");
            var bootstrap = JsonDocument.Parse(File.ReadAllText(Path.Combine(app.DataRoot, "server", "bootstrap.json")));
            Assert.Equal("http://127.0.0.1:51123", bootstrap.RootElement.GetProperty("endpoint").GetString()?.TrimEnd('/'));
            int serverPid = bootstrap.RootElement.GetProperty("processId").GetInt32();
            using var server = Process.GetProcessById(serverPid);
            string expectedServer = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!, "PaRuX.Server", "PaRuX.Server.exe");
            Assert.Equal(Path.GetFullPath(expectedServer), Path.GetFullPath(server.MainModule!.FileName), ignoreCase: true);
            name.Text = "FlaUI fixture game"; path.Text = marker;
            var add = UiWait.ById(app.MainWindow, "GameLibrary_Add_Button", ControlType.Button);
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
            add.Patterns.Invoke.Pattern.Invoke();
            string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
            UiWait.For(() => File.Exists(library) && File.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry persisted");
            using var libraryDocument = JsonDocument.Parse(File.ReadAllText(library));
            string gameId = libraryDocument.RootElement.GetProperty("Games")[0].GetProperty("Id").GetString()!;
            var launch = UiWait.ById(app.MainWindow, "GameLibrary_Launch_" + gameId, ControlType.Button);
            Assert.True(launch.Properties.IsEnabled.ValueOrDefault);
            // The game is deliberately non-executable. Verify the confirmation appears
            // and that cancelling it cannot start the fixture or change Gaming state.
            launch.Patterns.Invoke.Pattern.Invoke();
            var cancelLaunch = Find(() => app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button))),
                "manual game launch consent dialog");
            cancelLaunch.Patterns.Invoke.Pattern.Invoke();
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
            // Cancelling the dialog ends the library's busy state a moment later; a click before that is ignored by design.
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle after the launch dialog was cancelled");
            var remove = UiWait.ById(app.MainWindow, "GameLibrary_Remove_" + gameId, ControlType.Button);
            remove.Patterns.Invoke.Pattern.Invoke();
            var confirm = Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton").And(cf.ByControlType(ControlType.Button))), "remove confirmation");
            confirm.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !File.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry removed");
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Manual_library_adds_and_removes_entry_without_removing_game_file), ex); throw; }
    }
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Store_discovery_controls_use_existing_library_navigation_and_accessible_buttons()
    {
        using var app = new UiTestApplication(nameof(Store_discovery_controls_use_existing_library_navigation_and_accessible_buttons));
        try
        {
            var navigation = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(navigation);
            var refreshGaming = UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button);
            var returnToAi = UiWait.ById(app.MainWindow, "GameLibrary_ReturnToAi_Button", ControlType.Button);
            UiWait.For(() => refreshGaming.Properties.IsEnabled.ValueOrDefault,
                "Gaming status refresh finished initial load");
            Assert.False(string.IsNullOrWhiteSpace(returnToAi.Name));
            // In the isolated initial AI-ready state the destructive-equivalent
            // resource transition must not be offered without a known active session.
            Assert.False(returnToAi.Properties.IsEnabled.ValueOrDefault);
            var steam = UiWait.ById(app.MainWindow, "GameLibrary_Steam_Refresh_Button", ControlType.Button);
            var epic = UiWait.ById(app.MainWindow, "GameLibrary_Epic_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(steam.Name));
            Assert.False(string.IsNullOrWhiteSpace(epic.Name));
            Assert.True(steam.Properties.IsEnabled.ValueOrDefault);
            Assert.True(epic.Properties.IsEnabled.ValueOrDefault);
            var inspector = UiWait.ById(app.MainWindow, "GameLibrary_Installer_Inspect_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(inspector.Name));
            var publisherHash = UiWait.ById(app.MainWindow,
                "GameLibrary_Installer_PublisherHash_Input", ControlType.Edit);
            var stage = UiWait.ById(app.MainWindow,
                "GameLibrary_Installer_Stage_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(publisherHash.Name));
            Assert.False(string.IsNullOrWhiteSpace(stage.Name));
            Assert.False(stage.Properties.IsEnabled.ValueOrDefault);
            var inventory = UiWait.ById(app.MainWindow,
                "GameLibrary_Installer_Staged_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(inventory.Name));
            // Read-only inventory action is not invoked in this isolated smoke.
            // No SHA-256 is supplied and no package staging is triggered in this UI smoke.
            // Never select or hash a local package in this UI smoke.
            var controllers = UiWait.ById(app.MainWindow, "GameLibrary_Controllers_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(controllers.Name));
            Assert.True(controllers.Properties.IsEnabled.ValueOrDefault);
            var displays = UiWait.ById(app.MainWindow, "GameLibrary_Displays_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(displays.Name));
            Assert.True(displays.Properties.IsEnabled.ValueOrDefault);
            // Read-only display probe is not clicked in fixture: it must not depend on
            // the current machine monitor layout or change its configuration.
            var allStores = UiWait.ById(app.MainWindow, "GameLibrary_AllStores_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(allStores.Name));
            Assert.True(allStores.Properties.IsEnabled.ValueOrDefault);
            var storePrevious = UiWait.ById(app.MainWindow, "GameLibrary_Store_Previous_Button", ControlType.Button);
            var storeNext = UiWait.ById(app.MainWindow, "GameLibrary_Store_Next_Button", ControlType.Button);
            Assert.False(storePrevious.Properties.IsEnabled.ValueOrDefault);
            Assert.False(storeNext.Properties.IsEnabled.ValueOrDefault);
            var storePage = UiWait.ById(app.MainWindow, "GameLibrary_Store_Page_Status", ControlType.Text);
            Assert.False(string.IsNullOrWhiteSpace(storePage.Properties.AutomationId.ValueOrDefault));
            var gog = UiWait.ById(app.MainWindow, "GameLibrary_Gog_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(gog.Name));
            Assert.True(gog.Properties.IsEnabled.ValueOrDefault);
            // Do not click: this checks UI reachability without reading a real user's
            // installed game list or making a test depend on machine-specific stores.
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Store_discovery_controls_use_existing_library_navigation_and_accessible_buttons), ex);
            throw;
        }
    }

    private static AutomationElement Find(Func<AutomationElement?> find, string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = find()) != null, description);
        return element!;
    }

}
