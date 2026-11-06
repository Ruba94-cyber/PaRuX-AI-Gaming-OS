using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        // Dynamic per-profile filtering within shared categories. For example,
        // "General" contains media notifications and "Privacy & Network" contains
        // Gaming and browser controls even when their entire workspace is disabled.
        private readonly HashSet<FrameworkElement> _profileHiddenSettingsSections = new();

        private bool IsSettingsSectionHidden(FrameworkElement element) =>
            _corporateHiddenSettingsSections.Contains(element)
            || _profileHiddenSettingsSections.Contains(element);

        private void ApplyProfileSettingsSections()
        {
            _profileHiddenSettingsSections.Clear();
            SetupProfile profile = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
            bool corporate = CorporateMode.IsActive;
            foreach (UIElement child in SettingsContentPanel.Children)
            {
                if (child is not StackPanel section || section.Tag is not string category) continue;
                string heading = section.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
                if (!SettingsSectionVisibilityPolicy.IsVisible(category, heading, profile, corporate))
                    _profileHiddenSettingsSections.Add(section);
            }
        }
    }
}
