using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Presents a one-download review gate when repository license metadata is missing,
    /// unavailable, or outside the conservative permissive SPDX allow-list. This is not
    /// legal verification and deliberately does not persist consent.
    /// </summary>
    internal static class ModelLicenseConsentPrompt
    {
        private static string T(string en, string it) => PaRuX.Services.UiText.Pick(en, it);
        public static async Task<bool> ConfirmAndStartRepositoryDownloadAsync(
            XamlRoot xamlRoot, string repoId, string fileName,
            Func<string, ModelDownloadLicenseEvidence, Task> startDownload)
        {
            ModelRepoDetails? details = null;
            bool metadataRetrieved = false;
            try
            {
                details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, System.Threading.CancellationToken.None);
                metadataRetrieved = true;
            }
            catch (Exception ex)
            {
                Log.Write("Model license metadata lookup failed for " + repoId + ": " + ex.Message);
            }

            if (!ModelRevisionIdentity.IsValid(details?.Revision))
                return await ShowRevisionUnavailableAsync(xamlRoot);

            ModelLicenseConsentPolicy.Decision decision = ModelLicenseConsentPolicy.Evaluate(details?.License, metadataRetrieved);
            return await ConfirmAndStartAtRevisionAsync(xamlRoot, repoId, fileName, decision, details!.Revision!, startDownload);
        }

        public static async Task<bool> ConfirmAndStartAtRevisionAsync(
            XamlRoot xamlRoot,
            string repoId,
            string fileName,
            ModelLicenseConsentPolicy.Decision decision,
            string? revision,
            Func<string, ModelDownloadLicenseEvidence, Task> startDownload)
        {
            string? normalizedRevision = ModelRevisionIdentity.Normalize(revision);
            if (normalizedRevision == null) return await ShowRevisionUnavailableAsync(xamlRoot);
            if (startDownload == null) throw new ArgumentNullException(nameof(startDownload));

            return await ConfirmAndStartCoreAsync(
                xamlRoot, repoId, fileName, decision, normalizedRevision,
                evidence => startDownload(normalizedRevision, evidence));
        }

        private static async Task<bool> ConfirmAndStartCoreAsync(
            XamlRoot xamlRoot,
            string repoId,
            string fileName,
            ModelLicenseConsentPolicy.Decision decision,
            string revision,
            Func<ModelDownloadLicenseEvidence, Task> startDownload)
        {
            if (startDownload == null) throw new ArgumentNullException(nameof(startDownload));
            ModelDownloadLicenseEvidence evidence = new()
            {
                MetadataStatus = decision.LicenseMetadataStatus,
                PublisherDeclaredLicense = decision.PublisherDeclaredLicense,
                MetadataObservedUtc = decision.MetadataObservedUtc,
                ReviewRequired = decision.RequiresReview
            };
            if (!decision.RequiresReview)
            {
                await startDownload(evidence);
                return true;
            }

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = decision.MetadataStatus,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = decision.MetadataRetrieved
                    ? T("Hugging Face repository metadata is publisher-declared and has not been independently verified.", "I metadati del repository Hugging Face sono dichiarati dall'editore e non sono stati verificati in modo indipendente.")
                    : T("Hugging Face repository metadata could not be loaded. Review the repository terms before proceeding.", "Impossibile caricare i metadati del repository Hugging Face. Controlla i termini del repository prima di procedere."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });

            content.Children.Add(new TextBlock
            {
                Text = T("Selected immutable repository revision: ", "Revisione immutabile del repository selezionata: ") + revision,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = T("Publisher licence metadata observed (UTC): ", "Metadati di licenza dell'editore osservati (UTC): ") + decision.MetadataObservedUtc.ToString("u"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.82
            });

            Uri? modelCardUri = ModelLicenseConsentPolicy.GetModelCardUri(repoId, revision);
            if (modelCardUri != null)
            {
                content.Children.Add(new HyperlinkButton
                {
                    Content = T("Review model card and repository terms", "Leggi la scheda del modello e i termini del repository"),
                    NavigateUri = modelCardUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            var confirmation = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = T("I reviewed the repository/model card terms and choose to download ", "Ho letto i termini del repository/della scheda del modello e scelgo di scaricare ") + fileName + T(". This decision applies to this download only.", ". Questa decisione vale solo per questo download."),
                    TextWrapping = TextWrapping.Wrap
                },
                IsChecked = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmation, "ModelLicenseConsent_Confirm_CheckBox");
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(validation, "ModelLicenseConsent_Validation_Text");
            content.Children.Add(confirmation);
            content.Children.Add(validation);
            content.Children.Add(new TextBlock
            {
                Text = T("PaRuX does not independently determine whether these terms are appropriate for your use. No download starts unless you confirm below.", "PaRuX non stabilisce in modo indipendente se questi termini siano adatti al tuo uso. Nessun download parte se non confermi qui sotto."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = T("Review model terms before downloading", "Controlla i termini del modello prima di scaricare"),
                Content = content,
                PrimaryButtonText = T("Download selected file", "Scarica il file selezionato"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ModelLicenseConsent_Dialog");
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (confirmation.IsChecked != true)
                {
                    args.Cancel = true;
                    validation.Text = T("Review the repository terms and select the confirmation before downloading.", "Controlla i termini del repository e seleziona la conferma prima di scaricare.");
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            evidence.ReviewAcknowledged = true;
            evidence.ReviewAcknowledgedUtc = DateTime.UtcNow;
            await startDownload(evidence);
            return true;
        }

        private static async Task<bool> ShowRevisionUnavailableAsync(XamlRoot xamlRoot)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = T("Cannot pin this model revision", "Impossibile fissare questa revisione del modello"),
                Content = new TextBlock
                {
                    Text = T("Hugging Face did not provide a valid immutable commit SHA. PaRuX cannot bind the reviewed terms to exact model bytes, so this new download was not started. Try again when repository metadata is available.", "Hugging Face non ha fornito un SHA di commit immutabile valido. PaRuX non può legare i termini controllati ai byte esatti del modello, quindi questo nuovo download non è partito. Riprova quando i metadati del repository sono disponibili."),
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = T("Close", "Chiudi")
            };
            await dialog.ShowAsync();
            return false;
        }
    }
}
