using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// The onboarding state machine produces English reasons. This maps them to the UI language at display time so an
    /// Italian screen is never half English, and holds the Corporate wording (no installer or Vulkan hint).
    /// Unknown text is returned unchanged rather than guessed.
    /// </summary>
    public static class OnboardingReadinessText
    {
        public const string CorporateRuntimeMissingNextAction =
            "The CPU engine is part of the Corporate package and is missing from this copy. Extract the package again, or ask IT for a complete copy; nothing is downloaded or installed.";

        private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
        {
            ["Runtime and model setup not yet evaluated."] = "Configurazione del motore e del modello non ancora valutata.",
            ["Review local runtime folder and components."] = "Controlla la cartella del motore locale e i componenti.",
            ["Run the ready check to verify chat readiness."] = "Esegui il controllo di prontezza per verificare la chat.",
            ["PaRuX text runtime was skipped, and no external provider is verified."] = "Il motore di testo di PaRuX è stato saltato e nessun provider esterno è verificato.",
            ["Install a local llama.cpp text runtime or configure an external provider."] = "Installa un motore di testo llama.cpp locale o configura un provider esterno.",
            ["No text runtime is installed or detected."] = "Nessun motore di testo è installato o rilevato.",
            ["PaRuX bundles the Vulkan runtime in the installer — choose 'Install with PaRuX' or 'Locate existing' for llama.cpp in Components."] =
                "Il programma di installazione di PaRuX include il motore Vulkan: in Componenti scegli \"Installa con PaRuX\" o \"Individua esistente\" per llama.cpp.",
            [CorporateRuntimeMissingNextAction] =
                "Il motore CPU fa parte del pacchetto Corporate ed è assente da questa copia. Estrai di nuovo il pacchetto o chiedi all'IT una copia completa; non viene scaricato né installato nulla.",
            ["Chat answer has not been verified."] = "La risposta della chat non è stata verificata.",
            ["Setup is incomplete; unproven readiness accepted by user."] = "Configurazione incompleta: prontezza non dimostrata, accettata dall'utente.",
            ["No chat model is installed or loaded."] = "Nessun modello di chat è installato o caricato.",
            ["Select a recommended model and download it in Model Hub."] = "Scegli un modello consigliato e scaricalo nel Model Hub.",
            ["Verify the local runtime service is running or retry the ready check."] = "Verifica che il servizio del motore locale sia attivo o ripeti il controllo.",
            ["Model is downloaded on disk, but chat readiness requires a verified chat answer."] = "Il modello è sul disco, ma la chat è pronta solo dopo una risposta verificata.",
            ["Verify chat produces an answer before completing onboarding."] = "Verifica che la chat risponda prima di completare la configurazione.",
            ["Local AI storage location changed; chat must be verified again."] = "La cartella dell'IA locale è cambiata: verifica di nuovo la chat.",
            ["Component configuration changed; readiness must be re-evaluated."] = "La configurazione dei componenti è cambiata: la prontezza va valutata di nuovo.",
            ["Chat model configuration changed or became unavailable; verify local chat again."] = "La configurazione del modello è cambiata o non è più disponibile: verifica di nuovo la chat locale.",
            ["Selected chat model changed; chat must be verified again."] = "Il modello di chat selezionato è cambiato: verifica di nuovo la chat.",
            ["Model Hub selection may have changed; verify chat again."] = "La selezione nel Model Hub potrebbe essere cambiata: verifica di nuovo la chat.",
            ["Press Get Started to enter Chat."] = "Premi Inizia per entrare nella chat."
        };

        public static string Localize(string? english, bool italian)
        {
            if (string.IsNullOrEmpty(english) || !italian) return english ?? string.Empty;
            if (Exact.TryGetValue(english, out string? known)) return known;

            const string incomplete = "Setup is incomplete: ";
            if (english.StartsWith(incomplete, StringComparison.Ordinal))
                return "Configurazione incompleta: " + Localize(english.Substring(incomplete.Length), true);

            const string probeNot = "Local chat health probe has not succeeded";
            if (english.StartsWith(probeNot, StringComparison.Ordinal))
                return "Il controllo di salute della chat locale non è riuscito" + english.Substring(probeNot.Length);

            const string probe = "Local chat health probe failed";
            if (english.StartsWith(probe, StringComparison.Ordinal))
                return "Controllo di salute della chat locale non riuscito" + english.Substring(probe.Length);

            const string component = "Component '";
            const string changed = "' changed; readiness must be re-evaluated.";
            if (english.StartsWith(component, StringComparison.Ordinal) && english.EndsWith(changed, StringComparison.Ordinal))
                return "Il componente '" + english.Substring(component.Length, english.Length - component.Length - changed.Length)
                    + "' è cambiato: la prontezza va valutata di nuovo.";

            const string configured = "Chat is fully configured: ";
            if (english.StartsWith(configured, StringComparison.Ordinal))
            {
                string rest = english.Substring(configured.Length)
                    .Replace("bundled local CPU text runtime is healthy and ready without extra download",
                        "il motore CPU locale incluso è sano e pronto senza altri download")
                    .Replace("bundled local text runtime (Vulkan) is healthy and ready without extra download",
                        "il motore di testo locale incluso (Vulkan) è sano e pronto senza altri download")
                    .Replace("local text runtime is healthy", "il motore di testo locale è sano")
                    .Replace(", model is available (", ", il modello è disponibile (")
                    .Replace("), and chat health probe passed.", ") e il controllo di salute della chat è riuscito.");
                return "Chat configurata: " + rest;
            }
            return english;
        }
    }
}
