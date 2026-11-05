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
            long gpuFree = 0;
            if (hardware.GpuDetected && hardware.VramTotalMib > 0)
            {
                long mib = hardware.VramFreeMib ?? (long)(hardware.VramTotalMib * AssumedFreeVramFraction);
                gpuFree = Math.Max(0, mib) * MiB;
            }
            long ramFree = hardware.RamTotalBytes > 0 ? (long)(hardware.RamTotalBytes * AssumedFreeRamFraction) : 0;
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
