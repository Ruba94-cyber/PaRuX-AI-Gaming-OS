using System.Globalization;

namespace PaRuX.Intelligence;

/// <summary>What the model will mostly be used for. Technical work (code, protocols, identifiers, exact
/// numbers) is more sensitive to quantisation error than casual chat, so it gets a higher quality floor.</summary>
public enum QuantTask
{
    General,
    Technical
}

/// <summary>One complete downloadable or local file set of one model, as the Model Hub lists it.</summary>
/// <param name="Id">Caller's key (set name or file name); echoed back unchanged.</param>
/// <param name="Quantization">Canonical label from <see cref="GgufFileNaming"/> ("Q4_K_M", "IQ4_XS", ...), or "unknown".</param>
/// <param name="SizeBytes">Total bytes of all parts. Zero or negative = size not published = not selectable.</param>
/// <param name="Complete">False when a split set is missing parts.</param>
/// <param name="Name">File or set name; only used to recognise Unsloth Dynamic ("UD-") and imatrix files.</param>
public sealed record QuantCandidate(string Id, string Quantization, long SizeBytes, bool Complete = true, string? Name = null);

/// <summary>Memory the chosen file may use. All values are bytes; unknown = 0.</summary>
/// <param name="GpuFreeBytes">Free dedicated VRAM of the GPU that will run the model; 0 = no usable GPU.</param>
/// <param name="RamFreeBytes">Free system RAM (not total).</param>
/// <param name="ReserveBytes">Room kept for the KV cache, compute buffers and the runtime on the same device.</param>
public sealed record QuantBudget(long GpuFreeBytes, long RamFreeBytes, long ReserveBytes);

public enum QuantDecisionStatus
{
    /// <summary>Automatic choice made.</summary>
    Selected,
    /// <summary>The user pinned a file; the policy only reports how it compares.</summary>
    UserChoice,
    /// <summary>Files exist but none at or above the quality floor fits this PC.</summary>
    NothingFitsAtFloor,
    /// <summary>No file with a recognised quantisation, a published size and every part listed.</summary>
    NoUsableVariant
}

public enum QuantPlacementPlan
{
    Unknown,
    /// <summary>Whole file fits in free VRAM with the reserve.</summary>
    FullGpu,
    /// <summary>Does not fit in VRAM alone; layers spill to system RAM (slower).</summary>
    GpuPlusRam,
    /// <summary>No usable GPU; runs on the CPU from system RAM.</summary>
    CpuOnly
}

/// <summary>Result of <see cref="QuantLadderPolicy.Decide"/> with a plain-language explanation in both UI languages.</summary>
public sealed record QuantDecision(
    QuantDecisionStatus Status,
    QuantCandidate? Selected,
    QuantPlacementPlan Placement,
    string ReasonEn,
    string ReasonIt,
    bool BelowQualityFloor,
    bool ExceedsBudget);

/// <summary>
/// Pure, deterministic choice of the quantisation file for this PC. No I/O, no clock, no randomness.
///
/// Rules, in order:
/// 1. Only complete files with a recognised label and a published size are considered. Unknown labels are never guessed.
/// 2. Quality floor: Technical needs the equivalent of Q4_K_M or better; General needs IQ4_XS-class or better. Nothing lower
///    is ever chosen automatically (an Unsloth Dynamic "UD-" file or an imatrix file counts a little higher than its
///    plain label, because it keeps the sensitive layers at higher precision for the same size).
/// 3. Among files that fit in free VRAM, take the highest effective quality, but a bigger file must leave headroom:
///    Q5 and Q6 up to 75% of the VRAM budget, Q8 up to 50% (thresholds checked against a measured KLD/speed ladder, see XLVIII D). Decode speed is memory-bandwidth bound, so a
///    file twice as big decodes about half as fast for a small quality gain; that gain is only worth it with spare memory.
/// 4. If nothing fits in VRAM, take the smallest file at or above the floor that fits VRAM + RAM (or RAM alone without a GPU).
///    CPU-only machines never step above Q5-class.
/// 5. If nothing at or above the floor fits, say so (suggest a smaller model) instead of picking a worse quantisation.
/// 6. A user's pinned file always wins; the policy only warns if it is over budget or below the floor.
/// </summary>
public static class QuantLadderPolicy
{
    private const double BytesPerGiB = 1024d * 1024d * 1024d;
    /// <summary>Share of free RAM the model may use; the rest stays for Windows and the user's other programs.</summary>
    internal const double RamUsableFraction = 0.75;
    internal const double TechnicalFloorScore = 4.80;   // Q4_K_M is 4.89 nominal
    internal const double GeneralFloorScore = 4.20;     // IQ4_XS is 4.25 nominal
    internal const double DynamicBonus = 0.35;
    internal const double ImatrixBonus = 0.12;

    // Nominal bits per weight of the llama.cpp quantisation types (published figures, rounded).
    private static readonly Dictionary<string, double> BitsPerWeight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IQ4_XS"] = 4.25, ["IQ4_NL"] = 4.50, ["Q4_0"] = 4.55, ["Q4_K_S"] = 4.58, ["Q4_K_M"] = 4.89,
        ["Q4_1"] = 5.00, ["Q4_K_L"] = 5.00, ["Q4_K_XL"] = 5.05,
        ["Q5_0"] = 5.54, ["Q5_K_S"] = 5.54, ["Q5_1"] = 6.00, ["Q5_K_M"] = 5.69, ["Q5_K_L"] = 5.80, ["Q5_K_XL"] = 5.90,
        ["Q6_K"] = 6.56, ["Q6_K_L"] = 6.70, ["Q6_K_XL"] = 6.80,
        ["Q8_0"] = 8.50, ["Q8_K_XL"] = 8.80,
        ["Q3_K_S"] = 3.50, ["Q3_K_M"] = 3.89, ["Q3_K_L"] = 4.27, ["Q3_K_XL"] = 4.30,
        ["IQ3_XXS"] = 3.06, ["IQ3_XS"] = 3.30, ["IQ3_S"] = 3.44, ["IQ3_M"] = 3.66,
        ["Q2_K"] = 2.96, ["IQ2_XXS"] = 2.06, ["IQ2_XS"] = 2.31, ["IQ2_S"] = 2.50, ["IQ2_M"] = 2.70,
        ["IQ1_S"] = 1.56, ["IQ1_M"] = 1.75
    };

    /// <summary>Nominal bits per weight of a canonical label, or null when the label is not in the table.</summary>
    public static double? NominalBitsPerWeight(string? quantization) =>
        !string.IsNullOrWhiteSpace(quantization) && BitsPerWeight.TryGetValue(quantization.Trim(), out double bpw) ? bpw : null;

    public static bool IsDynamic(QuantCandidate candidate) =>
        candidate.Name is { Length: > 0 } n
        && (n.Contains("UD-", StringComparison.OrdinalIgnoreCase) || n.Contains("dynamic", StringComparison.OrdinalIgnoreCase));

    public static bool IsImatrix(QuantCandidate candidate) =>
        (candidate.Name is { Length: > 0 } n
            && (n.Contains("imatrix", StringComparison.OrdinalIgnoreCase) || n.Contains("imat", StringComparison.OrdinalIgnoreCase)
                || n.Contains("i1-", StringComparison.OrdinalIgnoreCase)))
        // The IQ family is only usable with an importance matrix, so the label itself proves it.
        || (candidate.Quantization?.StartsWith("IQ", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Nominal bits per weight plus the Dynamic / imatrix bonus; null for an unrecognised label.</summary>
    public static double? EffectiveScore(QuantCandidate candidate)
    {
        double? bpw = NominalBitsPerWeight(candidate.Quantization);
        if (bpw is null) return null;
        double score = bpw.Value;
        if (IsDynamic(candidate)) score += DynamicBonus;
        else if (IsImatrix(candidate)) score += ImatrixBonus;
        return score;
    }

    public static double FloorScore(QuantTask task) => task == QuantTask.Technical ? TechnicalFloorScore : GeneralFloorScore;

    /// <param name="userChoiceId">Id of a file the user pinned, or null for the automatic choice.</param>
    public static QuantDecision Decide(
        IReadOnlyList<QuantCandidate> candidates, QuantBudget budget, QuantTask task, string? userChoiceId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);

        long reserve = Math.Max(0, budget.ReserveBytes);
        long gpuBudget = budget.GpuFreeBytes > 0 ? Math.Max(0, budget.GpuFreeBytes - reserve) : 0;
        long ramBudget = (long)Math.Max(0, budget.RamFreeBytes * RamUsableFraction - (budget.GpuFreeBytes > 0 ? 0 : reserve));
        bool hasGpu = budget.GpuFreeBytes > 0;
        double floor = FloorScore(task);

        var usable = candidates
            .Where(c => c.Complete && c.SizeBytes > 0 && EffectiveScore(c) is not null)
            .ToList();

        if (userChoiceId is not null)
        {
            QuantCandidate? pinned = candidates.FirstOrDefault(c => string.Equals(c.Id, userChoiceId, StringComparison.Ordinal));
            if (pinned is not null) return DescribeUserChoice(pinned, candidates, usable, budget, gpuBudget, ramBudget, hasGpu, task, floor);
        }

        if (usable.Count == 0)
        {
            return new QuantDecision(QuantDecisionStatus.NoUsableVariant, null, QuantPlacementPlan.Unknown,
                "No file in this list can be chosen automatically: each one is incomplete, has no published size or an unrecognised quantisation. Pick one yourself.",
                "Nessun file di questo elenco si può scegliere in automatico: sono incompleti, senza dimensione pubblicata o con quantizzazione non riconosciuta. Scegline uno tu.",
                false, false);
        }

        var aboveFloor = usable.Where(c => EffectiveScore(c)!.Value >= floor).ToList();
        string floorName = task == QuantTask.Technical ? "Q4_K_M" : "IQ4_XS";

        // 3. Step-up on the GPU, with headroom required for each bigger class.
        if (hasGpu && gpuBudget > 0)
        {
            QuantCandidate? onGpu = aboveFloor
                .Where(c => c.SizeBytes <= gpuBudget * HeadroomFraction(c, cpuOnly: false))
                .OrderByDescending(c => EffectiveScore(c)!.Value)
                .ThenBy(c => c.SizeBytes)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            // The floor class (below Q5) has fraction 1.0, so it may use the whole budget.
            if (onGpu is not null)
                return Selected(onGpu, QuantPlacementPlan.FullGpu, candidates, usable, gpuBudget, task, floorName);
        }

        // 4. Spill to RAM (GPU + RAM) or run on the CPU: cheapest file at/above the floor that fits.
        long overflowBudget = hasGpu ? gpuBudget + ramBudget : ramBudget;
        QuantPlacementPlan spill = hasGpu ? QuantPlacementPlan.GpuPlusRam : QuantPlacementPlan.CpuOnly;
        QuantCandidate? spilled;
        if (!hasGpu)
        {
            // CPU-only: highest quality up to Q5-class that leaves headroom, else the smallest file at/above the floor.
            spilled = aboveFloor
                .Where(c => c.SizeBytes <= ramBudget * HeadroomFraction(c, cpuOnly: true))
                .OrderByDescending(c => EffectiveScore(c)!.Value)
                .ThenBy(c => c.SizeBytes)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            spilled ??= aboveFloor.Where(c => c.SizeBytes <= ramBudget)
                .OrderBy(c => c.SizeBytes).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
        }
        else
        {
            spilled = aboveFloor.Where(c => c.SizeBytes <= overflowBudget)
                .OrderBy(c => c.SizeBytes).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
        }
        if (spilled is not null)
            return Selected(spilled, spill, candidates, usable, hasGpu ? overflowBudget : ramBudget, task, floorName);

        // 5. Never pick below the floor.
        QuantCandidate? smallestAbove = aboveFloor.OrderBy(c => c.SizeBytes).FirstOrDefault();
        string need = smallestAbove is null
            ? "no file at or above " + floorName + " is offered"
            : "the smallest acceptable file (" + smallestAbove.Quantization + ", " + Gib(smallestAbove.SizeBytes) + ") needs more memory than this PC has free";
        string needIt = smallestAbove is null
            ? "non c'è nessun file pari o superiore a " + floorName
            : "il file accettabile più piccolo (" + smallestAbove.Quantization + ", " + Gib(smallestAbove.SizeBytes) + ") richiede più memoria di quella libera su questo PC";
        return new QuantDecision(QuantDecisionStatus.NothingFitsAtFloor, null, QuantPlacementPlan.Unknown,
            "Nothing was chosen: " + need + ". PaRuX will not drop to a lower quality that gets technical answers wrong; try a smaller model.",
            "Nessuna scelta automatica: " + needIt + ". PaRuX non scende a una qualità inferiore che sbaglia le risposte tecniche; prova un modello più piccolo.",
            false, false);
    }

    /// <summary>Share of the device budget a file of this class may take. The floor class (below Q5) may use all of it.</summary>
    internal static double HeadroomFraction(QuantCandidate candidate, bool cpuOnly)
    {
        double bpw = NominalBitsPerWeight(candidate.Quantization) ?? 0;
        if (bpw < 5.20) return 1.0;
        if (bpw < 6.20) return cpuOnly ? 0.60 : 0.75;
        if (cpuOnly) return 0.0;           // CPU decode speed scales with file size; no Q6/Q8 step-up.
        // Measured (Qwen3.5-4B, RTX 5080): Q6_K decodes as fast as Q5_K_M (160 vs 162 tok/s) with half its KL divergence,
        // so it gets the same headroom as Q5. Q8_0 is 16% slower than Q6_K for a KLD gain from 0.011 to ~0: keep it for spare memory.
        if (bpw < 7.50) return 0.75;
        return 0.50;
    }

    private static QuantDecision Selected(
        QuantCandidate pick, QuantPlacementPlan placement, IReadOnlyList<QuantCandidate> all,
        List<QuantCandidate> usable, long budget, QuantTask task, string floorName)
    {
        string label = Label(pick);
        string size = Gib(pick.SizeBytes);
        string taskEn = task == QuantTask.Technical ? "technical work (code, protocols, exact values)" : "general use";
        string taskIt = task == QuantTask.Technical ? "lavoro tecnico (codice, protocolli, valori esatti)" : "uso generale";
        double bpw = NominalBitsPerWeight(pick.Quantization) ?? 0;
        bool stepUp = bpw >= 5.20;
        bool dyn = IsDynamic(pick);

        string whereEn, whereIt;
        switch (placement)
        {
            case QuantPlacementPlan.FullGpu:
                whereEn = "fits entirely in free GPU memory (" + size + " of " + Gib(budget) + " usable)";
                whereIt = "sta interamente nella memoria libera della GPU (" + size + " su " + Gib(budget) + " utilizzabili)";
                break;
            case QuantPlacementPlan.GpuPlusRam:
                whereEn = "does not fit in GPU memory alone, so part runs from system RAM (" + size + "); this is the smallest acceptable file, to stay as fast as possible";
                whereIt = "non sta nella sola memoria GPU, quindi una parte gira dalla RAM di sistema (" + size + "); è il file accettabile più piccolo, per restare il più veloce possibile";
                break;
            default:
                whereEn = "runs on the CPU from system RAM (" + size + ")";
                whereIt = "gira sulla CPU dalla RAM di sistema (" + size + ")";
                break;
        }

        string whyEn = stepUp
            ? "there is spare memory, so a higher-precision file is used for better accuracy"
            : "it is the best balance of accuracy and speed for this PC; a bigger file would slow generation without much accuracy gain";
        string whyIt = stepUp
            ? "c'è memoria in più, quindi si usa un file a precisione maggiore per più accuratezza"
            : "è il miglior equilibrio tra accuratezza e velocità per questo PC; un file più grande rallenterebbe la generazione con poco guadagno";
        string dynEn = dyn ? " It is an Unsloth Dynamic file, which keeps sensitive layers at higher precision." : IsImatrix(pick) ? " It was made with an importance matrix, which improves accuracy at low bit-widths." : string.Empty;
        string dynIt = dyn ? " È un file Unsloth Dynamic, che mantiene i livelli sensibili a precisione più alta." : IsImatrix(pick) ? " È stato creato con una importance matrix, che migliora l'accuratezza a pochi bit." : string.Empty;

        return new QuantDecision(QuantDecisionStatus.Selected, pick, placement,
            label + " chosen for " + taskEn + ": " + whereEn + "; " + whyEn + "." + dynEn + " Quality never goes below " + floorName + " automatically. You can pick another file.",
            label + " scelto per " + taskIt + ": " + whereIt + "; " + whyIt + "." + dynIt + " La qualità non scende mai sotto " + floorName + " in automatico. Puoi scegliere un altro file.",
            false, false);
    }

    private static QuantDecision DescribeUserChoice(
        QuantCandidate pinned, IReadOnlyList<QuantCandidate> all, List<QuantCandidate> usable, QuantBudget budget,
        long gpuBudget, long ramBudget, bool hasGpu, QuantTask task, double floor)
    {
        double? score = EffectiveScore(pinned);
        bool below = score is not null && score.Value < floor;
        long limit = hasGpu ? gpuBudget + ramBudget : ramBudget;
        bool over = pinned.SizeBytes > 0 && pinned.SizeBytes > limit;
        QuantPlacementPlan placement = pinned.SizeBytes <= 0 ? QuantPlacementPlan.Unknown
            : hasGpu && pinned.SizeBytes <= gpuBudget ? QuantPlacementPlan.FullGpu
            : hasGpu ? QuantPlacementPlan.GpuPlusRam : QuantPlacementPlan.CpuOnly;
        string label = Label(pinned);
        string en = label + " is your choice and is used as is.";
        string it = label + " è la tua scelta e viene usato così com'è.";
        if (over)
        {
            en += " Warning: " + Gib(pinned.SizeBytes) + " is more than this PC has free; loading may fail or be very slow.";
            it += " Attenzione: " + Gib(pinned.SizeBytes) + " è più della memoria libera di questo PC; il caricamento può fallire o essere molto lento.";
        }
        if (below)
        {
            string floorName = task == QuantTask.Technical ? "Q4_K_M" : "IQ4_XS";
            en += " Note: this is below the " + floorName + " quality PaRuX recommends"
                + (task == QuantTask.Technical ? " for technical answers" : string.Empty) + "; expect more mistakes.";
            it += " Nota: è sotto la qualità " + floorName + " che PaRuX consiglia"
                + (task == QuantTask.Technical ? " per le risposte tecniche" : string.Empty) + "; aspettati più errori.";
        }
        return new QuantDecision(QuantDecisionStatus.UserChoice, pinned, placement, en, it, below, over);
    }

    private static string Label(QuantCandidate c) =>
        IsDynamic(c) && !c.Quantization.StartsWith("UD-", StringComparison.OrdinalIgnoreCase) ? "UD-" + c.Quantization : c.Quantization;

    private static string Gib(long bytes) =>
        (bytes / BytesPerGiB).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
}
