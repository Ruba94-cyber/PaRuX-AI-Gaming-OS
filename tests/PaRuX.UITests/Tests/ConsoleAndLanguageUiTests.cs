using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using System.Text.Json;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Console / TV keyboard navigation and Escape exit, UI language consistency (the app language follows the
/// process UI culture; there is no in-app IT/EN switch), and persistence of the Gaming display role and
/// Route-to-TV preference. No TV is required: no game is started, no display setting is changed and no
/// display is ever chosen as the Console target.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConsoleAndLanguageUiTests
{
    private const string ConsoleButtonId = "MainWindow_ConsoleMode_Button";
    private const string GamesNavId = "Navigation_GameLibrary_Item";
    private const string ConsoleHomeFocusId = "GameLibrary_Gaming_Refresh_Button";
    private const string RouteToggleId = "Settings_ConsoleDisplays_RouteGames_Toggle";
    private const string DisplayRefreshId = "Settings_ConsoleDisplays_Refresh_Button";
    private const string GamingCategory = "Gaming & AI Sharing";

    private static readonly string[] WatchedSettingKeys =
    [
        "Theme", "StudioModeEnabled", "RouteVerifiedWindowedGamesToTv", "AutoShowConsoleAfterVerifiedGameExit",
        "ConsoleTargetDisplayId", "ConsoleDisplays"
    ];

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Gaming")]
    public void Console_arrow_keys_move_focus_between_controls_and_stay_in_console_mode()
    {
        const string testName = nameof(Console_arrow_keys_move_focus_between_controls_and_stay_in_console_mode);
        using var app = new UiTestApplication(testName);
        try
        {
            string desktopName = EnterConsole(app);
            var keys = new KeyboardTraversalPage(app.MainWindow, app);

            var home = UiWait.ById(app.MainWindow, ConsoleHomeFocusId, ControlType.Button);
            UiWait.For(() => home.Properties.HasKeyboardFocus.ValueOrDefault,
                "Console Home focus lands on the Gaming refresh control");

            // Directional focus must move off the starting control for at least one of the D-pad equivalents.
            string? movedTo = null;
            string? startSignature = FocusSignature(keys);
            foreach (VirtualKeyShort key in new[] { VirtualKeyShort.DOWN, VirtualKeyShort.RIGHT, VirtualKeyShort.TAB })
            {
                keys.Tap(key, home);
                try
                {
                    UiWait.For(() =>
                    {
                        string? signature = FocusSignature(keys);
                        if (string.IsNullOrEmpty(signature) || signature == startSignature) return false;
                        movedTo = signature;
                        return true;
                    }, key + " moves keyboard focus away from the Console Home control", TimeSpan.FromSeconds(4));
                    break;
                }
                catch (TimeoutException) { }
            }
            Assert.False(string.IsNullOrEmpty(movedTo), "None of Down, Right or Tab moved focus between Console controls.");

            // Navigating with the keyboard never leaves Console mode or the Games workspace.
            Assert.NotEqual(desktopName, UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name);
            Assert.NotNull(UiWait.ById(app.MainWindow, ConsoleHomeFocusId, ControlType.Button));
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox", ControlType.Edit));

            // The opposite direction keeps focus on a real on-screen element of the window.
            keys.Tap(VirtualKeyShort.UP);
            keys.Tap(VirtualKeyShort.LEFT);
            UiWait.For(() => !string.IsNullOrEmpty(FocusedAutomationId(keys)),
                "focus remains on an identifiable control after reverse navigation");
            Assert.NotEqual(desktopName, UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Gaming")]
    public void Escape_exits_console_to_desktop_without_changing_persisted_settings()
    {
        const string testName = nameof(Escape_exits_console_to_desktop_without_changing_persisted_settings);
        using var app = new UiTestApplication(testName);
        try
        {
            var before = ReadWatchedSettings(app.SettingsPath);

            string desktopName = EnterConsole(app);
            var keys = new KeyboardTraversalPage(app.MainWindow, app);
            string consoleName = UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name;
            Assert.NotEqual(desktopName, consoleName);

            UiWait.InputUntil(
                () => keys.Tap(VirtualKeyShort.ESCAPE),
                () => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name == desktopName,
                "Escape returns from Console mode to the desktop presenter");

            // Desktop-only editing surfaces are back and Games stays the active workspace.
            Assert.NotNull(UiWait.ById(app.MainWindow, "GameLibrary_Name_TextBox", ControlType.Edit));
            var gamesItem = UiWait.ById(app.MainWindow, GamesNavId, ControlType.ListItem);
            UiWait.For(() => gamesItem.Patterns.SelectionItem.Pattern.IsSelected,
                "Games workspace remains selected after leaving Console");

            // Leaving Console is a view change only: no persisted preference may be written or altered.
            Assert.Equal(before, ReadWatchedSettings(app.SettingsPath));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Gaming")]
    public void Escape_from_console_returns_keyboard_focus_to_the_console_mode_button()
    {
        const string testName = nameof(Escape_from_console_returns_keyboard_focus_to_the_console_mode_button);
        using var app = new UiTestApplication(testName);
        try
        {
            string desktopName = EnterConsole(app);
            var keys = new KeyboardTraversalPage(app.MainWindow, app);

            UiWait.InputUntil(
                () => keys.Tap(VirtualKeyShort.ESCAPE),
                () => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name == desktopName,
                "Escape exits Console mode");

            UiWait.For(() => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button)
                    .Properties.HasKeyboardFocus.ValueOrDefault,
                "keyboard focus returns to the Console / TV button after Escape");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Localization"), Trait("Category", "Persistence")]
    public void Games_and_console_labels_use_one_consistent_language_that_survives_restart()
    {
        // The app language is the process UI culture (it/en); the UI exposes no switch, so this verifies
        // that every Games / Console label follows the same language and that a restart does not change it.
        const string testName = nameof(Games_and_console_labels_use_one_consistent_language_that_survives_restart);
        using var initial = new UiTestApplication(testName);
        bool italian = AssertGamesAndConsoleLabels(initial, expectedItalian: null);

        using var restarted = initial.Restart("language-labels-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            AssertGamesAndConsoleLabels(restarted, expectedItalian: italian);
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("language-labels-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Settings"), Trait("Category", "Persistence")]
    public void Route_to_TV_toggle_persists_across_restart_without_any_TV_connected()
    {
        const string testName = nameof(Route_to_TV_toggle_persists_across_restart_without_any_TV_connected);
        using var initial = new UiTestApplication(testName);
        OpenGamingDisplaySettings(initial);

        var route = UiWait.ById(initial.MainWindow, RouteToggleId, ControlType.Button);
        UiWait.For(() => ToggleOf(route) == ToggleState.On, "Route-to-TV defaults to on");
        route.Patterns.Toggle.Pattern.Toggle();
        UiWait.For(() => ToggleOf(route) == ToggleState.Off, "Route-to-TV toggled off in the UI");
        UiWait.For(() => ReadBool(initial.SettingsPath, "RouteVerifiedWindowedGamesToTv") == false,
            "RouteVerifiedWindowedGamesToTv=false persisted before restart");

        using var restarted = initial.Restart("route-to-tv-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            Assert.Equal(false, ReadBool(restarted.SettingsPath, "RouteVerifiedWindowedGamesToTv"));
            Assert.Equal(string.Empty, ReadString(restarted.SettingsPath, "ConsoleTargetDisplayId") ?? string.Empty);

            OpenGamingDisplaySettings(restarted);
            var routeAfter = UiWait.ById(restarted.MainWindow, RouteToggleId, ControlType.Button);
            UiWait.For(() => ToggleOf(routeAfter) == ToggleState.Off,
                "Route-to-TV toggle restored to off after restart");

            // The opt-in auto-return switch is independent and stays at its default.
            var autoReturn = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplays_AutoReturn_Toggle", ControlType.Button);
            Assert.Equal(ToggleState.Off, ToggleOf(autoReturn));

            routeAfter.Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => ReadBool(restarted.SettingsPath, "RouteVerifiedWindowedGamesToTv") == true,
                "RouteVerifiedWindowedGamesToTv=true persisted when turned back on");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("route-to-tv-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Settings"), Trait("Category", "Persistence")]
    public void Gaming_display_role_persists_across_restart_and_never_selects_a_console_target()
    {
        const string testName = nameof(Gaming_display_role_persists_across_restart_and_never_selects_a_console_target);
        using var initial = new UiTestApplication(testName);
        OpenGamingDisplaySettings(initial);

        AutomationElement? roleCombo = null;
        try
        {
            UiWait.For(() =>
            {
                roleCombo = initial.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("Settings_ConsoleDisplay_0_Role").And(cf.ByControlType(ControlType.ComboBox)));
                return roleCombo != null;
            }, "first detected display role selector", TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // The local Core reported no usable display topology: there is nothing to classify. The honest
            // outcome is that no assignment or target was written.
            Assert.Null(ReadProperty(initial.SettingsPath, "ConsoleDisplays"));
            Assert.Equal(string.Empty, ReadString(initial.SettingsPath, "ConsoleTargetDisplayId") ?? string.Empty);
            return;
        }

        try
        {
            AutomationElement? tvItem = null;
            UiWait.For(() =>
            {
                // Display detection may repopulate the list; re-open the drop-down on every attempt.
                try { roleCombo!.Patterns.ExpandCollapse.Pattern.Expand(); } catch (Exception) { }
                tvItem = initial.FindRawDescendant(initial.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                    && string.Equals(element.Properties.Name.ValueOrDefault, "TV", StringComparison.Ordinal));
                return tvItem != null;
            }, "TV option in the display type ComboBox");
            tvItem!.Patterns.SelectionItem.Pattern.Select();

            UiWait.For(() => HasTvAssignment(initial.SettingsPath), "TV display role persisted to settings.json");
            // Classifying a display as TV must not also select it as the Console destination.
            Assert.Equal(string.Empty, ReadString(initial.SettingsPath, "ConsoleTargetDisplayId") ?? string.Empty);
        }
        catch (Exception ex)
        {
            initial.CaptureFailure(testName, ex);
            throw;
        }

        using var restarted = initial.Restart("display-role-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            Assert.True(HasTvAssignment(restarted.SettingsPath), "TV display role must survive restart.");
            Assert.Equal(string.Empty, ReadString(restarted.SettingsPath, "ConsoleTargetDisplayId") ?? string.Empty);

            OpenGamingDisplaySettings(restarted);
            var comboAfter = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplay_0_Role", ControlType.ComboBox);
            // Read the selection directly: display detection may still be repopulating the drop-down list.
            UiWait.For(() =>
            {
                var current = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplay_0_Role", ControlType.ComboBox);
                var selected = current.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault;
                return selected != null && selected.Length > 0
                    && string.Equals(selected[0].Properties.Name.ValueOrDefault, "TV", StringComparison.Ordinal);
            }, "the persisted TV role is selected for the display after restart");

            // Entering Console with a TV classified but none chosen opens on the current screen: no blocking dialog.
            var button = UiWait.ById(restarted.MainWindow, ConsoleButtonId, ControlType.Button);
            string desktopName = button.Name;
            button.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(restarted.MainWindow, ConsoleButtonId, ControlType.Button).Name != desktopName,
                "Console opens on the current screen when no TV target is chosen");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("display-role-restart", ex);
            throw;
        }
    }

    private static string EnterConsole(UiTestApplication app)
    {
        var button = UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button);
        string desktopName = button.Name;
        Assert.False(string.IsNullOrWhiteSpace(desktopName));
        button.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name != desktopName,
            "Console mode entered");
        UiWait.ById(app.MainWindow, ConsoleHomeFocusId, ControlType.Button);
        return desktopName;
    }

    private static bool AssertGamesAndConsoleLabels(UiTestApplication app, bool? expectedItalian)
    {
        var games = UiWait.ById(app.MainWindow, GamesNavId, ControlType.ListItem);
        string gamesName = games.Name;
        Assert.Contains(gamesName, new[] { "Games", "Giochi" });
        bool italian = gamesName == "Giochi";
        if (expectedItalian.HasValue)
            Assert.Equal(expectedItalian.Value, italian);

        var button = UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button);
        string desktopName = button.Name;
        Assert.Equal(italian ? "Apri la modalità Console TV" : "Enter Console TV mode", desktopName);

        button.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name != desktopName,
            "Console mode entered for label check");
        Assert.Equal(italian ? "Esci dalla modalità Console" : "Exit Console mode",
            UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name);
        // The Games navigation label is unaffected by the Console presenter.
        Assert.Equal(gamesName, UiWait.ById(app.MainWindow, GamesNavId, ControlType.ListItem).Name);

        UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => UiWait.ById(app.MainWindow, ConsoleButtonId, ControlType.Button).Name == desktopName,
            "Console mode left for label check");
        return italian;
    }

    private static void OpenGamingDisplaySettings(UiTestApplication app)
    {
        new MainWindowPage(app.MainWindow).OpenSettings();
        new SettingsCategoriesPage(app.MainWindow).SelectCategory(GamingCategory);
        var refresh = UiWait.ById(app.MainWindow, DisplayRefreshId, ControlType.Button);
        // Refresh initializes the persisted toggles from settings before user edits are written back.
        UiWait.For(() => refresh.Properties.IsEnabled.ValueOrDefault, "display refresh enabled");
        refresh.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => UiWait.ById(app.MainWindow, DisplayRefreshId, ControlType.Button).IsEnabled,
            "display detection finished");
    }

    private static ToggleState? ToggleOf(AutomationElement element) =>
        element.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault;

    private static string? FocusedAutomationId(KeyboardTraversalPage keys) =>
        keys.GetFocusedElementSafe()?.Properties.AutomationId.ValueOrDefault;

    /// <summary>Identity of the focused element that also works for controls without an AutomationId.</summary>
    private static string? FocusSignature(KeyboardTraversalPage keys)
    {
        var element = keys.GetFocusedElementSafe();
        if (element == null) return null;
        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        return $"{element.Properties.AutomationId.ValueOrDefault}|{element.Properties.Name.ValueOrDefault}|" +
               $"{element.Properties.ControlType.ValueOrDefault}|{bounds.X:0},{bounds.Y:0}";
    }

    private static JsonDocument OpenSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static string? ReadProperty(string path, string name)
    {
        using var settings = OpenSettings(path);
        return settings.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : null;
    }

    private static bool? ReadBool(string path, string name)
    {
        using var settings = OpenSettings(path);
        if (!settings.RootElement.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadString(string path, string name)
    {
        using var settings = OpenSettings(path);
        return settings.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private static Dictionary<string, string?> ReadWatchedSettings(string path)
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (string key in WatchedSettingKeys)
            snapshot[key] = ReadProperty(path, key);
        return snapshot;
    }

    private static bool HasTvAssignment(string path)
    {
        using var settings = OpenSettings(path);
        if (!settings.RootElement.TryGetProperty("ConsoleDisplays", out var list)
            || list.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("Role", out var role)
                && string.Equals(role.GetString(), "tv", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
