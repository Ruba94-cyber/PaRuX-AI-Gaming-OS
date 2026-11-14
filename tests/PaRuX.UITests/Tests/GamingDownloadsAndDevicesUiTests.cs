using System.Net.NetworkInformation;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Reachability, enabled state, accessible names and invalid-input handling of the Downloads, Save backup and
/// Devices panels of the Games workspace. Only inputs that are rejected before any consent dialog or Core call are
/// exercised: no download, network request, driver call, display change or file deletion can happen here.
/// Status texts are asserted language-neutrally (the app follows the machine UI language, EN or IT).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingDownloadsAndDevicesUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Downloads_panel_is_accessible_and_rejects_invalid_input_without_starting_a_transfer()
    {
        const string name = nameof(Downloads_panel_is_accessible_and_rejects_invalid_input_without_starting_a_transfer);
        WaitForPort();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            var sourceKind = UiWait.ById(app.MainWindow, "GameDownloads_Kind", ControlType.ComboBox);
            var torrentStatus = UiWait.ById(app.MainWindow, "GameDownloads_TorrentEngine_Status", ControlType.Text);
            var portableInstall = UiWait.ById(app.MainWindow, "GameDownloads_AutoPortable", ControlType.CheckBox);
            var url = UiWait.ById(app.MainWindow, "GameDownloads_Url", ControlType.Edit);
            var destination = UiWait.ById(app.MainWindow, "GameDownloads_Destination", ControlType.Edit);
            var browse = UiWait.ById(app.MainWindow, "GameDownloads_Browse", ControlType.Button);
            var fileName = UiWait.ById(app.MainWindow, "GameDownloads_FileName", ControlType.Edit);
            var sha = UiWait.ById(app.MainWindow, "GameDownloads_SHA256", ControlType.Edit);
            var priority = UiWait.ById(app.MainWindow, "GameDownloads_Priority", ControlType.ComboBox);
            var speed = UiWait.ById(app.MainWindow, "GameDownloads_SpeedKiB", ControlType.Edit);
            var applySpeed = UiWait.ById(app.MainWindow, "GameDownloads_ApplySpeed", ControlType.Button);
            var start = UiWait.ById(app.MainWindow, "GameDownloads_Start", ControlType.Button);
            var refresh = UiWait.ById(app.MainWindow, "GameDownloads_Refresh", ControlType.Button);
            var status = UiWait.ById(app.MainWindow, "GameDownloads_Status", ControlType.Text);

            foreach (AutomationElement control in new[] { sourceKind, portableInstall, url, destination, browse, fileName, sha, priority, speed, applySpeed, start, refresh })
                Assert.False(string.IsNullOrWhiteSpace(control.Name));
            Assert.False(string.IsNullOrWhiteSpace(status.Name));
            Assert.False(string.IsNullOrWhiteSpace(torrentStatus.Name));
            // The .torrent picker must remain collapsed in the default HTTPS mode.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDownloads_TorrentBrowse")));
            // Auto-deployment must never be selected by default. Its executable
            // and game-title controls are deliberately collapsed until the owner
            // opts in, so no post-download code path runs after an ordinary download.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDownloads_PortableExe")));
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDownloads_PortableTitle")));

            // The initial read must finish first, otherwise it could overwrite the validation message below.
            UiWait.For(() => refresh.Properties.IsEnabled.ValueOrDefault, "the downloads panel finished its initial read");
            UiWait.AssertEnabled(start);
            UiWait.AssertEnabled(applySpeed);
            UiWait.AssertEnabled(browse);

            // Empty URL and destination: validation message, no consent dialog, nothing created.
            string before = status.Name;
            start.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => status.Name.Contains("SHA-256", StringComparison.Ordinal),
                "validation message for the empty download form");
            Assert.NotEqual(before, status.Name);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")));
            UiWait.AssertEnabled(start);

            // Valid loopback URL and folder but a malformed checksum is still refused before the consent dialog.
            url.AsTextBox().Text = "http://127.0.0.1:9/not-a-real-game.bin";
            destination.AsTextBox().Text = app.DataRoot;
            sha.AsTextBox().Text = "not-a-sha256";
            start.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => status.Name.Contains("SHA-256", StringComparison.Ordinal),
                "validation message for the malformed checksum");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")));

            // A speed outside the allowed range is refused locally, without calling the Core.
            speed.AsTextBox().Text = "abc";
            // The status text above re-flowed the panel; take a fresh element before invoking.
            var freshApplySpeed = UiWait.ById(app.MainWindow, "GameDownloads_ApplySpeed", ControlType.Button);
            UiWait.For(() => freshApplySpeed.Properties.IsEnabled.ValueOrDefault, "speed button enabled");
            freshApplySpeed.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => status.Name.Contains("000", StringComparison.Ordinal),
                "validation message for the invalid speed limit");

            Assert.Empty(Directory.GetFiles(app.DataRoot, "*.parux-part", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(app.DataRoot, "*.parux-part.validator", SearchOption.AllDirectories));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Save_backup_panel_is_accessible_and_refuses_an_empty_folder_without_creating_a_backup()
    {
        const string name = nameof(Save_backup_panel_is_accessible_and_refuses_an_empty_folder_without_creating_a_backup);
        WaitForPort();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            var source = UiWait.ById(app.MainWindow, "GameSaves_Source", ControlType.Edit);
            var browse = UiWait.ById(app.MainWindow, "GameSaves_Browse", ControlType.Button);
            var label = UiWait.ById(app.MainWindow, "GameSaves_Label", ControlType.Edit);
            var create = UiWait.ById(app.MainWindow, "GameSaves_Create", ControlType.Button);
            var refresh = UiWait.ById(app.MainWindow, "GameSaves_Refresh", ControlType.Button);
            var cancel = UiWait.ById(app.MainWindow, "GameSaves_Cancel", ControlType.Button);
            var status = UiWait.ById(app.MainWindow, "GameSaves_Status", ControlType.Text);

            foreach (AutomationElement control in new[] { source, browse, label, create, refresh, cancel })
                Assert.False(string.IsNullOrWhiteSpace(control.Name));
            UiWait.AssertEnabled(create);
            UiWait.AssertEnabled(refresh);
            UiWait.AssertEnabled(browse);
            // No operation is running, so there is nothing to cancel.
            Assert.False(cancel.Properties.IsEnabled.ValueOrDefault);

            // With no save folder chosen, Create refuses locally: message shown, no confirmation dialog, no operation.
            source.AsTextBox().Text = "";
            create.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !string.IsNullOrWhiteSpace(status.Name), "validation message for the empty save folder");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")));
            UiWait.AssertEnabled(create);
            Assert.False(cancel.Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Devices_panel_exposes_display_and_controller_controls_and_apply_without_a_mode_changes_nothing()
    {
        const string name = nameof(Devices_panel_exposes_display_and_controller_controls_and_apply_without_a_mode_changes_nothing);
        WaitForPort();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            var modes = UiWait.ById(app.MainWindow, "GameDevices_DisplayModes_ComboBox", ControlType.ComboBox);
            var loadModes = UiWait.ById(app.MainWindow, "GameDevices_DisplayModes_Load_Button", ControlType.Button);
            var apply = UiWait.ById(app.MainWindow, "GameDevices_Display_Apply_Button", ControlType.Button);
            var displayStatus = UiWait.ById(app.MainWindow, "GameDevices_Display_Status_Text", ControlType.Text);
            var controllerLoad = UiWait.ById(app.MainWindow, "GameDevices_Controller_Load_Button", ControlType.Button);
            var calibrate = UiWait.ById(app.MainWindow, "GameDevices_Controller_Calibrate_Button", ControlType.Button);
            var save = UiWait.ById(app.MainWindow, "GameDevices_Controller_Save_Button", ControlType.Button);
            var reset = UiWait.ById(app.MainWindow, "GameDevices_Controller_Reset_Button", ControlType.Button);
            var rawDetect = UiWait.ById(app.MainWindow, "GameDevices_Raw_Detect_Button", ControlType.Button);
            var rawRead = UiWait.ById(app.MainWindow, "GameDevices_Raw_Read_Button", ControlType.Button);
            var rawSelect = UiWait.ById(app.MainWindow, "GameDevices_RawController_ComboBox", ControlType.ComboBox);
            var hidStatus = UiWait.ById(app.MainWindow, "GameDevices_HidHide_Status_Button", ControlType.Button);
            var hidRestore = UiWait.ById(app.MainWindow, "GameDevices_HidHide_Restore_Button", ControlType.Button);
            var mappingLoad = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_Load_Button", ControlType.Button);
            var mappingSave = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_Save_Button", ControlType.Button);

            foreach (AutomationElement control in new[]
                     { modes, loadModes, apply, controllerLoad, calibrate, save, reset, rawDetect, rawRead,
                       rawSelect, hidStatus, hidRestore, mappingLoad, mappingSave })
                Assert.False(string.IsNullOrWhiteSpace(control.Name));
            foreach (AutomationElement button in new[] { loadModes, apply, controllerLoad, calibrate, save, reset,
                                                         rawDetect, rawRead, hidStatus, hidRestore, mappingLoad, mappingSave })
                UiWait.AssertEnabled(button);

            // Number boxes are reachable by their AutomationId (same convention as the existing raw-mapping checks).
            foreach (string id in new[]
                     { "GameDevices_LeftDeadzone_NumberBox", "GameDevices_RightDeadzone_NumberBox",
                       "GameDevices_TriggerThreshold_NumberBox", "GameDevices_RawMapping_A_Index",
                       "GameDevices_RawMapping_B_Index", "GameDevices_RawMapping_LeftX_Index",
                       "GameDevices_RawMapping_Deadzone" })
                Assert.False(string.IsNullOrWhiteSpace(
                    UiWait.ById(app.MainWindow, id, ControlType.Spinner).Properties.AutomationId.ValueOrDefault));

            // No pending display change exists, so the confirm/revert actions are not offered at all.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDevices_Display_Confirm_Button")));
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDevices_Display_Revert_Button")));

            // Apply with no mode loaded is refused locally (no Core call, no driver call, no display change).
            apply.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !string.IsNullOrWhiteSpace(displayStatus.Name), "message for applying without a loaded mode");
            UiWait.For(() => apply.Properties.IsEnabled.ValueOrDefault, "apply button idle again");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDevices_Display_Confirm_Button")));
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDevices_Display_Revert_Button")));
            // Device enumeration, calibration, saving and HidHide actions are deliberately not invoked.
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    private static void WaitForPort() =>
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));

    private static void OpenGames(UiTestApplication app)
    {
        var navigation = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
        new InstallProgressPage(app).Invoke(navigation);
        // Advanced Game Management is collapsed by default; expanding it is the owner's opt-in.
        AutomationElement? expander = null;
        UiWait.For(() => (expander = app.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId("AdvancedGameManagement_Expander"))) != null, "advanced section");
        expander!.Patterns.ExpandCollapse.Pattern.Expand();
    }
}
