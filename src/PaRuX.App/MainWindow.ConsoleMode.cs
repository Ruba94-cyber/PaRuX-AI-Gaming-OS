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
        GameLibraryPage.OnShown();
        UpdateConsoleModeButton();
        GameLibraryNavItem.Focus(FocusState.Programmatic);
    }

    private void ExitConsoleMode()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        _consoleModeActive = false;
        UpdateConsoleModeButton();
        ConsoleModeButton.Focus(FocusState.Programmatic);
    }

    private void RootGrid_ConsoleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_consoleModeActive) return;
        if (e.Key is VirtualKey.Escape or VirtualKey.GamepadB)
        {
            ExitConsoleMode();
            e.Handled = true;
        }
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
