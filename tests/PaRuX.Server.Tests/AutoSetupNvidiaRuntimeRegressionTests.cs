using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutoSetupNvidiaRuntimeRegressionTests
{
    private static string Root()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PaRuX.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found.");
    }

    [Fact]
    public void Automatic_setup_offers_cuda_before_vulkan_on_an_nvidia_gpu()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "src", "PaRuX.App", "Views", "OnboardingView.AutoSetup.cs"));
        int cuda = source.IndexOf("active == null && nvidia && cuda is { Skipped: false }", StringComparison.Ordinal);
        int vulkan = source.IndexOf("No text runtime was found. PaRuX can install its Vulkan runtime.", StringComparison.Ordinal);
        Assert.True(cuda >= 0, "The NVIDIA branch is missing.");
        Assert.True(vulkan > cuda, "Vulkan must only be the baseline when no NVIDIA GPU takes the CUDA branch.");
    }

    [Fact]
    public void Vulkan_launch_does_not_hardcode_the_first_adapter()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "src", "PaRuX.Server", "RuntimeLaunchOptimizer.cs"));
        Assert.Contains("VulkanDeviceSelector.ResolveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.Device = \"Vulkan0\";", source, StringComparison.Ordinal);
    }
}
