using System;
using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;

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
        // Event-driven Windows Gamepad UI Navigation: no XOutput/ViGEm, driver,
        // virtual device, background polling or per-frame input worker.
        ConsoleNavigationAction action = ConsoleNavigationPolicy.Resolve(
            (int)e.Key, GameLibraryPage.IsInteractionBusy);

        if (action == ConsoleNavigationAction.Exit)
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

        // The existing native WinUI control handles Accept (A/Enter) and text
        // input. Don't synthesize clicks: that would risk duplicate launches.
        FocusNavigationDirection? direction = action switch
        {
            ConsoleNavigationAction.Up => FocusNavigationDirection.Up,
            ConsoleNavigationAction.Down => FocusNavigationDirection.Down,
            ConsoleNavigationAction.Left => FocusNavigationDirection.Left,
            ConsoleNavigationAction.Right => FocusNavigationDirection.Right,
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
            ? "Usa la stessa libreria giochi a schermo intero. Usa D-pad o levetta sinistra per navigare, A per selezionare ed Esc o B per uscire."
            : "Use the same game library in fullscreen. Use D-pad or left stick to navigate, A to select and Escape or B to exit.");
    }
}
