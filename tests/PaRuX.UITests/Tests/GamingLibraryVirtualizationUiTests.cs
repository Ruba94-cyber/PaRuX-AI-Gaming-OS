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
    /// Makes an off-screen tile appear in the UI Automation tree: a virtualized tile does not exist until it is scrolled near the
    /// viewport, and the repeater exposes no ItemContainer pattern, so the page is scrolled with the mouse wheel over the grid.
    /// Returns null when the tile is still absent after the bounded number of scroll steps.
    /// </summary>
    internal static AutomationElement? ScrollToTile(UiTestApplication app, string key, int maxSteps = 120)
    {
        string id = "GameTile_" + key;
        for (int step = 0; step < maxSteps; step++)
        {
            AutomationElement? found = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (found != null) return found;
            AutomationElement? any = RealizedTiles(app).FirstOrDefault();
            if (any == null) return null;
            Mouse.MoveTo(any.GetClickablePoint());
            Mouse.Scroll(-5);
            Thread.Sleep(60);
        }
        return app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
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
            UiWait.For(() => RealizedTiles(app).Length > 0, "first library tile realized", TimeSpan.FromSeconds(60));
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
            UiWait.For(() => RealizedTiles(app).Length == 1 && TryById(app, "GameTile_" + SyntheticGameLibrary.KeyOf(1999)) != null,
                "search narrowed the library to one tile", TimeSpan.FromSeconds(30));

            // Back to the whole synthetic library, then drive it with the keyboard.
            search.Text = "Synthetic Game";
            string first = "GameTile_" + SyntheticGameLibrary.KeyOf(0), last = "GameTile_" + SyntheticGameLibrary.KeyOf(Count - 1);
            UiWait.For(() => TryById(app, first) != null, "first synthetic tile back after widening the search", TimeSpan.FromSeconds(30));
            Assert.InRange(RealizedTiles(app).Length, 2, MaxRealizedTiles);

            ById(app, first).Focus();
            UiWait.For(() => FocusedId(app) == first, "first tile focused");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.DOWN),
                () => (FocusedId(app) ?? "").StartsWith("GameTile_", StringComparison.Ordinal) && FocusedId(app) != first,
                "Down moves focus to a tile in the next row");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.END), () => FocusedId(app) == last,
                "End reaches the last tile although it was not realized");
            Assert.InRange(RealizedTiles(app).Length, 1, MaxRealizedTiles);
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.HOME), () => FocusedId(app) == first,
                "Home returns to the first tile");
            UiWait.InputUntil(() => Keyboard.Type(VirtualKeyShort.NEXT),
                () => (FocusedId(app) ?? "").StartsWith("GameTile_", StringComparison.Ordinal) && FocusedId(app) != first,
                "Page Down moves several rows");

            // An off-screen tile appears in the tree once it is scrolled near the viewport (the pattern other tests rely on).
            string far = SyntheticGameLibrary.KeyOf(1200);
            Assert.NotNull(ScrollToTile(app, far));
            Assert.InRange(RealizedTiles(app).Length, 1, MaxRealizedTiles);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Two_thousand_title_library_stays_responsive_virtualized_searchable_and_keyboard_navigable), ex);
            throw;
        }
    }
}
