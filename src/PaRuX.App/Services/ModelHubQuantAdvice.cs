using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Bridges the Model Hub's file list and the detected hardware to the pure <see cref="QuantLadderPolicy"/>:
    /// which quantisation file to preselect for this PC, and the plain-language reason shown beside it.
    /// Nothing here downloads or starts anything; the user still reviews size, source and licence and can
    /// pick any other file.
    /// </summary>
    public static class ModelHubQuantAdvice
    {
        private const long MiB = 1024L * 1024;
        /// <summary>KV cache (about 8-16k context) plus compute buffers and the runtime.</summary>
        public const long DefaultReserveBytes = 1536L * MiB;
        /// <summary>Used when the live free-RAM figure is not reported: the share of total RAM assumed free on a desktop in use.</summary>
        public const double AssumedFreeRamFraction = 0.60;
        /// <summary>Used when live free VRAM is not reported: the share of total dedicated VRAM assumed free.</summary>
        public const double AssumedFreeVramFraction = 0.85;

        public sealed record Advice(int? PreferredIndex, string Text, QuantDecision Decision);

        public static QuantBudget BudgetFor(RuntimeManagerHardwareStatus? hardware)
        {
            if (hardware == null) return new QuantBudget(0, 0, DefaultReserveBytes);

            RuntimeAcceleratorProfile? profile = hardware.AcceleratorProfile;
            long gpuFree = 0;

            if (profile != null)
            {
                // Normalized Core backend eligibility is authoritative.
                // Physical DXGI/NVIDIA VRAM alone does not prove that PaRuX
                // has an installed, usable text accelerator.
                if (profile.AcceleratorEligible && !profile.SharedMemoryPrimary)
                {
                    long capacity = Math.Max(0, profile.DedicatedVideoMemoryBytes);
                    long measuredFree = hardware.VramFreeMib is >= 0
                        ? Math.Min(capacity, (long)hardware.VramFreeMib.Value * MiB)
                        : (long)(capacity * AssumedFreeVramFraction);
                    gpuFree = Math.Clamp(measuredFree, 0, capacity);
                }
                // UMA: dedicated GPU budget must be zero because the shared
                // aperture and model RAM are backed by the same physical RAM.
                // QuantLadderPolicy must never count it twice. A conservative
                // RAM-budget path is safer than a false "Full GPU" promise.
            }
            else if (hardware.GpuDetected && hardware.VramTotalMib > 0)
            {
                // Older Core without a normalized profile: legacy, provisional
                // VRAM estimate. Preserve compatibility with existing installs.
                long totalMib = hardware.VramTotalMib;
                long freeMib = hardware.VramFreeMib.HasValue
                    ? Math.Clamp((long)hardware.VramFreeMib.Value, 0, totalMib)
                    : (long)(totalMib * AssumedFreeVramFraction);
                gpuFree = freeMib * MiB;
            }

            long totalRam = Math.Max(0, hardware.RamTotalBytes);
            long estimatedFreeRam = (long)(totalRam * AssumedFreeRamFraction);
            long ramFree = profile?.UsableSystemRamBytes is long knownUsableRam
                ? Math.Clamp(knownUsableRam, 0, totalRam > 0 ? totalRam : long.MaxValue)
                : estimatedFreeRam;

            return new QuantBudget(gpuFree, ramFree, DefaultReserveBytes);
        }

        /// <param name="options">The listed files as built by <see cref="ModelHubSplitSetPresentation.BuildQuantChoiceOptions"/>.</param>
        /// <param name="pinnedSetName">Set name the user chose by hand, or null for the automatic choice.</param>
        public static Advice Build(
            IReadOnlyList<ModelHubSplitSetPresentation.QuantChoiceOption> options,
            RuntimeManagerHardwareStatus? hardware, QuantTask task, bool italian, string? pinnedSetName = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (hardware == null)
            {
                var none = new QuantDecision(QuantDecisionStatus.NoUsableVariant, null, QuantPlacementPlan.Unknown,
                    "Hardware not detected yet, so no file is recommended. Pick one yourself.",
                    "Hardware non ancora rilevato, quindi nessun file è consigliato. Scegline uno tu.", false, false);
                return new Advice(null, italian ? none.ReasonIt : none.ReasonEn, none);
            }

            var candidates = new List<QuantCandidate>(options.Count);
            for (int i = 0; i < options.Count; i++)
            {
                ModelQuantChoice c = options[i].Choice;
                // The index is the identity: two files can share a quantisation label.
                candidates.Add(new QuantCandidate(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    c.Quantization, c.SizeBytes, c.Complete, c.SetName));
            }
            string? pinnedId = null;
            if (pinnedSetName != null)
                for (int i = 0; i < options.Count; i++)
                    if (string.Equals(options[i].Choice.SetName, pinnedSetName, StringComparison.OrdinalIgnoreCase))
                        pinnedId = i.ToString(System.Globalization.CultureInfo.InvariantCulture);

            QuantDecision decision = QuantLadderPolicy.Decide(candidates, BudgetFor(hardware), task, pinnedId);
            int? index = decision.Selected != null && int.TryParse(decision.Selected.Id, out int parsed) ? parsed : null;
            string reason = italian ? decision.ReasonIt : decision.ReasonEn;
            string prefix = decision.Status == QuantDecisionStatus.Selected
                ? (italian ? "Consigliato per questo PC: " : "Recommended for this PC: ")
                : string.Empty;
            return new Advice(index, prefix + reason, decision);
        }
    }
}
