using System;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure decisions for the dialog shown when a non-Git apply is blocked by an unresolved recovery journal. It
    /// chooses which explicit actions may be offered and the plain-language text for the verdict and for each
    /// outcome. It touches no file and no WinUI type. A button enabled here is only a request: the recovery service
    /// re-verifies the folder under the project lease before it writes or discards anything.
    /// </summary>
    internal static class ShadowRecoveryDialogModel
    {
        internal static string DialogTitle => UiText.Pick("Recovery of an interrupted apply", "Ripristino di un'applicazione interrotta");
        internal static string RestoreButtonText => UiText.Pick("Restore originals", "Ripristina gli originali");
        internal static string KeepButtonText => UiText.Pick("Keep applied changes", "Mantieni le modifiche applicate");
        internal static string CancelButtonText => UiText.Pick("Cancel", "Annulla");
        internal static string OpenFolderButtonText => UiText.Pick("Open recovery folder", "Apri la cartella di ripristino");
        internal static string CloseButtonText => UiText.Pick("Close", "Chiudi");
        internal static string KeepConfirmTitle => UiText.Pick("Delete the backups of the originals?", "Eliminare i backup degli originali?");
        internal static string KeepConfirmMessage => UiText.Pick(
            "Keeping the applied changes permanently deletes the recovery journal and the copies of the originals "
            + "saved by PaRuX. The project files are not modified, but PaRuX will no longer be able to restore "
            + "the originals automatically from that recovery copy. This operation cannot be undone.",
            "Mantenere le modifiche applicate elimina definitivamente il registro di ripristino e le copie degli originali "
            + "salvate da PaRuX. I file del progetto non vengono modificati, ma PaRuX non potrà più ripristinare "
            + "automaticamente gli originali da quella copia. L'operazione non può essere annullata.");
        internal static string KeepConfirmButtonText => UiText.Pick("Delete backups and keep", "Elimina i backup e mantieni");
        internal static string KeepDeclinedText => UiText.Pick(
            "No changes made: the recovery journal remains pending and you can choose again.",
            "Nessuna modifica: il registro di ripristino resta in sospeso e puoi scegliere di nuovo.");

        internal enum RecoveryChoice
        {
            Restore,
            KeepCurrentFiles
        }

        /// <summary>
        /// What the dialog shows. When OffersDecision is true the dialog offers restore, keep and cancel. Otherwise it
        /// only opens the recovery folder and closes, because no automatic action can be verified safely.
        /// </summary>
        internal sealed record Plan(
            string Title,
            string Explanation,
            string FileSummary,
            bool OffersDecision,
            bool RestoreEnabled,
            bool KeepEnabled);

        /// <summary>
        /// The explicit second step before the backups are deleted for good. Cancel is the default button, so Enter
        /// or Escape keeps every backup.
        /// </summary>
        internal sealed record KeepConfirmation(
            string Title,
            string Message,
            string ConfirmButtonText,
            string CancelButtonText,
            bool DefaultIsCancel);

        internal static KeepConfirmation DescribeKeepConfirmation() =>
            new(KeepConfirmTitle, KeepConfirmMessage, KeepConfirmButtonText, CancelButtonText, DefaultIsCancel: true);

        internal static Plan Describe(ShadowApplyRecoveryInspector.RecoveryAssessment assessment)
        {
            int total = assessment.Files.Count;
            int original = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact);
            int applied = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);
            int changed = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit);
            string summary = total == 0
                ? UiText.Pick("No verifiable files in the journal.", "Nessun file verificabile nel registro.")
                : UiText.Pick("Files in the journal: ", "File nel registro: ") + total + UiText.Pick(". Originals: ", ". Originali: ") + original
                  + UiText.Pick(". PaRuX version: ", ". Versione PaRuX: ") + applied
                  + UiText.Pick(". Modified after the interruption: ", ". Modificati dopo l'interruzione: ") + changed + ".";

            // Accepting the applied state needs every recorded file to be PaRuX's version, not merely most of them.
            bool everyFileApplied = total > 0 && applied == total;

            return assessment.Status switch
            {
                ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact => new Plan(DialogTitle,
                    UiText.Pick("The project files match the recorded originals, so the interrupted apply "
                    + "left no changes to undo. Restoring removes only the recovery journal "
                    + "and does not write to the project files.",
                    "I file del progetto corrispondono agli originali registrati, quindi l'applicazione interrotta "
                    + "non ha lasciato modifiche da annullare. Il ripristino rimuove solo il registro "
                    + "e non scrive nei file del progetto."),
                    summary, OffersDecision: true, RestoreEnabled: true, KeepEnabled: everyFileApplied),
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent => new Plan(DialogTitle,
                    UiText.Pick("A PaRuX apply was interrupted and some files still contain the applied version. "
                    + "Restoring brings the modified files back to the verified original version. Keeping the "
                    + "applied changes is only available if every file matches the PaRuX version.",
                    "Un'applicazione di PaRuX è stata interrotta e alcuni file contengono ancora la versione applicata. "
                    + "Il ripristino riporta i file modificati alla versione originale verificata. Mantenere le "
                    + "modifiche applicate è possibile solo se ogni file corrisponde alla versione PaRuX."),
                    summary, OffersDecision: true, RestoreEnabled: true, KeepEnabled: everyFileApplied),
                ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit => new Plan(DialogTitle,
                    UiText.Pick("Some files were modified after the interruption and match neither the original "
                    + "nor the PaRuX version. PaRuX does not overwrite external changes: the journal and backups "
                    + "are left unchanged. Check the project files and the journal manually before acting.",
                    "Alcuni file sono stati modificati dopo l'interruzione e non corrispondono né all'originale "
                    + "né alla versione PaRuX. PaRuX non sovrascrive le modifiche esterne: registro e backup "
                    + "restano invariati. Controlla a mano i file del progetto e il registro prima di agire."),
                    summary, OffersDecision: false, RestoreEnabled: false, KeepEnabled: false),
                _ => new Plan(DialogTitle,
                    UiText.Pick("The recovery journal cannot be verified safely, so PaRuX performs no "
                    + "automatic action and leaves everything unchanged. Open the recovery folder for a manual check.",
                    "Il registro di ripristino non può essere verificato in sicurezza, quindi PaRuX non esegue "
                    + "alcuna azione automatica e lascia tutto invariato. Apri la cartella di ripristino per un controllo manuale."),
                    summary, OffersDecision: false, RestoreEnabled: false, KeepEnabled: false)
            };
        }

        /// <summary>
        /// Plain-language result of a recovery attempt. A failure always says that the journal was kept, because a
        /// kept journal is what continues to block new applies until a person resolves it.
        /// </summary>
        internal static string DescribeOutcome(ShadowApplyRecoveryService.RestoreOutcome outcome, RecoveryChoice choice)
        {
            if (!outcome.Success)
                return UiText.Pick("Operation not completed: the recovery journal was kept, so new "
                    + "applies remain blocked until it is resolved. Details: ",
                    "Operazione non completata: il registro di ripristino è stato conservato, quindi le nuove "
                    + "applicazioni restano bloccate finché non viene risolto. Dettagli: ") + outcome.Message;
            if (choice == RecoveryChoice.KeepCurrentFiles)
                return UiText.Pick("Applied changes kept: the recovery journal and the backups of the originals were "
                    + "removed. No project file was modified. You can retry the apply.",
                    "Modifiche applicate mantenute: il registro di ripristino e i backup degli originali sono stati "
                    + "rimossi. Nessun file del progetto è stato modificato. Puoi riprovare l'applicazione.");
            return outcome.Status == ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact
                ? UiText.Pick("The recovery journal was removed. The files were already original, so no file was "
                    + "written. You can retry the apply.",
                    "Il registro di ripristino è stato rimosso. I file erano già originali, quindi nessun file è stato "
                    + "scritto. Puoi riprovare l'applicazione.")
                : UiText.Pick("Restore completed: the files are back to the verified original version and the journal was "
                    + "removed. You can retry the apply.",
                    "Ripristino completato: i file sono tornati alla versione originale verificata e il registro è stato "
                    + "rimosso. Puoi riprovare l'applicazione.");
        }

        private static int Count(ShadowApplyRecoveryInspector.RecoveryAssessment assessment,
            ShadowApplyRecoveryInspector.RecoveryStatus status) =>
            assessment.Files.Count(file => file.Status == status);
    }
}
