using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Views;

namespace PaRuX;

/// <summary>
/// Hosts the Console/TV on-screen keyboard. It opens only while Console mode is active, only when a text field
/// received focus from a gamepad key (never from the pointer or a physical keyboard), and on A/Y pressed on a
/// focused field. Desktop mode is untouched. Event-driven: no polling, no process, no telemetry.
/// </summary>
public sealed partial class MainWindow
{
    private OnScreenKeyboardOverlay? _keyboardOverlay;
    private bool _lastInputWasGamepad;

    private void InitializeOnScreenKeyboard()
    {
        if (_keyboardOverlay != null) return;
        _keyboardOverlay = new OnScreenKeyboardOverlay();
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_keyboardOverlay, 2);
        RootGrid.Children.Add(_keyboardOverlay);
        // Classify the last input before focus changes: PreviewKeyDown precedes the focus move it causes.
        RootGrid.PreviewKeyDown += (_, e) =>
        {
            if (_keyboardOverlay.IsOpen) return;
            _lastInputWasGamepad = OnScreenKeyboardModel.IsGamepadKey((int)e.Key);
        };
        RootGrid.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _lastInputWasGamepad = false), true);
        RootGrid.GotFocus += OnRootGotFocus;
        RootGrid.KeyDown += OnRootKeyDownForKeyboard;
    }

    private void OnRootGotFocus(object sender, RoutedEventArgs e)
    {
        if (_keyboardOverlay is null || e.OriginalSource is not DependencyObject source) return;
        if (!OnScreenKeyboardOverlay.IsSupportedField(source, out Control? field) || field is null) return;
        if (field.FocusState != FocusState.Keyboard) return;
        if (!OnScreenKeyboardModel.ShouldAutoOpen(_consoleModeActive, _lastInputWasGamepad, true, _keyboardOverlay.IsOpen))
            return;
        _keyboardOverlay.Open(field, RootGrid.ActualHeight);
    }

    private void OnRootKeyDownForKeyboard(object sender, KeyRoutedEventArgs e)
    {
        // A or Y on an already focused field (for example after cancelling) reopens the keyboard.
        // F4 does the same from a physical keyboard (keyboard-only testing); it is a shortcut, not typed text.
        if (!_consoleModeActive || _keyboardOverlay is null || _keyboardOverlay.IsOpen || e.Handled) return;
        if (e.Key is not (Windows.System.VirtualKey.GamepadA or Windows.System.VirtualKey.GamepadY or Windows.System.VirtualKey.F4)) return;
        if (e.OriginalSource is DependencyObject source
            && OnScreenKeyboardOverlay.IsSupportedField(source, out Control? field) && field is not null
            && _keyboardOverlay.Open(field, RootGrid.ActualHeight))
            e.Handled = true;
    }
}
