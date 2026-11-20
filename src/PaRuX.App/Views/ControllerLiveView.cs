using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Read-only live picture of one physical controller: two stick circles, every button as a chip that lights while pressed, the
    /// axes as bars, the hat, and a caption naming the control that just moved (so its index can be typed into the mapping boxes).
    /// It only reads the device; nothing is sent to games and no virtual pad is created. Reading stops when <see cref="Stop"/> is
    /// called or the view leaves the visual tree.
    /// </summary>
    internal sealed class ControllerLiveView : StackPanel
    {
        private const double StickSize = 96;
        private static readonly Windows.UI.Color Idle = Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80);
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly Ellipse _leftDot = Dot(), _rightDot = Dot();
        private readonly WrapRowPanel _buttonsPanel = new();
        private readonly List<Border> _chips = new();
        private readonly StackPanel _axisBars = new() { Spacing = 2 };
        private readonly List<ProgressBar> _bars = new();
        private readonly TextBlock _changed = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _hat = new() { Opacity = 0.7 };
        private WindowsRawGamepadSource? _source;
        private bool[]? _previousButtons;
        private double[]? _previousAxes;

        public ControllerLiveView()
        {
            Spacing = 8;
            AutomationProperties.SetAutomationId(this, "GameDevices_RawLive_View");
            var sticks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            sticks.Children.Add(StickPad(_leftDot, "Left stick", "Levetta sinistra"));
            sticks.Children.Add(StickPad(_rightDot, "Right stick", "Levetta destra"));
            Children.Add(sticks);
            Children.Add(_buttonsPanel);
            Children.Add(_axisBars);
            Children.Add(_hat);
            AutomationProperties.SetAutomationId(_changed, "GameDevices_RawLive_Changed_Text");
            Children.Add(_changed);
            _timer.Tick += (_, _) => Poll();
            Unloaded += (_, _) => Stop();
        }

        private static string T(string en, string it) =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private static Ellipse Dot() => new() { Width = 14, Height = 14, Fill = new SolidColorBrush(Colors.OrangeRed) };

        private static FrameworkElement StickPad(Ellipse dot, string en, string it)
        {
            var canvas = new Canvas { Width = StickSize, Height = StickSize };
            canvas.Children.Add(new Ellipse { Width = StickSize, Height = StickSize, Stroke = new SolidColorBrush(Colors.Gray), StrokeThickness = 1.5 });
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, (StickSize - 14) / 2);
            Canvas.SetTop(dot, (StickSize - 14) / 2);
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(canvas);
            panel.Children.Add(new TextBlock { Text = T(en, it), FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
            return panel;
        }

        public bool Start(string? deviceId)
        {
            Stop();
            _source = string.IsNullOrWhiteSpace(deviceId) ? null : WindowsRawGamepadSource.TryOpen(deviceId);
            if (_source is null)
            {
                _changed.Text = T("The controller is not available.", "Il controller non è disponibile.");
                return false;
            }
            _previousButtons = null;
            _previousAxes = null;
            _changed.Text = T("Press a button or move a stick: the control you touch is named here.",
                              "Premi un pulsante o muovi una levetta: qui compare il controllo che tocchi.");
            _timer.Start();
            return true;
        }

        public void Stop()
        {
            _timer.Stop();
            _source = null;
        }

        private void Poll()
        {
            if (_source is null || !_source.TryRead(out var state) || state is null)
            {
                Stop();
                _changed.Text = T("The controller was disconnected.", "Il controller è stato scollegato.");
                return;
            }
            bool[] buttons = state.Buttons.ToArray();
            double[] axes = state.Axes.ToArray();
            EnsureControls(buttons.Length, axes.Length);
            for (int i = 0; i < buttons.Length && i < _chips.Count; i++)
                _chips[i].Background = new SolidColorBrush(buttons[i] ? Colors.OrangeRed : Idle);
            Move(_leftDot, ControllerLiveModel.StickPoint(axes, 0));
            Move(_rightDot, ControllerLiveModel.StickPoint(axes, 2));
            for (int i = 0; i < axes.Length && i < _bars.Count; i++) _bars[i].Value = (axes[i] + 1) * 50;
            _hat.Text = T("Hat / D-pad: ", "Croce direzionale: ")
                + (state.HatDirection < 0 ? T("centered", "al centro") : state.HatDirection.ToString(CultureInfo.InvariantCulture));
            string? changed = ControllerLiveModel.DescribeChange(_previousButtons, _previousAxes, buttons, axes);
            if (changed is not null) _changed.Text = T("Last control touched: ", "Ultimo controllo toccato: ") + changed;
            _previousButtons = buttons;
            _previousAxes = axes;
        }

        private static void Move(Ellipse dot, (double X, double Y) p)
        {
            double travel = (StickSize - 14) / 2;
            Canvas.SetLeft(dot, travel + p.X * travel);
            Canvas.SetTop(dot, travel + p.Y * travel);
        }

        private void EnsureControls(int buttonCount, int axisCount)
        {
            while (_chips.Count < Math.Min(buttonCount, 32))
            {
                var chip = new Border
                {
                    Width = 30, Height = 24, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 4),
                    Background = new SolidColorBrush(Idle),
                    Child = new TextBlock { Text = _chips.Count.ToString(CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                _chips.Add(chip);
                _buttonsPanel.Children.Add(chip);
            }
            while (_bars.Count < Math.Min(axisCount, 12))
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 220, Height = 6 };
                int index = _bars.Count;
                AutomationProperties.SetName(bar, T("Axis ", "Asse ") + index);
                _bars.Add(bar);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = T("Axis ", "Asse ") + index, FontSize = 11, Width = 52, Opacity = 0.7 });
                row.Children.Add(bar);
                _axisBars.Children.Add(row);
            }
        }
    }
}
