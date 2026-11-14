using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using Windows.System;
using Windows.UI;

namespace PaRuX.Views;

/// <summary>
/// Controller-navigable on-screen keyboard for Console/TV mode (own implementation: no TabTip/osk.exe, no driver,
/// no injected input, no worker, no telemetry). Plain WinUI buttons, so D-pad / left stick use XY focus navigation
/// (trapped inside the keyboard while it is open) and A activates a key. All typing rules live in
/// <see cref="OnScreenKeyboardModel"/>. Edits are written live to the target field; Cancel restores the text the
/// field had when the keyboard opened. The caller decides when to open it (see MainWindow.OnScreenKeyboard.cs).
/// </summary>
internal sealed class OnScreenKeyboardOverlay : Grid
{
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _preview = new() { FontSize = 26, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 40 };
    private readonly StackPanel _keyRows = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _panelContent = new() { Spacing = 8, Width = 1040 };
    private readonly ToggleButton _shift = new();
    private readonly ToggleButton _caps = new();
    private readonly Button _layer = new();
    private readonly List<(Button Button, int Row, int Column)> _letterButtons = new();
    private readonly Border _panel;
    private OnScreenKeyboardModel? _model;
    private Control? _target;
    private OnScreenKeyboardLayer? _renderedLayer;
    private bool _renderedAccent;

    public OnScreenKeyboardOverlay()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
        AutomationProperties.SetAutomationId(this, "OnScreenKeyboard_Root");
        AutomationProperties.SetName(this, T("On-screen keyboard", "Tastiera su schermo"));
        SetValue(Canvas.ZIndexProperty, 1000);
        TabFocusNavigation = KeyboardNavigationMode.Cycle;

        // Theme tokens via XAML so ThemeResource follows Light/Dark/High contrast.
        _panel = (Border)XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='16' CornerRadius='12' BorderThickness='1' " +
            "Background='{ThemeResource PaRuXSurfaceSolidBrush}' BorderBrush='{ThemeResource PaRuXStrokeBrush}'/>");

        AutomationProperties.SetAutomationId(_preview, "OnScreenKeyboard_Preview");
        _panelContent.Children.Add(_preview);
        _panelContent.Children.Add(_keyRows);
        BuildActionRows();
        _panel.Child = _panelContent;
        _viewbox.Child = _panel;
        Children.Add(_viewbox);

        PreviewKeyDown += OnPreviewKeyDown;
        // Clicking the dimmed area outside the keyboard finishes typing (pointer users).
        PointerPressed += (_, e) =>
        {
            if (e.OriginalSource == this) { Close(commit: true); e.Handled = true; }
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;
    public event EventHandler? Closed;

    private static string T(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

    /// <summary>Opens the keyboard on a TextBox, PasswordBox or AutoSuggestBox. Returns false for any other control.</summary>
    public bool Open(Control target, double availableHeight)
    {
        if (IsOpen || !TryRead(target, out string text, out int caret, out bool password)) return false;
        _target = target;
        bool accent = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        int max = target is TextBox { MaxLength: > 0 } tb ? tb.MaxLength : OnScreenKeyboardModel.DefaultMaxLength;
        _model = new OnScreenKeyboardModel(text, caret, password, max, accent);
        _renderedLayer = null;
        string label = AutomationProperties.GetName(target);
        AutomationProperties.SetName(_preview, string.IsNullOrWhiteSpace(label) ? T("Text preview", "Anteprima testo") : label);
        _viewbox.MaxHeight = Math.Max(240, availableHeight * 0.96);
        Visibility = Visibility.Visible;
        Render();
        UpdateLayout();
        FocusFirstKey();
        return true;
    }

    public void Close(bool commit)
    {
        if (!IsOpen || _model is null || _target is null) return;
        var target = _target;
        var model = _model;
        if (!commit) Write(target, model.InitialText, model.InitialCaret);
        else Write(target, model.Text, model.Caret);
        Visibility = Visibility.Collapsed;
        _model = null;
        _target = null;
        // Same field gets focus back. Programmatic focus never re-triggers the gamepad auto-open rule.
        target.Focus(FocusState.Programmatic);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    internal static bool IsSupportedField(DependencyObject? element, out Control? field)
    {
        field = null;
        for (DependencyObject? d = element; d != null; d = VisualTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case AutoSuggestBox asb: field = asb; return asb.IsEnabled;
                case PasswordBox pb: field = pb; return pb.IsEnabled;
                case TextBox t: field = t; return t.IsEnabled && !t.IsReadOnly;
            }
        }
        return false;
    }

    private static bool TryRead(Control target, out string text, out int caret, out bool password)
    {
        text = string.Empty; caret = 0; password = false;
        switch (target)
        {
            case TextBox t: text = t.Text ?? string.Empty; caret = t.SelectionStart; return true;
            case PasswordBox p: text = p.Password ?? string.Empty; caret = text.Length; password = true; return true;
            case AutoSuggestBox a: text = a.Text ?? string.Empty; caret = text.Length; return true;
            default: return false;
        }
    }

    private static void Write(Control target, string text, int caret)
    {
        switch (target)
        {
            case TextBox t: t.Text = text; t.SelectionStart = Math.Clamp(caret, 0, text.Length); break;
            case PasswordBox p: p.Password = text; break;
            case AutoSuggestBox a: a.Text = text; break;
        }
    }

    private void Sync()
    {
        if (_model is null || _target is null) return;
        Write(_target, _model.Text, _model.Caret);
        Render();
    }

    private void BuildActionRows()
    {
        // Row A: modifiers and cursor. Row B: cancel / space / done.
        var rowA = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        ConfigureKey(_shift, "⇧ " + T("Shift", "Maiusc"), T("Shift", "Maiusc"), "OnScreenKeyboard_Shift", 150);
        _shift.Click += (_, _) => { _model?.ToggleShift(); Render(); };
        ConfigureKey(_caps, "⇪ " + T("Caps", "Blocco"), T("Caps Lock", "Blocco maiuscole"), "OnScreenKeyboard_Caps", 150);
        _caps.Click += (_, _) => { _model?.ToggleCaps(); Render(); };
        ConfigureKey(_layer, "123 / ABC", T("Switch symbols and letters", "Cambia simboli e lettere"), "OnScreenKeyboard_Layer", 150);
        _layer.Click += (_, _) => { _model?.ToggleLayer(); Render(); _layer.Focus(FocusState.Keyboard); };
        rowA.Children.Add(_shift);
        rowA.Children.Add(_caps);
        rowA.Children.Add(_layer);
        rowA.Children.Add(ActionKey("◀", T("Cursor left", "Cursore a sinistra"), "OnScreenKeyboard_CaretLeft", 100, () => _model?.MoveCaret(-1)));
        rowA.Children.Add(ActionKey("▶", T("Cursor right", "Cursore a destra"), "OnScreenKeyboard_CaretRight", 100, () => _model?.MoveCaret(1)));
        rowA.Children.Add(ActionKey("⌫", T("Backspace", "Cancella"), "OnScreenKeyboard_Backspace", 150, () => _model?.Backspace()));

        var rowB = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var cancel = new Button();
        ConfigureKey(cancel, T("Cancel", "Annulla"), T("Cancel", "Annulla"), "OnScreenKeyboard_Cancel", 200);
        cancel.Click += (_, _) => Close(commit: false);
        rowB.Children.Add(cancel);
        rowB.Children.Add(ActionKey(T("Space", "Spazio"), T("Space", "Spazio"), "OnScreenKeyboard_Space", 480, () => _model?.Space()));
        var done = new Button();
        ConfigureKey(done, "↵ " + T("Done", "Fine"), T("Done", "Fine"), "OnScreenKeyboard_Done", 200);
        done.Click += (_, _) => Close(commit: true);
        rowB.Children.Add(done);

        _panelContent.Children.Add(rowA);
        _panelContent.Children.Add(rowB);
    }

    private Button ActionKey(string content, string name, string id, double width, System.Action edit)
    {
        var b = new Button();
        ConfigureKey(b, content, name, id, width);
        b.Click += (_, _) => { edit(); Sync(); };
        return b;
    }

    private static void ConfigureKey(ContentControl key, string content, string name, string id, double width)
    {
        key.Content = content;
        key.Width = width;
        key.Height = 60;
        key.FontSize = 22;
        key.HorizontalContentAlignment = HorizontalAlignment.Center;
        key.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(key, name);
        AutomationProperties.SetAutomationId(key, id);
    }

    private void Render()
    {
        if (_model is null) return;
        _preview.Text = _model.PreviewWithCaret;
        _shift.IsChecked = _model.ShiftActive;
        _caps.IsChecked = _model.CapsLock;
        _layer.Content = _model.Layer == OnScreenKeyboardLayer.Letters ? "123" : "ABC";

        var rows = _model.GetRows();
        if (_renderedLayer != _model.Layer || _renderedAccent != _model.ShowAccentRow)
        {
            _keyRows.Children.Clear();
            _letterButtons.Clear();
            for (int r = 0; r < rows.Count; r++)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var key = rows[r][c];
                    var button = new Button();
                    ConfigureKey(button, key.Label, key.Label, "OnScreenKeyboard_Key_" + key.Id, 92);
                    int row = r, col = c;
                    button.Click += (_, _) =>
                    {
                        if (_model is null) return;
                        _model.Type(_model.GetRows()[row][col].Output);
                        Sync();
                    };
                    _letterButtons.Add((button, r, c));
                    line.Children.Add(button);
                }
                _keyRows.Children.Add(line);
            }
            _renderedLayer = _model.Layer;
            _renderedAccent = _model.ShowAccentRow;
        }
        else
        {
            foreach (var (button, r, c) in _letterButtons)
            {
                var key = rows[r][c];
                button.Content = key.Label;
                AutomationProperties.SetName(button, key.Label);
            }
        }
    }

    private void FocusFirstKey()
    {
        if (_letterButtons.Count > 0) _letterButtons[0].Button.Focus(FocusState.Keyboard);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsOpen || _model is null) return;
        int vk = (int)e.Key;

        FocusNavigationDirection? dir = vk switch
        {
            203 or 211 or (int)VirtualKey.Up => FocusNavigationDirection.Up,
            204 or 212 or (int)VirtualKey.Down => FocusNavigationDirection.Down,
            205 or 214 or (int)VirtualKey.Left => FocusNavigationDirection.Left,
            206 or 213 or (int)VirtualKey.Right => FocusNavigationDirection.Right,
            _ => null
        };
        if (dir.HasValue)
        {
            // Focus never leaves the keyboard while it is open.
            var options = new FindNextElementOptions { SearchRoot = _panel };
            if (FocusManager.FindNextElement(dir.Value, options) is Control next) next.Focus(FocusState.Keyboard);
            e.Handled = true;
            return;
        }

        var action = OnScreenKeyboardModel.ResolvePad(vk);
        switch (action)
        {
            case OnScreenKeyboardPadAction.Cancel: Close(commit: false); e.Handled = true; return;
            case OnScreenKeyboardPadAction.Done: Close(commit: true); e.Handled = true; return;
            case OnScreenKeyboardPadAction.None: break;
            default: _model.Apply(action); Sync(); e.Handled = true; return;
        }

        // A / Enter / Space activate the focused key natively. Every other gamepad key is swallowed so the
        // Console shell (paging, search, exit) cannot act behind the keyboard.
        if (OnScreenKeyboardModel.IsGamepadKey(vk) && vk != 195) e.Handled = true;
    }
}
