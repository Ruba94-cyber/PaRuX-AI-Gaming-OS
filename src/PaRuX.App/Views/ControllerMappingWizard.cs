using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>One thing to map: a Windows-visible label, the box that receives the index, and whether it is a button or an axis.</summary>
    internal sealed record MappingStep(string English, string Italian, NumberBox Target, bool IsButton);

    /// <summary>
    /// Guided mapping in the style of XOutput and Steam's auto-configure: "Press A on your controller", then B, and so on. The index of
    /// the control the owner touches is written into the mapping box for that step; nothing is saved until the owner presses Save
    /// mapping. It only reads the pad (no virtual pad, nothing sent to games) and stops when finished, cancelled or unloaded.
    /// </summary>
    internal sealed class ControllerMappingWizard : StackPanel
    {
        private readonly IReadOnlyList<MappingStep> _steps;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly TextBlock _prompt = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _progress = new() { Opacity = 0.7 };
        private readonly Button _skip;
        private readonly Button _cancel;
        private WindowsRawGamepadSource? _source;
        private int _index;
        private bool[]? _baselineButtons;
        private double[]? _baselineAxes;
        private int _stableTicks;
        private int? _candidate;
        private DateTime _pauseUntil;

        public ControllerMappingWizard(IReadOnlyList<MappingStep> steps)
        {
            _steps = steps;
            Spacing = 8;
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(this, "GameDevices_Wizard_Panel");
            AutomationProperties.SetAutomationId(_prompt, "GameDevices_Wizard_Prompt_Text");
            _skip = new Button { Content = T("Skip this one", "Salta questo") };
            _cancel = new Button { Content = T("Stop", "Interrompi") };
            AutomationProperties.SetAutomationId(_skip, "GameDevices_Wizard_Skip_Button");
            AutomationProperties.SetAutomationId(_cancel, "GameDevices_Wizard_Cancel_Button");
            _skip.Click += (_, _) => Advance(null);
            _cancel.Click += (_, _) => Finish(T("Stopped. Nothing is saved until you press Save mapping.", "Interrotto. Nulla viene salvato finché non premi Salva mappatura."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_skip);
            row.Children.Add(_cancel);
            Children.Add(_progress);
            Children.Add(_prompt);
            Children.Add(row);
            _timer.Tick += (_, _) => Poll();
            Unloaded += (_, _) => Stop();
        }

        private static string T(string en, string it) =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        /// <summary>Raised with a closing message when the wizard ends (finished, stopped or the pad vanished).</summary>
        public event Action<string>? Ended;

        public bool Begin(string? deviceId)
        {
            Stop();
            _source = string.IsNullOrWhiteSpace(deviceId) ? null : WindowsRawGamepadSource.TryOpen(deviceId);
            if (_source is null) return false;
            _index = 0;
            Visibility = Visibility.Visible;
            ResetStep();
            _timer.Start();
            return true;
        }

        public void Stop()
        {
            _timer.Stop();
            _source = null;
            Visibility = Visibility.Collapsed;
        }

        private void Finish(string message)
        {
            Stop();
            Ended?.Invoke(message);
        }

        private void ResetStep()
        {
            MappingStep step = _steps[_index];
            _progress.Text = T("Step ", "Passo ") + (_index + 1) + " / " + _steps.Count;
            _prompt.Text = step.IsButton
                ? T("Press and release ", "Premi e rilascia ") + T(step.English, step.Italian) + T(" on your controller", " sul tuo controller")
                : T("Move ", "Muovi ") + T(step.English, step.Italian) + T(" all the way, then let it go", " fino in fondo, poi lascialo");
            _stableTicks = 0;
            _candidate = null;
            _pauseUntil = DateTime.UtcNow.AddMilliseconds(600);   // let the previous press settle before listening again
            _baselineButtons = null;
            _baselineAxes = null;
        }

        private void Advance(int? assigned)
        {
            if (assigned is int value) _steps[_index].Target.Value = value;
            _index++;
            if (_index >= _steps.Count)
            {
                Finish(T("All steps done. Check the numbers, then press Save mapping.", "Tutti i passi completati. Controlla i numeri, poi premi Salva mappatura."));
                return;
            }
            ResetStep();
        }

        private void Poll()
        {
            if (_source is null || !_source.TryRead(out var state) || state is null)
            {
                Finish(T("The controller was disconnected.", "Il controller è stato scollegato."));
                return;
            }
            bool[] buttons = state.Buttons.ToArray();
            double[] axes = state.Axes.ToArray();
            if (_baselineButtons is null || DateTime.UtcNow < _pauseUntil)
            {
                _baselineButtons = buttons;
                _baselineAxes = axes;
                return;
            }
            int? hit = ControllerLiveModel.DetectIndex(_baselineButtons, _baselineAxes, buttons, axes, _steps[_index].IsButton);
            if (hit is null) { _stableTicks = 0; _candidate = null; return; }
            // The same control for three readings in a row (about 120 ms) is a deliberate press, not contact noise.
            if (_candidate == hit) _stableTicks++; else { _candidate = hit; _stableTicks = 1; }
            if (_stableTicks >= 3) Advance(hit);
        }
    }
}
