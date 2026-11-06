namespace PaRuX.Intelligence;

/// <summary>
/// A deliberately bounded first fine-tune using the existing Training Lab/Unsloth runtime.
/// It changes job settings, not backend or library ownership. Advanced training remains available
/// for longer/high-rank runs, multimodal work and experiments.
/// </summary>
public static class QuickTrainingPolicy
{
    public sealed record Settings(string Method, int LoraRank, int MaxSteps,
        double LearningRate, int MaxSequenceLength);

    // Never infer preferences from chat rows or invent rejected answers.
    public static string? ChooseMethod(bool supervised, bool preference) =>
        supervised ? "sft" : preference ? "dpo" : null;

    public static Settings Create(string method, int recommendedRank, int recommendedSteps,
        double recommendedLearningRate, int recommendedSequence)
    {
        if (method is not ("sft" or "dpo"))
            throw new ArgumentOutOfRangeException(nameof(method), "Quick training only supports verified SFT/DPO records.");

        int rank = Math.Clamp(recommendedRank > 0 ? recommendedRank : 16, 4, 16);
        int steps = Math.Clamp(recommendedSteps > 0 ? recommendedSteps : 60, 10, 300);
        int sequence = Math.Clamp(recommendedSequence > 0 ? recommendedSequence : 2048, 128, 2048);
        double rate = double.IsFinite(recommendedLearningRate) && recommendedLearningRate > 0
            ? recommendedLearningRate : method == "dpo" ? 5e-6 : 2e-4;
        return new Settings(method, rank, steps, rate, sequence);
    }

    /// <summary>
    /// A complete (possibly > 2,000-record) JSONL file has been checked by Core.
    /// The checksum is required so the frozen training request cannot silently
    /// use different bytes after preflight or a confirmation dialog.
    /// </summary>
    public static string? RefusalAfterFullVerification(
        bool canStart, bool fullyValidated, int verifiedRecords, string? sha256)
    {
        if (!canStart) return "Training preflight has blocking issues.";
        if (!fullyValidated || verifiedRecords < 1)
            return "The entire Quick Training dataset must be validated before GPU work.";
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            return "The dataset verification did not produce a valid SHA-256 identity.";
        return null;
    }

    /// <summary>
    /// Quick mode must not silently drop malformed data or treat a small preview as full validation.
    /// Longer or partially malformed datasets can still be reviewed in Advanced mode.
    /// </summary>
    public static string? Refusal(bool canStart, int examples, int validMethodRecords, int unparseableLines, bool truncated)
    {
        if (!canStart) return "Training preflight found blocking issues. Review the findings in Advanced training.";
        if (examples <= 0) return "The training dataset has no usable examples.";
        if (validMethodRecords != examples)
            return "Quick training requires every nonblank record to match the selected SFT/DPO schema. "
                + "Fix or separate incompatible rows before starting.";
        if (unparseableLines > 0) return "Malformed JSONL records were found. Fix them or review the dataset in Advanced training.";
        if (truncated) return "Only a sample of the file was inspected. Review the full dataset before starting in Advanced training.";
        return null;
    }
}
