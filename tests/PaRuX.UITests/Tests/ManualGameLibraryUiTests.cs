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
            // Core uses 51123 unless Windows reserved it (Hyper-V/WSL2/Docker); then it publishes another free loopback port.
            string? endpoint = bootstrap.RootElement.GetProperty("endpoint").GetString()?.TrimEnd('/');
            Assert.Matches(@"^http://127\.0\.0\.1:\d{4,5}$", endpoint);
            int serverPid = bootstrap.RootElement.GetProperty("processId").GetInt32();
            using var server = Process.GetProcessById(serverPid);
            string expectedServer = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!, "PaRuX.Server", "PaRuX.Server.exe");
            Assert.Equal(Path.GetFullPath(expectedServer), Path.GetFullPath(server.MainModule!.FileName), ignoreCase: true);
            name.Text = "FlaUI fixture game"; path.Text = marker;
            var add = UiWait.ById(app.MainWindow, "GameLibrary_Add_Button", ControlType.Button);
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
            add.Patterns.Invoke.Pattern.Invoke();
            string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
            UiWait.For(() => File.Exists(library) && SharedFile.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry persisted");
            using var libraryDocument = JsonDocument.Parse(SharedFile.ReadAllText(library));
            // Adding keeps the panel busy until the Gaming tile grid refreshed; a launch click before that is ignored by design.
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle after adding the entry");
            string gameId = libraryDocument.RootElement.GetProperty("Games")[0].GetProperty("Id").GetString()!;
            var launch = UiWait.ById(app.MainWindow, "GameLibrary_Launch_" + gameId, ControlType.Button);
            UiWait.AssertEnabled(launch);
            // The game is deliberately non-executable. Verify the confirmation appears
            // and that cancelling it cannot start the fixture or change Gaming state.
            launch.Patterns.Invoke.Pattern.Invoke();
            AutomationElement cancelLaunch;
            try
            {
                cancelLaunch = Find(() => app.MainWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button))),
                    "manual game launch consent dialog");
            }
            catch (TimeoutException ex)
            {
                string? status = app.MainWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId("GameLibrary_Status_Text"))?.Name;
                throw new TimeoutException(ex.Message + " Library status: " + (status ?? "<none>"), ex);
            }
            cancelLaunch.Patterns.Invoke.Pattern.Invoke();
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
            // Cancelling the dialog ends the library's busy state a moment later; a click before that is ignored by design.
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle after the launch dialog was cancelled");
            var remove = UiWait.ById(app.MainWindow, "GameLibrary_Remove_" + gameId, ControlType.Button);
            remove.Patterns.Invoke.Pattern.Invoke();
            var confirm = Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton").And(cf.ByControlType(ControlType.Button))), "remove confirmation");
            confirm.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !SharedFile.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry removed");
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
            var collectionFilter = UiWait.ById(app.MainWindow, "GameTitles_Collection_Filter", ControlType.Edit);
            var collectionApply = UiWait.ById(app.MainWindow, "GameTitles_Collection_Apply", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(collectionFilter.Name));
            Assert.False(string.IsNullOrWhiteSpace(collectionApply.Name));
            // A fixture with no games must still expose and permit the read-only filter.
            // No real store discovery, game execution or collection mutation is required.
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
            // Store buttons are disabled while the library's initial load is busy; they must become usable once it settles.
            UiWait.For(() => steam.Properties.IsEnabled.ValueOrDefault && epic.Properties.IsEnabled.ValueOrDefault,
                "store refresh buttons enabled after the initial library load");
            UiWait.AssertEnabled(steam);
            UiWait.AssertEnabled(epic);
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
            // Read-only RawGameController controls: no device enumeration or gamepad polling
            // is started merely by opening Games. The actual hardware is intentionally not
            // exercised in this fixture to keep it independent of the developer's peripherals.
            var rawDetect = UiWait.ById(app.MainWindow, "GameDevices_Raw_Detect_Button", ControlType.Button);
            var rawRead = UiWait.ById(app.MainWindow, "GameDevices_Raw_Read_Button", ControlType.Button);
            var rawSelect = UiWait.ById(app.MainWindow, "GameDevices_RawController_ComboBox", ControlType.ComboBox);
            var mappingLoad = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_Load_Button", ControlType.Button);
            var mappingSave = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_Save_Button", ControlType.Button);
            var hidStatus = UiWait.ById(app.MainWindow, "GameDevices_HidHide_Status_Button", ControlType.Button);
            var hidRestore = UiWait.ById(app.MainWindow, "GameDevices_HidHide_Restore_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(hidStatus.Name));
            Assert.False(string.IsNullOrWhiteSpace(hidRestore.Name));
            var mappingA = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_A_Index", ControlType.Spinner);
            var mappingDeadzone = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_Deadzone", ControlType.Spinner);
            Assert.False(string.IsNullOrWhiteSpace(mappingLoad.Name));
            Assert.False(string.IsNullOrWhiteSpace(mappingSave.Name));
            Assert.False(string.IsNullOrWhiteSpace(mappingA.Properties.AutomationId.ValueOrDefault));
            Assert.False(string.IsNullOrWhiteSpace(mappingDeadzone.Properties.AutomationId.ValueOrDefault));
            Assert.False(string.IsNullOrWhiteSpace(rawDetect.Name));
            Assert.False(string.IsNullOrWhiteSpace(rawRead.Name));
            Assert.False(string.IsNullOrWhiteSpace(rawSelect.Name));
            var controllers = UiWait.ById(app.MainWindow, "GameLibrary_Controllers_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(controllers.Name));
            UiWait.AssertEnabled(controllers);
            var displays = UiWait.ById(app.MainWindow, "GameLibrary_Displays_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(displays.Name));
            UiWait.AssertEnabled(displays);
            // Read-only display probe is not clicked in fixture: it must not depend on
            // the current machine monitor layout or change its configuration.
            var allStores = UiWait.ById(app.MainWindow, "GameLibrary_AllStores_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(allStores.Name));
            UiWait.AssertEnabled(allStores);
            var storePrevious = UiWait.ById(app.MainWindow, "GameLibrary_Store_Previous_Button", ControlType.Button);
            var storeNext = UiWait.ById(app.MainWindow, "GameLibrary_Store_Next_Button", ControlType.Button);
            Assert.False(storePrevious.Properties.IsEnabled.ValueOrDefault);
            Assert.False(storeNext.Properties.IsEnabled.ValueOrDefault);
            var storePage = UiWait.ById(app.MainWindow, "GameLibrary_Store_Page_Status", ControlType.Text);
            Assert.False(string.IsNullOrWhiteSpace(storePage.Properties.AutomationId.ValueOrDefault));
            var gog = UiWait.ById(app.MainWindow, "GameLibrary_Gog_Refresh_Button", ControlType.Button);
            Assert.False(string.IsNullOrWhiteSpace(gog.Name));
            UiWait.AssertEnabled(gog);
            // Do not click: this checks UI reachability without reading a real user's
            // installed game list or making a test depend on machine-specific stores.
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Store_discovery_controls_use_existing_library_navigation_and_accessible_buttons), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Game_download_ui_requires_consent_and_does_not_start_unapproved_transfer()
    {
        using var app = new UiTestApplication(nameof(Game_download_ui_requires_consent_and_does_not_start_unapproved_transfer));
        try
        {
            var navigation = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(navigation);

            var refresh = UiWait.ById(app.MainWindow, "GameDownloads_Refresh", ControlType.Button);
            var start = UiWait.ById(app.MainWindow, "GameDownloads_Start", ControlType.Button);
            var speed = UiWait.ById(app.MainWindow, "GameDownloads_SpeedKiB", ControlType.Edit);
            var applySpeed = UiWait.ById(app.MainWindow, "GameDownloads_ApplySpeed", ControlType.Button);
            var priority = UiWait.ById(app.MainWindow, "GameDownloads_Priority", ControlType.ComboBox);
            var url = UiWait.ById(app.MainWindow, "GameDownloads_Url", ControlType.Edit).AsTextBox();
            var folder = UiWait.ById(app.MainWindow, "GameDownloads_Destination", ControlType.Edit).AsTextBox();

            UiWait.For(() => refresh.Properties.IsEnabled.ValueOrDefault,
                "the downloads panel finished its initial read");
            UiWait.AssertEnabled(start);
            Assert.False(string.IsNullOrWhiteSpace(start.Name));
            Assert.False(string.IsNullOrWhiteSpace(speed.Name));
            Assert.False(string.IsNullOrWhiteSpace(applySpeed.Name));
            Assert.False(string.IsNullOrWhiteSpace(priority.Name));

            // A local loopback URL is accepted by the validation policy; cancellation
            // must prevent the Core from making any request or creating a part file.
            url.Text = "http://127.0.0.1:9/not-a-real-game.bin";
            folder.Text = app.DataRoot;
            start.Patterns.Invoke.Pattern.Invoke();
            var cancel = Find(() => app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button))),
                "download-start consent dialog");
            cancel.Patterns.Invoke.Pattern.Invoke();

            Assert.Empty(Directory.GetFiles(app.DataRoot, "*.parux-part", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(app.DataRoot, "*.parux-part.validator", SearchOption.AllDirectories));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Game_download_ui_requires_consent_and_does_not_start_unapproved_transfer), ex);
            throw;
        }
    }

    /// <summary>UX-002: while Gaming mode is active, "Return to AI" is one action away from every Gaming section.</summary>
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Gaming")]
    public void Return_to_ai_strip_is_on_every_gaming_section_only_while_gaming_is_active()
    {
        using var app = new UiTestApplication(nameof(Return_to_ai_strip_is_on_every_gaming_section_only_while_gaming_is_active));
        try
        {
            const string stripButton = "GameLibrary_Strip_ReturnToAi_Button";
            string[] sections = ["Library", "Downloads", "Add", "Devices"];
            var navigation = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(navigation);
            UiWait.ById(app.MainWindow, "GameLibrary_Sections");

            // Presses "Refresh Gaming status" and waits for the Devices status text that proves the Core answer was applied
            // (the marker alone shows the "resource lock" text, no marker shows "AI ready"). A click that was lost, or dropped
            // while the view was busy, is sent again (bounded); the last observed text is reported when the effect never appears.
            void RefreshStatus(bool expectGamingMarker)
            {
                string expected = expectGamingMarker ? "Gaming resource lock is still active" : "AI ready";
                string last = "<not read>";
                try
                {
                    UiWait.InputUntil(() =>
                    {
                        GamingSectionsPage.SelectSection(app.MainWindow, "Devices");
                        var refresh = UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Refresh_Button", ControlType.Button);
                        UiWait.For(() => refresh.Properties.IsEnabled.ValueOrDefault, "Gaming status refresh usable");
                        refresh.Patterns.Invoke.Pattern.Invoke();
                    }, () =>
                    {
                        last = UiWait.ById(app.MainWindow, "GameLibrary_Gaming_Status_Text").Properties.Name.ValueOrDefault ?? "<null>";
                        return last.Contains(expected, StringComparison.Ordinal);
                    }, "Gaming status refreshed to '" + expected + "'");
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException(ex.Message + " Last Devices status text: '" + last + "'.", ex);
                }
            }
            RefreshStatus(expectGamingMarker: false);

            AutomationElement? Strip() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(stripButton));

            // AI ready: no strip on any section.
            foreach (string section in sections)
            {
                GamingSectionsPage.SelectSection(app.MainWindow, section);
                Thread.Sleep(300);
                Assert.Null(Strip());
            }

            string marker = Path.Combine(app.LocalAiStateRoot, "game-active.json");
            Directory.CreateDirectory(app.LocalAiStateRoot);
            File.WriteAllText(marker, "{\"started\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
            RefreshStatus(expectGamingMarker: true);

            foreach (string section in sections)
            {
                GamingSectionsPage.SelectSection(app.MainWindow, section);
                var button = Find(Strip, "Return to AI strip on section " + section);
                UiWait.For(() => button.Properties.IsEnabled.ValueOrDefault, "strip Return to AI enabled on " + section);
                Assert.Equal("Return to AI without closing the game", button.Name);
                Assert.True(button.Properties.IsKeyboardFocusable.ValueOrDefault, "keyboard reachable");
                var bounds = button.BoundingRectangle;
                Assert.True(bounds.Height >= 44, "touch target height " + bounds.Height);
                Assert.True(bounds.Width >= 44, "touch target width " + bounds.Width);
                Assert.True(bounds.Bottom <= app.MainWindow.BoundingRectangle.Bottom, "strip stays inside the window on " + section);
                Assert.True(bounds.Top - app.MainWindow.BoundingRectangle.Top < 400, "strip sits above the section content on " + section);
            }

            // Gaming ends: the strip goes away on every section.
            File.Delete(marker);
            RefreshStatus(expectGamingMarker: false);
            foreach (string section in sections)
            {
                GamingSectionsPage.SelectSection(app.MainWindow, section);
                UiWait.For(() => Strip() == null, "strip hidden on " + section + " after Gaming ended");
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Return_to_ai_strip_is_on_every_gaming_section_only_while_gaming_is_active), ex);
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
