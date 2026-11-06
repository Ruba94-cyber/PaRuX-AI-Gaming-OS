namespace PaRuX.Intelligence;

/// <summary>
/// Conservative local-GPU batch shape used by the managed Unsloth runner and the
/// user-facing preflight. One pure policy prevents the preview saying "ready"
/// when the worker would immediately refuse KTO's four-sample physical batch.
/// </summary>
public static class TrainingBatchShapePolicy
{
    public const int DefaultEffectiveBatch = 4;

    /// <summary>
    /// The baseline was measured with Unsloth on a 16 GiB RTX 5080 at 1,024
    /// tokens (approx. 0.56 GiB for 1 sample, 0.93 GiB for 4).
    /// This is a bounded estimate, NOT a substitute for live VRAM admission.
    /// </summary>
    public static (int MicroBatch, int GradientAccumulation) Plan(
        int maxSequenceLength, long totalVramBytes)
    {
        if (totalVramBytes <= 0) return (1, DefaultEffectiveBatch);
        const double BaseFootprintBytes = 640d * 1024 * 1024;
        double perSequenceBytes = 128d * 1024 * 1024 * Math.Max(1, maxSequenceLength) / 1024d;
        double usableBytes = totalVramBytes * 0.25 - BaseFootprintBytes;
        int affordable = usableBytes <= 0 ? 1 : (int)Math.Floor(usableBytes / perSequenceBytes);
        int microBatch = Math.Clamp(affordable, 1, DefaultEffectiveBatch);
        while (microBatch > 1 && DefaultEffectiveBatch % microBatch != 0) microBatch--;
        return (microBatch, DefaultEffectiveBatch / microBatch);
    }
}
