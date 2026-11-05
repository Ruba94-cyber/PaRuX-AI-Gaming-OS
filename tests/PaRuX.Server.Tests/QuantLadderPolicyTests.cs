using System.Collections.Generic;
using System.Linq;
using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuantLadderPolicyTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static QuantCandidate C(string quant, double gib, string? name = null, bool complete = true) =>
        new(name ?? ("model-" + quant), quant, (long)(gib * Gib), complete, name ?? ("model-" + quant));

    // A synthetic 8B-class ladder (sizes follow bits-per-weight; no real files involved).
    private static List<QuantCandidate> Ladder() => new()
    {
        C("Q4_K_M", 4.9), C("Q5_K_M", 5.7), C("Q6_K", 6.6), C("Q8_0", 8.5), C("Q3_K_M", 3.9), C("IQ4_XS", 4.3)
    };

    private static QuantBudget Gpu(double freeGib, double reserveGib = 1.5, double ramFreeGib = 16) =>
        new((long)(freeGib * Gib), (long)(ramFreeGib * Gib), (long)(reserveGib * Gib));

    private static QuantBudget CpuOnly(double ramFreeGib, double reserveGib = 1.5) =>
        new(0, (long)(ramFreeGib * Gib), (long)(reserveGib * Gib));

    [Fact]
    public void TightGpu_ChoosesQ4KM_NotLower_ForTechnical()
    {
        QuantDecision d = QuantLadderPolicy.Decide(Ladder(), Gpu(6.6), QuantTask.Technical);
        Assert.Equal(QuantDecisionStatus.Selected, d.Status);
        Assert.Equal("Q4_K_M", d.Selected!.Quantization);
        Assert.Equal(QuantPlacementPlan.FullGpu, d.Placement);
    }

    [Fact]
    public void SpareVram_StepsUpToQ5_ThenQ6_ThenQ8()
    {
        // budget = free - 1.5. Q5 needs size <= 0.75 budget, Q6 <= 0.65 budget, Q8 <= 0.50 budget.
        Assert.Equal("Q5_K_M", QuantLadderPolicy.Decide(Ladder(), Gpu(9.5), QuantTask.Technical).Selected!.Quantization);   // budget 8.0: Q5 5.7<=6.0 ok, Q6 6.6>5.2
        Assert.Equal("Q6_K", QuantLadderPolicy.Decide(Ladder(), Gpu(12.0), QuantTask.Technical).Selected!.Quantization);    // budget 10.5: Q6 6.6<=6.8 ok, Q8 8.5>5.2
        Assert.Equal("Q8_0", QuantLadderPolicy.Decide(Ladder(), Gpu(20.0), QuantTask.Technical).Selected!.Quantization);    // budget 18.5: Q8 8.5<=9.2 ok
    }

    [Fact]
    public void Technical_NeverPicksBelowQ4KM_EvenIfOnlyLowerFits()
    {
        var onlyLow = new List<QuantCandidate> { C("Q3_K_M", 3.9), C("IQ4_XS", 4.3), C("Q4_K_S", 4.6), C("Q4_K_M", 4.9) };
        QuantDecision d = QuantLadderPolicy.Decide(onlyLow, Gpu(5.0, ramFreeGib: 1), QuantTask.Technical);   // VRAM budget 3.5 + RAM 0.75 < 4.9
        Assert.Equal(QuantDecisionStatus.NothingFitsAtFloor, d.Status);
        Assert.Null(d.Selected);
        Assert.Contains("will not drop", d.ReasonEn);
        Assert.Contains("non scende", d.ReasonIt);
    }

    [Fact]
    public void General_MayUseIq4Xs_ButNeverQ3()
    {
        var set = new List<QuantCandidate> { C("Q3_K_M", 3.9), C("IQ4_XS", 4.3), C("Q4_K_M", 4.9) };
        QuantDecision d = QuantLadderPolicy.Decide(set, Gpu(6.0), QuantTask.General);   // budget 4.5: Q4_K_M no, IQ4_XS yes
        Assert.Equal("IQ4_XS", d.Selected!.Quantization);
        var q3Only = new List<QuantCandidate> { C("Q3_K_M", 3.9), C("Q2_K", 3.0) };
        Assert.Equal(QuantDecisionStatus.NothingFitsAtFloor, QuantLadderPolicy.Decide(q3Only, Gpu(24), QuantTask.General).Status);
    }

    [Fact]
    public void Dynamic_UD_File_IsPreferred_OverPlainSameLabel_AndCanLiftQ4KS_AboveTheTechnicalFloor()
    {
        var set = new List<QuantCandidate>
        {
            C("Q4_K_M", 4.9, "Model-Q4_K_M.gguf"),
            C("Q4_K_M", 5.0, "Model-UD-Q4_K_M.gguf")
        };
        Assert.Equal("Model-UD-Q4_K_M.gguf", QuantLadderPolicy.Decide(set, Gpu(9.0), QuantTask.Technical).Selected!.Id);

        var ks = new List<QuantCandidate> { C("Q4_K_S", 4.6, "Model-Q4_K_S.gguf"), C("Q4_K_S", 4.7, "Model-UD-Q4_K_S.gguf") };
        QuantDecision d = QuantLadderPolicy.Decide(ks, Gpu(8.0), QuantTask.Technical);
        Assert.Equal("Model-UD-Q4_K_S.gguf", d.Selected!.Id);     // plain Q4_K_S (4.58) is under the floor, UD (4.93) is not
        Assert.Contains("Dynamic", d.ReasonEn);
        Assert.Contains("Dynamic", d.ReasonIt);
    }

    [Fact]
    public void Imatrix_BonusAlone_DoesNotLiftIq4XsOverTheTechnicalFloor()
    {
        var set = new List<QuantCandidate> { C("IQ4_XS", 4.3, "Model-i1-IQ4_XS.gguf") };
        Assert.Equal(QuantDecisionStatus.NothingFitsAtFloor, QuantLadderPolicy.Decide(set, Gpu(24), QuantTask.Technical).Status);
        Assert.Equal("IQ4_XS", QuantLadderPolicy.Decide(set, Gpu(24), QuantTask.General).Selected!.Quantization);
    }

    [Fact]
    public void NoFitInVram_SpillsToRam_WithSmallestAcceptableFile()
    {
        QuantDecision d = QuantLadderPolicy.Decide(Ladder(), Gpu(4.0, ramFreeGib: 16), QuantTask.Technical);   // gpu budget 2.5 < 4.9
        Assert.Equal("Q4_K_M", d.Selected!.Quantization);
        Assert.Equal(QuantPlacementPlan.GpuPlusRam, d.Placement);
    }

    [Fact]
    public void CpuOnly_IsCapped_AtQ5_AndUsesRamFraction()
    {
        QuantDecision rich = QuantLadderPolicy.Decide(Ladder(), CpuOnly(64), QuantTask.Technical);
        Assert.Equal("Q5_K_M", rich.Selected!.Quantization);                  // Q6/Q8 never on CPU
        Assert.Equal(QuantPlacementPlan.CpuOnly, rich.Placement);
        QuantDecision tight = QuantLadderPolicy.Decide(Ladder(), CpuOnly(10), QuantTask.Technical);   // ram budget 10*0.75-1.5 = 6.0
        Assert.Equal("Q4_K_M", tight.Selected!.Quantization);                 // Q5 needs <=3.6
        Assert.Equal(QuantDecisionStatus.NothingFitsAtFloor, QuantLadderPolicy.Decide(Ladder(), CpuOnly(6), QuantTask.Technical).Status);
    }

    [Fact]
    public void IncompleteUnknownAndUnsizedFiles_AreNeverChosen()
    {
        var set = new List<QuantCandidate>
        {
            C("Q8_0", 8.5, complete: false), C("unknown", 4.0), new("x", "Q6_K", 0, true, "x"), C("Q4_K_M", 4.9)
        };
        Assert.Equal("Q4_K_M", QuantLadderPolicy.Decide(set, Gpu(24), QuantTask.Technical).Selected!.Quantization);
        var none = new List<QuantCandidate> { C("unknown", 4.0), C("Q8_0", 8.5, complete: false) };
        Assert.Equal(QuantDecisionStatus.NoUsableVariant, QuantLadderPolicy.Decide(none, Gpu(24), QuantTask.Technical).Status);
    }

    [Fact]
    public void UserChoice_AlwaysWins_AndWarnsAboutBudgetAndFloor()
    {
        QuantDecision low = QuantLadderPolicy.Decide(Ladder(), Gpu(24), QuantTask.Technical, "model-Q3_K_M");
        Assert.Equal(QuantDecisionStatus.UserChoice, low.Status);
        Assert.Equal("Q3_K_M", low.Selected!.Quantization);
        Assert.True(low.BelowQualityFloor);
        Assert.False(low.ExceedsBudget);
        Assert.Contains("below", low.ReasonEn);
        Assert.Contains("sotto", low.ReasonIt);

        QuantDecision big = QuantLadderPolicy.Decide(Ladder(), Gpu(3.0, ramFreeGib: 4), QuantTask.General, "model-Q8_0");
        Assert.True(big.ExceedsBudget);
        Assert.Contains("Warning", big.ReasonEn);
        Assert.Contains("Attenzione", big.ReasonIt);
        Assert.Equal("Q8_0", big.Selected!.Quantization);
    }

    [Fact]
    public void Decision_IsDeterministic_AndIndependentOfInputOrder()
    {
        List<QuantCandidate> a = Ladder();
        List<QuantCandidate> b = Ladder().AsEnumerable().Reverse().ToList();
        QuantDecision da = QuantLadderPolicy.Decide(a, Gpu(12), QuantTask.Technical);
        QuantDecision db = QuantLadderPolicy.Decide(b, Gpu(12), QuantTask.Technical);
        Assert.Equal(da.Selected!.Id, db.Selected!.Id);
        Assert.Equal(da.ReasonEn, db.ReasonEn);
        Assert.Equal(da.ReasonIt, db.ReasonIt);
    }

    [Fact]
    public void Reasons_AreNonEmpty_InBothLanguages_AndNameTheFloor()
    {
        QuantDecision d = QuantLadderPolicy.Decide(Ladder(), Gpu(12), QuantTask.Technical);
        Assert.Contains("Q4_K_M", d.ReasonEn);
        Assert.Contains("GiB", d.ReasonEn);
        Assert.Contains("scelto", d.ReasonIt);
        Assert.Contains("Puoi scegliere", d.ReasonIt);
    }

    [Theory]
    [InlineData("Q4_K_M", 4.89)]
    [InlineData("q8_0", 8.5)]
    [InlineData("IQ4_XS", 4.25)]
    public void NominalBitsPerWeight_KnownLabels(string label, double expected) =>
        Assert.Equal(expected, QuantLadderPolicy.NominalBitsPerWeight(label));

    [Fact]
    public void NominalBitsPerWeight_UnknownLabel_IsNull() =>
        Assert.Null(QuantLadderPolicy.NominalBitsPerWeight("unknown"));
}

public sealed class SameFamilyDraftSelectionTests
{
    private const long Mib = 1024L * 1024;
    private static readonly string Target = @"C:\m\Qwen3-8B-Q4_K_M.gguf";

    private static IReadOnlyList<SpeculativeDraftDiscovery.Candidate> Pick(params (string Path, long Size)[] files) =>
        SpeculativeDraftDiscovery.SelectSameFamilySmallModels(Target, 5000 * Mib, files);

    [Fact]
    public void FamilyKey_IgnoresSizeQuantAndInstructMarkers()
    {
        Assert.Equal("qwen3", SpeculativeDraftDiscovery.FamilyKey("Qwen3-8B-Q4_K_M"));
        Assert.Equal("qwen3", SpeculativeDraftDiscovery.FamilyKey("Qwen3-0.6B-Instruct-UD-Q4_K_XL"));
        Assert.Equal("qwen3-coder", SpeculativeDraftDiscovery.FamilyKey("Qwen3-Coder-30B-A3B-Instruct-Q3_K_M"));
        Assert.Equal("qwen3-5", SpeculativeDraftDiscovery.FamilyKey("Qwen3.5-4B-Q4_K_M"));
        Assert.Equal("qwen3", SpeculativeDraftDiscovery.FamilyKey("Qwen3-8B-Q4_K_M-00001-of-00002"));
    }

    [Fact]
    public void SameFamilySmallerModel_IsOffered_AsDraftSimple()
    {
        var picks = Pick((@"C:\m\Qwen3-0.6B-Q8_0.gguf", 640 * Mib));
        SpeculativeDraftDiscovery.Candidate c = Assert.Single(picks);
        Assert.Equal("draft-simple", c.Type);
        Assert.Equal(@"C:\m\Qwen3-0.6B-Q8_0.gguf", c.Path);
    }

    [Fact]
    public void DifferentFamily_TooBig_Projector_Embedding_AndSelf_AreRejected()
    {
        Assert.Empty(Pick(
            (@"C:\m\Qwen3-Coder-0.6B-Q8_0.gguf", 640 * Mib),            // different family
            (@"C:\m\Llama-3.2-1B-Q8_0.gguf", 1200 * Mib),               // different family
            (@"C:\m\Qwen3-4B-Q4_K_M.gguf", 2500 * Mib),                 // too big (>25%)
            (@"C:\m\mmproj-Qwen3-0.6B-F16.gguf", 400 * Mib),            // projector
            (@"C:\m\Qwen3-0.6B-Q8_0.gguf", 10 * Mib),                   // stub
            (Target, 5000 * Mib)));                                      // the target itself
    }

    [Fact]
    public void NamedSizes_MustBeAtLeastThreeTimesSmaller()
    {
        // 4B under an 8B target is only 2x smaller by name even if the file is small (heavily quantised).
        Assert.Empty(Pick((@"C:\m\Qwen3-4B-Q2_K.gguf", 1200 * Mib)));
        Assert.Single(Pick((@"C:\m\Qwen3-1.7B-Q4_K_M.gguf", 1100 * Mib)));
    }

    [Fact]
    public void AtMostTwo_LargestFirst_Deterministic()
    {
        var picks = Pick(
            (@"C:\m\Qwen3-0.6B-Q8_0.gguf", 640 * Mib),
            (@"C:\m\Qwen3-1.7B-Q8_0.gguf", 1100 * Mib),
            (@"C:\m\Qwen3-1.7B-Q4_K_M.gguf", 1000 * Mib));
        Assert.Equal(2, picks.Count);
        Assert.Equal(@"C:\m\Qwen3-1.7B-Q8_0.gguf", picks[0].Path);
        Assert.Equal(@"C:\m\Qwen3-1.7B-Q4_K_M.gguf", picks[1].Path);
    }

    [Fact]
    public void ExistingDraftNamedFiles_AreNotDuplicatedAsPlainDrafts()
    {
        Assert.Empty(Pick((@"C:\m\Qwen3-8B-eagle3-draft.gguf", 300 * Mib)));
    }
}
