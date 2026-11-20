using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Generation settings for chat (creativity, nucleus sampling, reply length, seed), the controls LM Studio, Jan and Open WebUI
    /// expose. Off by default: with "Choose my own values" off nothing is sent and PaRuX keeps picking sensible values itself.
    /// </summary>
    public sealed partial class ChatView
    {
        private void SamplingButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout { Content = BuildSamplingFlyoutContent() };
            flyout.ShowAt((FrameworkElement)sender);
        }

        private UIElement BuildSamplingFlyoutContent()
        {
            ChatSamplingValues saved = ChatSamplingState.Current;
            var panel = new StackPanel { Spacing = 8, MinWidth = 300, MaxWidth = 380 };

            var custom = new ToggleSwitch
            {
                Header = ChatT("Choose my own values", "Scelgo io i valori"),
                OnContent = ChatT("On", "Attivo"), OffContent = ChatT("Off (PaRuX decides)", "Spento (decide PaRuX)"),
                IsOn = !saved.IsDefault,
            };
            AutomationProperties.SetAutomationId(custom, "Chat_Generation_Custom_Toggle");
            var temperature = new Slider { Header = ChatT("Creativity (temperature)", "Creatività (temperatura)"), Minimum = 0, Maximum = 2, StepFrequency = 0.05, Value = saved.Temperature ?? 0.7 };
            var topP = new Slider { Header = "Top P", Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = saved.TopP ?? 1.0 };
            var maxTokens = new NumberBox
            {
                Header = ChatT("Longest reply (tokens, empty = automatic)", "Risposta massima (token, vuoto = automatico)"),
                Minimum = 1, Maximum = ChatSamplingRules.MaxTokensCeiling, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = saved.MaxTokens ?? double.NaN,
            };
            var seed = new NumberBox
            {
                Header = ChatT("Seed (empty = different every time)", "Seed (vuoto = diverso ogni volta)"),
                Minimum = 0, Maximum = int.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = saved.Seed ?? double.NaN,
            };
            AutomationProperties.SetAutomationId(temperature, "Chat_Generation_Temperature_Slider");
            AutomationProperties.SetAutomationId(topP, "Chat_Generation_TopP_Slider");
            AutomationProperties.SetAutomationId(maxTokens, "Chat_Generation_MaxTokens_Box");
            AutomationProperties.SetAutomationId(seed, "Chat_Generation_Seed_Box");

            var note = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
                Text = ChatT("Applies to the next message. Lower creativity gives steadier answers; a seed makes a reply repeatable on the same model.",
                             "Vale dal prossimo messaggio. Meno creatività dà risposte più stabili; un seed rende ripetibile una risposta con lo stesso modello."),
            };

            void Persist()
            {
                bool on = custom.IsOn;
                temperature.IsEnabled = topP.IsEnabled = maxTokens.IsEnabled = seed.IsEnabled = on;
                ChatSamplingState.Save(!on ? ChatSamplingValues.Default : new ChatSamplingValues(
                    temperature.Value, topP.Value,
                    double.IsNaN(maxTokens.Value) ? null : (int)maxTokens.Value,
                    double.IsNaN(seed.Value) ? null : (long)seed.Value));
            }
            custom.Toggled += (_, _) => Persist();
            temperature.ValueChanged += (_, _) => Persist();
            topP.ValueChanged += (_, _) => Persist();
            maxTokens.ValueChanged += (_, _) => Persist();
            seed.ValueChanged += (_, _) => Persist();
            temperature.IsEnabled = topP.IsEnabled = maxTokens.IsEnabled = seed.IsEnabled = custom.IsOn;

            foreach (UIElement element in new UIElement[] { custom, temperature, topP, maxTokens, seed, note })
                panel.Children.Add(element);
            return panel;
        }
    }
}
