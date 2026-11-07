using System;

namespace PaRuX.Services
{
    /// <summary>What the "Set up automatically" card shows for one state. Pure, so tests can pin it.</summary>
    public sealed record AutoSetupViewModel(
        string Headline,
        string Detail,
        string PrimaryLabel,
        bool PrimaryEnabled,
        bool ShowCancel,
        bool ShowRetry,
        bool ShowSkip,
        bool ShowProgress);

    public static class OnboardingAutoSetupPresenter
    {
        public static AutoSetupViewModel Present(AutoSetupState state, bool italian = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (italian) return PresentItalian(state);
            string message = state.Message ?? string.Empty;
            string runtime = string.IsNullOrWhiteSpace(state.RuntimeNote) ? string.Empty : " " + state.RuntimeNote;

            return state.Phase switch
            {
                AutoSetupPhase.NotStarted => new AutoSetupViewModel(
                    "Set up PaRuX in one step",
                    "PaRuX will check this PC, keep your text runtime, pick a model that fits, ask you to review its licence, "
                    + "download it, assign roles and verify a local chat answer. Nothing downloads before the licence review.",
                    "Set up automatically", true, false, false, false, false),
                AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime or AutoSetupPhase.SelectingModel
                    or AutoSetupPhase.AssigningRoles or AutoSetupPhase.VerifyingChat => new AutoSetupViewModel(
                    StepTitle(state.Phase), message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Downloading => new AutoSetupViewModel(
                    "Downloading " + (state.ModelTitle ?? "the model"),
                    message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Ready => new AutoSetupViewModel(
                    "PaRuX is ready", message, "Start chatting", true, false, false, false, false),
                AutoSetupPhase.NoModelFits => new AutoSetupViewModel(
                    "No recommended model fits this PC", message, "Try again", true, false, false, true, false),
                AutoSetupPhase.Failed => new AutoSetupViewModel(
                    "Automatic setup stopped", message, "Set up automatically", false, false, true, true, false),
                AutoSetupPhase.Cancelled => new AutoSetupViewModel(
                    "Automatic setup cancelled", message, "Set up automatically", true, false, false, true, false),
                AutoSetupPhase.Skipped => new AutoSetupViewModel(
                    "Automatic setup skipped", message, "Set up automatically", true, false, false, false, false),
                _ => new AutoSetupViewModel("Set up PaRuX", message, "Set up automatically", true, false, false, false, false)
            };
        }

        private static AutoSetupViewModel PresentItalian(AutoSetupState state)
        {
            string message = state.Message?.Trim() ?? string.Empty;
            string failure = RuntimeRecoveryGuidance.Classify(message) is not RuntimeRecoveryGuidance.Reason.Unknown
                ? RuntimeRecoveryGuidance.Describe(message, italian: true)
                : string.IsNullOrWhiteSpace(message)
                    ? "Consulta i controlli manuali qui sotto per verificare componenti, modello e chat."
                    : "Dettaglio: " + message + " Controlla i passaggi manuali qui sotto prima di riprovare.";

            return state.Phase switch
            {
                AutoSetupPhase.NotStarted => new AutoSetupViewModel(
                    "Configura PaRuX in un passaggio",
                    "PaRuX controllerà il PC e i runtime esistenti, proporrà un modello adatto "
                    + "e verificherà la chat locale. Prima di ogni download potrai controllare e accettare la licenza.",
                    "Configura automaticamente", true, false, false, false, false),
                AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime or AutoSetupPhase.SelectingModel
                    or AutoSetupPhase.AssigningRoles or AutoSetupPhase.VerifyingChat => new AutoSetupViewModel(
                    StepTitleItalian(state.Phase), ProgressDetailItalian(state.Phase),
                    "Configurazione...", false, true, false, false, true),
                AutoSetupPhase.Downloading => new AutoSetupViewModel(
                    "Download di " + (state.ModelTitle ?? "modello locale"),
                    "Scaricamento in corso. Il progresso viene mostrato qui sotto; puoi annullare se necessario.",
                    "Configurazione...", false, true, false, false, true),
                AutoSetupPhase.Ready => new AutoSetupViewModel(
                    "PaRuX è pronto",
                    "La verifica della chat locale è stata completata. Puoi iniziare.",
                    "Apri la chat", true, false, false, false, false),
                AutoSetupPhase.NoModelFits => new AutoSetupViewModel(
                    "Nessun modello consigliato è adatto a questo PC",
                    "PaRuX non forzerà il download di un modello troppo grande. "
                    + "Apri le opzioni manuali, valuta un modello più piccolo o verifica RAM, GPU e runtime.",
                    "Riprova", true, false, false, true, false),
                AutoSetupPhase.Failed => new AutoSetupViewModel(
                    "Configurazione automatica interrotta",
                    failure, "Configura automaticamente", false, false, true, true, false),
                AutoSetupPhase.Cancelled => new AutoSetupViewModel(
                    "Configurazione automatica annullata",
                    "La verifica non è completa. Puoi riprendere manualmente oppure ricominciare.",
                    "Configura automaticamente", true, false, false, true, false),
                AutoSetupPhase.Skipped => new AutoSetupViewModel(
                    "Configurazione automatica saltata",
                    "I controlli manuali restano disponibili. La chat non è ancora stata verificata.",
                    "Configura automaticamente", true, false, false, false, false),
                _ => new AutoSetupViewModel(
                    "Configura PaRuX", message,
                    "Configura automaticamente", true, false, false, false, false)
            };
        }

        private static string StepTitleItalian(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Passaggio 1 di 5: analisi del PC",
            AutoSetupPhase.CheckingRuntime => "Passaggio 2 di 5: runtime di testo",
            AutoSetupPhase.SelectingModel => "Passaggio 3 di 5: scelta del modello",
            AutoSetupPhase.AssigningRoles => "Passaggio 4 di 5: assegnazione dei ruoli",
            AutoSetupPhase.VerifyingChat => "Passaggio 5 di 5: verifica della chat locale",
            _ => "Configurazione in corso"
        };

        private static string ProgressDetailItalian(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Verifica delle risorse hardware disponibili.",
            AutoSetupPhase.CheckingRuntime => "Controllo dei runtime locali, senza modificare quelli gestiti dall'utente.",
            AutoSetupPhase.SelectingModel => "Ricerca di un modello compatibile con questo PC.",
            AutoSetupPhase.AssigningRoles => "Impostazione dei ruoli dei modelli installati.",
            AutoSetupPhase.VerifyingChat => "Verifica che il Core locale risponda correttamente.",
            _ => "Operazione in corso."
        };

        private static string StepTitle(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Step 1 of 5: reading this PC",
            AutoSetupPhase.CheckingRuntime => "Step 2 of 5: text runtime",
            AutoSetupPhase.SelectingModel => "Step 3 of 5: choosing a model",
            AutoSetupPhase.AssigningRoles => "Step 4 of 5: assigning roles",
            AutoSetupPhase.VerifyingChat => "Step 5 of 5: verifying local chat",
            _ => "Setting up"
        };
    }
}
