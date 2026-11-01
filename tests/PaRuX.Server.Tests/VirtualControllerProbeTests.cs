using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VirtualControllerProbeTests
{
    [Fact]
    public void Installed_driver_is_reported_but_input_forwarding_is_never_claimed()
    {
        var status = VirtualControllerProbe.Probe(() => true);
        Assert.True(status.DriverInstalled);
        Assert.False(status.CanForwardInput);
        Assert.Contains("does not forward", status.Message);
    }

    [Fact]
    public void Missing_driver_says_it_is_optional_and_never_installed_automatically()
    {
        var status = VirtualControllerProbe.Probe(() => false);
        Assert.False(status.DriverInstalled);
        Assert.False(status.CanForwardInput);
        Assert.Contains("optional", status.Message);
        Assert.Contains("never installs", status.Message);
    }

    [Fact]
    public void Native_failures_are_reported_as_not_installed_not_as_crashes()
    {
        Assert.False(VirtualControllerProbe.Probe(() => throw new DllNotFoundException()).DriverInstalled);
        Assert.False(VirtualControllerProbe.Probe(() => throw new EntryPointNotFoundException()).DriverInstalled);
    }

    [Fact]
    public void The_real_windows_query_runs_without_throwing()
    {
        // No assertion on the value: it depends on the PC. The point is that the SetupAPI call is safe to make.
        var status = VirtualControllerProbe.Probe();
        Assert.Equal("ViGEmBus", status.Backend);
        Assert.False(status.CanForwardInput);
    }
}
