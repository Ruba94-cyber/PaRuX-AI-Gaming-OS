using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class GamingLibraryCollectionsUiTests
{
    private static AutomationElement? Find(UiTestApplication app, string id) => GamingSectionsPage.TryFind(app.MainWindow, id);

    private static void RevealFilters(UiTestApplication app)
    {
        // The compact layout places the rail inside an Expander; its actual header peer owns ExpandCollapse.
        AutomationElement? expander = Find(app, "GameHome_CompactFilters");
        if (expander is null) return;
        var header = expander.Patterns.ExpandCollapse.PatternOrDefault != null ? expander :
            expander.FindAllDescendants().FirstOrDefault(e => e.Patterns.ExpandCollapse.PatternOrDefault != null);
        var pattern = header?.Patterns.ExpandCollapse.PatternOrDefault
            ?? throw new InvalidOperationException("Compact filters have no expandable UI Automation header peer.");
        if (pattern.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded) pattern.Expand();
        UiWait.For(() => pattern.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded, "compact filter header expanded");
    }

    private static bool HasGames(UiTestApplication app, bool third) =>
        Find(app, "GameTile_" + SyntheticGameLibrary.KeyOf(0)) != null
        && Find(app, "GameTile_" + SyntheticGameLibrary.KeyOf(1)) != null
        && (Find(app, "GameTile_" + SyntheticGameLibrary.KeyOf(2)) != null) == third;

    [Fact, Trait("Category", "FlaUI")]
    public void Capture_returning_library_hero()
    {
        const string name = nameof(Capture_returning_library_hero);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(3, includeCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            var dismiss = UiWait.ById(app.MainWindow, "GameLibrary_FirstRunScan_Dismiss");
            UiWait.AssertEnabled(dismiss);
            page.Invoke(dismiss);
            app.ResizeMainWindow(1200, 760);
            page.Invoke(UiWait.ById(app.MainWindow, "TogglePaneButton"));
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () =>
                Find(app, "GameHome_Hero_Title") is { } title && !title.Properties.IsOffscreen.ValueOrDefault,
                "returning library hero visible");
            UiWait.ForStable(() => Find(app, "GameHome_Hero_Title") is { } title
                && !title.Properties.IsOffscreen.ValueOrDefault, "returning hero remains visible");
            Assert.NotNull(app.CaptureSnapshot(name));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Collection_picker_reaches_collection_beyond_quick_entries()
    {
        const string name = nameof(Collection_picker_reaches_collection_beyond_quick_entries);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(12, includeCollections: true, manyCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            RevealFilters(app);
            var picker = UiWait.ById(app.MainWindow, "GameSide_AllCollections").AsComboBox();
            Assert.Null(Find(app, "GameSide_Collection_Zeta09"));
            picker.Select("Zeta09  (1)");
            string expected = "GameTile_" + SyntheticGameLibrary.KeyOf(11);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Find(app, expected) != null, "last collection game visible");
            UiWait.ForStable(() => Find(app, expected) != null
                && GamingLibraryVirtualizationUiTests.RealizedTiles(app).Length == 1,
                "picker filters to exactly the selected collection game");
            Assert.NotNull(app.CaptureSnapshot(name + "-result"));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Collection_selection_filters_exact_games_and_all_restores_them()
    {
        const string name = nameof(Collection_selection_filters_exact_games_and_all_restores_them);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(3, includeCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            UiWait.For(() => Find(app, "GameHome_Hero_Title") is { } hero
                && hero.Name.Contains(SyntheticGameLibrary.NameOf(0), StringComparison.Ordinal), "played title shown in hero");
            RevealFilters(app);
            UiWait.For(() => Find(app, "GameSide_Collection_Racing") != null,
                "Racing collection available");
            Assert.Equal("Racing, 2", UiWait.ById(app.MainWindow, "GameSide_Collection_Racing").Name);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () =>
                Find(app, "GameHome_Hero_Title") is { } title && !title.Properties.IsOffscreen.ValueOrDefault,
                "hero title visible in viewport");
            UiWait.ForStable(() => Find(app, "GameHome_Hero_Title") is { } title
                && !title.Properties.IsOffscreen.ValueOrDefault, "hero title remains visible");
            Assert.NotNull(app.CaptureSnapshot(name + "-hero"));

            page.Invoke(UiWait.ById(app.MainWindow, "GameSide_Collection_Racing"));
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => HasGames(app, third: false), "Racing results visible");
            UiWait.ForStable(() => HasGames(app, third: false), "settled Racing results contain exactly the two fixture games");
            Assert.NotNull(app.CaptureSnapshot(name + "-collection"));

            RevealFilters(app);
            page.Invoke(UiWait.ById(app.MainWindow, "GameSide_All"));
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => HasGames(app, third: true), "All games results visible");
            UiWait.ForStable(() => HasGames(app, third: true), "All games restores the other collection");

            app.ResizeMainWindow(800, 800);
            UiWait.For(() => Find(app, "GameHome_CompactFilters") != null, "compact filters after narrow resize");
            var lastBounds = app.MainWindow.BoundingRectangle;
            UiWait.ForStable(() =>
            {
                var current = app.MainWindow.BoundingRectangle;
                bool stable = current == lastBounds;
                lastBounds = current;
                return stable;
            }, "narrow window bounds settled");
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () =>
                Find(app, "GameHome_Hero_Title") is { } title && !title.Properties.IsOffscreen.ValueOrDefault,
                "hero visible in narrow window");
            // Capture the actual narrow rendered window, before expanding filters so the content position is visible.
            Assert.NotNull(app.CaptureSnapshot(name + "-narrow"));
            RevealFilters(app);
            Assert.Equal("Racing, 2", UiWait.ById(app.MainWindow, "GameSide_Collection_Racing").Name);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
