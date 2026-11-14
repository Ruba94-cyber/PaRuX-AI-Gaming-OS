using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// SIMULATED multi-monitor scenarios (no driver, no virtual display, no system setting touched, no physical TV or second
/// monitor): a fake topology provider feeds the same services the product uses, so 1/2-monitor layouts, different
/// resolutions and DPI-sized panels, a secondary to the left or above the primary (negative coordinates), hot removal,
/// a powered-off secondary and a failing display query are all exercised deterministically. These prove the policy and
/// geometry code; they are NOT a physical-monitor test.
/// </summary>
public sealed class DisplayTopologySimulationTests
{
    private const string Primary = @"\\.\DISPLAY1";
    private const string Secondary = @"\\.\DISPLAY2";

    private static GamingDisplayModeDto Mode(string id, bool primary, int x, int y, int w, int h, int hz = 60) => new(id, primary, x, y, w, h, hz);

    private static IReadOnlyList<ConsoleDisplayAssignment> TvOn(string id) => new[] { new ConsoleDisplayAssignment(id, ConsoleDisplaySelectionPolicy.TvRole) };

    /// <summary>A topology the test can change between calls, like a hot-plug.</summary>
    private sealed class FakeDesktop
    {
        public List<GamingDisplayModeDto> Monitors { get; } = new();
        public Exception? Failure { get; set; }
        public GamingDisplayTopologyService Service() => new(() => Failure != null ? throw Failure : Monitors.ToArray());
    }

    public static IEnumerable<object[]> TwoMonitorLayouts()
    {
        // secondary left of the primary (negative X), above it (negative Y), right of it, below it
        yield return new object[] { "left", -2560, 0 };
        yield return new object[] { "above", 0, -1440 };
        yield return new object[] { "right", 1920, 0 };
        yield return new object[] { "below", 0, 1080 };
        yield return new object[] { "left-and-up", -2560, -360 };
    }

    [Theory]
    [MemberData(nameof(TwoMonitorLayouts))]
    public void Two_monitors_report_the_primary_first_wherever_the_secondary_sits(string name, int sx, int sy)
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Secondary, false, sx, sy, 2560, 1440, 144));
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));

        GamingDisplaySnapshotDto snapshot = desktop.Service().Snapshot();

        Assert.True(snapshot.Supported, name);
        Assert.Equal(2, snapshot.Displays.Count);
        Assert.True(snapshot.Displays[0].Primary);
        Assert.Equal(Primary, snapshot.Displays[0].DisplayId);
        Assert.Equal(sx, snapshot.Displays[1].X);
        Assert.Equal(sy, snapshot.Displays[1].Y);
    }

    [Theory]
    [MemberData(nameof(TwoMonitorLayouts))]
    public void A_window_centered_on_the_secondary_stays_on_the_secondary_for_every_layout(string name, int sx, int sy)
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        desktop.Monitors.Add(Mode(Secondary, false, sx, sy, 2560, 1440, 144));
        GamingDisplaySnapshotDto snapshot = desktop.Service().Snapshot();

        GamingDisplayModeDto? target = ConsoleDisplaySelectionPolicy.SelectTarget(snapshot, TvOn(Secondary), Secondary);
        Assert.NotNull(target);

        ConsoleHomeRepositionPolicy.Bounds? anchor = ConsoleHomeRepositionPolicy.CenteredAnchor(target!);
        Assert.NotNull(anchor);
        Assert.True(ConsoleHomeRepositionPolicy.IsOnTarget(target!, anchor!.Value), name);

        // A window straddling the seam with the primary is not "on" the secondary.
        var straddle = new ConsoleHomeRepositionPolicy.Bounds(target!.X + target.Width - 200, target.Y, 800, 600);
        Assert.False(ConsoleHomeRepositionPolicy.IsOnTarget(target, straddle), name);

        (int X, int Y)? game = OwnedGameWindowPlacementService.CenterUnchangedWindow(target, 1600, 900);
        Assert.NotNull(game);
        Assert.True(game!.Value.X >= target.X && game.Value.X + 1600 <= target.X + target.Width, name);
        Assert.True(game.Value.Y >= target.Y && game.Value.Y + 900 <= target.Y + target.Height, name);
    }

    [Fact]
    public void Different_resolutions_and_dpi_sized_panels_each_get_a_window_that_fits_or_a_refusal()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1366, 768, 60));          // small laptop-class panel
        desktop.Monitors.Add(Mode(Secondary, false, 1366, 0, 3840, 2160, 120));  // 4K secondary
        GamingDisplaySnapshotDto snapshot = desktop.Service().Snapshot();

        GamingDisplayModeDto small = snapshot.Displays.Single(d => d.Primary);
        GamingDisplayModeDto big = snapshot.Displays.Single(d => !d.Primary);

        Assert.NotNull(OwnedGameWindowPlacementService.CenterUnchangedWindow(big, 1920, 1080));
        // a 1920x1080 game window never fits the 1366x768 panel and must not be clipped across the neighbour
        Assert.Null(OwnedGameWindowPlacementService.CenterUnchangedWindow(small, 1920, 1080));
        Assert.Equal(120, big.RefreshRateHz);
    }

    [Fact]
    public void Hot_removal_of_the_selected_secondary_fails_closed_everywhere_and_never_reroutes_to_the_primary()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        desktop.Monitors.Add(Mode(Secondary, false, -2560, 0, 2560, 1440, 144));
        GamingDisplayTopologyService topology = desktop.Service();
        var placement = new OwnedGameWindowPlacementService(topology);
        GamingDisplayModeDto expected = topology.Snapshot().Displays.Single(d => d.DisplayId == Secondary);

        Assert.True(placement.HasConnectedTarget(Secondary));
        Assert.True(GameTvLaunchAdmissionPolicy.TryResolveTarget(true, topology.Snapshot(), TvOn(Secondary), Secondary, out string? resolved));
        Assert.Equal(Secondary, resolved);

        desktop.Monitors.RemoveAll(m => m.DisplayId == Secondary); // unplugged while PaRuX is running

        Assert.False(placement.HasConnectedTarget(Secondary));
        Assert.False(GameTvLaunchAdmissionPolicy.TryResolveTarget(true, topology.Snapshot(), TvOn(Secondary), Secondary, out string? after));
        Assert.Null(after);
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(topology.Snapshot(), TvOn(Secondary), Secondary));
        Assert.False(OwnedGameWindowPlacementService.HasUnchangedTarget(topology.Snapshot(), expected));
        // routing disabled means no routing and therefore no refusal
        Assert.True(GameTvLaunchAdmissionPolicy.TryResolveTarget(false, topology.Snapshot(), TvOn(Secondary), Secondary, out string? disabled));
        Assert.Null(disabled);
    }

    [Fact]
    public void A_single_monitor_desktop_never_treats_the_only_monitor_as_the_missing_TV()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 3840, 2160, 60));

        GamingDisplaySnapshotDto snapshot = desktop.Service().Snapshot();

        Assert.True(snapshot.Supported);
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(snapshot, TvOn(Secondary), Secondary));
        Assert.False(new OwnedGameWindowPlacementService(desktop.Service()).HasConnectedTarget(Secondary));
        // even a 4K panel is not a TV unless the person said so
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(snapshot, Array.Empty<ConsoleDisplayAssignment>(), Primary));
    }

    [Fact]
    public void A_secondary_that_changes_resolution_position_or_primary_flag_no_longer_authorizes_the_earlier_placement()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        desktop.Monitors.Add(Mode(Secondary, false, -2560, 0, 2560, 1440, 144));
        GamingDisplayTopologyService topology = desktop.Service();
        GamingDisplayModeDto earlier = topology.Snapshot().Displays.Single(d => d.DisplayId == Secondary);
        Assert.True(OwnedGameWindowPlacementService.HasUnchangedTarget(topology.Snapshot(), earlier));

        desktop.Monitors[1] = Mode(Secondary, false, -1920, 0, 1920, 1080, 60); // user switched the panel to 1080p and rearranged
        Assert.False(OwnedGameWindowPlacementService.HasUnchangedTarget(topology.Snapshot(), earlier));

        desktop.Monitors[1] = Mode(Secondary, false, -2560, 0, 2560, 1440, 144);
        desktop.Monitors[0] = Mode(Primary, false, 0, 0, 1920, 1080);
        desktop.Monitors[1] = Mode(Secondary, true, -2560, 0, 2560, 1440, 144); // primary swapped to the secondary
        Assert.False(OwnedGameWindowPlacementService.HasUnchangedTarget(topology.Snapshot(), earlier));
        Assert.Equal(Secondary, topology.Snapshot().Displays[0].DisplayId); // and it is now reported first
    }

    [Fact]
    public void A_powered_off_secondary_is_the_same_as_an_absent_one()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        // Windows keeps a switched-off panel enumerated with an empty mode on some drivers: it must not count as a target.
        desktop.Monitors.Add(Mode(Secondary, false, 1920, 0, 0, 0, 0));

        GamingDisplaySnapshotDto snapshot = desktop.Service().Snapshot();

        Assert.Single(snapshot.Displays);
        Assert.False(new OwnedGameWindowPlacementService(desktop.Service()).HasConnectedTarget(Secondary));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(snapshot, TvOn(Secondary), Secondary));
    }

    [Fact]
    public void Duplicate_or_conflicting_monitor_ids_never_pick_one_by_enumeration_order()
    {
        var desktop = new FakeDesktop();
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        desktop.Monitors.Add(Mode(Secondary, false, -2560, 0, 2560, 1440, 144));
        var conflicting = new[]
        {
            new ConsoleDisplayAssignment(Secondary, ConsoleDisplaySelectionPolicy.TvRole),
            new ConsoleDisplayAssignment(Secondary, ConsoleDisplaySelectionPolicy.MonitorRole),
        };

        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(desktop.Service().Snapshot(), conflicting, Secondary));
    }

    [Fact]
    public void A_failing_or_empty_display_query_degrades_to_unsupported_without_throwing()
    {
        var desktop = new FakeDesktop { Failure = new InvalidOperationException("display driver is resetting") };
        GamingDisplayTopologyService topology = desktop.Service();

        GamingDisplaySnapshotDto failed = topology.Snapshot();
        Assert.False(failed.Supported);
        Assert.Empty(failed.Displays);
        Assert.False(new OwnedGameWindowPlacementService(topology).HasConnectedTarget(Secondary));

        desktop.Failure = null; // the driver came back: the next snapshot works without a restart
        desktop.Monitors.Add(Mode(Primary, true, 0, 0, 1920, 1080));
        Assert.True(topology.Snapshot().Supported);

        desktop.Monitors.Clear(); // every monitor disappears (docking glitch)
        GamingDisplaySnapshotDto none = topology.Snapshot();
        Assert.False(none.Supported);
        Assert.Contains("did not expose", none.Message);
    }

    [Theory]
    [InlineData(-2400, 100)]   // saved on a secondary that was to the left and is now unplugged
    [InlineData(100, -1300)]   // saved on a secondary above
    [InlineData(5000, 5000)]   // saved on a far right/below monitor
    [InlineData(int.MinValue / 2, int.MaxValue / 2)]
    public void A_bubble_saved_on_a_removed_monitor_is_pulled_back_onto_the_remaining_work_area(int savedX, int savedY)
    {
        var work = new BubblePlacementPolicy.Rect(0, 0, 1920, 1040);
        var size = new BubblePlacementPolicy.Size(132, 44);

        BubblePlacementPolicy.Point point = BubblePlacementPolicy.Clamp(work, new BubblePlacementPolicy.Point(savedX, savedY), size);

        Assert.InRange(point.X, work.X, work.Right - size.Width);
        Assert.InRange(point.Y, work.Y, work.Bottom - size.Height);
    }

    [Fact]
    public void A_bubble_on_a_secondary_with_negative_coordinates_stays_inside_that_secondary()
    {
        var work = new BubblePlacementPolicy.Rect(-2560, -300, 2560, 1400); // work area of a left/up secondary
        var size = new BubblePlacementPolicy.Size(132, 44);

        BubblePlacementPolicy.Point fits = BubblePlacementPolicy.Clamp(work, new BubblePlacementPolicy.Point(-1000, 200), size);
        Assert.Equal(new BubblePlacementPolicy.Point(-1000, 200), fits); // already on the secondary: untouched

        BubblePlacementPolicy.Point leaked = BubblePlacementPolicy.Clamp(work, new BubblePlacementPolicy.Point(-30, 5), size); // leaking onto the primary
        Assert.Equal(work.Right - size.Width, leaked.X);
        Assert.Equal(5, leaked.Y);

        BubblePlacementPolicy.Point corner = BubblePlacementPolicy.DefaultCollapsedOrigin(work, mainWindow: null, size, inset: 18, composerClearance: 150);
        Assert.Equal(work.Right - size.Width - 18, corner.X);
        Assert.Equal(work.Bottom - size.Height - 18, corner.Y);
    }

    [Fact]
    public void An_expanded_bubble_at_the_left_edge_of_a_negative_coordinate_secondary_grows_inward()
    {
        var work = new BubblePlacementPolicy.Rect(-2560, 0, 2560, 1400);
        var collapsed = new BubblePlacementPolicy.Size(132, 44);
        var expanded = new BubblePlacementPolicy.Size(400, 480);

        BubblePlacementPolicy.Expansion result = BubblePlacementPolicy.ExpandedOrigin(
            work, new BubblePlacementPolicy.Point(work.X + 4, work.Bottom - collapsed.Height - 4), collapsed, expanded);

        Assert.InRange(result.Origin.X, work.X, work.Right - expanded.Width);
        Assert.InRange(result.Origin.Y, work.Y, work.Bottom - expanded.Height);
        Assert.False(result.GrowsLeft);
    }

    [Theory]
    [InlineData(true, -2400, 100, true)]   // dragged onto a secondary monitor to the left of the primary
    [InlineData(true, 100, -1300, true)]   // ... or above it
    [InlineData(true, -2400, -1300, true)]
    [InlineData(true, 500, 500, true)]
    [InlineData(true, -1, -1, false)]      // the unset sentinel of a fresh profile
    [InlineData(false, 500, 500, false)]   // never dragged
    [InlineData(false, -2400, 100, false)]
    public void A_bubble_position_on_a_left_or_upper_monitor_is_a_valid_saved_position(bool manual, int x, int y, bool expected)
    {
        Assert.Equal(expected, BubblePlacementPolicy.HasSavedManualPosition(manual, x, y));
    }

    private static readonly ConsoleHomeRepositionPolicy.Bounds PrimaryWork = new(0, 0, 1920, 1040);
    private static readonly ConsoleHomeRepositionPolicy.Bounds SecondaryWork = new(-2560, 0, 2560, 1400);

    [Fact]
    public void Leaving_Console_mode_returns_to_the_saved_bounds_while_their_monitor_is_still_present()
    {
        var original = new ConsoleHomeRepositionPolicy.Bounds(-2200, 100, 1180, 820); // on the left secondary
        var both = new[] { PrimaryWork, SecondaryWork };

        Assert.Equal(original, ConsoleHomeRepositionPolicy.SafeRestoreBounds(original, both, PrimaryWork));
    }

    [Fact]
    public void Leaving_Console_mode_after_the_original_monitor_was_unplugged_never_restores_off_screen()
    {
        var original = new ConsoleHomeRepositionPolicy.Bounds(-2200, 100, 1180, 820); // monitor gone: only the primary remains

        var restored = ConsoleHomeRepositionPolicy.SafeRestoreBounds(original, new[] { PrimaryWork }, PrimaryWork);

        Assert.NotEqual(original, restored);
        Assert.True(restored.X >= PrimaryWork.X && restored.X + restored.Width <= PrimaryWork.X + PrimaryWork.Width);
        Assert.True(restored.Y >= PrimaryWork.Y && restored.Y + restored.Height <= PrimaryWork.Y + PrimaryWork.Height);
        Assert.Equal(original.Width, restored.Width); // same size, only moved
    }

    [Theory]
    [InlineData(1800, 100)]    // only 120 px of a 1180 px window left on the primary: not reachable enough
    [InlineData(100, 1000)]    // title bar below the visible work area
    [InlineData(100, -900)]    // above every monitor
    public void A_window_that_is_mostly_off_every_monitor_is_pulled_back(int x, int y)
    {
        var original = new ConsoleHomeRepositionPolicy.Bounds(x, y, 1180, 820);

        var restored = ConsoleHomeRepositionPolicy.SafeRestoreBounds(original, new[] { PrimaryWork }, PrimaryWork);

        Assert.NotEqual(original, restored);
        Assert.InRange(restored.X, PrimaryWork.X, PrimaryWork.X + PrimaryWork.Width - restored.Width);
        Assert.InRange(restored.Y, PrimaryWork.Y, PrimaryWork.Y + PrimaryWork.Height - restored.Height);
    }

    [Fact]
    public void An_oversized_or_invalid_saved_window_is_shrunk_to_the_primary_and_never_enlarged()
    {
        var oversized = new ConsoleHomeRepositionPolicy.Bounds(-5000, -5000, 4000, 3000);
        var restored = ConsoleHomeRepositionPolicy.SafeRestoreBounds(oversized, new[] { PrimaryWork }, PrimaryWork);
        Assert.True(restored.Width <= PrimaryWork.Width && restored.Height <= PrimaryWork.Height);

        var invalid = new ConsoleHomeRepositionPolicy.Bounds(0, 0, 0, 0);
        var fallback = ConsoleHomeRepositionPolicy.SafeRestoreBounds(invalid, Array.Empty<ConsoleHomeRepositionPolicy.Bounds>(), PrimaryWork);
        Assert.True(fallback.Width > 0 && fallback.Height > 0);
        Assert.InRange(fallback.X, PrimaryWork.X, PrimaryWork.X + PrimaryWork.Width - fallback.Width);
    }
}
