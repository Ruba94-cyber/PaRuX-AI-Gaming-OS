using System;
using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PaRuX;

/// <summary>
/// Console/TV is an opt-in presenter mode of the EXISTING WinUI MainWindow.
/// No second executable, runtime, settings store, virtual display, driver or
/// background controller service is created. UI navigation stays shared.
/// </summary>
public sealed partial class MainWindow
{
    private bool _consoleModeActive;

    private void ConsoleModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_consoleModeActive) ExitConsoleMode();
            else EnterConsoleMode();
        }
        catch (Exception ex)
        {
            // Keep the presenter toggle recoverable if fullscreen is unavailable.
            Log.Write("Console window presenter change failed: " + ex.GetType().Name);
            _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            UpdateConsoleModeButton();
        }
    }

    private void EnterConsoleMode()
    {
        // No display-mode or resolution changes: let Windows own the monitor/HDR/VRR.
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _consoleModeActive = true;
        NavView.IsPaneOpen = true;
        ShowStudioWorkspace(StudioWorkspace.Games);
        GameLibraryPage.ApplyConsoleMode(true);
        GameLibraryPage.OnShown();
        UpdateConsoleModeButton();
        GameLibraryPage.FocusConsoleHome();
    }

    private void ExitConsoleMode()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        _consoleModeActive = false;
        GameLibraryPage.ApplyConsoleMode(false);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        UpdateConsoleModeButton();
        ConsoleModeButton.Focus(FocusState.Programmatic);
    }

    private void RootGrid_ConsoleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_consoleModeActive) return;
        // A modal consent or a running library action owns B/Escape until it
        // completes; never dismiss fullscreen underneath a game-launch prompt.
        if (GameLibraryPage.IsInteractionBusy && e.Key is VirtualKey.Escape or VirtualKey.GamepadB)
            return;

        if (e.Key is VirtualKey.Escape or VirtualKey.GamepadB)
        {
            try
            {
                ExitConsoleMode();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Write("Console exit input failed: " + ex.GetType().Name);
                _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
                UpdateConsoleModeButton();
            }
            return;
        }

        // Keyboard/gamepad focus navigation only inside Console Home. No background
        // controller polling, key injection, virtual XInput device or game-time worker.
        FocusNavigationDirection? direction = e.Key switch
        {
            VirtualKey.GamepadDPadUp => FocusNavigationDirection.Up,
            VirtualKey.GamepadDPadDown => FocusNavigationDirection.Down,
            VirtualKey.GamepadDPadLeft => FocusNavigationDirection.Left,
            VirtualKey.GamepadDPadRight => FocusNavigationDirection.Right,
            _ => null
        };
        if (direction.HasValue && FocusManager.TryMoveFocus(direction.Value))
            e.Handled = true;
    }

    private void UpdateConsoleModeButton()
    {
        bool italian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        ConsoleModeButton.Content = _consoleModeActive
            ? italian ? "Torna al desktop" : "Return to desktop"
            : italian ? "Console / TV" : "Console / TV";
        AutomationProperties.SetName(ConsoleModeButton, _consoleModeActive
            ? italian ? "Esci dalla modalità Console" : "Exit Console mode"
            : italian ? "Apri la modalità Console TV" : "Enter Console TV mode");
        AutomationProperties.SetHelpText(ConsoleModeButton, italian
            ? "Usa la stessa libreria giochi a schermo intero. Premi Esc o B sul controller per tornare."
            : "Use the same game library in fullscreen. Press Escape or gamepad B to return.");
    }
}
