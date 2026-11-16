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

    [Fact]
    public void Corporate_auto_role_and_exact_loads_never_choose_gpu_runtime()
    {
        string manager = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string process = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");

        int finalGate = manager.IndexOf("private async Task<HardwareFitEstimate?> EstimateHardwareFitForAdmissionAsync(", StringComparison.Ordinal);
        int cpuGuard = manager.IndexOf("if (CorporateMode.IsActive)", finalGate, StringComparison.Ordinal);
        int gpuProbe = manager.IndexOf("_process.GetVramMibAsync(ct)", finalGate, StringComparison.Ordinal);
        Assert.True(finalGate > 0 && cpuGuard > finalGate && gpuProbe > cpuGuard,
            "Final corporate admission must use CPU live fit before reading GPU telemetry.");

        Assert.Contains("CorporateMode.IsActive || useRequestedModelPath", manager, StringComparison.Ordinal);
        Assert.Contains("resident && !CorporateMode.IsActive", manager, StringComparison.Ordinal);

        int arbitrary = process.IndexOf("private async Task<RuntimeResult> StartArbitraryTextModelCoreAsync(", StringComparison.Ordinal);
        int corporate = process.IndexOf("if (CorporateMode.IsActive)", arbitrary, StringComparison.Ordinal);
        int cpuLaunch = process.IndexOf("StartCorporateCpuTextAsync(ct, modelPath)", corporate, StringComparison.Ordinal);
        int gpuRuntime = process.IndexOf("LocalAiPaths.ResolveGpuTextRuntimePath(hasGpu)", arbitrary, StringComparison.Ordinal);
        Assert.True(arbitrary > 0 && corporate > arbitrary && cpuLaunch > corporate && gpuRuntime > cpuLaunch,
            "Corporate arbitrary and exact candidate launches must reach the bundled CPU engine before GPU runtime selection.");
        Assert.Contains("Corporate CPU mode does not support LoRA adapter launches", process, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
