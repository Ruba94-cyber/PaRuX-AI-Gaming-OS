using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>PERF-003b: AUTO escalation to a larger model is bounded by memory, by marginal gain and by host constraints, and is explained.</summary>
public sealed class AutoEscalationPolicyTests
{
    private const string LongPrompt = "Analyze the trade-off between a write-ahead log and an LSM tree, reason step by step about the root cause of "
        + "write amplification, compare compaction strategies and prove which one bounds read latency under skewed workloads. "
        + "Be comprehensive about every assumption you make about the storage engine and the workload it serves in production.";

    private static CandidateEvidenceDto Model(string name, double quality, long gb, bool loaded, double? need = null, double? available = null) => new()
    {
        FilePath = "C:\\m\\" + name, FileName = name, IsCurrentAssignment = false, IsCurrentlyLoaded = loaded,
        IntelligenceScore = quality, CapabilityScore = quality, SizeBytes = gb * 1_000_000_000L,
        HardwareCompatible = true, EstimatedNeededMib = need, AvailableMib = available, BenchmarkTokensPerSecond = 150
    };

    private static (CandidateEvidenceDto Selected, string? Reason) Route(
        string autoProfile, bool constrained, params CandidateEvidenceDto[] pool)
    {
        AutoEscalationPolicy.Outcome outcome = AutoEscalationPolicy.Apply(pool, autoProfile, constrained);
        CandidateEvidenceDto selected = CandidatePoolRanker.Rank(outcome.Pool, RouteMode.Auto, "coding", LongPrompt, autoProfile)!;
        return (selected, outcome.Describe(selected));
    }

    [Fact]
    public void A_target_that_needs_more_than_the_available_memory_is_never_chosen_and_the_skip_is_explained()
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.55, 3, loaded: true, need: 4_000, available: 16_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_500, available: 16_000);

        (CandidateEvidenceDto selected, string? reason) = Route(AutoIntelligenceProfile.Balanced, false, small, big);

        Assert.Equal("small-4b.gguf", selected.FileName);
        Assert.Contains("Skipped coder-30b.gguf", reason);
        Assert.Contains("15500", reason);
    }

    [Fact]
    public void A_target_that_fits_and_clearly_outperforms_the_loaded_model_is_still_chosen_and_the_escalation_is_explained()
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.55, 3, loaded: true, need: 4_000, available: 24_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_000, available: 24_000);

        (CandidateEvidenceDto selected, string? reason) = Route(AutoIntelligenceProfile.Balanced, false, small, big);

        Assert.Equal("coder-30b.gguf", selected.FileName);
        Assert.Contains("Escalated from small-4b.gguf to coder-30b.gguf", reason);
        Assert.Contains("+64%", reason);
        Assert.Contains("15000 MiB of 24000 MiB", reason);
    }

    [Fact]
    public void A_marginal_gain_keeps_the_loaded_model()
    {
        CandidateEvidenceDto loaded = Model("mid-14b.gguf", 0.86, 8, loaded: true, need: 9_000, available: 24_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_000, available: 24_000);

        (CandidateEvidenceDto selected, string? reason) = Route(AutoIntelligenceProfile.Balanced, false, loaded, big);

        Assert.Equal("mid-14b.gguf", selected.FileName);
        Assert.Contains("Stayed on mid-14b.gguf", reason);
        Assert.Contains("marginal", reason);
    }

    [Theory]
    [InlineData(AutoIntelligenceProfile.MaxIntelligence)]
    [InlineData(AutoIntelligenceProfile.Custom)]
    public void Profiles_that_pay_for_quality_do_not_apply_the_marginal_rule(string profile)
    {
        CandidateEvidenceDto loaded = Model("mid-14b.gguf", 0.86, 8, loaded: true, need: 9_000, available: 24_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_000, available: 24_000);

        AutoEscalationPolicy.Outcome outcome = AutoEscalationPolicy.Apply([loaded, big], profile, false);

        // The policy leaves the choice to the ranker and the profile's own weights; it narrows nothing.
        Assert.Equal(2, outcome.Pool.Count);
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void Memory_limits_apply_even_to_a_quality_profile()
    {
        CandidateEvidenceDto loaded = Model("small-4b.gguf", 0.55, 3, loaded: true, need: 4_000, available: 16_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_900, available: 16_000);

        (CandidateEvidenceDto selected, _) = Route(AutoIntelligenceProfile.MaxIntelligence, false, loaded, big);

        Assert.Equal("small-4b.gguf", selected.FileName);
    }

    [Fact]
    public void Corporate_or_cpu_only_hosts_never_escalate()
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.55, 3, loaded: true, need: 4_000, available: 64_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_000, available: 64_000);

        (CandidateEvidenceDto selected, string? reason) = Route(AutoIntelligenceProfile.MaxIntelligence, constrained: true, small, big);

        Assert.Equal("small-4b.gguf", selected.FileName);
        Assert.Contains("Corporate or CPU-only", reason);
    }

    [Fact]
    public void A_cold_start_with_nothing_loaded_is_not_an_escalation_and_the_pool_is_untouched()
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.55, 3, loaded: false, need: 4_000, available: 16_000);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_500, available: 16_000);

        AutoEscalationPolicy.Outcome outcome = AutoEscalationPolicy.Apply([small, big], AutoIntelligenceProfile.Balanced, false);

        Assert.Equal(2, outcome.Pool.Count);
        Assert.Null(outcome.Describe(big));
    }

    [Fact]
    public void An_unmeasured_loaded_model_is_never_called_marginal()
    {
        CandidateEvidenceDto loaded = Model("unknown.gguf", 0, 3, loaded: true, need: 4_000, available: 24_000);
        loaded.IntelligenceScore = null; loaded.CapabilityScore = null;
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false, need: 15_000, available: 24_000);

        (CandidateEvidenceDto selected, string? reason) = Route(AutoIntelligenceProfile.Balanced, false, loaded, big);

        Assert.Equal("coder-30b.gguf", selected.FileName);
        Assert.Contains("unmeasured", reason);
    }

    [Fact]
    public void An_unknown_memory_estimate_does_not_block_a_target_the_ranker_already_accepted()
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.40, 3, loaded: true);
        CandidateEvidenceDto big = Model("coder-30b.gguf", 0.90, 14, loaded: false);

        (CandidateEvidenceDto selected, _) = Route(AutoIntelligenceProfile.Balanced, false, small, big);

        Assert.Equal("coder-30b.gguf", selected.FileName);
    }

    [Theory]
    [InlineData(RouteMode.Auto, true)]
    [InlineData(RouteMode.Manual, false)]
    [InlineData(RouteMode.Quality, false)]
    [InlineData(RouteMode.Performance, false)]
    [InlineData(RouteMode.LowVram, false)]
    [InlineData(RouteMode.Smart, false)]
    [InlineData(null, false)]
    public void Only_auto_is_bounded_so_an_explicit_user_choice_is_never_overridden(string? mode, bool applies)
    {
        Assert.Equal(applies, AutoEscalationPolicy.AppliesTo(mode));
    }
}
