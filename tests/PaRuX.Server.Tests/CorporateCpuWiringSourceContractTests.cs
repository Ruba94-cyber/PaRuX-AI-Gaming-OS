using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Narrow source contracts for pieces of the CPU first-run status path that
/// otherwise used separate CUDA/Vulkan assumptions. Pure policy tests cover
/// behavior; native process tests must still run on the Windows build PC.
/// </summary>
public sealed class CorporateCpuWiringSourceContractTests
{
    [Fact]
    public void Corporate_status_inventory_and_onboarding_all_resolve_same_cpu_backend()
    {
        string runtime = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string inventory = Read("src", "PaRuX.Server", "ComponentInventoryService.cs");
        string onboarding = Read("src", "PaRuX.App", "Views", "OnboardingView.xaml.cs");
        string state = Read("src", "PaRuX.App", "Services", "OnboardingStateMachine.cs");

        Assert.Contains("DescribeLlamaBackend(\n                \"text-cpu\"", runtime, StringComparison.Ordinal);
        Assert.Contains("Backend == \"text-cpu\"", inventory, StringComparison.Ordinal);
        Assert.Contains("Id = \"llama-server-cpu\"", inventory, StringComparison.Ordinal);
        Assert.Contains("Actions = Array.Empty<string>()", inventory, StringComparison.Ordinal);
        Assert.Contains("OnboardingTextReadinessPolicy.HasInstalledRuntime", onboarding, StringComparison.Ordinal);
        Assert.Contains("OnboardingTextReadinessPolicy.HasHealthyTextBackend", onboarding, StringComparison.Ordinal);
        Assert.Contains("\"llama-server-cpu\"", state, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_model_routing_uses_real_starter_and_no_corporate_vision_claims()
    {
        string paths = Read("src", "PaRuX.Server", "LocalAiPaths.cs");
        string roles = Read("src", "PaRuX.Server", "Program.cs");
        Assert.Contains("using PaRuX.Contracts;", paths, StringComparison.Ordinal);
        Assert.Contains("CorporateStandardModelPath()", paths, StringComparison.Ordinal);
        Assert.Contains("CorporateTextModelChoicePolicy.Resolve(", paths, StringComparison.Ordinal);
        Assert.Contains("!CorporateMode.IsActive && IsFallbackOnlyModel(modelPath)", paths, StringComparison.Ordinal);
        Assert.Contains("HasVisionSupport = !CorporateMode.IsActive", roles, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_diagnostics_and_verification_do_not_demand_cuda_vulkan_or_media()
    {
        string runtime = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string onboarding = Read("src", "PaRuX.App", "Views", "OnboardingView.xaml.cs");
        Assert.Contains("if (CorporateMode.IsActive)", runtime, StringComparison.Ordinal);
        Assert.Contains("Bundled Corporate CPU runtime missing", runtime, StringComparison.Ordinal);
        Assert.Contains("PaRuX.Contracts.CorporateMode.IsActive ? 7 : 3", onboarding, StringComparison.Ordinal);
        Assert.Contains("OnboardingTextReadinessPolicy.MissingModelMessage(", onboarding, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray()));
    }
}
