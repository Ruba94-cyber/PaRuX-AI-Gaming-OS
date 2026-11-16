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
        long reclaimableOwnedTextBytes = 0)
    {
        // Do not read a multi-GB model's metadata if Windows RAM could not be
        // measured in the first place. Fail closed with an actionable reason.
        if (ram?.IsValid != true)
            return new Plan(NormalContextTokens, 0, 0, false);
        return Select(ram, maxSystemRamPercent, reclaimableOwnedTextBytes,
            CpuFallbackAdmission.CreateContextEstimator(modelPath));
    }

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

    /// <summary>
    /// The CPU runtime itself releases its idle model state; shorten that wait
    /// when the *approved* headroom left after the selected model is small.
    /// This does not unload a model while it is answering and does not modify
    /// system apps, the RAM ceiling, or the selected GGUF.
    /// </summary>
    internal static int IdleUnloadSeconds(Plan plan)
    {
        if (!plan.Fits) return LocalAiProcessManager.CorporateIdleUnloadSeconds;
        double remainingMib = Math.Max(0, plan.AvailableMib - plan.NeededMib);
        if (remainingMib < 768) return 120;
        if (remainingMib < 1536) return 180;
        return LocalAiProcessManager.CorporateIdleUnloadSeconds;
    }

    internal static string Shortfall(Plan plan, SystemRamSnapshot? ram, double maxSystemRamPercent)
    {
        if (ram?.IsValid != true || plan.NeededMib <= 0)
            return "Corporate CPU memory availability could not be measured safely, or the model footprint is unknown. "
                + "Retry in Runtime Manager; no other application was stopped.";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double availableWindowsGiB = ram.AvailableBytes / 1073741824d;
        return "Not enough free memory for CPU mode: the selected model needs about "
            + (plan.NeededMib / 1024d).ToString("0.0", inv)
            + " GB even with a 2048-token context, but only "
            + (plan.AvailableMib / 1024d).ToString("0.0", inv)
            + " GB is permitted by the existing PaRuX profile (maximum total system RAM use "
            + maxSystemRamPercent.ToString("0.#", inv)
            + "%). Windows reports " + availableWindowsGiB.ToString("0.0", inv)
            + " GB physically available. PaRuX did not expand the profile limit, close other programs or substitute the selected model.";
    }
}
