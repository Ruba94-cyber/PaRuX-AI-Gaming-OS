using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private bool _modelDownloadSettingsAdded;

        private void AddModelDownloadSettingsSection()
        {
            if (_modelDownloadSettingsAdded || SettingsContentPanel == null) return;
            _modelDownloadSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "Models",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "Models", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(section, "Settings_ModelDownloads_Section");
            section.Children.Add(new TextBlock
            {
                Text = T("MODEL DOWNLOADS", "DOWNLOAD DEI MODELLI"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke)
                && stroke is Microsoft.UI.Xaml.Media.Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = T("One bandwidth limit is shared by every model download. Set 0 for unlimited.", "Un unico limite di banda è condiviso da ogni download di modelli. Imposta 0 per nessun limite."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var bandwidthBox = new TextBox
            {
                Header = T("Shared limit (MB/s)", "Limite condiviso (MB/s)"),
                PlaceholderText = T("0 = unlimited", "0 = nessun limite"),
                MinWidth = 220
            };
            AutomationProperties.SetAutomationId(bandwidthBox, "Settings_ModelDownloadBandwidth_TextBox");
            content.Children.Add(bandwidthBox);

            var bandwidthActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var saveBandwidth = new Button { Content = T("Save bandwidth limit", "Salva il limite di banda") };
            AutomationProperties.SetAutomationId(saveBandwidth, "Settings_ModelDownloadBandwidth_Save_Button");
            var bandwidthStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(bandwidthStatus, "Settings_ModelDownloadBandwidth_Status");
            bandwidthActions.Children.Add(saveBandwidth);
            bandwidthActions.Children.Add(bandwidthStatus);
            content.Children.Add(bandwidthActions);

            var divider = new Border { Height = 1, Opacity = 0.25, Margin = new Thickness(0, 4, 0, 4) };
            content.Children.Add(divider);
            content.Children.Add(new TextBlock
            {
                Text = T("Hugging Face access token", "Token di accesso a Hugging Face"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = T("Only needed for gated or private repositories. PaRuX stores it with the server's local credential protection. The saved value is write-only and is never shown here.", "Serve solo per repository protetti o privati. PaRuX lo conserva con la protezione locale delle credenziali del server. Il valore salvato è in sola scrittura e non viene mai mostrato qui."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var tokenBox = new PasswordBox { PlaceholderText = T("Enter a token to save or replace it", "Inserisci un token per salvarlo o sostituirlo") };
            AutomationProperties.SetAutomationId(tokenBox, "Settings_HuggingFaceToken_PasswordBox");
            content.Children.Add(tokenBox);

            var credentialActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var saveToken = new Button { Content = T("Save token", "Salva il token") };
            AutomationProperties.SetAutomationId(saveToken, "Settings_HuggingFaceToken_Save_Button");
            var removeToken = new Button { Content = T("Remove saved token", "Rimuovi il token salvato"), IsEnabled = false };
            AutomationProperties.SetAutomationId(removeToken, "Settings_HuggingFaceToken_Remove_Button");
            credentialActions.Children.Add(saveToken);
            credentialActions.Children.Add(removeToken);
            content.Children.Add(credentialActions);

            var credentialStatus = new TextBlock { Text = T("Checking saved-token status…", "Controllo dello stato del token salvato…"), TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
            AutomationProperties.SetAutomationId(credentialStatus, "Settings_HuggingFaceToken_Status");
            content.Children.Add(credentialStatus);

            saveBandwidth.Click += async (sender, args) =>
            {
                if (!ModelDownloadSettingsPolicy.TryParseBandwidthLimit(bandwidthBox.Text, out double limit))
                {
                    bandwidthStatus.Text = T("Enter a number from 0 to 10000 MB/s.", "Inserisci un numero da 0 a 10000 MB/s.");
                    return;
                }

                saveBandwidth.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.SaveTransferSettingsAsync(
                        new ModelDownloadTransferSettings { BandwidthLimitMegabytesPerSecond = limit }, default);
                    bandwidthStatus.Text = T("Saved · ", "Salvato · ") + ModelDownloadSettingsPolicy.DescribeBandwidthLimit(limit)
                        + T(" shared across model downloads.", " condivisi tra i download dei modelli.");
                }
                catch (Exception ex) { bandwidthStatus.Text = T("Couldn't save the bandwidth limit: ", "Impossibile salvare il limite di banda: ") + ex.Message; }
                finally { saveBandwidth.IsEnabled = true; }
            };

            saveToken.Click += async (sender, args) =>
            {
                string token = tokenBox.Password;
                if (string.IsNullOrWhiteSpace(token))
                {
                    credentialStatus.Text = T("Enter a token to save or replace the current one.", "Inserisci un token per salvare o sostituire quello attuale.");
                    return;
                }

                saveToken.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.SaveHubCredentialAsync(token.Trim(), default);
                    tokenBox.Password = string.Empty;
                    await RefreshCredentialStatusAsync();
                }
                catch
                {
                    // Do not include request or response content here; the token is never reflected into UI text.
                    credentialStatus.Text = T("Couldn't save the token. Check PaRuX Core and try again.", "Impossibile salvare il token. Controlla PaRuX Core e riprova.");
                }
                finally { saveToken.IsEnabled = true; }
            };

            removeToken.Click += async (sender, args) =>
            {
                removeToken.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.ClearHubCredentialAsync(default);
                    tokenBox.Password = string.Empty;
                    await RefreshCredentialStatusAsync();
                }
                catch
                {
                    credentialStatus.Text = T("Couldn't remove the saved token. Check PaRuX Core and try again.", "Impossibile rimuovere il token salvato. Controlla PaRuX Core e riprova.");
                    removeToken.IsEnabled = true;
                }
            };

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
            _ = LoadModelDownloadSettingsAsync();

            async System.Threading.Tasks.Task RefreshCredentialStatusAsync()
            {
                try
                {
                    ModelDownloadCredentialStatus status = await ModelDownloadClient.Current.GetHubCredentialStatusAsync(default);
                    credentialStatus.Text = ModelDownloadSettingsPolicy.DescribeCredentialStatus(status);
                    removeToken.IsEnabled = status.TokenConfigured;
                }
                catch
                {
                    credentialStatus.Text = T("Couldn't check saved-token status. The token value is never displayed.", "Impossibile controllare lo stato del token salvato. Il valore del token non viene mai mostrato.");
                    removeToken.IsEnabled = false;
                }
            }

            async System.Threading.Tasks.Task LoadModelDownloadSettingsAsync()
            {
                try
                {
                    ModelDownloadTransferSettings settings = await ModelDownloadClient.Current.GetTransferSettingsAsync(default);
                    bandwidthBox.Text = settings.BandwidthLimitMegabytesPerSecond.ToString(
                        "0.##", System.Globalization.CultureInfo.CurrentCulture);
                    bandwidthStatus.Text = T("Current: ", "Attuale: ")
                        + ModelDownloadSettingsPolicy.DescribeBandwidthLimit(settings.BandwidthLimitMegabytesPerSecond);
                }
                catch (Exception ex) { bandwidthStatus.Text = T("Couldn't load the shared limit: ", "Impossibile caricare il limite condiviso: ") + ex.Message; }
                await RefreshCredentialStatusAsync();
            }
        }
    }
}
