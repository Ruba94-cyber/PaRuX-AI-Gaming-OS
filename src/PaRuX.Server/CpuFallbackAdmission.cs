namespace PaRuX.Server;

/// <summary>
/// RAM admission for the bundled CPU engine (Corporate Lite, and Standard's fallback after a failed GPU launch).
/// Reuses <see cref="RuntimeMemoryBudget"/> (profile ceiling plus free memory) so the CPU path obeys the same RAM policy as
/// every other model load. It only refuses when the model clearly cannot fit; an unmeasurable machine or an unreadable
/// GGUF header is admitted, so offline chat is never blocked by a failed probe. It never touches other processes.
/// </summary>
internal static class CpuFallbackAdmission
{
    internal const int DefaultCpuContextTokens = 4096; // matches LocalAiProcessManager.BuildCorporateCpuArguments
    private const double ReserveMib = 768;
    private const double WeightsOverheadFactor = 1.20;

    /// <summary>Estimated resident footprint of the CPU launch, in MiB: weights + KV cache at the CPU context + runtime reserve.</summary>
    internal static double EstimateNeededMib(string modelPath, int contextTokens = DefaultCpuContextTokens) =>
        CreateContextEstimator(modelPath)(contextTokens);

    /// <summary>
    /// Parse GGUF metadata once per admission decision. An adaptive 4K/3K/2K
    /// context search otherwise repeatedly scans the same model header.
    /// </summary>
    internal static Func<int, double> CreateContextEstimator(string modelPath)
    {
        double weightsMib = new FileInfo(modelPath).Length / 1024d / 1024d;
        PaRuX.Intelligence.GgufModelInfo? metadata = null;
        try
        {
            PaRuX.Intelligence.GgufModelInfo candidate = PaRuX.Intelligence.GgufMetadataReader.Read(modelPath);
            if (candidate.ParsedSuccessfully) metadata = candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException) { }

        return contextTokens =>
        {
            if (contextTokens is < 2048 or > DefaultCpuContextTokens)
                throw new ArgumentOutOfRangeException(nameof(contextTokens));
            try
            {
                long? kv = metadata is not null
                    ? PaRuX.Intelligence.KvCacheSizeEstimator.Estimate(metadata, contextTokens, "f16", "f16")
                    : null;
                if (kv.HasValue) return weightsMib + kv.Value / 1024d / 1024d + ReserveMib;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException) { }
            // Missing/unparseable GGUF geometry cannot justify silently dropping
            // the CPU runtime reserve. The old 1.20x-only heuristic could claim
            // that a tiny model fits while leaving almost no RAM for the process.
            return Math.Max(weightsMib * WeightsOverheadFactor, weightsMib + ReserveMib);
        };
    }

    /// <summary>Returns a readable refusal, or null to admit. Pure: the snapshot and the estimate are inputs.</summary>
    internal static string? Evaluate(SystemRamSnapshot? ram, double maxSystemRamPercent, long reclaimableBytes, double neededMib)
    {
        if (ram is not { IsValid: true }) return null;
        long availableBytes = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(ram, maxSystemRamPercent, reclaimableBytes);
        double availableMib = availableBytes / 1024d / 1024d;
        if (neededMib <= availableMib) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double windowsAvailableGiB = ram.AvailableBytes / 1024d / 1024d / 1024d;
        double profilePercent = double.IsFinite(maxSystemRamPercent)
            ? Math.Clamp(maxSystemRamPercent, 0d, 100d)
            : 0d;
        return "Not enough free memory for CPU mode: this model needs about " + (neededMib / 1024d).ToString("0.0", inv)
            + " GB of RAM but only " + (availableMib / 1024d).ToString("0.0", inv)
            + " GB is available within the memory limit. Windows reports " + windowsAvailableGiB.ToString("0.0", inv)
            + " GB available, while the PaRuX profile caps total system RAM usage (including other applications) at "
            + profilePercent.ToString("0.#", inv)
            + "%. Close competing applications or choose a smaller model in Model Hub. No other program was stopped.";
    }

    internal static string? Check(string modelPath, SystemRamSnapshot? ram, long reclaimableBytes)
    {
        double percent = ExecutionProfileStore.Current.Get(modelPath).Effective.Budget.MaxSystemRamPercent;
        double needed;
        try { needed = EstimateNeededMib(modelPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return Evaluate(ram, percent, reclaimableBytes, needed);
    }
}
