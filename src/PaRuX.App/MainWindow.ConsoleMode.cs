using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Console/TV is an opt-in presenter mode of the EXISTING WinUI MainWindow.
/// No second executable, runtime, settings store, virtual display, driver or
/// background controller service is created. UI navigation stays shared.
/// </summary>
public sealed partial class MainWindow
{
    private bool _consoleModeActive;
    private bool _consoleRoutingInProgress;
    private RectInt32? _consolePreviousWindowBounds;
    private DateTime? _lastObservedVerifiedReturnUtc;
    private bool _handlingVerifiedReturn;

    /// <summary>Subscribe to the already-running LocalAI state watcher, not a
    /// second watcher or a timer. Ignore any event already on disk at startup.</summary>
    private void InitializeConsoleVerifiedReturn()
    {
        RememberLatestVerifiedReturn();
        LocalAiController.Current.VerifiedGameReturnChanged += OnVerifiedGameReturnChanged;
    }

    private void RememberLatestVerifiedReturn()
    {
        if (VerifiedGameReturnSignal.TryRead(
                VerifiedGameReturnSignal.FileIn(AppPaths.LocalAiStateDir), out var notice))
            _lastObservedVerifiedReturnUtc = notice?.ExitUtc;
    }

    private void OnVerifiedGameReturnChanged(object? sender, EventArgs args)
    {
        // The FileSystemWatcher callback is a worker thread, not a WinUI thread.
        // It already listens to the exact directory in which Core publishes the
        // atomic verified-return notice. No long-lived polling task is needed.
        // _dispatcher is captured on MainWindow's owning thread in its
        // constructor; do not read a WinUI Window property from the watcher.
        _dispatcher.TryEnqueue(() => _ = HandleVerifiedGameReturnAsync());
    }

    private async Task HandleVerifiedGameReturnAsync()
    {
        if (_handlingVerifiedReturn || App.IsShuttingDown) return;
        if (!VerifiedGameReturnSignal.TryRead(
                VerifiedGameReturnSignal.FileIn(AppPaths.LocalAiStateDir), out var notice)
            || notice is null || _lastObservedVerifiedReturnUtc == notice.ExitUtc)
            return;
        // Consume the signal exactly once, including when the opt-in is off,
        // Console is closed, or user interaction makes focus restoration unsafe.
        _lastObservedVerifiedReturnUtc = notice.ExitUtc;
        if (!_consoleModeActive || !SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit
            || GameLibraryPage.IsInteractionBusy || LocalAiController.Current.IsGameActive())
            return;

        _handlingVerifiedReturn = true;
        try
        {
            GamingStatusDto? state = await GamingOrchestratorClient.Current.GetStatusAsync();
            if (App.IsShuttingDown || !ConsoleAutoReturnPolicy.CanActivate(
                SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit,
                _consoleModeActive, GameLibraryPage.IsInteractionBusy,
                LocalAiController.Current.IsGameActive(), state, notice, DateTime.UtcNow))
                return;

            // A TV disconnected during gameplay may have caused Windows to
            // relocate PaRuX to another screen. Never automatically show Home
            // on an unselected PC monitor when the user chose TV-only Console.
            string displayId = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(displayId))
            {
                var topology = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!topology.Succeeded ||
                    ConsoleDisplaySelectionPolicy.SelectTarget(topology.Value,
                        SettingsStore.Current.Data.ConsoleDisplays, displayId) is null)
                    return;
            }
            if (App.IsShuttingDown || !_consoleModeActive ||
                LocalAiController.Current.IsGameActive()) return;

            // This is the sole intentionally foregrounding behavior: explicitly
            // opt-in, same Console window, exact Core-confirmed process exit.
            // Windows may still deny foreground activation; never force it with
            // synthetic keyboard input, taskbar hacks or process privileges.
            App.ShowMainWindow();
            await GameLibraryPage.OnConsoleForegroundActivatedAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Verified Console Home return failed: " + ex.GetType().Name);
        }
        finally { _handlingVerifiedReturn = false; }
    }

    /// <summary>Only the user's activation of the existing PaRuX window triggers this.
    /// No background game-process scan, focus stealing or permanent controller poller.</summary>
    private async void OnConsoleWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_consoleModeActive || args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        try { await GameLibraryPage.OnConsoleForegroundActivatedAsync(); }
        catch (Exception ex) { Log.Write("Console foreground status refresh failed: " + ex.GetType().Name); }
    }

    private async void ConsoleModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_consoleRoutingInProgress) return;
        _consoleRoutingInProgress = true;
        ConsoleModeButton.IsEnabled = false;
        try
        {
            if (_consoleModeActive) ExitConsoleMode();
            else await EnterConsoleModeAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Console window presenter change failed: " + ex.GetType().Name);
            _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            if (!_consoleModeActive)
            {
                try { RestorePreviousConsoleDisplay(); }
                catch (Exception restore) { Log.Write("Console display recovery failed: " + restore.GetType().Name); }
            }
            UpdateConsoleModeButton();
        }
        finally
        {
            _consoleRoutingInProgress = false;
            ConsoleModeButton.IsEnabled = true;
        }
    }

    private async Task EnterConsoleModeAsync()
    {
        // A previous session's verified exit is not a future Home-return event.
        RememberLatestVerifiedReturn();
        // A selected TV changes only THIS app window's display. It does not
        // change Windows' monitor arrangement, mirroring, HDR, VRR, or default.
        string targetId = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var result = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            GamingDisplayModeDto? destination = result.Succeeded
                ? ConsoleDisplaySelectionPolicy.SelectTarget(
                    result.Value, SettingsStore.Current.Data.ConsoleDisplays, targetId)
                : null;
            if (destination is null)
            {
                await new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = ConsoleText("TV not available", "TV non disponibile"),
                    Content = ConsoleText(
                        "The chosen Console TV is disconnected, changed its Windows display ID or the local Core cannot read it. Reconnect the TV or update Gaming > Console / TV display routing. PaRuX has not moved windows or changed any display settings.",
                        "La TV scelta non è collegata, ha cambiato identificativo Windows o il Core locale non riesce a rilevarla. Ricollega la TV o aggiorna Gaming > Destinazione Console / TV. PaRuX non ha spostato finestre né modificato le impostazioni degli schermi."),
                    CloseButtonText = "OK"
                }.ShowAsync();
                return;
            }

            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            _consolePreviousWindowBounds = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
            if (AppWindow.Presenter is OverlappedPresenter overlapped)
                overlapped.Restore();
            // Center a bounded normal window fully inside the selected physical
            // desktop display BEFORE asking Windows to fullscreen this window.
            // The fullscreen presenter then uses the monitor that owns the window.
            AppWindow.MoveAndResize(new RectInt32(
                destination.X + destination.Width / 4,
                destination.Y + destination.Height / 4,
                Math.Max(1, destination.Width / 2),
                Math.Max(1, destination.Height / 2)));
        }

        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _consoleModeActive = true;
            NavView.IsPaneOpen = true;
            ShowStudioWorkspace(StudioWorkspace.Games);
            GameLibraryPage.ApplyConsoleMode(true);
            GameLibraryPage.OnShown();
            UpdateConsoleModeButton();
            GameLibraryPage.FocusConsoleHome();
        }
        catch
        {
            if (_consoleModeActive)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _consoleModeActive = false;
            }
            RestorePreviousConsoleDisplay();
            throw;
        }
    }

    private static string ConsoleText(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

    private void RestorePreviousConsoleDisplay()
    {
        if (_consolePreviousWindowBounds is not { } original) return;
        if (AppWindow.Presenter is OverlappedPresenter overlapped)
            overlapped.Restore();
        AppWindow.MoveAndResize(original);
        _consolePreviousWindowBounds = null;
    }

    private void ExitConsoleMode()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        _consoleModeActive = false;
        GameLibraryPage.ApplyConsoleMode(false);
        RestorePreviousConsoleDisplay();
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

        switch (action)
        {
            case ConsoleNavigationAction.PreviousPage: GameLibraryPage.PageLists(-1); e.Handled = true; return;
            case ConsoleNavigationAction.NextPage: GameLibraryPage.PageLists(1); e.Handled = true; return;
            case ConsoleNavigationAction.ScrollUp: GameLibraryPage.ScrollBy(-240); e.Handled = true; return;
            case ConsoleNavigationAction.ScrollDown: GameLibraryPage.ScrollBy(240); e.Handled = true; return;
            case ConsoleNavigationAction.FocusSearch: GameLibraryPage.FocusSearch(); e.Handled = true; return;
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
