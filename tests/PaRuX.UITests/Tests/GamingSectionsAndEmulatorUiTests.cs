using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class GamingSectionsAndEmulatorUiTests
{
    private static AutomationElement? Raw(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static void OpenEmulators(UiTestApplication app)
    {
        Press(app, "MainWindow_ModeTab_Gaming");
        GamingSectionsPage.SelectSection(app.MainWindow, "Emulators");
        Press(app, "Emulators_Detect_Button");
        var combo = UiWait.ById(app.MainWindow, "Emulators_Select_ComboBox").AsComboBox();
        // The list holds over a hundred emulators and the drop-down only realizes the rows in view, so choose by typing the name.
        DateTime last = DateTime.MinValue;
        UiWait.For(() =>
        {
            string selected = "";
            try { selected = combo.SelectedItem?.Name ?? ""; } catch { }
            if (selected.StartsWith("Dolphin", StringComparison.Ordinal)) return true;
            if (DateTime.UtcNow - last > TimeSpan.FromSeconds(2))
            {
                last = DateTime.UtcNow;
                try { combo.Focus(); FlaUI.Core.Input.Keyboard.Type("Dolphin"); } catch { }
            }
            return false;
        }, "Dolphin descriptor chosen", TimeSpan.FromSeconds(75)); // detection walks the disk and takes longer on a busy PC
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Sections_isolate_import_and_emulators_and_dummy_rom_setup_survives_restart()
    {
        const string name = nameof(Sections_isolate_import_and_emulators_and_dummy_rom_setup_survives_restart);
        UiTestApplication.WaitForCorePortReleased();
        UiTestApplication app = new(name);
        try
        {
            string folder = Path.Combine(app.DataRoot, "synthetic-roms");
            Directory.CreateDirectory(folder);
            string rom = Path.Combine(folder, "Synthetic Dolphin Game.iso");
            string executable = Path.Combine(app.DataRoot, "Dolphin.exe");
            File.WriteAllText(rom, "fixture: not a real game");
            File.WriteAllText(executable, "fixture: not an executable");
            Press(app, "MainWindow_ModeTab_Gaming");
            foreach (string section in new[] { "Library", "Add", "Emulators", "Devices" })
                Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_Section_" + section));
            foreach (string id in new[] { "GameLibrary_FirstRunScan_Start", "AdvancedGameManagement_Expander", "Emulators_Scan_Button" })
                Assert.Null(Raw(app, id));
            GamingSectionsPage.SelectSection(app.MainWindow, "Add");
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_FirstRunScan_Start"));
            Assert.NotNull(UiWait.ById(app.MainWindow, "AdvancedGameManagement_Expander"));
            Assert.Null(Raw(app, "Emulators_Scan_Button"));

            OpenEmulators(app);
            Assert.Null(Raw(app, "GameLibrary_FirstRunScan_Start"));
            Assert.Null(Raw(app, "AdvancedGameManagement_Expander"));
            UiWait.ById(app.MainWindow, "Emulators_Exe_TextBox").AsTextBox().Text = executable;
            UiWait.ById(app.MainWindow, "Emulators_Folder_TextBox").AsTextBox().Text = folder;
            Press(app, "Emulators_SaveSetup_Button");
            UiWait.For(() => File.ReadAllText(app.SettingsPath).Contains(folder.Replace("\\", "\\\\"), StringComparison.Ordinal), "emulator setup saved");
            Press(app, "Emulators_Scan_Button");
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Synthetic Dolphin Game")) != null, "dummy ROM offered");
            string emptyFolder = Path.Combine(app.DataRoot, "empty-roms");
            Directory.CreateDirectory(emptyFolder);
            UiWait.ById(app.MainWindow, "Emulators_Folder_TextBox").AsTextBox().Text = emptyFolder;
            UiWait.ForStable(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Synthetic Dolphin Game")) == null,
                "changing folder clears previous scan results");
            UiWait.ById(app.MainWindow, "Emulators_Folder_TextBox").AsTextBox().Text = folder;
            Press(app, "Emulators_Scan_Button");
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Synthetic Dolphin Game")) != null, "dummy ROM offered after rescan");
            Press(app, "Emulators_Import_Button");
            string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
            UiWait.For(() => File.Exists(library) && SharedFile.ReadAllText(library).Contains("Synthetic Dolphin Game", StringComparison.Ordinal), "dummy ROM imported");
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            UiWait.ById(app.MainWindow, "GameTile_syntheticdolphingame");
            app = app.Restart(name + "-restart");
            OpenEmulators(app);
            Assert.Equal(executable, UiWait.ById(app.MainWindow, "Emulators_Exe_TextBox").AsTextBox().Text);
            Assert.Equal(folder, UiWait.ById(app.MainWindow, "Emulators_Folder_TextBox").AsTextBox().Text);
            Assert.Equal("fixture: not a real game", File.ReadAllText(rom));
            Assert.Equal("fixture: not an executable", File.ReadAllText(executable));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        finally { app.Dispose(); }
    }
}
