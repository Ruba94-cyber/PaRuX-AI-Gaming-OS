using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A 16 GiB office PC can satisfy the *static* Corporate profile ceiling and yet
/// have too little live headroom to load a 2.9 GiB CPU model. AUTO must not
/// promote that static fit to a launch decision. These tests use synthetic RAM
/// snapshots, not a customer's machine or production documents.
/// </summary>
public sealed class CorporateAutoMemoryRoutingTests
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    [Fact]
    public void Auto_uses_live_ram_when_static_capacity_would_wrongly_admit_the_bundled_model()
    {
        const double neededMib = 2.9 * 1024;
        HardwareFitEstimate capacity = CorporateFitPolicy.Estimate(neededMib, 16L * (long)GiB, 86);
        HardwareFitEstimate? live = CorporateFitPolicy.EstimateLive(
            neededMib, new SystemRamSnapshot(16 * GiB, 4 * GiB), 86);

        Assert.True(capacity.Compatible);
        Assert.NotNull(live);
        Assert.False(live.Compatible);
        Assert.Equal((16 * .86 - 12) * 1024, live.AvailableMib, 3);
    }

    [Fact]
    public void Auto_selects_only_models_with_confirmed_current_cpu_headroom()
    {
        SystemRamSnapshot snapshot = new(16 * GiB, 4 * GiB);
        HardwareFitEstimate? small = CorporateFitPolicy.EstimateLive(1024, snapshot, 86);
        HardwareFitEstimate? big = CorporateFitPolicy.EstimateLive(2.9 * 1024, snapshot, 86);
        Assert.True(small!.Compatible);
        Assert.False(big!.Compatible);

        var candidates = new List<CandidateEvidenceDto>
        {
            Candidate("bundled-4b.gguf", big.Compatible),
            Candidate("light-1b.gguf", small.Compatible),
            Candidate("unknown.gguf", null)
        };
        IReadOnlyList<CandidateEvidenceDto> eligible = SmartRouterService.EligibleCorporateAutoCandidates(candidates);

        Assert.Single(eligible);
        Assert.Equal("light-1b.gguf", eligible[0].FileName);
        Assert.Equal(3, candidates.Count); // no saved/manual role mutation
    }

    [Fact]
    public void Auto_refuses_when_every_candidate_is_unfittable_or_memory_is_unmeasurable()
    {
        Assert.Empty(SmartRouterService.EligibleCorporateAutoCandidates(new[]
        {
            Candidate("4b.gguf", false), Candidate("unknown.gguf", null)
        }));
        Assert.Null(CorporateFitPolicy.EstimateLive(100, null, 86));
        Assert.Null(CorporateFitPolicy.EstimateLive(100, new SystemRamSnapshot(0, 0), 86));
    }

    [Fact]
    public void Live_fit_credits_only_explicitly_supplied_reclaimable_memory_and_keeps_profile_limit()
    {
        var snapshot = new SystemRamSnapshot(16 * GiB, 1 * GiB);
        HardwareFitEstimate? withoutResident = CorporateFitPolicy.EstimateLive(1600, snapshot, 86);
        HardwareFitEstimate? withResident = CorporateFitPolicy.EstimateLive(
            1600, snapshot, 86, reclaimableOwnedTextBytes: 3L * (long)GiB);

        Assert.NotNull(withoutResident);
        Assert.False(withoutResident.Compatible);
        Assert.NotNull(withResident);
        Assert.True(withResident.Compatible);
        Assert.True(withResident.AvailableMib < 2 * 1024);
    }

    [Fact]
    public void Capacity_hint_remains_deterministic_and_profile_cap_is_never_widened()
    {
        var busy = new SystemRamSnapshot(16 * GiB, 2 * GiB);
        HardwareFitEstimate? live = CorporateFitPolicy.EstimateLive(512, busy, 80);
        Assert.NotNull(live);
        Assert.False(live.Compatible);
        Assert.Equal(0, live.AvailableMib, 3);
        Assert.True(CorporateFitPolicy.Estimate(512, 16L * (long)GiB, 80).Compatible);
    }

    [Fact]
    public void Routing_source_uses_live_fit_only_for_automatic_corporate_candidates()
    {
        string router = Read("src", "PaRuX.Server", "SmartRouterService.cs");
        string runtime = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string launch = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");

        Assert.Contains("CorporateMode.IsActive && includeManagedLibrary", router, StringComparison.Ordinal);
        Assert.Contains("SampleCorporateAutoRam()", router, StringComparison.Ordinal);
        Assert.Contains("EstimateCorporateAutoFit(path, corporateRam)", router, StringComparison.Ordinal);
        Assert.Contains("EligibleCorporateAutoCandidates(candidates)", router, StringComparison.Ordinal);
        Assert.Contains("EstimateHardwareFitAsync(path, ct)", router, StringComparison.Ordinal);
        Assert.Contains("GetReclaimableTextWorkingSetBytes()", runtime, StringComparison.Ordinal);
        Assert.Contains("SystemRamSnapshot? ram = ExecutionResourceGuard.TryGetSystemRamSnapshot()", launch, StringComparison.Ordinal);
        Assert.Contains("CorporateCpuMemoryPlanner.ForModel(model, ram, memoryCeiling)", launch, StringComparison.Ordinal);
    }

    private static CandidateEvidenceDto Candidate(string name, bool? fit) => new()
    {
        FilePath = Path.Combine("synthetic", name),
        FileName = name,
        IsCurrentAssignment = false,
        HardwareCompatible = fit
    };

    private static string Read(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray()));
    }
}
