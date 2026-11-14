namespace PaRuX.Server;

/// <summary>
/// Router/preflight fit for Corporate Lite (bundled CPU engine). Deterministic: the verdict depends only on the model's
/// CPU-launch footprint, the machine's total RAM and the profile's RAM ceiling, never on how much RAM happens to be free
/// at this instant. The launch itself is still checked against live memory by <see cref="CpuFallbackAdmission"/>.
/// </summary>
internal static class CorporateFitPolicy
{
    internal static HardwareFitEstimate Estimate(double neededMib, long totalRamBytes, double maxSystemRamPercent)
    {
        double percent = double.IsFinite(maxSystemRamPercent) ? Math.Clamp(maxSystemRamPercent, 0d, 100d) : 0d;
        double availableMib = Math.Max(0, totalRamBytes) / 1024d / 1024d * percent / 100d;
        return new HardwareFitEstimate(neededMib <= availableMib, neededMib, availableMib, null, null);
    }
}
