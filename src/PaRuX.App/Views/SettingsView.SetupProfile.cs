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

        // The profile the item NAMES, not the one in force: in Corporate Lite Parse() maps everything to AI only, which made all
        // three items look the same (the last one, "Gaming only", showed as selected and the other two did nothing).
        private static SetupProfile ProfileOfItem(ComboBoxItem item) => SetupProfilePolicy.ParseChoice(item.Tag as string);

        private void LoadSetupProfile()
        {
            bool it = SetupProfileItalian;
            SetupProfileTitle.Text = PaRuX.Contracts.CorporateMode.IsActive
                ? (it ? "Corporate Lite · Chat AI e documenti" : "Corporate Lite · AI Chat and documents")
                : (it ? "A cosa serve PaRuX" : "What PaRuX is for");
            // The Corporate package is intrinsically AI-only. Showing an editable
            // Both/Gaming picker that silently snaps back is misleading UX.
            SetupProfileCombo.Visibility = PaRuX.Contracts.CorporateMode.IsActive
                ? Visibility.Collapsed : Visibility.Visible;
            foreach (object entry in SetupProfileCombo.Items)
                if (entry is ComboBoxItem item) item.Content = SetupProfilePolicy.Label(ProfileOfItem(item), it);

            SetupProfile current = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
            foreach (object entry in SetupProfileCombo.Items)
                if (entry is ComboBoxItem item && ProfileOfItem(item) == current) SetupProfileCombo.SelectedItem = item;
            UpdateSetupProfileHint(current);
            // The role-profile selector is built once; Advanced follows Studio mode.
            RoleProfileHost.Content ??= new RoleProfilePanel(startAdvanced: SettingsStore.Current.Data.StudioModeEnabled);
        }

        private void UpdateSetupProfileHint(SetupProfile profile)
        {
            RoleProfileHost.Visibility = SetupProfilePolicy.Plan(profile).ShowAiWorkspaces
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateSetupProfileHintText(profile);
        }

        private void UpdateSetupProfileHintText(SetupProfile profile) =>
            SetupProfileHint.Text = PaRuX.Contracts.CorporateMode.IsActive
                ? (SetupProfileItalian
                    ? "Solo Chat AI e ricerca documenti. Il motore CPU è già incluso. Aggiungi o cambia modello da Chat o Model Hub, senza modificare la modalità di installazione."
                    : "AI Chat and document search only. The CPU engine is already included. Add or switch models from Chat or Model Hub without changing the installation mode.")
                : SetupProfilePolicy.Description(profile, SetupProfileItalian)
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
            RefreshSettingsCategoriesForProfile();
            if (!result.Success) SetupProfileHint.Text = result.Message + " " + SetupProfileHint.Text;
            else if (effective != profile)
                SetupProfileHint.Text = SetupProfilePolicy.CorporateKeptNote(SetupProfileItalian) + " " + SetupProfileHint.Text;   // no silent snap-back
            App.MainWin?.ApplySetupProfile();
        }
    }
}
