using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHubQuantAdviceTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static ModelQuantChoice Q(string quant, double gib, string set) => new()
    {
        Quantization = quant, SetName = set, SizeBytes = (long)(gib * Gib), Complete = true, PartCount = 1
    };

    private static IReadOnlyList<ModelHubSplitSetPresentation.QuantChoiceOption> Options() =>
        ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            Q("Q4_K_M", 4.9, "m-Q4_K_M"), Q("Q5_K_M", 5.7, "m-Q5_K_M"), Q("Q8_0", 8.5, "m-Q8_0"), Q("Q3_K_M", 3.9, "m-Q3_K_M")
        });

    private static RuntimeManagerHardwareStatus Hw(int vramMib, int? freeMib, long ramGib) => new()
    {
        GpuDetected = vramMib > 0, VramTotalMib = vramMib, VramFreeMib = freeMib, RamTotalBytes = ramGib * Gib
    };

    [Fact]
    public void Gpu16Gb_StepsUpToQ5_AndExplainsInBothLanguages()
    {
        var options = Options();
        var hw = Hw(16303, 14000, 32);   // free 13.67 GiB -> budget 12.17: Q5 5.7 <= 9.1 ok, Q8 8.5 > 6.1 no
        var en = ModelHubQuantAdvice.Build(options, hw, QuantTask.Technical, italian: false);
        var it = ModelHubQuantAdvice.Build(options, hw, QuantTask.Technical, italian: true);
        Assert.Equal("Q5_K_M", options[en.PreferredIndex!.Value].Choice.Quantization);
        Assert.Equal(en.PreferredIndex, it.PreferredIndex);
        Assert.StartsWith("Recommended for this PC: Q5_K_M", en.Text);
        Assert.StartsWith("Consigliato per questo PC: Q5_K_M", it.Text);
    }

    [Fact]
    public void SmallGpu_WithRamSpill_StaysAtTheFloor()
    {
        var options = Options();
        var d = ModelHubQuantAdvice.Build(options, Hw(4096, 3500, 16), QuantTask.Technical, italian: false);
        Assert.Equal("Q4_K_M", options[d.PreferredIndex!.Value].Choice.Quantization);
        Assert.Equal(QuantPlacementPlan.GpuPlusRam, d.Decision.Placement);
    }

    [Fact]
    public void NoHardware_RecommendsNothing()
    {
        var d = ModelHubQuantAdvice.Build(Options(), null, QuantTask.Technical, italian: true);
        Assert.Null(d.PreferredIndex);
        Assert.Contains("Scegline uno tu", d.Text);
    }

    [Fact]
    public void Ineligible_physical_gpu_does_not_count_as_runnable_text_vram()
    {
        var hw = Hw(16 * 1024, 14 * 1024, 16);
        hw.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = false,
            DedicatedVideoMemoryBytes = 16 * Gib,
            Backend = "cpu",
            UsableSystemRamBytes = 8 * Gib
        };

        QuantBudget budget = ModelHubQuantAdvice.BudgetFor(hw);
        Assert.Equal(0, budget.GpuFreeBytes);
        Assert.Equal(8 * Gib, budget.RamFreeBytes);
        var advice = ModelHubQuantAdvice.Build(Options(), hw, QuantTask.Technical, italian: false);
        Assert.NotEqual(QuantPlacementPlan.FullGpu, advice.Decision.Placement);
    }

    [Fact]
    public void Shared_memory_integrated_gpu_never_counts_ram_twice_or_claims_cpu_only()
    {
        var hw = Hw(16 * 1024, 14 * 1024, 16);
        hw.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = true,
            SharedMemoryPrimary = true,
            Backend = "text-vulkan",
            SharedSystemMemoryLimitBytes = 8 * Gib,
            UsableSystemRamBytes = 12 * Gib
        };

        QuantBudget budget = ModelHubQuantAdvice.BudgetFor(hw);
        Assert.Equal(0, budget.GpuFreeBytes);
        Assert.Equal(12 * Gib, budget.RamFreeBytes);
        var en = ModelHubQuantAdvice.Build(Options(), hw, QuantTask.Technical, italian: false);
        var it = ModelHubQuantAdvice.Build(Options(), hw, QuantTask.Technical, italian: true);
        Assert.NotNull(en.PreferredIndex);
        Assert.Equal(QuantPlacementPlan.Unknown, en.Decision.Placement);
        Assert.DoesNotContain("runs on the CPU", en.Text);
        Assert.Contains("Shared-memory GPU", en.Text);
        Assert.Contains("memoria condivisa", it.Text);
        Assert.Equal(en.PreferredIndex, it.PreferredIndex);
    }

    [Fact]
    public void Free_gpu_memory_telemetry_never_exceeds_normalized_adapter_capacity()
    {
        var hw = Hw(32 * 1024, 24 * 1024, 32);
        hw.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = true,
            Backend = "text-cuda",
            DedicatedVideoMemoryBytes = 8 * Gib
        };
        Assert.Equal(8 * Gib, ModelHubQuantAdvice.BudgetFor(hw).GpuFreeBytes);
        hw.VramFreeMib = -1;
        Assert.InRange(ModelHubQuantAdvice.BudgetFor(hw).GpuFreeBytes,
            0, 8 * Gib);
        hw.VramFreeMib = 100;
        Assert.Equal(100L * 1024 * 1024, ModelHubQuantAdvice.BudgetFor(hw).GpuFreeBytes);
    }

    [Fact]
    public void Missing_hardware_memory_does_not_claim_a_model_will_not_fit()
    {
        var hw = Hw(0, null, 0);
        var advice = ModelHubQuantAdvice.Build(Options(), hw, QuantTask.General, italian: false);
        Assert.Null(advice.PreferredIndex);
        Assert.Contains("not measured", advice.Text);
        Assert.Equal(QuantDecisionStatus.NoUsableVariant, advice.Decision.Status);
    }

    [Fact]
    public void ManualPick_WinsAndIsNotReplacedByTheRecommendation()
    {
        var options = Options();
        var d = ModelHubQuantAdvice.Build(options, Hw(16303, 14000, 32), QuantTask.Technical, italian: false, pinnedSetName: "m-Q3_K_M");
        Assert.Equal(QuantDecisionStatus.UserChoice, d.Decision.Status);
        Assert.Equal("Q3_K_M", options[d.PreferredIndex!.Value].Choice.Quantization);
        Assert.True(d.Decision.BelowQualityFloor);
    }
}
