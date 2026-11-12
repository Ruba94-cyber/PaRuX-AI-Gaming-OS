using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Shared "Delete" flow for a PaRuX-managed model (Model Hub and Settings): confirm with the file name and
    /// size, let PaRuX Core refuse when the model is assigned / loaded / linked / user-managed, and offer to switch the
    /// blocking role(s) to Automatic and retry.</summary>
    public static class ManagedModelDeleteFlow
    {
        private static string T(string en, string it) => PaRuX.Services.UiText.Pick(en, it);
        /// <summary>Returns a status line, and sets deleted when the files were removed.</summary>
        public static async Task<(bool Deleted, string Message)> RunAsync(XamlRoot xamlRoot, string fileName, long sizeBytes)
        {
            var confirm = new ContentDialog
            {
                Title = T("Delete this model?", "Eliminare questo modello?"),
                Content = fileName + " (" + StorageQuotaService.FormatBytes(sizeBytes)
                    + T(") will be permanently deleted from the PaRuX model library. This cannot be undone.", ") verrà eliminato definitivamente dalla libreria modelli di PaRuX. L'operazione non può essere annullata."),
                PrimaryButtonText = T("Delete", "Elimina"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return (false, "Delete cancelled.");

            try
            {
                ManagedModelDeleteOutcome outcome = await ModelDownloadClient.Current.DeleteManagedModelCheckedAsync(fileName, CancellationToken.None);
                if (!outcome.OtherBlockers && outcome.TextRolesToReset.Count > 0)
                {
                    var switchDialog = new ContentDialog
                    {
                        Title = T("This model is in use", "Questo modello è in uso"),
                        Content = outcome.Message + T("\n\nSwitch ", "\n\nPassare ") + string.Join(", ", outcome.TextRolesToReset)
                            + T(" to Automatic and delete it?", " ad Automatico ed eliminarlo?"),
                        PrimaryButtonText = T("Switch to Automatic and delete", "Passa ad Automatico ed elimina"),
                        CloseButtonText = T("Keep model", "Mantieni il modello"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = xamlRoot
                    };
                    if (await switchDialog.ShowAsync() != ContentDialogResult.Primary) return (false, outcome.Message);
                    foreach (string role in outcome.TextRolesToReset)
                        await ModelRolesClient.Current.ResetRoleAsync(role, CancellationToken.None);
                    outcome = await ModelDownloadClient.Current.DeleteManagedModelCheckedAsync(fileName, CancellationToken.None);
                }
                if (outcome.Deleted)
                {
                    Log.Write("Deleted managed model " + fileName);
                    return (true, "Deleted " + fileName + ".");
                }
                Log.Write("Delete refused for " + fileName + ": " + outcome.Message);
                return (false, outcome.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Delete managed model failed: " + ex.Message);
                return (false, "Delete failed: " + ex.Message);
            }
        }
    }
}
