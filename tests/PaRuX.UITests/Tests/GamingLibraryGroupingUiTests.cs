using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// GAM-004 remainder: the cover-size control and the grouping of the Games library (platform, source, completion status), both
/// remembered per user. Grouping shows one virtualized grid per group, so the 2,000-title checks assert the same bounded
/// realized-tile count, search and keyboard reach as the ungrouped library (<see cref="GamingLibraryVirtualizationUiTests"/>).
/// The synthetic library cycles the completion status over six values, which gives six status groups.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingLibraryGroupingUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";
    private const int Count = 2000;
    private const int MaxRealizedTiles = 150;

    private static AutomationElement ById(UiTestApplication app, string id) => UiWait.ById(app.MainWindow, id);

    private static AutomationElement? TryById(UiTestApplication app, string id) => GamingSectionsPage.TryFind(app.MainWindow, id);

    private static string? FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault;

    private static void WaitForCoreIdle() =>
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));

    private static void Press(UiTestApplication app, AutomationElement element) => new InstallProgressPage(app).Invoke(element);

    private static string Settings(string root, string key, string value)
    {
        string path = Path.Combine(root, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[key] = value;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static bool SettingsContain(UiTestApplication app, string fragment) =>
        File.Exists(app.SettingsPath) && File.ReadAllText(app.SettingsPath).Contains(fragment, StringComparison.Ordinal);

    private static string SelectedText(AutomationElement combo) =>
        combo.AsComboBox().SelectedItem?.Name ?? string.Empty;

    private static void OpenLibrary(UiTestApplication app)
    {
        new InstallProgressPage(app).Invoke(ById(app, GamingTab));
        ById(app, "GameTitles_Search_TextBox");
        GamingLibraryVirtualizationUiTests.ScrollPageUntil(app,
            () => GamingLibraryVirtualizationUiTests.RealizedTiles(app).Length > 0, "first library tile realized");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Group_and_cover_size_controls_are_named_saved_and_cover_size_is_disabled_in_list_view()
    {
        WaitForCoreIdle();
        string root = SyntheticGameLibrary.CreateRoot(40);
        using var app = new UiTestApplication(nameof(Group_and_cover_size_controls_are_named_saved_and_cover_size_is_disabled_in_list_view),
            restartDataRoot: root);
        try
        {
            OpenLibrary(app);
            AutomationElement group = ById(app, "GameHome_Group"), cover = ById(app, "GameHome_CoverSize");
            Assert.False(string.IsNullOrWhiteSpace(group.Name));
            Assert.False(string.IsNullOrWhiteSpace(cover.Name));
            UiWait.ForOnScreen(group, "group control");
            UiWait.AssertEnabled(group);
            UiWait.AssertEnabled(cover);
            UiWait.For(() => group.BoundingRectangle.Height >= 44 && cover.BoundingRectangle.Height >= 44, "touch-sized targets");

            group.AsComboBox().Select(3); // status
            UiWait.For(() => SettingsContain(app, "\"GameLibraryGroup\": \"status\""), "grouping saved");
            cover.AsComboBox().Select(2); // large
            UiWait.For(() => SettingsContain(app, "\"GameLibraryCoverSize\": \"large\""), "cover size saved");

            // The list rows have a fixed size: the cover-size control is disabled there and comes back with the grid.
            Press(app, ById(app, "GameHome_LayoutToggle"));
            UiWait.ForStable(() => !cover.Properties.IsEnabled.ValueOrDefault, "cover size disabled in list view");
            Press(app, ById(app, "GameHome_LayoutToggle"));
            UiWait.AssertEnabled(cover);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Group_and_cover_size_controls_are_named_saved_and_cover_size_is_disabled_in_list_view), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Saved_group_and_cover_size_are_restored_on_the_next_start()
    {
        WaitForCoreIdle();
        string root = SyntheticGameLibrary.CreateRoot(40);
        Settings(root, "GameLibraryGroup", "status");
        Settings(root, "GameLibraryCoverSize", "small");
        using var app = new UiTestApplication(nameof(Saved_group_and_cover_size_are_restored_on_the_next_start), restartDataRoot: root);
        try
        {
            OpenLibrary(app);
            UiWait.ForStable(() => SelectedText(ById(app, "GameHome_Group")).Contains("Status", StringComparison.OrdinalIgnoreCase),
                "saved grouping selected");
            UiWait.ForStable(() => SelectedText(ById(app, "GameHome_CoverSize")).Contains("Small", StringComparison.OrdinalIgnoreCase),
                "saved cover size selected");
            // Forty titles over six statuses: the group headings exist, each with its own grid.
            foreach (string id in new[] { "playing", "backlog", "onhold", "completed", "dropped", "unknown" })
                GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, "GameHome_Group_" + id) != null, "heading " + id);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Saved_group_and_cover_size_are_restored_on_the_next_start), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Cover_size_changes_the_tile_size_in_the_running_grid()
    {
        WaitForCoreIdle();
        string root = SyntheticGameLibrary.CreateRoot(60);
        using var app = new UiTestApplication(nameof(Cover_size_changes_the_tile_size_in_the_running_grid), restartDataRoot: root);
        try
        {
            OpenLibrary(app);
            string first = "GameTile_" + SyntheticGameLibrary.KeyOf(0);
            double WidthOf() => ById(app, first).BoundingRectangle.Width;

            ById(app, "GameHome_CoverSize").AsComboBox().Select(0); // small
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, first) != null, "first tile after choosing small covers");
            double small = 0;
            UiWait.ForStable(() => (small = WidthOf()) > 0, "small tile measured");

            ById(app, "GameHome_CoverSize").AsComboBox().Select(2); // large
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, first) != null, "first tile after choosing large covers");
            double large = 0;
            UiWait.ForStable(() => (large = WidthOf()) > small * 1.3, "large tiles wider than small tiles (small " + small + ")");
            Assert.True(large > small, "large " + large + " vs small " + small);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Cover_size_changes_the_tile_size_in_the_running_grid), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Grouped_two_thousand_title_library_stays_virtualized_searchable_and_keyboard_navigable_across_groups()
    {
        WaitForCoreIdle();
        string root = SyntheticGameLibrary.CreateRoot(Count);
        Settings(root, "GameLibraryGroup", "status");
        using var app = new UiTestApplication(
            nameof(Grouped_two_thousand_title_library_stays_virtualized_searchable_and_keyboard_navigable_across_groups),
            restartDataRoot: root);
        try
        {
            OpenLibrary(app);

            // Six group headings exist (statuses cycle over six values) yet only a bounded set of tiles is in the UIA tree.
            Assert.InRange(GamingLibraryVirtualizationUiTests.RealizedTiles(app).Length, 1, MaxRealizedTiles);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, "GameHome_Group_playing") != null, "first group heading");

            // The first group is "playing" (every sixth title from index 2); its first tile is Synthetic Game 0002.
            string first = "GameTile_" + SyntheticGameLibrary.KeyOf(2);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, first) != null, "first tile of the first group");
            ById(app, first).Focus();
            UiWait.For(() => FocusedId(app) == first, "first grouped tile focused");

            // Right moves within the group; End reaches the last tile of the last group ("no status", every sixth title: 1998)
            // although neither is realized; Home comes back to the first tile of the first group.
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.RIGHT),
                () => (FocusedId(app) ?? "").StartsWith("GameTile_", StringComparison.Ordinal) && FocusedId(app) != first,
                "Right moves focus to the next tile of the group");
            string last = "GameTile_" + SyntheticGameLibrary.KeyOf(1998);
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.END), () => FocusedId(app) == last,
                "End reaches the last tile of the last group");
            UiWait.For(() => TryById(app, last) is { } shown && !shown.Properties.IsOffscreen.ValueOrDefault,
                "the last tile realized and on screen after End");
            UiWait.For(() => GamingLibraryVirtualizationUiTests.RealizedTiles(app).Length is >= 1 and <= MaxRealizedTiles,
                "a bounded set of tiles realized around the last one");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.HOME), () => FocusedId(app) == first,
                "Home returns to the first tile of the first group");

            // Down from the first group's last row eventually enters the next group: keep pressing Down until a tile that is not in
            // the first group (its index is not 2 mod 6) has focus. Bounded: at most 120 presses.
            string FocusedKey() => (FocusedId(app) ?? string.Empty)["GameTile_syntheticgame".Length..];
            bool InFirstGroup()
            {
                string text = FocusedKey();
                return int.TryParse(text, out int n) && n % 6 == 2;
            }
            for (int i = 0; i < 120 && InFirstGroup(); i++) { Keyboard.Type(VirtualKeyShort.DOWN); Thread.Sleep(120); }
            Assert.False(InFirstGroup(), "Down never left the first group; focus stayed on " + FocusedId(app));

            // Search narrows across groups: exactly one title, in exactly one group.
            var search = ById(app, "GameTitles_Search_TextBox").AsTextBox();
            search.Text = "game 1999";
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app,
                () => GamingLibraryVirtualizationUiTests.RealizedTiles(app).Length == 1
                      && TryById(app, "GameTile_" + SyntheticGameLibrary.KeyOf(1999)) != null,
                "search narrowed the grouped library to one tile");
            search.Text = "Synthetic Game";
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, first) != null, "grouped library back after widening the search");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(
                nameof(Grouped_two_thousand_title_library_stays_virtualized_searchable_and_keyboard_navigable_across_groups), ex);
            throw;
        }
    }
}
