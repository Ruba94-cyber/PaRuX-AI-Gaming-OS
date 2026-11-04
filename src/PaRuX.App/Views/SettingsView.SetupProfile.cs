using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>The General-settings switch for what this PC is used for (AI only / Gaming only / Both). Pure policy:
    /// <see cref="SetupProfilePolicy"/>. Changing it only changes what starts and which workspaces show; nothing is deleted.</summary>
    public sealed partial class SettingsView
    {
        private static bool SetupProfileItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        private static SetupProfile ProfileOfItem(ComboBoxItem item) => SetupProfilePolicy.Parse(item.Tag as string);

        private void LoadSetupProfile()
        {
            bool it = SetupProfileItalian;
            SetupProfileTitle.Text = it ? "A cosa serve PaRuX" : "What PaRuX is for";
            foreach (object entry in SetupProfileCombo.Items)
                if (entry is ComboBoxItem item) item.Content = SetupProfilePolicy.Label(ProfileOfItem(item), it);

            SetupProfile current = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
            foreach (object entry in SetupProfileCombo.Items)
                if (entry is ComboBoxItem item && ProfileOfItem(item) == current) SetupProfileCombo.SelectedItem = item;
            UpdateSetupProfileHint(current);
        }

        private void UpdateSetupProfileHint(SetupProfile profile) =>
            SetupProfileHint.Text = SetupProfilePolicy.Description(profile, SetupProfileItalian)
                + " " + SetupProfilePolicy.SwitchNote(SetupProfileItalian)
                + (SetupProfileItalian
                    ? " I servizi in background si adeguano al prossimo avvio."
                    : " Background services adjust the next time PaRuX starts.");

        private void SetupProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SetupProfileCombo.SelectedItem is not ComboBoxItem item) return;
            SetupProfile profile = ProfileOfItem(item);
            if (SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) == profile) return;
            SettingsStore.Current.Data.SetupProfileId = SetupProfilePolicy.ToStorage(profile);
            SettingsStore.Current.Save();
            UpdateSetupProfileHint(profile);
            // The shell hides or shows the AI/Gaming switch and moves to the right workspace right away. Services that were
            // not started under the previous profile start on the next launch (nothing is torn down or reinstalled).
            App.MainWin?.ApplySetupProfile();
        }
    }
}
