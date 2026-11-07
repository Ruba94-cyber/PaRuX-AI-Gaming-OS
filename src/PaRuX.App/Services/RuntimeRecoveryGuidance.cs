using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Converts known Core runtime-start failures into actionable, user-safe guidance.
    /// This policy does not retry, mutate a model selection, install a component,
    /// attach a GPU or change Gaming/AI mode. It is safe for Chat, Settings and tray.
    /// Unrecognised diagnostics remain visible rather than being called a success.
    /// </summary>
    public static class RuntimeRecoveryGuidance
    {
        public enum Reason
        {
            Unknown,
            GamingActive,
            AiOff,
            AcceleratorUnavailable,
            MissingModel,
            MissingRuntime,
            OutOfMemory,
            IncompatibleModel,
            PortOccupied,
            CoreUnavailable,
            TimedOut
        }

        private static bool Has(string source, string value) =>
            source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        public static Reason Classify(string? message)
        {
            string raw = message?.Trim() ?? string.Empty;
            if (raw.Length == 0) return Reason.Unknown;

            // Order matters: when the gaming policy owns the GPU, avoid suggesting
            // that the user should bypass it by changing runtimes or GPU settings.
            if (Has(raw, "game is currently active") || Has(raw, "gaming mode is active")
                || Has(raw, "game is running") || Has(raw, "Gaming Mode became active"))
                return Reason.GamingActive;

            if (Has(raw, "OFF mode") || Has(raw, "local AI is switched off"))
                return Reason.AiOff;

            if (Has(raw, "No sufficiently powerful GPU") || Has(raw, "configured discrete GPU isn't attached"))
                return Reason.AcceleratorUnavailable;

            // The actual PaRuX process manager returns "File not found: <path>".
            // A .gguf path identifies a missing model; a llama-server path
            // identifies the missing runtime executable. Do not show paths.
            if ((Has(raw, "File not found:") && Has(raw, ".gguf"))
                || Has(raw, "model file not found") || Has(raw, "model does not exist"))
                return Reason.MissingModel;

            if (Has(raw, "bundled CPU runtime") || Has(raw, "runtime not installed")
                || Has(raw, "runtime is missing") || Has(raw, "runtime missing")
                || (Has(raw, "File not found:") && Has(raw, "llama-server")))
                return Reason.MissingRuntime;

            if (Has(raw, "out of memory") || Has(raw, "CUDA_ERROR_OUT_OF_MEMORY")
                || Has(raw, "failed to allocate memory") || Has(raw, "not enough VRAM")
                || Has(raw, "insufficient VRAM") || Has(raw, "not enough system memory"))
                return Reason.OutOfMemory;

            if (Has(raw, "unknown model architecture") || Has(raw, "unsupported model architecture")
                || Has(raw, "unsupported GGUF architecture"))
                return Reason.IncompatibleModel;

            // Do not classify any occurrence of the word "bind" as a port
            // conflict: it appears in unrelated component / model binding logs.
            if (Has(raw, "address already in use") || Has(raw, "failed to bind")
                || Has(raw, "could not bind") || Has(raw, "port 1234 is already in use"))
                return Reason.PortOccupied;

            if (Has(raw, "connection refused") || Has(raw, "No connection could be made")
                || Has(raw, "actively refused it"))
                return Reason.CoreUnavailable;

            if (Has(raw, "timed out") || Has(raw, "timeout"))
                return Reason.TimedOut;

            return Reason.Unknown;
        }

        public static string Describe(string? message, bool italian)
        {
            string raw = message?.Trim() ?? string.Empty;
            return Classify(raw) switch
            {
                Reason.GamingActive => italian
                    ? "Gaming attivo: PaRuX mantiene libera la GPU per il gioco. Chiudi il gioco e riprova; non forzare l'avvio dell'IA."
                    : "Gaming is active: PaRuX keeps the GPU free for the game. Close the game and try again; do not force AI startup.",
                Reason.AiOff => italian
                    ? "L'IA locale è disattivata. Dal menu PaRuX scegli AI Auto o AI On e riprova."
                    : "Local AI is switched off. Choose AI Auto or AI On from the PaRuX tray menu, then try again.",
                Reason.AcceleratorUnavailable => italian
                    ? "La GPU richiesta da questo profilo non è disponibile. Ricollega l'acceleratore oppure scegli un profilo compatibile con l'hardware rilevato; nessun driver verrà installato automaticamente."
                    : "The GPU required by this profile is unavailable. Reconnect the accelerator or choose a profile compatible with detected hardware; no driver will be installed automatically.",
                Reason.MissingModel => italian
                    ? "Il file del modello selezionato non è disponibile. Apri Model Hub, controlla il percorso e la presenza di tutte le parti GGUF, poi scegli un modello installato o rivedi il download."
                    : "The selected model file is unavailable. Open Model Hub, check its path and every GGUF part, then select an installed model or review its download.",
                Reason.MissingRuntime => italian
                    ? "Il runtime locale richiesto non è presente. Apri Runtime Manager per individuare un'installazione esistente oppure verificare e installare il componente compatibile."
                    : "The required local runtime is missing. Open Runtime Manager to locate an existing installation or review and install a compatible component.",
                Reason.OutOfMemory => italian
                    ? "Memoria RAM/VRAM insufficiente per avviare questo modello. Chiudi altre attività o scegli in Model Hub un modello/quantizzazione più piccolo; nessuna impostazione verrà cambiata automaticamente."
                    : "Not enough RAM/VRAM to start this model. Close competing workloads or choose a smaller model/quantization in Model Hub; settings will not be changed automatically.",
                Reason.IncompatibleModel => italian
                    ? "Il runtime non riconosce l'architettura di questo modello. Controlla compatibilità e versione in Runtime Manager e valuta una versione compatibile, senza sovrascrivere runtime gestiti dall'utente."
                    : "The runtime does not recognise this model architecture. Check compatibility and version in Runtime Manager and review a compatible version without overwriting user-managed runtimes.",
                Reason.PortOccupied => italian
                    ? "La porta del server del modello è occupata da un altro processo. Controlla in Runtime Manager e chiudi soltanto i servizi che gestisci tu; PaRuX non interromperà processi esterni automaticamente."
                    : "The model server port is occupied by another process. Check Runtime Manager and stop only services you own; PaRuX will not kill external processes automatically.",
                Reason.CoreUnavailable => italian
                    ? "Impossibile raggiungere il Core locale. Verifica che PaRuX Core sia in esecuzione e riprova da Chat o Runtime Manager."
                    : "Cannot reach the local PaRuX Core. Check that Core is running, then retry from Chat or Runtime Manager.",
                Reason.TimedOut => italian
                    ? "Il modello non è diventato pronto nel tempo previsto. Potrebbe essere ancora in caricamento: controlla lo stato in Runtime Manager prima di riprovare, senza avviare caricamenti duplicati."
                    : "The model did not become ready in time. It may still be loading: check Runtime Manager status before retrying to avoid duplicate starts.",
                _ => raw.Length == 0
                    ? (italian ? "Il controller dell'IA locale ha segnalato un errore senza dettagli. Controlla Runtime Manager."
                               : "The local AI controller reported a failure without details. Check Runtime Manager.")
                    : raw
            };
        }
    }
}
