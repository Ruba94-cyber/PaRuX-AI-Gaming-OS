using System.Net.NetworkInformation;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Gaming home (Continue shelf, tile library, toolbar, detail panel, multi-select). Fixtures are non-executable
/// files added through the real library form; every launch is cancelled at the consent dialog, and no game file
/// may ever be modified or deleted.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingHomeUiTests
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

    private static void WaitForCoreIdle()
    {
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));
    }

    private static void OpenGamingHome(UiTestApplication app)
    {
        var page = new InstallProgressPage(app);
        page.Invoke(ById(app, GamingTab));
        ById(app, "GameHome_Sort");
        var add = ById(app, "GameLibrary_Add_Button");
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
    }

    /// <summary>Title key of a tile: the normalised game name (letters and digits, lower case).</summary>
    private static string KeyOf(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

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
        UiWait.For(() => File.Exists(library) && File.ReadAllText(library).Contains(name, StringComparison.Ordinal), name + " persisted");
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library idle after adding");
        return marker;
    }

    private static string EntryId(UiTestApplication app, string name)
    {
        string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
        using var document = JsonDocument.Parse(File.ReadAllText(library));
        foreach (var game in document.RootElement.GetProperty("Games").EnumerateArray())
            if (game.GetProperty("Name").GetString() == name) return game.GetProperty("Id").GetString()!;
        throw new InvalidOperationException("Entry not found: " + name);
    }

    private static void Press(UiTestApplication app, AutomationElement element) => new InstallProgressPage(app).Invoke(element);

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Cross_game_AI_mod_studio_opens_and_cancels_without_generating_assets()
    {
        const string name = nameof(Cross_game_AI_mod_studio_opens_and_cancels_without_generating_assets);
        WaitForCoreIdle();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGamingHome(app);
            Press(app, ById(app, "GameHome_AssetStudio_Open"));
            foreach (string id in new[]
                     {
                         "GameAssetStudio_GameName", "GameAssetStudio_Source",
                         "GameAssetStudio_Browse", "GameAssetStudio_RelativePath",
                         "GameAssetStudio_Prompt", "GameAssetStudio_OfflineConsent"
                     })
                Assert.NotNull(ById(app, id));
            Find(() => app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("CloseButton")), "close local asset editor without generation")
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("GameAssetStudio_Prompt")) is null,
                "cross-game asset editor closed");

            // Read-only inventory can open and close when no ZIPs were
            // installed; no loader, model or filesystem installation is run.
            Press(app, ById(app, "GameHome_InstalledMods_Open"));
            Find(() => app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("CloseButton")), "close empty installed mod inventory")
                .Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void External_library_preview_and_saved_title_manager_are_accessible_without_importing()
    {
        const string name = nameof(External_library_preview_and_saved_title_manager_are_accessible_without_importing);
        WaitForCoreIdle();
        using var app = new UiTestApplication(name);
        try
        {
            OpenGamingHome(app);
            Press(app, ById(app, "GameHome_ExternalLibraryPreview_Button"));
            Assert.NotNull(ById(app, "GameHome_ExternalLibraryProvider"));
            Assert.NotNull(ById(app, "GameHome_ExternalLibraryJson"));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")),
                "close external import preview without any data").Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => TryById(app, "GameHome_ExternalLibraryJson") is null,
                "preview cancelled");
            Press(app, ById(app, "GameHome_ExternalLibrarySaved_Button"));
            Assert.NotNull(ById(app, "GameHome_ExternalLibrarySaved_List"));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")),
                "close saved title manager").Patterns.Invoke.Pattern.Invoke();
            string imported = Path.Combine(app.DataRoot, "server", "external-game-titles.json");
            Assert.False(File.Exists(imported)); // inspections do not persist metadata
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Home_exposes_named_toolbar_controls_with_touch_sized_targets()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Home_exposes_named_toolbar_controls_with_touch_sized_targets));
        try
        {
            OpenGamingHome(app);
            foreach (string id in new[]
                     {
                         "GameTitles_Search_TextBox", "GameHome_Sort", "GameHome_SourceFilter", "GameHome_PlatformFilter",
                         "GameHome_InstalledFilter", "GameHome_TagFilter", "GameTitles_Collection_Filter", "GameTitles_Collection_Apply",
                         "GameTitles_FavoritesOnly_Toggle", "GameHome_ShowHidden", "GameHome_LayoutToggle", "GameHome_SelectMode",
                         "GameTitles_Refresh_Button",
                         "GameHome_ExternalLibraryPreview_Button", "GameHome_ExternalLibrarySaved_Button"
                     })
            {
                var element = ById(app, id);
                Assert.False(string.IsNullOrWhiteSpace(element.Name), id + " has an accessible name");
                // The toolbar now sits beside the filter rail and may wrap below the fold: bring it into view before measuring.
                try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch (Exception) { /* best effort */ }
                UiWait.For(() => element.BoundingRectangle.Height >= 44, id + " is at least 44px tall (was " + element.BoundingRectangle.Height + ")");
            }

            // Asset Mod Studio is first-class in Gaming Home, NOT tied to
            // Minecraft or to a suspended Quick Resume process.
            var assetStudio = ById(app, "GameHome_AssetStudio_Open");
            Assert.False(string.IsNullOrWhiteSpace(assetStudio.Name));
            Assert.True(assetStudio.Properties.IsEnabled.ValueOrDefault);
            Assert.True(assetStudio.BoundingRectangle.Height >= 44,
                "asset studio has a keyboard/controller accessible touch target");
            var installedMods = ById(app, "GameHome_InstalledMods_Open");
            Assert.False(string.IsNullOrWhiteSpace(installedMods.Name));
            Assert.True(installedMods.Properties.IsEnabled.ValueOrDefault);
            Assert.True(installedMods.BoundingRectangle.Height >= 44,
                "installed mod management has an accessible touch target");
            // This safe smoke does not launch an image model or modify a game.

            // An empty library has no Continue shelf and no bulk bar until select mode is turned on.
            var shelf = TryById(app, "GameHome_Continue_Shelf");
            Assert.True(shelf == null || shelf.Properties.IsOffscreen.ValueOrDefault, "no Continue shelf without play history");
            var bulk = TryById(app, "GameHome_BulkBar");
            Assert.True(bulk == null || bulk.Properties.IsOffscreen.ValueOrDefault, "no bulk bar outside select mode");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Home_exposes_named_toolbar_controls_with_touch_sized_targets), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Tile_play_requires_consent_and_details_edit_and_remove_never_delete_game_files()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Tile_play_requires_consent_and_details_edit_and_remove_never_delete_game_files));
        try
        {
            OpenGamingHome(app);
            const string name = "Home Fixture Alpha";
            string marker = AddGame(app, name);
            string key = KeyOf(name);

            var tile = ById(app, "GameTile_" + key);
            Assert.Contains(name, tile.Name, StringComparison.Ordinal);
            var play = ById(app, "GameTilePlay_" + key);
            try { play.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch (Exception) { /* best effort */ }
            try { play.Focus(); } catch (Exception) { /* best effort */ }
            UiWait.For(() => play.BoundingRectangle.Height > 0, "play button laid out");
            Assert.True(play.BoundingRectangle.Height >= 44, "play height " + play.BoundingRectangle.Height);

            // One-click Play still goes through the existing launch consent; cancelling starts nothing.
            Press(app, play);
            var cancelLaunch = Find(() => app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button))), "launch consent dialog");
            cancelLaunch.Patterns.Invoke.Pattern.Invoke();
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));

            // Configure must enter the existing detail panel WITHOUT requesting a game launch.
            var configure = ById(app, "GameTileConfigure_" + key);
            Assert.Contains(name, configure.Name, StringComparison.Ordinal);
            Press(app, configure);
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
            Assert.Equal(name, ById(app, "GameDetail_Title").Name);
            Assert.NotNull(ById(app, "GameTitle_Favorite_" + key));
            Assert.NotNull(ById(app, "GameDetail_Hidden"));
            Assert.NotNull(ById(app, "GameTitle_Tags_" + key));
            Assert.NotNull(ById(app, "GameTitle_Collections_" + key));
            string id = EntryId(app, name);
            UiWait.ExpandById(app.MainWindow, "GameDetail_LaunchDetails_" + id);
            Assert.NotNull(ById(app, "GameDetail_Edit_" + id));

            // Remove asks first; Cancel keeps the entry, Remove deletes only the library record.
            Press(app, ById(app, "GameDetail_Remove_" + id));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")), "remove confirmation");
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")), "remove cancel")
                .Patterns.Invoke.Pattern.Invoke();
            Assert.Contains(name, File.ReadAllText(Path.Combine(app.DataRoot, "server", "manual-games.json")));

            Press(app, ById(app, "GameDetail_Remove_" + id));
            Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")), "remove confirmation")
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !File.ReadAllText(Path.Combine(app.DataRoot, "server", "manual-games.json")).Contains(name, StringComparison.Ordinal),
                "entry removed from the library");
            UiWait.For(() => TryById(app, "GameTile_" + key) == null, "tile removed");
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Tile_play_requires_consent_and_details_edit_and_remove_never_delete_game_files), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Empty_filter_state_clears_search_without_losing_saved_library_layout()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Empty_filter_state_clears_search_without_losing_saved_library_layout));
        try
        {
            OpenGamingHome(app);
            const string name = "Home Graphene Filter Fixture";
            AddGame(app, name);
            string key = KeyOf(name);
            ById(app, "GameTile_" + key);

            // Keep a non-default layout so the recovery path has something to preserve.
            Press(app, ById(app, "GameHome_LayoutToggle"));
            UiWait.For(() => File.Exists(app.SettingsPath) &&
                File.ReadAllText(app.SettingsPath).Contains("\"GameLibraryLayout\": \"list\"", StringComparison.Ordinal),
                "list view preference saved");

            var search = ById(app, "GameTitles_Search_TextBox").AsTextBox();
            search.Text = "this title cannot possibly match the fixture";
            UiWait.For(() => TryById(app, "GameHome_ClearFilters")?.Properties.IsEnabled.ValueOrDefault == true,
                "clear filters is actionable on a zero-result query");
            // The Border container exposes no UIA element; its title text is the observable empty state.
            Assert.NotNull(ById(app, "GameHome_EmptyTitle"));
            Press(app, ById(app, "GameHome_ClearFilters"));

            UiWait.For(() => TryById(app, "GameTile_" + key) is not null,
                "game restored after clearing the filters");
            Assert.Equal(string.Empty, ById(app, "GameTitles_Search_TextBox").AsTextBox().Text);
            Assert.Contains("\"GameLibraryLayout\": \"list\"", File.ReadAllText(app.SettingsPath), StringComparison.Ordinal);
            Assert.True(TryById(app, "GameHome_ClearFilters") is null ||
                !TryById(app, "GameHome_ClearFilters")!.Properties.IsEnabled.ValueOrDefault ||
                TryById(app, "GameHome_ClearFilters")!.Properties.IsOffscreen.ValueOrDefault,
                "clear filters must no longer be actionable after recovery");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Empty_filter_state_clears_search_without_losing_saved_library_layout), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Hidden_titles_leave_the_library_until_show_hidden_is_chosen()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Hidden_titles_leave_the_library_until_show_hidden_is_chosen));
        try
        {
            OpenGamingHome(app);
            const string name = "Home Fixture Hidden";
            AddGame(app, name);
            string key = KeyOf(name);
            Press(app, ById(app, "GameTile_" + key));
            var hidden = ById(app, "GameDetail_Hidden");
            Press(app, hidden);
            UiWait.For(() => TryById(app, "GameTile_" + key) == null, "hidden tile left the library");

            Press(app, ById(app, "GameHome_ShowHidden"));
            UiWait.For(() => TryById(app, "GameTile_" + key) != null, "hidden tile returns with Show hidden");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Hidden_titles_leave_the_library_until_show_hidden_is_chosen), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Layout_and_sort_are_remembered_in_settings()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Layout_and_sort_are_remembered_in_settings));
        try
        {
            OpenGamingHome(app);
            Press(app, ById(app, "GameHome_LayoutToggle"));
            UiWait.For(() => File.Exists(app.SettingsPath) && File.ReadAllText(app.SettingsPath).Contains("\"GameLibraryLayout\": \"list\"", StringComparison.Ordinal),
                "list layout saved");
            var sort = ById(app, "GameHome_Sort").AsComboBox();
            sort.Select(2); // play time
            UiWait.For(() => File.ReadAllText(app.SettingsPath).Contains("\"GameLibrarySort\": \"playtime\"", StringComparison.Ordinal),
                "sort saved");
            Press(app, ById(app, "GameHome_LayoutToggle"));
            UiWait.For(() => File.ReadAllText(app.SettingsPath).Contains("\"GameLibraryLayout\": \"grid\"", StringComparison.Ordinal),
                "grid layout saved");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Layout_and_sort_are_remembered_in_settings), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Select_mode_bulk_favorite_and_hide_apply_to_every_selected_title()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Select_mode_bulk_favorite_and_hide_apply_to_every_selected_title));
        try
        {
            OpenGamingHome(app);
            const string first = "Home Bulk One", second = "Home Bulk Two";
            AddGame(app, first);
            AddGame(app, second);
            string k1 = KeyOf(first), k2 = KeyOf(second);
            ById(app, "GameTile_" + k1);
            ById(app, "GameTile_" + k2);

            Press(app, ById(app, "GameHome_SelectMode"));
            ById(app, "GameHome_BulkBar");
            // Select mode must never leave a Play/Configure action reachable as an
            // enabled button: the whole tile is now strictly a selection toggle.
            foreach (string id in new[] { "GameTilePlay_" + k1, "GameTileConfigure_" + k1 })
            {
                var action = TryById(app, id);
                Assert.True(action is null || !action.Properties.IsEnabled.ValueOrDefault ||
                            action.Properties.IsOffscreen.ValueOrDefault,
                    id + " must not be actionable while selecting games");
            }
            Press(app, ById(app, "GameTile_" + k1));
            Press(app, ById(app, "GameTile_" + k2));
            UiWait.For(() => ById(app, "GameHome_Bulk_Count").Name.Contains('2'), "two titles selected");

            Press(app, ById(app, "GameHome_Bulk_Favorite"));
            Press(app, ById(app, "GameTitles_FavoritesOnly_Toggle"));
            UiWait.For(() => TryById(app, "GameTile_" + k1) != null && TryById(app, "GameTile_" + k2) != null,
                "both titles are favorites");
            Press(app, ById(app, "GameTitles_FavoritesOnly_Toggle"));

            Press(app, ById(app, "GameHome_Bulk_Hide"));
            UiWait.For(() => TryById(app, "GameTile_" + k1) == null && TryById(app, "GameTile_" + k2) == null,
                "both titles are hidden");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Select_mode_bulk_favorite_and_hide_apply_to_every_selected_title), ex); throw; }
    }
}
