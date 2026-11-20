using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Runs against what is actually on THIS PC: the Bluetooth controller, the HID pads Windows reports, and the installed Steam
/// library. Nobody presses a button during an automated run, so these check what can be proven from an idle pad (it is found, it
/// reads, the guided flow runs and changes nothing) and from the real library (Steam titles appear, the card answers honestly
/// without a key). They return early with a note when the hardware or Steam is absent, so a different PC never fails them.
/// Nothing launches a game and nothing changes a display mode, a driver or a controller setting.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RealDevicesAndGamesUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static void OpenGames(UiTestApplication app) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem));

    private static void Note(UiTestApplication app, string message) =>
        File.WriteAllText(Path.Combine(app.DataRoot, "NOT-RUN.txt"), message);

    /// <summary>Selects the first physical controller Windows lists, or returns false when there is none.</summary>
    private static bool FindFirstController(UiTestApplication app)
    {
        UiWait.ById(app.MainWindow, "GameDevices_Raw_Detect_Button", ControlType.Button).AsButton().Invoke();
        var status = UiWait.ById(app.MainWindow, "GameDevices_Raw_Status_Text", ControlType.Text);
        UiWait.For(() => Text(status).Length > 0, "controller detection answers", TimeSpan.FromSeconds(30));
        return !Text(status).StartsWith("No compatible", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void The_real_controller_is_found_and_the_live_view_reads_it_while_idle()
    {
        const string name = nameof(The_real_controller_is_found_and_the_live_view_reads_it_while_idle);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            if (!FindFirstController(app)) { Note(app, "NOT RUN: Windows lists no raw game controller on this PC."); return; }

            var live = UiWait.ById(app.MainWindow, "GameDevices_RawLive_Toggle");
            live.Patterns.Toggle.Pattern.Toggle();
            var changed = UiWait.ById(app.MainWindow, "GameDevices_RawLive_Changed_Text", ControlType.Text);
            UiWait.For(() => Text(changed).Length > 0, "the live view answers", TimeSpan.FromSeconds(20));
            // Either the prompt to touch a control, or (if someone is holding the pad) the control they touched; never "not available".
            Assert.DoesNotContain("not available", Text(changed), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("disconnected", Text(changed), StringComparison.OrdinalIgnoreCase);

            // The view really read the device: at least one button chip and one axis row exist in the tree.
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => Text(e).StartsWith("Axis ", StringComparison.Ordinal)) ,
                "the live view lists the pad's axes", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void Guided_mapping_runs_on_the_real_controller_and_skipping_changes_nothing()
    {
        const string name = nameof(Guided_mapping_runs_on_the_real_controller_and_skipping_changes_nothing);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            if (!FindFirstController(app)) { Note(app, "NOT RUN: Windows lists no raw game controller on this PC."); return; }

            string aBefore = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_A_Index").AsTextBox().Text;
            string lxBefore = UiWait.ById(app.MainWindow, "GameDevices_RawMapping_LeftX_Index").AsTextBox().Text;

            UiWait.ById(app.MainWindow, "GameDevices_Wizard_Start_Button", ControlType.Button).AsButton().Invoke();
            var prompt = UiWait.ById(app.MainWindow, "GameDevices_Wizard_Prompt_Text", ControlType.Text);
            UiWait.For(() => Text(prompt).Contains("button A", StringComparison.Ordinal), "the first prompt asks for button A", TimeSpan.FromSeconds(20));

            // Skip all 16 steps. If the pad could not be read, the wizard would end early with "disconnected".
            for (int i = 0; i < 16; i++)
            {
                var skip = UiWait.ById(app.MainWindow, "GameDevices_Wizard_Skip_Button", ControlType.Button);
                skip.AsButton().Invoke();
                Thread.Sleep(150);
            }
            var status = UiWait.ById(app.MainWindow, "GameDevices_Raw_Status_Text", ControlType.Text);
            UiWait.For(() => Text(status).Contains("All steps done", StringComparison.Ordinal), "the guided flow finishes", TimeSpan.FromSeconds(20));

            // Skipping assigned nothing: the boxes keep their values.
            Assert.Equal(aBefore, UiWait.ById(app.MainWindow, "GameDevices_RawMapping_A_Index").AsTextBox().Text);
            Assert.Equal(lxBefore, UiWait.ById(app.MainWindow, "GameDevices_RawMapping_LeftX_Index").AsTextBox().Text);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void Real_display_modes_and_hidhide_status_are_read_without_changing_anything()
    {
        const string name = nameof(Real_display_modes_and_hidhide_status_are_read_without_changing_anything);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            UiWait.ById(app.MainWindow, "GameDevices_DisplayModes_Load_Button", ControlType.Button).AsButton().Invoke();
            var displayStatus = UiWait.ById(app.MainWindow, "GameDevices_Display_Status_Text", ControlType.Text);
            UiWait.For(() => Text(displayStatus).Length > 0 && !Text(displayStatus).StartsWith("Reading", StringComparison.Ordinal),
                "the display modes are read", TimeSpan.FromSeconds(30));
            var modes = UiWait.ById(app.MainWindow, "GameDevices_DisplayModes_ComboBox", ControlType.ComboBox).AsComboBox();
            Assert.True(modes.Items.Length > 0, "the real display lists selectable modes");
            // Nothing was applied: no pending change, so confirm and revert are not offered.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDevices_Display_Confirm_Button")));

            UiWait.ById(app.MainWindow, "GameDevices_HidHide_Status_Button", ControlType.Button).AsButton().Invoke();
            var raw = UiWait.ById(app.MainWindow, "GameDevices_Raw_Status_Text", ControlType.Text);
            UiWait.For(() => Text(raw).Contains("HidHide", StringComparison.Ordinal), "the HidHide check answers", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void The_installed_steam_library_appears_and_a_steam_card_answers_honestly_without_a_key()
    {
        const string name = nameof(The_installed_steam_library_appears_and_a_steam_card_answers_honestly_without_a_key);
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps");
        // 228980 is "Steamworks Common Redistributables", which PaRuX deliberately never lists as a game.
        if (!Directory.Exists(steam) || !Directory.EnumerateFiles(steam, "appmanifest_*.acf").Any(f => !f.EndsWith("appmanifest_228980.acf", StringComparison.OrdinalIgnoreCase)))
        {
            // Nothing to prove on a PC with no Steam game registered with Steam (on this PC the game folders under steamapps\common have no manifest).
            return;
        }
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            // Store scanning is opt-in: open the first-visit prompt from the empty library and start the scan (read-only).
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "GameHome_ImportGames_Button"));
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "GameLibrary_FirstRunScan_Start"));
            var status = UiWait.ById(app.MainWindow, "GameTitles_Status_Text", ControlType.Text);
            UiWait.For(() => System.Text.RegularExpressions.Regex.IsMatch(Text(status), @"^[1-9]\d* games"),
                "the real Steam titles are listed", TimeSpan.FromSeconds(90));

            // Open the first card that has a Steam installation: its achievements button must answer without a key, not crash or hang.
            UiWait.For(() =>
            {
                var tile = app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("GameTile_", StringComparison.Ordinal));
                if (tile is null) return false;
                tile.AsButton().Invoke();
                return app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null;
            }, "a game card opens", TimeSpan.FromSeconds(60));

            var button = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Achievements_Button"));
            if (button is null) { Note(app, "The first listed game has no Steam installation; the achievements button only shows for Steam titles."); return; }
            button.AsButton().Invoke();
            var summary = UiWait.ById(app.MainWindow, "GameDetail_Achievements_Summary", ControlType.Text);
            UiWait.For(() => Text(summary).Contains("Steam Web API key", StringComparison.OrdinalIgnoreCase), "an honest request for the key", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void The_real_eden_and_switch_rom_folder_offer_only_the_games_not_their_updates()
    {
        // Real: Eden and the owner's Switch ROM folder on this PC, the Emulators section, the scan and the import. Nothing is launched.
        const string name = nameof(The_real_eden_and_switch_rom_folder_offer_only_the_games_not_their_updates);
        string eden = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Eden-Windows-v0.0.3-rc2-amd64", "eden.exe");
        string roms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Switch Roms");
        if (!File.Exists(eden) || !Directory.Exists(roms)) return;
        // Independent of the product's own rule: a Switch base title has an id ending in 000 and no update or DLC tag.
        int baseGames = Directory.EnumerateFiles(roms, "*.nsp", SearchOption.TopDirectoryOnly).Count(f =>
        {
            string n = Path.GetFileName(f);
            var id = System.Text.RegularExpressions.Regex.Match(n, @"\[([0-9A-Fa-f]{16})\]");
            return id.Success && id.Groups[1].Value.EndsWith("000", StringComparison.Ordinal)
                && !System.Text.RegularExpressions.Regex.IsMatch(n, @"\[(UPD|DLC)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        });
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            GamingSectionsPage.SelectSection(app.MainWindow, "Emulators");
            Press(app, "Emulators_Detect_Button");
            var combo = UiWait.ById(app.MainWindow, "Emulators_Select_ComboBox").AsComboBox();
            DateTime last = DateTime.MinValue;
            UiWait.For(() =>
            {
                string selected = "";
                try { selected = combo.SelectedItem?.Name ?? ""; } catch { }
                if (selected.StartsWith("Eden", StringComparison.Ordinal) && !selected.StartsWith("Eden CLI", StringComparison.Ordinal)) return true;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(2)) { last = DateTime.UtcNow; try { combo.Focus(); FlaUI.Core.Input.Keyboard.Type("Eden"); } catch { } }
                return false;
            }, "Eden chosen", TimeSpan.FromSeconds(90));

            UiWait.ById(app.MainWindow, "Emulators_Exe_TextBox").AsTextBox().Text = eden;
            UiWait.ById(app.MainWindow, "Emulators_Folder_TextBox").AsTextBox().Text = roms;
            Press(app, "Emulators_SaveSetup_Button");
            Press(app, "Emulators_Scan_Button");
            var status = UiWait.ById(app.MainWindow, "Emulators_Status_Text", ControlType.Text);
            UiWait.For(() => Text(status).Contains(baseGames.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "the scan reports the games found", TimeSpan.FromSeconds(60));
            // An update file's title must never be offered as a game of its own.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByName("Mario Kart 8 Deluxe Booster Course")));
            Assert.Null(app.MainWindow.FindAllDescendants().FirstOrDefault(e => Text(e).Contains("[UPD]", StringComparison.Ordinal)));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XState { public uint Packet; public ushort Buttons; public byte Lt, Rt; public short Lx, Ly, Rx, Ry; }
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    private delegate uint GetStateFn(uint index, ref XState state);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XVib { public ushort Left, Right; }
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    private delegate uint SetStateFn(uint index, ref XVib vib);
    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);
    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void The_real_controller_reaches_a_game_as_an_xbox_pad_through_the_driverless_shim()
    {
        // Real: the 8BitDo pad, Windows.Gaming.Input, PaRuX's mapper, the shared memory, the shim file installed into a game folder by the
        // Core, and the shim DLL loaded from THAT folder and called the way a game calls XInput. Simulated: only the "game" (a folder
        // with an empty executable header) and the fact that nobody presses a button, so the pad is read at rest.
        const string name = nameof(The_real_controller_reaches_a_game_as_an_xbox_pad_through_the_driverless_shim);
        if (!Environment.Is64BitProcess) return;
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGames(app);
            if (!FindFirstController(app)) { Note(app, "NOT RUN: Windows lists no raw game controller on this PC."); return; }

            // The fake game: an x64 executable header and nothing else.
            string game = Path.Combine(app.DataRoot, "games", "fakegame");
            Directory.CreateDirectory(game);
            var exe = new byte[4096];
            exe[0] = (byte)'M'; exe[1] = (byte)'Z';
            BitConverter.GetBytes(128).CopyTo(exe, 0x3C);
            exe[128] = (byte)'P'; exe[129] = (byte)'E';
            BitConverter.GetBytes((ushort)0x8664).CopyTo(exe, 132);
            File.WriteAllBytes(Path.Combine(game, "fakegame.exe"), exe);

            // Map the pad's first four axes to the two sticks and save (the buttons keep their defaults).
            UiWait.ById(app.MainWindow, "GameDevices_RawMapping_LeftX_Index").AsTextBox().Text = "0";
            UiWait.ById(app.MainWindow, "GameDevices_RawMapping_LeftY_Index").AsTextBox().Text = "1";
            UiWait.ById(app.MainWindow, "GameDevices_RawMapping_RightX_Index").AsTextBox().Text = "2";
            UiWait.ById(app.MainWindow, "GameDevices_RawMapping_RightY_Index").AsTextBox().Text = "3";
            Press(app, "GameDevices_RawMapping_Save_Button");
            var rawStatus = UiWait.ById(app.MainWindow, "GameDevices_Raw_Status_Text", ControlType.Text);
            UiWait.For(() => Text(rawStatus).Contains("Mapping saved", StringComparison.Ordinal), "the mapping is saved", TimeSpan.FromSeconds(20));

            // Put the shim next to the game through the UI and check what landed there.
            UiWait.ById(app.MainWindow, "GameDevices_Shim_Folder_TextBox").AsTextBox().Text = game;
            Press(app, "GameDevices_Shim_Install_Button");
            var shimStatus = UiWait.ById(app.MainWindow, "GameDevices_Shim_Status_Text", ControlType.Text);
            UiWait.For(() => Text(shimStatus).StartsWith("Installed", StringComparison.Ordinal), "the shim is installed", TimeSpan.FromSeconds(60));
            foreach (string n in new[] { "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll" }) Assert.True(File.Exists(Path.Combine(game, n)), n);

            // Start the Xbox pad from the UI.
            Press(app, "GameDevices_Driverless_Start_Button");
            var padStatus = UiWait.ById(app.MainWindow, "GameDevices_Driverless_Status_Text", ControlType.Text);
            UiWait.For(() => Text(padStatus).Contains("is on", StringComparison.Ordinal), "the Xbox pad starts", TimeSpan.FromSeconds(30));
            // The panel says plainly whether this controller can vibrate for the game.
            Assert.True(Text(padStatus).Contains("vibration", StringComparison.OrdinalIgnoreCase), "the vibration support is stated");
            File.WriteAllText(Path.Combine(app.DataRoot, "driverless-status.txt"), Text(padStatus));

            // Load the shim from the game's own folder and call it like a game does.
            IntPtr module = LoadLibraryW(Path.Combine(game, "xinput1_4.dll"));
            Assert.NotEqual(IntPtr.Zero, module);
            var getState = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<GetStateFn>(GetProcAddress(module, "XInputGetState"));
            var state = new XState();
            uint result = 1;
            UiWait.For(() => (result = getState(0, ref state)) == 0, "the game reads slot 0 as connected", TimeSpan.FromSeconds(20));
            Assert.True(state.Packet > 0, "a packet number was published");
            Assert.InRange((int)state.Lt, 0, 255);
            Assert.InRange((int)state.Lx, short.MinValue, short.MaxValue);
            // At rest nobody is pressing a trigger.
            Assert.Equal(0, state.Lt);
            Assert.Equal(0, state.Rt);

            // A game's vibration request is accepted (a quarter of a second, then silence), whether or not this pad has a motor.
            var setState = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SetStateFn>(GetProcAddress(module, "XInputSetState"));
            var buzz = new XVib { Left = 20000, Right = 20000 };
            Assert.Equal(0u, setState(0, ref buzz));
            Thread.Sleep(400);
            var silence = new XVib();
            Assert.Equal(0u, setState(0, ref silence));

            // Stop: the shim forwards to Windows again (this PC's pad is not XInput, so "not connected" or a real pad).
            Press(app, "GameDevices_Driverless_Stop_Button");
            UiWait.For(() => { var after = new XState(); uint r = getState(0, ref after); return r == 1167 || r == 0; }, "pass-through after stop", TimeSpan.FromSeconds(20));

            // Remove it again through the UI: the game folder returns to its original files.
            Press(app, "GameDevices_Shim_Remove_Button");
            UiWait.For(() => Text(shimStatus).StartsWith("Removed", StringComparison.Ordinal), "the shim is removed", TimeSpan.FromSeconds(60));
            Assert.Equal(new[] { "fakegame.exe" }, Directory.EnumerateFiles(game).Select(Path.GetFileName).ToArray());
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
