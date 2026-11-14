using System.Diagnostics;
using System.Net.NetworkInformation;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// GAM-004: the Gaming library grid is virtualized (an ItemsRepeater), so only the tiles near the viewport are in the UI
/// Automation tree. This opens a 2,000-title synthetic library (generated into an isolated data root; invented names and
/// solid-colour covers; nothing launchable) and checks that the window stays responsive, that the number of tile elements is
/// bounded, that search narrows the list, and that keyboard navigation reaches tiles that are not realized yet.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingLibraryVirtualizationUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";
    private const int Count = 2000;
    // Generous bound: a 1280x800 window realizes a few dozen tiles (viewport plus a one-viewport cache). Anything near Count means
    // virtualization is off.
    private const int MaxRealizedTiles = 150;

    private static AutomationElement ById(UiTestApplication app, string id) => UiWait.ById(app.MainWindow, id);

    private static AutomationElement? TryById(UiTestApplication app, string id) => GamingSectionsPage.TryFind(app.MainWindow, id);

    private static string? FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault;

    /// <summary>The title buttons currently realized (Play and Configure buttons use other prefixes).</summary>
    internal static AutomationElement[] RealizedTiles(UiTestApplication app) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith("GameTile_", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Makes an off-screen tile appear in the UI Automation tree: a virtualized tile does not exist until the page is scrolled near
    /// it. The page is jumped to the tile's proportional position with the ScrollPattern, then corrected one page at a time towards
    /// the tile. Returns null when the tile is still absent after the bounded number of steps.
    /// </summary>
    internal static AutomationElement? ScrollToTile(UiTestApplication app, string key, int index, int maxSteps = 60)
    {
        string id = "GameTile_" + key;
        PageScroller(app)?.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 100.0 * index / Count);
        for (int step = 0; step < maxSteps; step++)
        {
            AutomationElement? found = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (found != null) return found;
            int[] realized = RealizedTiles(app)
                .Select(e => int.Parse((e.Properties.AutomationId.ValueOrDefault ?? "").AsSpan("GameTile_syntheticgame".Length)))
                .ToArray();
            var amount = realized.Length == 0 || realized.Min() < index
                ? FlaUI.Core.Definitions.ScrollAmount.LargeIncrement : FlaUI.Core.Definitions.ScrollAmount.LargeDecrement;
            try { PageScroller(app)?.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, amount); }
            catch (System.Runtime.InteropServices.COMException) { /* layout in progress */ }
            Thread.Sleep(200);
        }
        return app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
    }

    private static bool Holds(Func<bool> condition)
    {
        try { return condition(); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { return false; }
    }

    /// <summary>The vertically scrollable ancestor of the library search box: the Gaming page's own ScrollViewer.</summary>
    private static FlaUI.Core.Patterns.IScrollPattern? PageScroller(UiTestApplication app)
    {
        for (AutomationElement? e = ById(app, "GameTitles_Search_TextBox").Parent; e != null; e = e.Parent)
        {
            var scroll = e.Patterns.Scroll.PatternOrDefault;
            if (scroll != null && scroll.VerticallyScrollable.ValueOrDefault) return scroll;
        }
        return null;
    }

    /// <summary>
    /// Scrolls the Gaming page through UI Automation (ScrollPattern, one page at a time) until the condition holds: down first, then
    /// back up (a filter that shrinks the list can leave the grid above the viewport). Deterministic: no mouse position involved.
    /// </summary>
    internal static void ScrollPageUntil(UiTestApplication app, Func<bool> condition, string description, int stepsEachWay = 12)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var amount = FlaUI.Core.Definitions.ScrollAmount.LargeIncrement;
        while (DateTime.UtcNow < until)
        {
            for (int step = 0; step < stepsEachWay && DateTime.UtcNow < until; step++)
            {
                if (Holds(condition)) return;
                try { PageScroller(app)?.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, amount); }
                catch (System.Runtime.InteropServices.COMException) { /* layout in progress: try again */ }
                Thread.Sleep(250);
            }
            amount = amount == FlaUI.Core.Definitions.ScrollAmount.LargeIncrement
                ? FlaUI.Core.Definitions.ScrollAmount.LargeDecrement : FlaUI.Core.Definitions.ScrollAmount.LargeIncrement;
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static void WaitForCoreIdle() =>
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Two_thousand_title_library_stays_responsive_virtualized_searchable_and_keyboard_navigable()
    {
        WaitForCoreIdle();
        string root = SyntheticGameLibrary.CreateRoot(Count);
        using var app = new UiTestApplication(nameof(Two_thousand_title_library_stays_responsive_virtualized_searchable_and_keyboard_navigable),
            restartDataRoot: root);
        try
        {
            // First paint: from selecting the Gaming workspace to the first library tile in the UIA tree.
            var page = new InstallProgressPage(app);
            var clock = Stopwatch.StartNew();
            page.Invoke(ById(app, GamingTab));
            // The grid sits below the hero, the Continue shelf and the filter row: on a maximized 1280x800 window it starts more
            // than a viewport plus cache below the fold, so no tile exists until the page is scrolled towards it (that is what
            // virtualization means) and a filter that shrinks the list can move the page, too. The page is therefore scrolled
            // (UIA ScrollPattern) until the expected tiles are in the tree, as a user would.
            ScrollPageUntil(app, () => RealizedTiles(app).Length > 0, "first library tile realized");
            clock.Stop();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "first tile took " + clock.Elapsed);

            // Bounded UIA tree: a 2,000-title library must not realize 2,000 tiles.
            int realized = RealizedTiles(app).Length;
            Assert.InRange(realized, 1, MaxRealizedTiles);

            // The window answers: a property read and a control that needs the UI thread both come back quickly.
            var ping = Stopwatch.StartNew();
            _ = app.MainWindow.Properties.IsEnabled.ValueOrDefault;
            var search = ById(app, "GameTitles_Search_TextBox").AsTextBox();
            ping.Stop();
            Assert.True(ping.Elapsed < TimeSpan.FromSeconds(5), "UI thread answered in " + ping.Elapsed);

            // Search narrows the virtualized list: "game 1999" is exactly one synthetic title.
            search.Text = "game 1999";
            ScrollPageUntil(app, () => RealizedTiles(app).Length == 1 && TryById(app, "GameTile_" + SyntheticGameLibrary.KeyOf(1999)) != null,
                "search narrowed the library to one tile");

            // Back to the whole synthetic library, then drive it with the keyboard.
            search.Text = "Synthetic Game";
            string first = "GameTile_" + SyntheticGameLibrary.KeyOf(0), last = "GameTile_" + SyntheticGameLibrary.KeyOf(Count - 1);
            ScrollPageUntil(app, () => TryById(app, first) != null, "first synthetic tile back after widening the search");
            Assert.InRange(RealizedTiles(app).Length, 2, MaxRealizedTiles);

            ById(app, first).Focus();
            UiWait.For(() => FocusedId(app) == first, "first tile focused");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.DOWN),
                () => (FocusedId(app) ?? "").StartsWith("GameTile_", StringComparison.Ordinal) && FocusedId(app) != first,
                "Down moves focus to a tile in the next row");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.END), () => FocusedId(app) == last,
                "End reaches the last tile although it was not realized");
            // The jump realizes the tiles around the target after the layout pass; the count is bounded, never 2,000.
            UiWait.For(() => TryById(app, last) is { } shown && !shown.Properties.IsOffscreen.ValueOrDefault,
                "the last tile realized and on screen after End");
            UiWait.For(() => RealizedTiles(app).Length is >= 1 and <= MaxRealizedTiles, "a bounded set of tiles realized around the last one");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.HOME), () => FocusedId(app) == first,
                "Home returns to the first tile");
            UiWait.For(() => TryById(app, first) is { } top && !top.Properties.IsOffscreen.ValueOrDefault, "the first tile on screen after Home");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.NEXT),
                () => (FocusedId(app) ?? "").StartsWith("GameTile_", StringComparison.Ordinal) && FocusedId(app) != first,
                "Page Down moves several rows");

            // An off-screen tile appears in the tree once it is scrolled near the viewport.
            string far = SyntheticGameLibrary.KeyOf(1200);
            Assert.NotNull(ScrollToTile(app, far, 1200));
            UiWait.For(() => RealizedTiles(app).Length is >= 1 and <= MaxRealizedTiles, "a bounded set of tiles realized around tile 1200");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Two_thousand_title_library_stays_responsive_virtualized_searchable_and_keyboard_navigable), ex);
            throw;
        }
    }
}
