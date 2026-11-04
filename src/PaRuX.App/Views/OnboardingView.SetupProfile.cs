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
        }

        private IEnumerable<(RadioButton Button, SetupProfile Profile)> OptionButtons()
        {
            yield return (SetupProfileBothOption, SetupProfile.Both);
            yield return (SetupProfileAiOption, SetupProfile.AiOnly);
            yield return (SetupProfileGamingOption, SetupProfile.GamingOnly);
        }

        private void SetupProfileChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_setupProfileInitialising) return;
            if (SetupProfileChoice.SelectedItem is not RadioButton chosen) return;
            SetupProfile profile = OptionButtons().First(o => ReferenceEquals(o.Button, chosen)).Profile;
            SetupProfile previous = StoredSetupProfile;
            SettingsStore.Current.Data.SetupProfileId = SetupProfilePolicy.ToStorage(profile);
            SettingsStore.Current.Save();
            ApplySetupProfileToLayout(profile);
            App.MainWin?.ApplySetupProfile();
            // Coming back from Gaming only: the AI checks were skipped, so run them now.
            if (previous == SetupProfile.GamingOnly && profile != SetupProfile.GamingOnly) StartAiRefreshes();
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

        private void GamingOnlyStart_Click(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.Data.SetupProfileId = SetupProfilePolicy.ToStorage(SetupProfile.GamingOnly);
            SettingsStore.Current.Data.OnboardingCompleted = true;
            SettingsStore.Current.Save();
            Completed?.Invoke();
        }
    }
}
