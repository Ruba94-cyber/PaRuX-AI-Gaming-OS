using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// First-run choice between AI only, Gaming only and Both. It writes the existing settings store, hides the language-model
    /// setup when it is not needed (Gaming only) and lets that path finish without any model, runtime or chat check. The same
    /// choice is available later in Settings and never deletes anything.
    /// </summary>
    public sealed partial class OnboardingView
    {
        private bool _setupProfileInitialising;
        private bool _setupProfileSwitchBusy;
        private readonly Dictionary<UIElement, Visibility> _aiSectionVisibility = new Dictionary<UIElement, Visibility>();

        private static bool SetupProfileItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        private static SetupProfile StoredSetupProfile => SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);

        private void InitializeSetupProfileChoice()
        {
            bool it = SetupProfileItalian;
            SetupProfileHeader.Text = it ? "COME USERAI PaRuX?" : "HOW WILL YOU USE PaRuX?";
            foreach ((RadioButton button, SetupProfile profile) in OptionButtons())
            {
                button.Content = SetupProfilePolicy.Label(profile, it);
                AutomationProperties.SetName(button, SetupProfilePolicy.Label(profile, it) + ". " + SetupProfilePolicy.Description(profile, it));
            }
            GamingOnlyStartButton.Content = it ? "Inizia con i giochi" : "Start with games";
            OnboardingWelcomeSubtitle.Text = it
                ? "Scegli IA, Gaming o entrambe. La configurazione guidata ti porta alla prima chat; i dettagli tecnici restano facoltativi."
                : "Choose AI, Gaming or both. Guided setup gets you to your first chat; technical settings are optional.";
            ManualSetupExpander.Header = it ? "Configurazione manuale, modelli e download" : "Manual setup, models and downloads";

            if (SetupProfileSection.Parent is Panel parent)
            {
                int own = parent.Children.IndexOf(SetupProfileSection);
                for (int i = own + 1; i < parent.Children.Count; i++) _aiSectionVisibility[parent.Children[i]] = parent.Children[i].Visibility;
            }

            SetupProfile current = StoredSetupProfile;
            _setupProfileInitialising = true;
            try { SetupProfileChoice.SelectedIndex = OptionButtons().ToList().FindIndex(o => o.Profile == current); }
            finally { _setupProfileInitialising = false; }
            ApplySetupProfileToLayout(current);
            if (PaRuX.Contracts.CorporateMode.IsActive)
            {
                // Corporate is delivered preconfigured for local Chat and documents.
                // Its setup profile cannot be changed: don't show choices the user
                // cannot actually select or unrelated Gaming/Media onboarding.
                SetupProfileSection.Visibility = Visibility.Collapsed;
                AutoSetupSection.Visibility = Visibility.Collapsed;
                BundledDetailsSection.Visibility = Visibility.Collapsed;
                StorageDetailsSection.Visibility = Visibility.Collapsed;
                ManualSetupExpander.Visibility = Visibility.Collapsed;
                CorporateManageModelsButton.Visibility = Visibility.Visible;
                VerifyLocalChatButton.Content = it ? "Verifica la Chat locale" : "Verify local Chat";
                CorporateManageModelsButton.Content = it ? "Scegli o importa un altro modello" : "Choose or import another model";
                GetStartedButton.Content = it ? "Apri Chat" : "Open Chat";
                OnboardingWelcomeSubtitle.Text = it
                    ? "Corporate Lite: Chat AI e documenti sul PC. Il motore CPU è già incluso; puoi importare altri modelli quando vuoi."
                    : "Corporate Lite: AI Chat and documents on this PC. The CPU engine is already included; add more models whenever you need.";
                ManualSetupExpander.Header = it
                    ? "Modelli e opzioni locali (facoltative)"
                    : "Models and local options (optional)";
            }
        }

        private IEnumerable<(RadioButton Button, SetupProfile Profile)> OptionButtons()
        {
            yield return (SetupProfileBothOption, SetupProfile.Both);
            yield return (SetupProfileAiOption, SetupProfile.AiOnly);
            yield return (SetupProfileGamingOption, SetupProfile.GamingOnly);
        }

        private async void SetupProfileChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_setupProfileInitialising || _setupProfileSwitchBusy
                || PaRuX.Contracts.CorporateMode.IsActive) return;
            if (SetupProfileChoice.SelectedItem is not RadioButton chosen) return;
            SetupProfile requested = OptionButtons().First(o => ReferenceEquals(o.Button, chosen)).Profile;
            SetupProfile previous = StoredSetupProfile;
            if (requested == previous) return;

            // A user cannot replace the running AI setup profile halfway through a model download
            // or runtime verification. Revert the visual choice instead of silently switching workers.
            if (_autoSetupCts != null || _verifyLocalChatCts != null || _componentsBusy)
            {
                _setupProfileInitialising = true;
                try { SetupProfileChoice.SelectedIndex = OptionButtons().ToList().FindIndex(o => o.Profile == previous); }
                finally { _setupProfileInitialising = false; }
                SetupProfileDescription.Text = SetupProfileItalian
                    ? "Completa la verifica o annulla la configurazione in corso prima di cambiare modalità."
                    : "Finish the current check or cancel setup before changing mode.";
                return;
            }

            _setupProfileSwitchBusy = true;
            SetupProfileChoice.IsEnabled = false;
            GamingOnlyStartButton.IsEnabled = false;
            AutoSetupStartButton.IsEnabled = false;
            GetStartedButton.IsEnabled = false;
            SetupProfileDescription.Text = SetupProfileItalian ? "Cambio modalità in corso…" : "Switching mode…";

            try
            {
                // The transactional switch persists only after all services changed successfully.
                SetupTransitionResult result;
                try { result = await SetupProfileRuntime.SwitchAsync(requested); }
                catch (Exception ex)
                {
                    Log.Write("Onboarding profile switch threw: " + ex.GetType().Name);
                    result = new SetupTransitionResult(false, previous, Array.Empty<SetupTransitionStep>(),
                        Array.Empty<SetupTransitionStep>(), null, false, ex.GetType().Name);
                }
                SetupProfile effective = StoredSetupProfile;
                if (effective != requested)
                {
                    _setupProfileInitialising = true;
                    try { SetupProfileChoice.SelectedIndex = OptionButtons().ToList().FindIndex(o => o.Profile == effective); }
                    finally { _setupProfileInitialising = false; }
                }
                ApplySetupProfileToLayout(effective);
                if (!result.Success)
                    SetupProfileDescription.Text = (SetupProfileItalian
                        ? "Cambio non riuscito; modalità precedente mantenuta. "
                        : "Mode change failed; previous mode retained. ")
                        + result.Message + " " + SetupProfileDescription.Text;
                App.MainWin?.ApplySetupProfile();
                if (previous == SetupProfile.GamingOnly && effective != SetupProfile.GamingOnly)
                    StartAiRefreshes();
            }
            finally
            {
                _setupProfileSwitchBusy = false;
                SetupProfileChoice.IsEnabled = true;
                GamingOnlyStartButton.IsEnabled = true;
                AutoSetupStartButton.IsEnabled = _autoSetup == null
                    || OnboardingAutoSetupPresenter.Present(_autoSetup.State).PrimaryEnabled;
                GetStartedButton.IsEnabled = !_componentsBusy && _verifyLocalChatCts == null && _autoSetupCts == null;
            }
        }

        private void ApplySetupProfileToLayout(SetupProfile profile)
        {
            bool gamingOnly = profile == SetupProfile.GamingOnly;
            SetupProfileDescription.Text = SetupProfilePolicy.Description(profile, SetupProfileItalian);
            GamingOnlyStartButton.Visibility = gamingOnly ? Visibility.Visible : Visibility.Collapsed;
            // Gaming only: nothing below is about a language model, so the whole AI setup is out of the way.
            if (SetupProfileSection.Parent is not Panel parent) return;
            foreach (KeyValuePair<UIElement, Visibility> original in _aiSectionVisibility)
                original.Key.Visibility = gamingOnly ? Visibility.Collapsed : original.Value;
        }

        private async void GamingOnlyStart_Click(object sender, RoutedEventArgs e)
        {
            if (_setupProfileSwitchBusy || _autoSetupCts != null || StoredSetupProfile != SetupProfile.GamingOnly) return;
            GamingOnlyStartButton.IsEnabled = false;
            try
            {
                SetupTransitionResult result = await SetupProfileRuntime.SwitchAsync(SetupProfile.GamingOnly);
                if (!result.Success || result.EffectiveProfile != SetupProfile.GamingOnly)
                {
                    SetupProfileDescription.Text = (SetupProfileItalian
                        ? "Non è possibile avviare Gaming: "
                        : "Cannot start Gaming: ") + result.Message;
                    return;
                }

                SettingsStore.Current.Data.OnboardingCompleted = true;
                SettingsStore.Current.Save();
                Completed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Write("Gaming-only start failed: " + ex.GetType().Name);
                SetupProfileDescription.Text = SetupProfileItalian
                    ? "Impossibile completare l'avvio Gaming. Riprova."
                    : "Could not complete Gaming setup. Please retry.";
            }
            finally { GamingOnlyStartButton.IsEnabled = true; }
        }
    }
}
