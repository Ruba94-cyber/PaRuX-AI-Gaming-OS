using System.Net;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure monitor normalization tests; no physical display changes or drivers.</summary>
public sealed class GamingDisplayTopologyTests
{
    [Fact]
    public void Reads_primary_first_and_deduplicates_duplicate_monitor_handles()
    {
        var raw = new GamingDisplayModeDto[]
        {
            new(@"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 144),
            new(@"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 60),
            new(@"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 60)
        };
        var items = GamingDisplayTopologyService.NormalizeModes(raw);
        Assert.Equal(2, items.Length);
        Assert.True(items[0].Primary);
        Assert.Equal(1920, items[0].Width);
        Assert.Equal(144, items[1].RefreshRateHz);
    }

    [Theory]
    [InlineData("", 1920, 1080, 60, false)]
    [InlineData("screen", 0, 1080, 60, false)]
    [InlineData("screen", 1920, -1, 60, false)]
    [InlineData("screen", 1920, 1080, 1001, false)]
    [InlineData("screen", 1920, 1080, 0, true)]
    [InlineData("screen", 3840, 2160, 120, true)]
    public void Rejects_invalid_modes_without_inventing_refresh(string id, int width, int height, int hz, bool valid)
    {
        Assert.Equal(valid, GamingDisplayTopologyService.ValidMode(
            new GamingDisplayModeDto(id, false, 0, 0, width, height, hz)));
    }

    [Fact]
    public void Returned_monitor_count_is_bounded()
    {
        var raw = Enumerable.Range(0, 100)
            .Select(i => new GamingDisplayModeDto("DISPLAY" + i, i == 0, i * 1920, 0, 1920, 1080, 60));
        Assert.Equal(16, GamingDisplayTopologyService.NormalizeModes(raw).Length);
    }

    [Fact]
    public void A_selected_TV_is_the_only_valid_console_fullscreen_destination()
    {
        var displays = new GamingDisplaySnapshotDto(true, "Windows", new[]
        {
            new GamingDisplayModeDto(@"\\.\DISPLAY1", true, 0, 0, 2560, 1440, 144),
            new GamingDisplayModeDto(@"\\.\DISPLAY2", false, 2560, 0, 3840, 2160, 120),
            new GamingDisplayModeDto(@"\\.\DISPLAY3", false, -1920, 0, 1920, 1080, 60)
        });
        ConsoleDisplayAssignment[] roles =
        {
            new(@"\\.\DISPLAY1", ConsoleDisplaySelectionPolicy.MonitorRole),
            new(@"\\.\DISPLAY2", ConsoleDisplaySelectionPolicy.TvRole),
            new(@"\\.\DISPLAY3", ConsoleDisplaySelectionPolicy.MonitorRole)
        };

        var target = ConsoleDisplaySelectionPolicy.SelectTarget(displays, roles, @"\\.\DISPLAY2");
        Assert.NotNull(target);
        Assert.Equal(3840, target.Width);
        Assert.Equal(2560, target.X);
        Assert.False(target.Primary);

        // Never choose the primary monitor merely because it exists.
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(displays, roles, @"\\.\DISPLAY1"));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(displays, roles, @"\\.\DISPLAY4"));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(displays, roles, ""));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(null, roles, @"\\.\DISPLAY2"));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(
            displays with { Supported = false }, roles, @"\\.\DISPLAY2"));
    }

    [Fact]
    public void TV_disconnection_or_role_change_fails_closed_without_automatic_monitor_rerouting()
    {
        var connected = new GamingDisplaySnapshotDto(true, "Connected", new[]
        {
            new GamingDisplayModeDto("DISPLAY1", true, 0, 0, 1920, 1080, 60)
        });
        var tv = new[] { new ConsoleDisplayAssignment("DISPLAY2", ConsoleDisplaySelectionPolicy.TvRole) };
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(connected, tv, "DISPLAY2"));
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(connected,
            new[] { new ConsoleDisplayAssignment("DISPLAY1", ConsoleDisplaySelectionPolicy.MonitorRole) }, "DISPLAY1"));
        Assert.True(ConsoleDisplaySelectionPolicy.ValidDisplayId("DISPLAY2"));
        Assert.False(ConsoleDisplaySelectionPolicy.ValidDisplayId("DISPLAY\n2"));
        Assert.False(ConsoleDisplaySelectionPolicy.ValidRole("projector"));
        // A hand-edited settings file may contain contradictory roles for the
        // same Windows device. Never pick a destination from ambiguous input.
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(connected,
            new[]
            {
                new ConsoleDisplayAssignment("DISPLAY1", ConsoleDisplaySelectionPolicy.TvRole),
                new ConsoleDisplayAssignment("DISPLAY1", ConsoleDisplaySelectionPolicy.MonitorRole)
            }, "DISPLAY1"));
    }

    [Fact]
    public void Missing_platform_or_monitor_does_not_claim_HDR_or_available_displays()
    {
        var service = new GamingDisplayTopologyService(() => Array.Empty<GamingDisplayModeDto>());
        GamingDisplaySnapshotDto snapshot = service.Snapshot();
        Assert.False(snapshot.Supported);
        Assert.Empty(snapshot.Displays);
        Assert.DoesNotContain("HDR supported", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GamingDisplayTopologySecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GamingDisplayTopologySecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Display_topology_requires_local_desktop_owner()
    {
        using var client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/gaming/displays");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
