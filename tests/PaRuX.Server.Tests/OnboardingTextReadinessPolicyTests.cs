using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OnboardingTextReadinessPolicyTests
{
    [Fact]
    public void Corporate_accepts_only_the_preinstalled_cpu_component()
    {
        var gpu = new[] { new ComponentStatus { Id = "llama-server-vulkan", Detected = true } };
        var cpu = new[] { new ComponentStatus { Id = "llama-server-cpu", Detected = true } };
        Assert.False(OnboardingTextReadinessPolicy.HasInstalledRuntime(gpu, corporateMode: true));
        Assert.True(OnboardingTextReadinessPolicy.HasInstalledRuntime(cpu, corporateMode: true));
        Assert.False(OnboardingTextReadinessPolicy.HasInstalledRuntime(cpu, corporateMode: false));
        Assert.True(OnboardingTextReadinessPolicy.HasInstalledRuntime(gpu, corporateMode: false));
        Assert.False(OnboardingTextReadinessPolicy.HasInstalledRuntime(
            new[] { new ComponentStatus { Id = "llama-server-cpu", Detected = false } }, true));
    }

    [Fact]
    public void Backend_must_be_both_running_and_healthy_no_false_installed_ready_signal()
    {
        var sleeping = new[] { new RuntimeBackendStatus
            { Backend = "text-cpu", Installed = true, Running = false, Healthy = false } };
        Assert.False(OnboardingTextReadinessPolicy.HasHealthyTextBackend(sleeping, corporateMode: true));

        var unhealthy = new[] { new RuntimeBackendStatus
            { Backend = "text-cpu", Installed = true, Running = true, Healthy = false } };
        Assert.False(OnboardingTextReadinessPolicy.HasHealthyTextBackend(unhealthy, corporateMode: true));

        var running = new[] { new RuntimeBackendStatus
            { Backend = "text-cpu", Installed = true, Running = true, Healthy = true } };
        Assert.True(OnboardingTextReadinessPolicy.HasHealthyTextBackend(running, corporateMode: true));
        Assert.False(OnboardingTextReadinessPolicy.HasHealthyTextBackend(running, corporateMode: false));

        var gpu = new[] { new RuntimeBackendStatus
            { Backend = "text-vulkan", Installed = true, Running = true, Healthy = true } };
        Assert.True(OnboardingTextReadinessPolicy.HasHealthyTextBackend(gpu, corporateMode: false));
        Assert.False(OnboardingTextReadinessPolicy.HasHealthyTextBackend(gpu, corporateMode: true));
    }

    [Fact]
    public void Corporate_missing_runtime_message_does_not_suggest_downloading_an_executable()
    {
        string english = OnboardingTextReadinessPolicy.MissingRuntimeMessage(true, false);
        Assert.Contains("Re-extract", english);
        Assert.Contains("CPU runtime", english);
        Assert.DoesNotContain("install a local text runtime", english, StringComparison.OrdinalIgnoreCase);
    }
}
