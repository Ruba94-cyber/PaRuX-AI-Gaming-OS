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
    public void ManualPick_WinsAndIsNotReplacedByTheRecommendation()
    {
        var options = Options();
        var d = ModelHubQuantAdvice.Build(options, Hw(16303, 14000, 32), QuantTask.Technical, italian: false, pinnedSetName: "m-Q3_K_M");
        Assert.Equal(QuantDecisionStatus.UserChoice, d.Decision.Status);
        Assert.Equal("Q3_K_M", options[d.PreferredIndex!.Value].Choice.Quantization);
        Assert.True(d.Decision.BelowQualityFloor);
    }
}
