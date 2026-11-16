namespace PaRuX.Server;

/// <summary>
/// Chooses the largest safe context for the SAME Corporate CPU GGUF, never a
/// different model. A lower context reduces KV memory without tampering with
/// other Windows processes or raising a user's per-model system RAM ceiling.
/// This is a just-in-time launch decision; a concurrent OS memory change can
/// still make native allocation fail, which is reported separately.
/// </summary>
internal static class CorporateCpuMemoryPlanner
{
    internal const int NormalContextTokens = 4096;
    internal const int MinimumUsefulContextTokens = 2048;
    internal static readonly int[] CandidateContexts = [4096, 3072, 2048];

    internal sealed record Plan(int ContextTokens, double NeededMib, double AvailableMib, bool Fits)
    {
        public bool Reduced => Fits && ContextTokens < NormalContextTokens;
    }

    internal static Plan ForModel(string modelPath, SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes = 0) =>
        Select(ram, maxSystemRamPercent, reclaimableOwnedTextBytes,
            context => CpuFallbackAdmission.EstimateNeededMib(modelPath, context));

    /// <summary>Pure deterministic test seam; no Windows APIs or file access.</summary>
    internal static Plan Select(SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes, Func<int, double> estimateMib)
    {
        if (ram?.IsValid != true || !double.IsFinite(maxSystemRamPercent) ||
            maxSystemRamPercent is <= 0 or > 100)
            return new Plan(NormalContextTokens, 0, 0, false);

        double availableMib = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            ram, maxSystemRamPercent, reclaimableOwnedTextBytes) / 1048576d;
        double lastNeededMib = 0;
        foreach (int contextTokens in CandidateContexts)
        {
            double neededMib = estimateMib(contextTokens);
            if (!double.IsFinite(neededMib) || neededMib <= 0)
                return new Plan(NormalContextTokens, 0, availableMib, false);
            lastNeededMib = neededMib;
            if (neededMib <= availableMib)
                return new Plan(contextTokens, neededMib, availableMib, true);
        }
        return new Plan(MinimumUsefulContextTokens, lastNeededMib, availableMib, false);
    }

    internal static string Shortfall(Plan plan, SystemRamSnapshot? ram, double maxSystemRamPercent)
    {
        if (ram?.IsValid != true || plan.NeededMib <= 0)
            return "Corporate CPU memory availability or the model footprint could not be measured safely. "
                + "Retry in Runtime Manager; no other application was stopped.";
        return CpuFallbackAdmission.Evaluate(
            ram, maxSystemRamPercent, 0, plan.NeededMib)
            + " PaRuX also checked a reduced 2048-token context, but that did not fit.";
    }
}
