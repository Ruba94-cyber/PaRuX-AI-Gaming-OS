using System.Net.NetworkInformation;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Playnite / Hydra style Games home: hero, filter rail with counts, keyboard movement between tiles, per-game save backup and
/// restore with a confirmed preview, off-by-default launch helpers, the optional IGDB credentials dialog and the Quick Resume
/// status line. Every game is a synthetic non-executable marker file added through the real form; launches are cancelled at the
/// consent dialog; the pre-seeded play history is written to the isolated PARUX_DATA_ROOT only. No test depends on UI language.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingHomeCompetitiveUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";

    private static AutomationElement Find(Func<AutomationElement?> find, string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = find()) != null, description);
        return element!;
    }

    private static AutomationElement ById(UiTestApplication app, string id) =>
        Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static AutomationElement? TryById(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    /// <summary>Collapsed WinUI elements are absent from the UIA tree; scrolled-off ones are present but offscreen, which still counts as shown.</summary>
    private static bool Shown(UiTestApplication app, string id) => TryById(app, id) != null;

    private static string TextOf(UiTestApplication app, string id) => ById(app, id).Properties.Name.ValueOrDefault ?? string.Empty;

    /// <summary>The count a rail entry announces after the last comma of its accessible name (language independent).</summary>
    private static int CountOf(UiTestApplication app, string id) =>
        int.Parse(TextOf(app, id)[(TextOf(app, id).LastIndexOf(',') + 1)..].Trim());

    /// <summary>Presses a button once its previous operation (e.g. a closing dialog) has re-enabled it.</summary>
    private static void PressWhenEnabled(UiTestApplication app, string id)
    {
        UiWait.For(() => TryById(app, id)?.Properties.IsEnabled.ValueOrDefault == true, id + " enabled");
        Press(app, ById(app, id));
    }

    private static void WaitForCoreIdle() =>
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));

    private static void OpenGamingHome(UiTestApplication app)
    {
        new InstallProgressPage(app).Invoke(ById(app, GamingTab));
        ById(app, "GameHome_Sort");
        var add = ById(app, "GameLibrary_Add_Button");
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
    }

    private static string KeyOf(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static void Press(UiTestApplication app, AutomationElement element) => new InstallProgressPage(app).Invoke(element);

    private static string AddGame(UiTestApplication app, string name)
    {
        string marker = Path.Combine(app.DataRoot, KeyOf(name) + ".exe");
        File.WriteAllText(marker, "fixture, not executable");
        var nameBox = UiWait.ById(app.MainWindow, "GameLibrary_Name_TextBox", ControlType.Edit).AsTextBox();
        var pathBox = UiWait.ById(app.MainWindow, "GameLibrary_Path_TextBox", ControlType.Edit).AsTextBox();
        nameBox.Text = name;
        pathBox.Text = marker;
        var add = UiWait.ById(app.MainWindow, "GameLibrary_Add_Button", ControlType.Button);
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle before adding");
        add.Patterns.Invoke.Pattern.Invoke();
        string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
        UiWait.For(() => File.Exists(library) && SharedFile.ReadAllText(library).Contains(name, StringComparison.Ordinal), name + " persisted");
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle after adding");
        return marker;
    }

    /// <summary>Writes per-title state (play history, favourite) the way the Core's own store does; the Core re-reads it on a refresh.</summary>
    private static void SeedTitleState(UiTestApplication app, params (string Key, bool Favorite, long Seconds, DateTime? LastPlayed)[] titles)
    {
        var map = titles.ToDictionary(t => t.Key, t => new
        {
            Favorite = t.Favorite, Hidden = false, AddedUtc = (DateTime?)DateTime.UtcNow.AddDays(-40), Tags = Array.Empty<string>(),
            Collections = Array.Empty<string>(), PlaySeconds = t.Seconds, LastPlayedUtc = t.LastPlayed, DefaultInstallationId = (string?)null
        });
        string path = Path.Combine(app.DataRoot, "server", "game-titles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Titles = map }));
    }

    private static void RefreshLibrary(UiTestApplication app) => Press(app, ById(app, "GameTitles_Refresh_Button"));

    private static void CancelDialog(UiTestApplication app) =>
        Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")), "dialog cancel").Patterns.Invoke.Pattern.Invoke();

    private static void ConfirmDialog(UiTestApplication app) =>
        Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")), "dialog confirm").Patterns.Invoke.Pattern.Invoke();

    private static string DialogText(UiTestApplication app) =>
        string.Join("\n", app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name ?? string.Empty));

    private static string? FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault;

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Offline_wishlist_dialog_is_accessible_and_writes_nothing_without_an_owner_action()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Offline_wishlist_dialog_is_accessible_and_writes_nothing_without_an_owner_action));
        try
        {
            OpenGamingHome(app);
            string wishlistPath = Path.Combine(app.DataRoot, "server", "game-wishlist.json");
            Assert.False(File.Exists(wishlistPath), "no wishlist metadata created before consent");

            PressWhenEnabled(app, "GameHome_Wishlist_Button");
            ById(app, "GameHome_Wishlist_Select");
            Assert.False(ById(app, "GameHome_Wishlist_Quote").Properties.IsEnabled.ValueOrDefault,
                "cannot contact Steam before an explicit saved-game selection");
            string displayed = TextOf(app, "GameHome_Wishlist_Status");
            Assert.False(string.IsNullOrWhiteSpace(displayed), "empty-list state has accessible status");

            Assert.False(File.Exists(wishlistPath), "opening a read-only dialog cannot persist or enable price watching");
            CancelDialog(app);
            UiWait.For(() => ById(app, "GameHome_Wishlist_Button").Properties.IsEnabled.ValueOrDefault,
                "wishlist button re-enabled after closing the dialog");
            Assert.False(File.Exists(wishlistPath));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Offline_wishlist_dialog_is_accessible_and_writes_nothing_without_an_owner_action), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Hero_and_filter_rail_follow_play_history_and_filter_the_library()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Hero_and_filter_rail_follow_play_history_and_filter_the_library));
        try
        {
            OpenGamingHome(app);
            Assert.False(Shown(app, "GameHome_Hero"), "no hero without play history");

            string[] names = { "Rail Alpha", "Rail Beta", "Rail Gamma" };
            string alphaMarker = AddGame(app, names[0]);
            AddGame(app, names[1]);
            AddGame(app, names[2]);
            SeedTitleState(app,
                (KeyOf(names[0]), true, 7200, DateTime.UtcNow.AddDays(-1)),
                (KeyOf(names[1]), false, 600, DateTime.UtcNow.AddDays(-20)),
                (KeyOf(names[2]), false, 0, null));
            RefreshLibrary(app);

            UiWait.For(() => Shown(app, "GameHome_Hero"), "hero appears once a game has been played");
            Assert.Equal(names[0], TextOf(app, "GameHome_Hero_Title"));
            ById(app, "GameHome_Hero_Details");

            // Rail counts are part of each entry's accessible name; assert the numbers, not the localized words.
            Assert.True(CountOf(app, "GameSide_All") >= 3, "All counts at least the three synthetic games");
            Assert.Equal(1, CountOf(app, "GameSide_Favorites"));
            Assert.Equal(2, CountOf(app, "GameSide_Recent"));
            Assert.Equal(3, CountOf(app, "GameSide_Store_manual"));

            Press(app, ById(app, "GameSide_Favorites"));
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[1])) == null && TryById(app, "GameTile_" + KeyOf(names[0])) != null,
                "favourites filter leaves only Rail Alpha");

            Press(app, ById(app, "GameSide_All"));
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[2])) != null && TryById(app, "GameTile_" + KeyOf(names[1])) != null,
                "All games restores the whole library");

            Press(app, ById(app, "GameSide_Recent"));
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[2])) == null && TryById(app, "GameTile_" + KeyOf(names[1])) != null,
                "recently played hides the never-played game");
            Press(app, ById(app, "GameSide_All"));

            // The rail drives the same state as the toolbar, so the installed-state entry narrows the list too.
            Press(app, ById(app, "GameSide_NotInstalled"));
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[0])) == null, "not-installed filter hides installed games");
            Press(app, ById(app, "GameSide_All"));
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[0])) != null, "library back");

            // Hero Play uses the same consent flow as every tile; cancelling starts nothing.
            Press(app, ById(app, "GameHome_Hero_Play"));
            CancelDialog(app);
            Assert.Equal("fixture, not executable", File.ReadAllText(alphaMarker));

            Press(app, ById(app, "GameHome_Hero_Details"));
            Assert.Equal(names[0], TextOf(app, "GameDetail_Title"));
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Hero_and_filter_rail_follow_play_history_and_filter_the_library), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Arrow_home_and_end_keys_move_focus_between_game_tiles()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Arrow_home_and_end_keys_move_focus_between_game_tiles));
        try
        {
            OpenGamingHome(app);
            string[] names = { "Keyfx Alpha", "Keyfx Beta", "Keyfx Gamma" };
            foreach (string name in names) AddGame(app, name);
            // This PC may have real store games; narrow the library to the three synthetic ones.
            ById(app, "GameTitles_Search_TextBox").AsTextBox().Text = "Keyfx";
            UiWait.For(() => TryById(app, "GameTile_" + KeyOf(names[0])) != null && TryById(app, "GameTile_" + KeyOf("Unrelated Real Game")) == null
                && TryById(app, "GameTile_steamworkscommonredistributables") == null, "search narrowed to the synthetic games");
            string a = "GameTile_" + KeyOf(names[0]), b = "GameTile_" + KeyOf(names[1]), c = "GameTile_" + KeyOf(names[2]);
            ById(app, a); ById(app, b); ById(app, c);

            ById(app, a).Focus();
            UiWait.For(() => FocusedId(app) == a, "first tile focused");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.RIGHT), () => FocusedId(app) == b, "Right arrow moves to the next tile");
            try { UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.END), () => FocusedId(app) == c, "End jumps to the last tile"); }
            catch (TimeoutException) { throw new TimeoutException("End did not reach the last tile; focus stayed on " + FocusedId(app)); }
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.HOME), () => FocusedId(app) == a, "Home jumps to the first tile");
            // Left of the first tile is the end of the grid: the grid hands over to the filter rail beside it (spatial focus) instead of wrapping.
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.LEFT), () => (FocusedId(app) ?? "").StartsWith("GameSide_", StringComparison.Ordinal),
                "Left from the first tile enters the filter rail");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Arrow_home_and_end_keys_move_focus_between_game_tiles), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Quick_resume_status_line_is_shown_on_the_games_home()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Quick_resume_status_line_is_shown_on_the_games_home));
        try
        {
            OpenGamingHome(app);
            UiWait.For(() => TextOf(app, "GameHome_QuickResume_Summary").StartsWith("Quick Resume", StringComparison.Ordinal),
                "Quick Resume summary line populated");
            Assert.True(ById(app, "GameHome_QuickResume_Manage").BoundingRectangle.Height >= 44);
            // The full panel stays on the page for pausing and resuming; its recovery list is empty (so not exposed) on a fresh data root.
            ById(app, "QuickResume_Heading");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Quick_resume_status_line_is_shown_on_the_games_home), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Per_game_save_backup_and_confirmed_restore_never_touch_files_without_consent()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Per_game_save_backup_and_confirmed_restore_never_touch_files_without_consent));
        try
        {
            OpenGamingHome(app);
            const string name = "Save Fixture";
            AddGame(app, name);
            string key = KeyOf(name);
            string saves = Path.Combine(app.DataRoot, "synthetic-saves");
            Directory.CreateDirectory(saves);
            string slot = Path.Combine(saves, "slot1.sav");
            File.WriteAllText(slot, "synthetic save v1");

            Press(app, ById(app, "GameTile_" + key));
            UiWait.ExpandById(app.MainWindow, "GameDetail_Saves_Expander_" + key);
            string suffix = "_" + key;
            UiWait.ById(app.MainWindow, "GameSaves_Source" + suffix, ControlType.Edit).AsTextBox().Text = saves;

            // Backup asks first; cancelling creates nothing.
            PressWhenEnabled(app, "GameSaves_Create" + suffix);
            CancelDialog(app);
            string backups = Path.Combine(app.DataRoot, "server", "game-save-backups");
            Assert.False(Directory.Exists(backups) && Directory.GetFiles(backups, "save-*.zip").Length > 0, "no archive before confirmation");

            PressWhenEnabled(app, "GameSaves_Create" + suffix);
            ConfirmDialog(app);
            UiWait.For(() => Directory.Exists(backups) && Directory.GetFiles(backups, "save-*.zip").Length == 1, "archive created after confirmation");
            Assert.Equal("synthetic save v1", File.ReadAllText(slot));

            // The game changes its save; the user restores the backup.
            File.WriteAllText(slot, "synthetic save v2 (newer)");
            Press(app, ById(app, "GameSaves_Refresh" + suffix));
            AutomationElement? restore = null;
            UiWait.For(() => (restore = FindRestore(app, suffix)) != null, "restore button for the archive");

            // Cancelling the preview leaves the newer save untouched.
            Press(app, restore!);
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")), "restore preview dialog");
            CancelDialog(app);
            Assert.Equal("synthetic save v2 (newer)", File.ReadAllText(slot));

            // Confirming restores, after the Core made its automatic pre-restore copy.
            UiWait.For(() => TryById(app, "GameSaves_Create" + suffix)?.Properties.IsEnabled.ValueOrDefault == true, "previous operation finished");
            Press(app, FindRestore(app, suffix)!);
            ConfirmDialog(app);
            UiWait.For(() => File.ReadAllText(slot) == "synthetic save v1", "save restored from the backup");
            UiWait.For(() => Directory.GetFiles(backups, "save-*.zip").Length == 2, "pre-restore copy of the newer save kept as a backup");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Per_game_save_backup_and_confirmed_restore_never_touch_files_without_consent), ex); throw; }
    }

    private static AutomationElement? FindRestore(UiTestApplication app, string suffix) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => (b.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("GameSaves_Restore_" + suffix, StringComparison.Ordinal));

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Launch_helpers_are_off_by_default_and_every_change_is_confirmed_and_validated()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Launch_helpers_are_off_by_default_and_every_change_is_confirmed_and_validated));
        try
        {
            OpenGamingHome(app);
            const string name = "Hook Fixture";
            AddGame(app, name);
            string key = KeyOf(name);
            string hooksFile = Path.Combine(app.DataRoot, "server", "game-hooks.json");

            Press(app, ById(app, "GameTile_" + key));
            UiWait.ExpandById(app.MainWindow, "GameDetail_Hooks_Expander_" + key);
            var toggle = ById(app, "GameHooks_Enabled_" + key);
            UiWait.For(() => toggle.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault == ToggleState.Off, "switch shown off");
            Assert.False(File.Exists(hooksFile), "nothing is persisted until the user acts");

            // A script is refused in the form itself: no dialog, nothing saved.
            ById(app, "GameHooks_PrePath_" + key).AsTextBox().Text = Path.Combine(app.DataRoot, "helper.bat");
            Press(app, ById(app, "GameHooks_Save_" + key));
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")));
            Assert.False(File.Exists(hooksFile), "a refused helper is not saved");

            // Allowing helpers is confirmed; cancelling keeps them off.
            Press(app, toggle);
            CancelDialog(app);
            UiWait.For(() => toggle.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault == ToggleState.Off, "switch back to off after cancel");
            Assert.False(File.Exists(hooksFile) && File.ReadAllText(hooksFile).Contains("\"Enabled\": true", StringComparison.Ordinal));

            Press(app, toggle);
            ConfirmDialog(app);
            UiWait.For(() => File.Exists(hooksFile) && File.ReadAllText(hooksFile).Contains("\"Enabled\": true", StringComparison.Ordinal), "switch persisted");

            // A real local .exe (cmd.exe from the system folder, never started by this test) is saved only after confirmation.
            string exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            ById(app, "GameHooks_PrePath_" + key).AsTextBox().Text = exe;
            ById(app, "GameHooks_PreArgs_" + key).AsTextBox().Text = "/c exit 0";
            Press(app, ById(app, "GameHooks_Save_" + key));
            CancelDialog(app);
            Assert.DoesNotContain("cmd.exe", File.ReadAllText(hooksFile), StringComparison.OrdinalIgnoreCase);
            Press(app, ById(app, "GameHooks_Save_" + key));
            ConfirmDialog(app);
            UiWait.For(() => File.ReadAllText(hooksFile).Contains("cmd.exe", StringComparison.OrdinalIgnoreCase), "helper saved after confirmation");

            Press(app, ById(app, "GameHooks_Clear_" + key));
            UiWait.For(() => !File.ReadAllText(hooksFile).Contains("cmd.exe", StringComparison.OrdinalIgnoreCase), "helper removed");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Launch_helpers_are_off_by_default_and_every_change_is_confirmed_and_validated), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Igdb_credentials_are_validated_stored_encrypted_and_removable_without_any_lookup()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Igdb_credentials_are_validated_stored_encrypted_and_removable_without_any_lookup));
        try
        {
            OpenGamingHome(app);
            Assert.False(ById(app, "GameHome_Igdb_Button").Properties.IsEnabled.ValueOrDefault, "IGDB stays off while Covers & info is off");

            // The library is empty, so accepting the consent does not look anything up.
            Press(app, ById(app, "GameHome_Metadata_Toggle"));
            ConfirmDialog(app);
            UiWait.For(() => ById(app, "GameHome_Igdb_Button").Properties.IsEnabled.ValueOrDefault, "IGDB button enabled after consent");

            string secretFile = Path.Combine(app.DataRoot, "secrets", "igdb.key");
            const string clientId = "syntheticclientid12", secret = "syntheticsecretvalue34";

            Press(app, ById(app, "GameHome_Igdb_Button"));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameHome_Igdb_ClientId")), "IGDB dialog");
            ById(app, "GameHome_Igdb_ClientId").AsTextBox().Text = "not valid!";
            ById(app, "GameHome_Igdb_ClientSecret").Focus();
            Keyboard.Type("x");
            ConfirmDialog(app);
            UiWait.For(() => Shown(app, "GameHome_Igdb_Error"), "malformed credentials are refused in the dialog");
            Assert.False(File.Exists(secretFile));

            ById(app, "GameHome_Igdb_ClientId").AsTextBox().Text = clientId;
            var secretBox = ById(app, "GameHome_Igdb_ClientSecret");
            secretBox.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(secret);
            ConfirmDialog(app);
            UiWait.For(() => File.Exists(secretFile), "credentials stored");
            string stored = File.ReadAllText(secretFile);
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(clientId, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(app.SettingsPath), StringComparison.Ordinal);

            Press(app, ById(app, "GameHome_Igdb_Button"));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SecondaryButton")), "remove button")
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !File.Exists(secretFile), "credentials removed");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Igdb_credentials_are_validated_stored_encrypted_and_removable_without_any_lookup), ex); throw; }
    }
}
