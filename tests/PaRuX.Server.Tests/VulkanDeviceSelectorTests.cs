using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VulkanDeviceSelectorTests
{
    // Real `llama-server --list-devices` output measured on the RTX 5080 + Radeon 780M development PC.
    private const string MeasuredOutput =
        "ggml_vulkan: Found 2 Vulkan devices\r\nAvailable devices:\r\n" +
        "  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n" +
        "  Vulkan1: NVIDIA GeForce RTX 5080 (16193 MiB, 15424 MiB free)\r\n";

    private static GpuInventoryStatus Inventory(params (string Name, long DedicatedBytes)[] adapters) => new()
    {
        Available = true,
        Adapters = adapters.Select((a, i) => new GpuAdapterStatus
        {
            Index = i, Name = a.Name, DedicatedVideoMemoryBytes = a.DedicatedBytes
        }).ToList()
    };

    [Fact]
    public void Parses_the_measured_device_list_and_ignores_other_lines()
    {
        var devices = VulkanDeviceSelector.ParseDevices(MeasuredOutput);
        Assert.Equal(2, devices.Count);
        Assert.Equal(new VulkanDeviceSelector.VulkanDevice(1, "NVIDIA GeForce RTX 5080", 16193), devices[1]);
        Assert.Empty(VulkanDeviceSelector.ParseDevices(null));
        Assert.Empty(VulkanDeviceSelector.ParseDevices("no devices here"));
    }

    [Fact]
    public void Discrete_gpu_wins_over_the_integrated_vulkan0()
    {
        var devices = VulkanDeviceSelector.ParseDevices(MeasuredOutput);
        var inventory = Inventory(("AMD Radeon(TM) 780M Graphics", 512L << 20), ("NVIDIA GeForce RTX 5080", 16L << 30));
        Assert.Equal("Vulkan1", VulkanDeviceSelector.Choose(devices, inventory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Falls_back_to_vulkan0_when_nothing_can_be_matched(bool inventoryAvailable)
    {
        var devices = VulkanDeviceSelector.ParseDevices(MeasuredOutput);
        var inventory = Inventory(("Some Other Adapter", 8L << 30));
        inventory.Available = inventoryAvailable;
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Choose(devices, inventory));
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Choose([], inventory));
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Choose(devices, null));
    }

    [Fact]
    public void A_single_device_keeps_vulkan0()
    {
        var devices = VulkanDeviceSelector.ParseDevices("  Vulkan0: NVIDIA GeForce RTX 5080 (16193 MiB, 15424 MiB free)");
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Choose(devices, Inventory(("NVIDIA GeForce RTX 5080", 16L << 30))));
    }
}

public sealed class AgentContextWindowTests
{
    [Theory]
    [InlineData("coding", 8192, true)]
    [InlineData("long-context", 8192, true)]
    [InlineData("coding", 16384, false)]
    [InlineData("coding", 65536, false)]
    [InlineData("chat", 8192, false)]
    [InlineData("coding", 0, false)] // unknown window: never force a restart
    public void A_resident_server_with_a_small_window_is_not_reused_for_coding(string workload, int perSlot, bool tooSmall) =>
        Assert.Equal(tooSmall, PaRuX.Server.LocalAiProcessManager.WindowTooSmallForWorkload(workload, perSlot));
}
