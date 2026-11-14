using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// PERF-003b. AUTO may move a turn from the model that is loaded to a larger one ("escalate"). Unbounded, that sent a long
/// prompt to a 30B coder model (14 GB VRAM, 7.8 s to first token) that the caller never asked for. This policy runs before
/// <see cref="CandidatePoolRanker"/> and only NARROWS the pool, like <see cref="AutoTrivialTurnPolicy"/>, so it can never
/// pick a model the ranker would not. It is pure and is applied to AUTO only: an explicit model choice (manual role, exact
/// or scoped Force/Prefer, per-capability override, Quality/Performance/LowVram modes) never reaches it.
///
/// Rules, in order. An escalation target is an unloaded candidate that needs more memory than the loaded one.
/// 1. Corporate or CPU-only hosts never escalate: a swap there costs a long CPU load and extra RAM for little gain.
/// 2. A target whose estimated need exceeds the memory budget (with headroom) is dropped. The estimate is the router's
///    existing hardware-fit estimate; the live admission check at load time stays the final authority.
/// 3. Staying wins when the loaded model is within <see cref="MarginalGain"/> of the best remaining candidate, unless the
///    user chose the Max intelligence or Custom auto profile (those mean "pay for quality").
/// Every narrowing is reported in a sentence that the router appends to the decision's selection reason.
/// </summary>
public static class AutoEscalationPolicy
{
    /// <summary>The loaded model is kept when its capability is at least this share of the best remaining candidate's.</summary>
    public const double MarginalGain = 0.10;

    /// <summary>An escalation target may need at most this share of the memory budget.</summary>
    public const double MemoryHeadroom = 0.90;

    /// <summary>Only AUTO is bounded. Manual, Quality, Performance, LowVram and Smart are the user's own choice of trade-off and are never narrowed here.</summary>
    public static bool AppliesTo(string? mode) => string.Equals(mode, RouteMode.Auto, StringComparison.Ordinal);

    public sealed record Outcome(IReadOnlyList<CandidateEvidenceDto> Pool, CandidateEvidenceDto? Loaded, IReadOnlyList<string> Notes)
    {
        public static Outcome Unchanged(IReadOnlyList<CandidateEvidenceDto> pool) => new(pool, null, Array.Empty<string>());

        /// <summary>The sentence(s) for the decision's selection reason, given the candidate the ranker finally chose; null when nothing was decided.</summary>
        public string? Describe(CandidateEvidenceDto? selected)
        {
            if (Loaded is null) return null;
            var parts = new List<string>(Notes);
            if (selected is not null && !SamePath(selected, Loaded) && IsEscalation(selected, Loaded))
            {
                string gain = Quality(Loaded) > 0
                    ? "capability " + Quality(Loaded).ToString("0.00") + " -> " + Quality(selected).ToString("0.00")
                        + " (+" + ((Quality(selected) / Quality(Loaded) - 1) * 100).ToString("0") + "%)"
                    : "the loaded model's capability is unmeasured";
                string fit = selected.EstimatedNeededMib is { } need && selected.AvailableMib is { } have
                    ? "; needs about " + need.ToString("0") + " MiB of " + have.ToString("0") + " MiB available"
                    : string.Empty;
                parts.Add("Escalated from " + Loaded.FileName + " to " + selected.FileName + ": " + gain + fit + ".");
            }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <param name="constrained">Corporate mode or a host with no capable GPU (CPU-only).</param>
    public static Outcome Apply(
        IReadOnlyList<CandidateEvidenceDto> candidates, string? autoProfile, bool constrained)
    {
        CandidateEvidenceDto? loaded = candidates.FirstOrDefault(c => c.IsCurrentlyLoaded && c.HardwareCompatible != false);
        if (loaded is null || candidates.Count < 2) return Outcome.Unchanged(candidates);

        var notes = new List<string>();
        List<CandidateEvidenceDto> pool = candidates.ToList();
        List<CandidateEvidenceDto> targets = pool.Where(c => !SamePath(c, loaded) && IsEscalation(c, loaded)).ToList();
        if (targets.Count == 0) return new Outcome(candidates, loaded, notes);

        if (constrained)
        {
            pool.RemoveAll(targets.Contains);
            notes.Add("Stayed on " + loaded.FileName + ": Corporate or CPU-only hosts do not escalate to a larger model.");
            return new Outcome(pool, loaded, notes);
        }

        foreach (CandidateEvidenceDto target in targets)
        {
            if (target.EstimatedNeededMib is { } need && target.AvailableMib is { } have && need > have * MemoryHeadroom)
            {
                pool.Remove(target);
                notes.Add("Skipped " + target.FileName + ": needs about " + need.ToString("0") + " MiB, only "
                    + (have * MemoryHeadroom).ToString("0") + " MiB of " + have.ToString("0") + " MiB is usable.");
            }
        }

        bool paidForQuality = AutoIntelligenceProfile.Normalize(autoProfile)
            is AutoIntelligenceProfile.MaxIntelligence or AutoIntelligenceProfile.Custom;
        double loadedQuality = Quality(loaded);
        double best = pool.Max(Quality);
        // An unmeasured loaded model cannot be shown to be "close enough", so the gain is never called marginal then.
        if (!paidForQuality && loadedQuality > 0 && best > 0 && loadedQuality >= best * (1 - MarginalGain)
            && pool.Any(c => !SamePath(c, loaded) && IsEscalation(c, loaded)))
        {
            pool.RemoveAll(c => !SamePath(c, loaded) && IsEscalation(c, loaded));
            notes.Add("Stayed on " + loaded.FileName + ": the larger model's gain is marginal (under "
                + (MarginalGain * 100).ToString("0") + "%) and a swap costs load time and memory.");
        }
        return new Outcome(pool, loaded, notes);
    }

    private static bool IsEscalation(CandidateEvidenceDto candidate, CandidateEvidenceDto loaded) =>
        candidate.SizeBytes > loaded.SizeBytes;

    private static double Quality(CandidateEvidenceDto c) => c.IntelligenceScore ?? c.CapabilityScore ?? 0;

    private static bool SamePath(CandidateEvidenceDto a, CandidateEvidenceDto b) =>
        string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
}
