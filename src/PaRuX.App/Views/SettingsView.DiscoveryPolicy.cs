using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private bool _discoveryPolicyLoading;
        private string _lastConfirmedAutoEvaluate = DiscoveryPolicyPresenter.DefaultAutoEvaluate;
        private string _lastConfirmedPromotion = DiscoveryPolicyPresenter.DefaultPromotion;

        public async Task RefreshDiscoveryPolicyAsync(CancellationToken ct = default)
        {
            if (Settings_DiscoveryPolicy_AutoEvaluate_Toggle == null || Settings_DiscoveryPolicy_Promotion_ComboBox == null)
                return;

            SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatLoadingStatus());
            try
            {
                bool gamingActive = GamingManager.Current.IsActive;
                if (DiscoveryPolicyClient.Current is StubDiscoveryPolicyClient stub)
                {
                    stub.SetGamingActive(gamingActive);
                }

                DiscoveryPolicyState state = await DiscoveryPolicyClient.Current.GetPolicyAsync(ct);
                _lastConfirmedAutoEvaluate = state.AutoEvaluate;
                _lastConfirmedPromotion = state.PromotionPolicy;

                ApplyDiscoveryPolicyPresentation(state.AutoEvaluate, state.PromotionPolicy, gamingActive || state.GamingActive);
                SetDiscoveryPolicyStatus(string.IsNullOrEmpty(state.WarningCode)
                    ? string.Empty
                    : DiscoveryPolicyPresenter.FormatCorruptFileWarning());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The view is going away; nothing to report.
            }
            catch (Exception ex)
            {
                Log.Write("RefreshDiscoveryPolicyAsync failed: " + ex.Message);
                // Do not offer controls whose value Core never confirmed.
                SetDiscoveryPolicyControlsEnabled(false);
                SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatLoadErrorStatus(ex.Message));
            }
        }

        private void SetDiscoveryPolicyStatus(string text)
        {
            if (Settings_DiscoveryPolicy_Status_Text != null)
            {
                Settings_DiscoveryPolicy_Status_Text.Text = text;
            }
        }

        private void SetDiscoveryPolicyControlsEnabled(bool enabled)
        {
            if (Settings_DiscoveryPolicy_AutoEvaluate_Toggle != null)
            {
                Settings_DiscoveryPolicy_AutoEvaluate_Toggle.IsEnabled = enabled && !GamingManager.Current.IsActive;
            }
            if (Settings_DiscoveryPolicy_Promotion_ComboBox != null)
            {
                Settings_DiscoveryPolicy_Promotion_ComboBox.IsEnabled = enabled;
            }
        }

        private void ApplyDiscoveryPolicyPresentation(string autoEvaluate, string promotionPolicy, bool gamingActive)
        {
            DiscoveryPolicyPresentation presentation = DiscoveryPolicyPresenter.Present(
                autoEvaluate, promotionPolicy, gamingActive);

            _discoveryPolicyLoading = true;
            try
            {
                if (Settings_DiscoveryPolicy_AutoEvaluate_Toggle != null)
                {
                    Settings_DiscoveryPolicy_AutoEvaluate_Toggle.IsEnabled = presentation.AutoEvaluate.IsEnabled;
                    Settings_DiscoveryPolicy_AutoEvaluate_Toggle.IsOn = DiscoveryPolicyPresenter.IsAutoEvaluateOn(presentation.AutoEvaluate.Value);
                    AutomationProperties.SetName(Settings_DiscoveryPolicy_AutoEvaluate_Toggle, presentation.AutoEvaluate.Label);
                }

                if (DiscoveryPolicyAutoEvaluateExplanation != null)
                {
                    DiscoveryPolicyAutoEvaluateExplanation.Text = presentation.AutoEvaluate.Explanation;
                }

                if (Settings_DiscoveryPolicy_Promotion_ComboBox != null)
                {
                    Settings_DiscoveryPolicy_Promotion_ComboBox.IsEnabled = presentation.Promotion.IsEnabled;
                    SelectPromotionByTag(presentation.Promotion.Value);
                    AutomationProperties.SetName(Settings_DiscoveryPolicy_Promotion_ComboBox, presentation.Promotion.Label);
                }

                if (DiscoveryPolicyPromotionExplanation != null)
                {
                    DiscoveryPolicyPromotionExplanation.Text = presentation.Promotion.Explanation;
                }
            }
            finally
            {
                _discoveryPolicyLoading = false;
            }
        }

        private void SelectPromotionByTag(string tag)
        {
            if (Settings_DiscoveryPolicy_Promotion_ComboBox == null) return;
            string normalized = DiscoveryPolicyPresenter.NormalizePromotion(tag);
            for (int i = 0; i < Settings_DiscoveryPolicy_Promotion_ComboBox.Items.Count; i++)
            {
                if (Settings_DiscoveryPolicy_Promotion_ComboBox.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    Settings_DiscoveryPolicy_Promotion_ComboBox.SelectedIndex = i;
                    return;
                }
            }
            if (Settings_DiscoveryPolicy_Promotion_ComboBox.Items.Count > 0)
            {
                Settings_DiscoveryPolicy_Promotion_ComboBox.SelectedIndex = 0;
            }
        }

        private async void DiscoveryPolicyAutoEvaluateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_discoveryPolicyLoading || Settings_DiscoveryPolicy_AutoEvaluate_Toggle == null) return;

            string requestedValue = DiscoveryPolicyPresenter.ToAutoEvaluateValue(Settings_DiscoveryPolicy_AutoEvaluate_Toggle.IsOn);
            SetDiscoveryPolicyControlsEnabled(false);
            SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatSavingStatus());

            try
            {
                DiscoveryPolicyState state = await DiscoveryPolicyClient.Current.SetAutoEvaluateAsync(requestedValue);
                _lastConfirmedAutoEvaluate = state.AutoEvaluate;
                _lastConfirmedPromotion = state.PromotionPolicy;
                SetDiscoveryPolicyControlsEnabled(true);
                SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatSavedStatus());
                if (DiscoveryPolicyAutoEvaluateExplanation != null)
                {
                    bool gamingActive = GamingManager.Current.IsActive || state.GamingActive;
                    DiscoveryPolicyAutoEvaluateExplanation.Text = DiscoveryPolicyPresenter.GetAutoEvaluateExplanation(state.AutoEvaluate, gamingActive);
                }
            }
            catch (Exception ex)
            {
                Log.Write("DiscoveryPolicyAutoEvaluateToggle_Toggled failed: " + ex.Message);

                // Revert the toggle and keep the previous confirmed value on screen.
                _discoveryPolicyLoading = true;
                try
                {
                    Settings_DiscoveryPolicy_AutoEvaluate_Toggle.IsOn = DiscoveryPolicyPresenter.IsAutoEvaluateOn(_lastConfirmedAutoEvaluate);
                }
                finally
                {
                    _discoveryPolicyLoading = false;
                }

                SetDiscoveryPolicyControlsEnabled(true);
                SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatErrorStatus(ex.Message));
                if (DiscoveryPolicyAutoEvaluateExplanation != null)
                {
                    bool gamingActive = GamingManager.Current.IsActive;
                    DiscoveryPolicyAutoEvaluateExplanation.Text = DiscoveryPolicyPresenter.GetAutoEvaluateExplanation(_lastConfirmedAutoEvaluate, gamingActive);
                }
            }
        }

        private async void DiscoveryPolicyPromotionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_discoveryPolicyLoading || Settings_DiscoveryPolicy_Promotion_ComboBox == null) return;

            string? requestedTag = (Settings_DiscoveryPolicy_Promotion_ComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(requestedTag)) return;
            string requestedValue = DiscoveryPolicyPresenter.NormalizePromotion(requestedTag);
            SetDiscoveryPolicyControlsEnabled(false);
            SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatSavingStatus());

            try
            {
                DiscoveryPolicyState state = await DiscoveryPolicyClient.Current.SetPromotionPolicyAsync(requestedValue);
                _lastConfirmedAutoEvaluate = state.AutoEvaluate;
                _lastConfirmedPromotion = state.PromotionPolicy;
                SetDiscoveryPolicyControlsEnabled(true);
                SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatSavedStatus());
                if (DiscoveryPolicyPromotionExplanation != null)
                {
                    DiscoveryPolicyPromotionExplanation.Text = DiscoveryPolicyPresenter.GetPromotionExplanation(state.PromotionPolicy);
                }
            }
            catch (Exception ex)
            {
                Log.Write("DiscoveryPolicyPromotionComboBox_SelectionChanged failed: " + ex.Message);

                // Revert the combo and keep the previous confirmed value on screen.
                _discoveryPolicyLoading = true;
                try
                {
                    SelectPromotionByTag(_lastConfirmedPromotion);
                }
                finally
                {
                    _discoveryPolicyLoading = false;
                }

                SetDiscoveryPolicyControlsEnabled(true);
                SetDiscoveryPolicyStatus(DiscoveryPolicyPresenter.FormatErrorStatus(ex.Message));
                if (DiscoveryPolicyPromotionExplanation != null)
                {
                    DiscoveryPolicyPromotionExplanation.Text = DiscoveryPolicyPresenter.GetPromotionExplanation(_lastConfirmedPromotion);
                }
            }
        }
    }
}
