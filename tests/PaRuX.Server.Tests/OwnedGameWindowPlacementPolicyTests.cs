using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pure, deterministic safety tests. No game is started, no GUI window moved and
/// no Windows desktop display settings are touched by these tests.
/// </summary>
public sealed class OwnedGameWindowPlacementPolicyTests
{
    private static OwnedGameWindowPlacementService.WindowCandidate Window(
        long handle, int pid = 42, bool visible = true, bool owned = false,
        bool tool = false, bool minimized = false, bool maximized = false,
        bool caption = true) => new(new IntPtr(handle), pid, visible, owned,
            tool, minimized, maximized, caption);

    [Fact]
    public void Selects_only_one_visible_windowed_top_level_window_of_exact_owned_pid()
    {
        var candidates = new[]
        {
            Window(1, pid: 4100),
            Window(2, pid: 42),
            Window(3, pid: 42, tool: true),
            Window(4, pid: 42, owned: true),
            Window(5, pid: 42, minimized: true),
            Window(6, pid: 42, maximized: true),
            Window(7, pid: 42, caption: false),
            Window(8, pid: 42, visible: false)
        };
        Assert.Equal(new IntPtr(2),
            OwnedGameWindowPlacementService.SelectUniqueWindow(candidates, 42));
        Assert.Equal(IntPtr.Zero,
            OwnedGameWindowPlacementService.SelectUniqueWindow(candidates, 99));
    }

    [Fact]
    public void Refuses_to_guess_when_game_has_multiple_eligible_windows_or_none()
    {
        Assert.Equal(IntPtr.Zero,
            OwnedGameWindowPlacementService.SelectUniqueWindow(new[]
            {
                Window(1), Window(2)
            }, 42));
        Assert.Equal(IntPtr.Zero,
            OwnedGameWindowPlacementService.SelectUniqueWindow(new[]
            {
                Window(1, caption: false), Window(2, tool: true)
            }, 42));
        Assert.Equal(IntPtr.Zero,
            OwnedGameWindowPlacementService.SelectUniqueWindow(Array.Empty<OwnedGameWindowPlacementService.WindowCandidate>(), 42));
    }

    [Theory]
    [InlineData(1920, 0, 3840, 2160, 1280, 720, 3200, 720)]
    [InlineData(-3840, -2160, 3840, 2160, 1920, 1080, -2880, -1620)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080, 0, 0)]
    public void Centers_window_on_selected_monitor_without_reordering_or_resizing_windows(
        int x, int y, int monitorWidth, int monitorHeight,
        int windowWidth, int windowHeight, int expectedX, int expectedY)
    {
        var monitor = new GamingDisplayModeDto("DISPLAY2", false, x, y,
            monitorWidth, monitorHeight, 60);
        var position = OwnedGameWindowPlacementService.CenterUnchangedWindow(
            monitor, windowWidth, windowHeight);
        Assert.NotNull(position);
        Assert.Equal((expectedX, expectedY), position.Value);
    }

    [Fact]
    public void Rejects_oversized_or_invalid_windows_instead_of_clipping_them_across_other_monitors()
    {
        var monitor = new GamingDisplayModeDto("DISPLAY2", false, 1920, 0, 1920, 1080, 60);
        Assert.Null(OwnedGameWindowPlacementService.CenterUnchangedWindow(monitor, 3840, 2160));
        Assert.Null(OwnedGameWindowPlacementService.CenterUnchangedWindow(monitor, 0, 720));
        Assert.Null(OwnedGameWindowPlacementService.CenterUnchangedWindow(monitor, 1280, -1));
    }

    [Fact]
    public void Optional_placement_result_preserves_legacy_manual_launch_json()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ManualGameLaunchResult>(
            """{"Launched":true,"AutomaticReturnOnExit":true,"Message":"Game started"}""");
        Assert.NotNull(legacy);
        Assert.Equal(string.Empty, legacy.WindowPlacement);
        var placed = legacy with { WindowPlacement = "placed" };
        Assert.Contains("placed", System.Text.Json.JsonSerializer.Serialize(placed));
    }
}
