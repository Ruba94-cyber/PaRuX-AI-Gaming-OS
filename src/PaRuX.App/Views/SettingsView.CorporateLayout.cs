using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;

namespace PaRuX.Views
{
    /// <summary>Corporate settings presents the actually supported CPU Chat controls,
    /// not the standard product's CUDA/Vulkan, cloud, media and benchmark settings.
    /// Existing XAML controls remain intact for the standard editions.</summary>
    public sealed partial class SettingsView
    {
        private readonly HashSet<FrameworkElement> _corporateHiddenSettingsSections = new();

        private void InitializeCorporateSettingsLayout()
        {
            if (!CorporateMode.IsActive || SettingsContentPanel == null) return;

            foreach (UIElement child in SettingsContentPanel.Children)
            {
                if (child is FrameworkElement section && section.Tag is string tag
                    && tag is "Models" or "Privacy & Network")
                    _corporateHiddenSettingsSections.Add(section);
            }

            bool it = SetupProfileItalian;
            var panel = new StackPanel { Tag = "Models", Spacing = 14, MaxWidth = 720 };
            AutomationProperties.SetAutomationId(panel, "Settings_CorporateModels_Panel");
            var heading = new TextBlock
            {
                Text = it ? "Modelli Chat" : "Chat models",
                FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            panel.Children.Add(heading);
            panel.Children.Add(new TextBlock
            {
                Text = it
                    ? "Il motore CPU è già pronto. Da qui puoi importare modelli GGUF, scegliere quale usare per Chat e controllare i download autorizzati. Non serve installare altri runtime."
                    : "The CPU engine is already ready. Import GGUF models, choose the Chat model and review approved downloads. No additional runtime installation is needed.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = CorporateMode.ModelDownloadsAllowed
                    ? (it ? "Download modelli disponibili per questa sessione (solo dopo autorizzazione IT)."
                          : "Model downloads enabled for this session (IT approval required).")
                    : (it ? "Offline: puoi comunque importare e utilizzare modelli già presenti sul PC."
                          : "Offline: you can still import and use models already on this PC."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            });
            var manage = new Button
            {
                Content = it ? "Apri libreria modelli" : "Open model library",
                MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(manage, "Settings_Corporate_OpenModelHub_Button");
            manage.Click += (_, _) => App.MainWin?.ShowModelHub();
            panel.Children.Add(manage);
            SettingsContentPanel.Children.Add(panel);

            var privacy = new StackPanel { Tag = "Privacy & Network", Spacing = 12, MaxWidth = 720 };
            AutomationProperties.SetAutomationId(privacy, "Settings_CorporatePrivacy_Panel");
            privacy.Children.Add(new TextBlock
            {
                Text = it ? "Privacy aziendale" : "Corporate privacy",
                FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            privacy.Children.Add(new TextBlock
            {
                Text = it
                    ? "Le risposte e l'analisi dei documenti vengono eseguite in locale. L'accesso al Web e ai servizi cloud resta disattivato; non vengono installati nuovi runtime dal PC aziendale."
                    : "Chat answers and document analysis run locally. General web and cloud services stay disabled; the company PC never installs a new runtime online.",
                TextWrapping = TextWrapping.Wrap
            });
            privacy.Children.Add(new TextBlock
            {
                Text = CorporateMode.ModelDownloadsAllowed
                    ? (it ? "Questa sessione consente soltanto il download di modelli GGUF da fonti approvate, nel rispetto delle regole IT."
                          : "This session only allows approved GGUF model downloads under IT network policy.")
                    : (it ? "Questa sessione è offline. Puoi usare e importare modelli senza rete."
                          : "This session is offline. You can use and import models without a network."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            });
            SettingsContentPanel.Children.Add(privacy);
        }
    }
}
