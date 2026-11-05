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
            // The role-profile selector is built once; Advanced follows Studio mode.
            RoleProfileHost.Content ??= new RoleProfilePanel(startAdvanced: SettingsStore.Current.Data.StudioModeEnabled);
        }

        private void UpdateSetupProfileHint(SetupProfile profile) =>
            SetupProfileHint.Text = SetupProfilePolicy.Description(profile, SetupProfileItalian)
                + " " + SetupProfilePolicy.SwitchNote(SetupProfileItalian)
                + (SetupProfileItalian
                    ? " Il cambio vale subito, senza riavviare."
                    : " The change applies right away, no restart needed.");

        private async void SetupProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SetupProfileCombo.SelectedItem is not ComboBoxItem item) return;
            SetupProfile profile = ProfileOfItem(item);
            if (SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) == profile) return;

            // Real switch, no restart: the planner starts/stops only the services that differ (stops first, rolled back as a whole
            // on failure) and the profile is saved only after every step worked. Nothing is deleted or installed.
            SetupProfileCombo.IsEnabled = false;
            SetupProfileHint.Text = SetupProfileItalian ? "Applico il cambio…" : "Applying the change…";
            SetupTransitionResult result;
            try { result = await SetupProfileRuntime.SwitchAsync(profile); }
            catch (Exception ex)
            {
                Log.Write("Setup profile switch threw: " + ex.GetType().Name);
                result = new SetupTransitionResult(false, SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId),
                    Array.Empty<SetupTransitionStep>(), Array.Empty<SetupTransitionStep>(), null, false, ex.GetType().Name);
            }
            finally { SetupProfileCombo.IsEnabled = true; }

            SetupProfile effective = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
            if (effective != profile)
            {
                // Corporate Lite keeps AI only, or the switch failed and was rolled back: show the profile that is really active.
                _loading = true;
                try
                {
                    foreach (object entry in SetupProfileCombo.Items)
                        if (entry is ComboBoxItem candidate && ProfileOfItem(candidate) == effective) SetupProfileCombo.SelectedItem = candidate;
                }
                finally { _loading = false; }
            }
            UpdateSetupProfileHint(effective);
            if (!result.Success) SetupProfileHint.Text = result.Message + " " + SetupProfileHint.Text;
            App.MainWin?.ApplySetupProfile();
        }
    }
}
