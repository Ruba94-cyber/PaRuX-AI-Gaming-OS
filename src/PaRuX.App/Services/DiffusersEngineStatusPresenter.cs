using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Localizes the stable reason code of the managed Diffusers engine (<see cref="ManagedMediaEngineStatus.StatusCode"/>), so the
    /// engine setup UI says the true reason (no runtime / no model / no CUDA GPU / failed install) in the selected language.
    /// An unknown code falls back to the server detail, which is never hidden.
    /// </summary>
    public static class DiffusersEngineStatusPresenter
    {
        public static string Describe(ManagedMediaEngineStatus status, Func<string, string, string> t)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (status.Ready || status.StatusCode == "ready")
                return t("Diffusers engine ready (offline, local only).", "Motore Diffusers pronto (offline, solo in locale).");
            return status.StatusCode switch
            {
                "runtime_missing" => t(
                    "The Diffusers engine is not installed yet. Press Install to add the PaRuX-managed runtime and the pinned model.",
                    "Il motore Diffusers non è ancora installato. Premi Installa per aggiungere il runtime gestito da PaRuX e il modello fissato."),
                "model_missing" => t(
                    "The Diffusers runtime is installed, but the pinned model snapshot has not been downloaded yet. Press Install again to finish it.",
                    "Il runtime Diffusers è installato, ma l'istantanea del modello fissato non è ancora stata scaricata. Premi di nuovo Installa per completarla."),
                "cuda_unavailable" => t(
                    "The Diffusers engine cannot run on this PC: PaRuX-managed PyTorch cannot use a CUDA GPU here. It needs a supported NVIDIA GPU; nothing was generated.",
                    "Il motore Diffusers non può funzionare su questo PC: il PyTorch gestito da PaRuX non può usare una GPU CUDA. Serve una GPU NVIDIA supportata; non è stato generato nulla."),
                "install_failed" => t(
                    "The Diffusers engine install is incomplete or failed. Press Install to run it again.",
                    "L'installazione del motore Diffusers è incompleta o non riuscita. Premi Installa per ripeterla."),
                _ => string.IsNullOrWhiteSpace(status.Detail)
                    ? t("The Diffusers engine is not ready.", "Il motore Diffusers non è pronto.")
                    : status.Detail!
            };
        }

        /// <summary>The install button makes sense for every state except ready and the hardware refusal that a reinstall cannot fix.</summary>
        public static bool CanInstall(ManagedMediaEngineStatus status) =>
            !(status.Ready || status.StatusCode == "ready" || status.StatusCode == "cuda_unavailable");
    }
}
