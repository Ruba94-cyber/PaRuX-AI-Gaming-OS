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

            // A manual selection is authoritative even while the hardware probe is unavailable.
            // Never silently replace a stale selection with a different quantisation.
            if (pinnedSetName != null && pinnedId == null)
            {
                var missing = new QuantDecision(QuantDecisionStatus.NoUsableVariant, null,
                    QuantPlacementPlan.Unknown,
                    "Your previously selected GGUF set is not listed. PaRuX will not replace it automatically; refresh the list or choose another variant manually.",
                    "Il set GGUF selezionato non è più nell'elenco. PaRuX non lo sostituisce automaticamente; aggiorna l'elenco oppure scegli una variante manualmente.",
                    false, false);
                return new Advice(null, italian ? missing.ReasonIt : missing.ReasonEn, missing);
            }
            if (hardware == null)
            {
                if (pinnedId != null)
                {
                    int index = int.Parse(pinnedId, System.Globalization.CultureInfo.InvariantCulture);
                    QuantCandidate selected = candidates[index];
                    var manual = new QuantDecision(QuantDecisionStatus.UserChoice, selected,
                        QuantPlacementPlan.Unknown,
                        "Your selected GGUF file remains selected. Hardware has not been detected; memory fit, acceleration and speed cannot be verified yet. Review its size and licence before download.",
                        "Il file GGUF scelto resta selezionato. L'hardware non è ancora stato rilevato; memoria, accelerazione e velocità non sono verificabili. Controlla dimensioni e licenza prima del download.",
                        false, false);
                    return new Advice(index, italian ? manual.ReasonIt : manual.ReasonEn, manual);
                }
                var none = new QuantDecision(QuantDecisionStatus.NoUsableVariant, null, QuantPlacementPlan.Unknown,
                    "Hardware not detected yet, so no file is recommended. Pick one yourself.",
                    "Hardware non ancora rilevato, quindi nessun file è consigliato. Scegline uno tu.", false, false);
                return new Advice(null, italian ? none.ReasonIt : none.ReasonEn, none);
            }

            QuantBudget budget = BudgetFor(hardware);
            if (pinnedId == null && budget.GpuFreeBytes <= 0 && budget.RamFreeBytes <= 0)
            {
                // Distinguish "hardware unknown" from "this model is too large".
                // A missing Core probe must never look like a measured failure.
                var unknown = new QuantDecision(QuantDecisionStatus.NoUsableVariant, null,
                    QuantPlacementPlan.Unknown,
                    "Usable hardware memory is not measured yet. No variant is recommended; check Core hardware status or choose a file manually.",
                    "La memoria hardware utilizzabile non è stata ancora rilevata. Nessuna variante viene consigliata; controlla il Core oppure scegli un file manualmente.",
                    false, false);
                return new Advice(null, italian ? unknown.ReasonIt : unknown.ReasonEn, unknown);
            }

            QuantDecision decision = QuantLadderPolicy.Decide(candidates, budget, task, pinnedId);
            if (hardware.AcceleratorProfile is { AcceleratorEligible: true, SharedMemoryPrimary: true }
                && decision.Placement == QuantPlacementPlan.CpuOnly)
            {
                // A UMA iGPU (Vulkan, etc.) can use shared RAM without CPU
                // fallback. The quant chooser is only comparing bytes in a
                // single host pool and cannot certify runtime GPU placement.
                decision = decision with
                {
                    Placement = QuantPlacementPlan.Unknown,
                    ReasonEn = decision.ReasonEn.Replace(
                        "runs on the CPU from system RAM",
                        "was sized against shared system RAM",
                        StringComparison.OrdinalIgnoreCase)
                        + " Shared-memory GPU: this estimate avoids counting the same RAM twice; GPU acceleration and speed must be verified at launch.",
                    ReasonIt = decision.ReasonIt.Replace(
                        "gira sulla CPU dalla RAM di sistema",
                        "è dimensionato sulla RAM di sistema condivisa",
                        StringComparison.OrdinalIgnoreCase)
                        + " GPU a memoria condivisa: questa stima evita di contare due volte la RAM; accelerazione GPU e velocità vanno verificate all'avvio."
                };
            }
            int? index = decision.Selected != null && int.TryParse(decision.Selected.Id, out int parsed) ? parsed : null;
            string reason = italian ? decision.ReasonIt : decision.ReasonEn;
            string prefix = decision.Status == QuantDecisionStatus.Selected
                ? (italian ? "Consigliato per questo PC: " : "Recommended for this PC: ")
                : string.Empty;
            return new Advice(index, prefix + reason, decision);
        }
    }
}
