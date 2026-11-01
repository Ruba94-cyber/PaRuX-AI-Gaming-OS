using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Settings > Gaming & AI Sharing: user-defined roles and one explicit Console TV.
/// This does not switch displays, change HDR/VRR, create virtual displays, or
/// claim control over the output monitor of an independently launched game.
/// </summary>
public sealed partial class SettingsView
{
    private bool _syncingOwnedGameRouteSwitch;
    private bool _consoleTvSettingsInitialized;
    private int _consoleDisplayRefreshVersion;

    private void ConsoleRouteOwnedGamesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.RouteVerifiedWindowedGamesToTv =
            ConsoleRouteOwnedGamesSwitch.IsOn;
        SettingsStore.Current.Save();
    }
    private GamingDisplaySnapshotDto? _lastConsoleDisplaySnapshot;

    private void ConsoleDisplaysUseCurrent_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Data.ConsoleTargetDisplayId = string.Empty;
        SettingsStore.Current.Save();
        if (_lastConsoleDisplaySnapshot is { Supported: true } snapshot)
            RenderConsoleDisplays(snapshot);
        else
            ConsoleDisplaysStatus.Text = TvT(
                "Automatic output restored: Console opens on the current monitor.",
                "Destinazione automatica ripristinata: Console si apre sul monitor attuale.");
    }
    private static bool TvSettingsItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string TvT(string en, string it) => TvSettingsItalian ? it : en;

    private async void ConsoleDisplaysRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshConsoleDisplaysAsync();

    private async Task RefreshConsoleDisplaysAsync()
    {
        ConsoleDisplaysHeading.Text = TvT("Console / TV display routing", "Destinazione schermo Console / TV");
        ConsoleDisplaysIntro.Text = TvT(
            "Mark each Windows display as a PC monitor or TV, then explicitly choose one TV for PaRuX Console fullscreen. Other monitors remain available. PaRuX moves its own window only, not external games or emulators. Duplicated Windows displays cannot be separated.",
            "Classifica ogni schermo Windows come monitor PC o TV, quindi scegli esplicitamente una TV per la Console PaRuX a schermo intero. Gli altri monitor restano disponibili. PaRuX sposta soltanto la propria finestra, non giochi o emulatori esterni. Gli schermi duplicati di Windows non possono essere separati.");
        ConsoleDisplaysRefresh.Content = TvT("Detect connected displays", "Rileva schermi collegati");
        ConsoleDisplaysUseCurrent.Content = TvT("Use current monitor (automatic)", "Usa monitor attuale (automatico)");
        ConsoleRouteOwnedGamesSwitch.Header = TvT(
            "Prefer selected TV for directly launched windowed games (never force fullscreen or third-party launchers)",
            "Preferisci la TV scelta per i giochi in finestra avviati direttamente (mai forzare fullscreen o launcher esterni)");
        _syncingOwnedGameRouteSwitch = true;
        try
        {
            ConsoleRouteOwnedGamesSwitch.IsOn =
                SettingsStore.Current.Data.RouteVerifiedWindowedGamesToTv;
        }
        finally { _syncingOwnedGameRouteSwitch = false; }
        _consoleTvSettingsInitialized = true;
        int version = ++_consoleDisplayRefreshVersion;
        ConsoleDisplaysRefresh.IsEnabled = false;
        ConsoleDisplaysStatus.Text = TvT("Detecting connected screens…", "Rilevamento schermi collegati…");
        try
        {
            var reply = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            if (version != _consoleDisplayRefreshVersion) return;
            if (!reply.Succeeded || reply.Value is null || !reply.Value.Supported)
            {
                ConsoleDisplaysList.Children.Clear();
                ConsoleDisplaysStatus.Text = TvT(
                    "Displays are unavailable. Check the local Core. Saved TV assignments were not changed.",
                    "Schermi non disponibili. Controlla il Core locale. Le assegnazioni TV salvate non sono state modificate.");
                return;
            }

            GamingDisplaySnapshotDto snapshot = reply.Value;
            _lastConsoleDisplaySnapshot = snapshot;
            RenderConsoleDisplays(snapshot);
        }
        catch (Exception)
        {
            if (version == _consoleDisplayRefreshVersion)
                ConsoleDisplaysStatus.Text = TvT(
                    "Unable to read Windows displays. The saved assignment is unchanged.",
                    "Impossibile leggere gli schermi Windows. L'assegnazione salvata resta invariata.");
        }
        finally
        {
            if (version == _consoleDisplayRefreshVersion)
                ConsoleDisplaysRefresh.IsEnabled = true;
        }
    }

    private void RenderConsoleDisplays(GamingDisplaySnapshotDto snapshot)
    {
        ConsoleDisplaysList.Children.Clear();
        var settings = SettingsStore.Current.Data;
        settings.ConsoleDisplays ??= new List<ConsoleDisplayAssignment>();
        string selected = settings.ConsoleTargetDisplayId ?? string.Empty;
        var target = ConsoleDisplaySelectionPolicy.SelectTarget(
            snapshot, settings.ConsoleDisplays, selected);
        string state = target is not null
            ? TvT("Console fullscreen destination: ", "Destinazione Console a schermo intero: ") + target.DisplayId
            : string.IsNullOrWhiteSpace(selected)
                ? TvT("No TV output chosen. Console opens on the current screen.",
                    "Nessuna TV selezionata. La Console si apre sullo schermo attuale.")
                : TvT("Previously selected TV is disconnected or no longer classified as a TV. Console will not silently use another screen.",
                    "La TV selezionata non è collegata o non è più classificata come TV. La Console non utilizzerà un altro schermo di nascosto.");
        ConsoleDisplaysStatus.Text = state;

        for (int i = 0; i < snapshot.Displays.Count; i++)
        {
            GamingDisplayModeDto display = snapshot.Displays[i];
            if (!ConsoleDisplaySelectionPolicy.ValidDisplayId(display.DisplayId)) continue;
            string deviceId = display.DisplayId;
            string role = settings.ConsoleDisplays.LastOrDefault(a =>
                string.Equals(a.DisplayId, deviceId, StringComparison.OrdinalIgnoreCase))?.Role
                ?? ConsoleDisplaySelectionPolicy.MonitorRole;

            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(10) };
            panel.Children.Add(new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(display.FriendlyName)
                    ? deviceId : display.FriendlyName + " · " + deviceId) +
                    $" · {display.Width} × {display.Height}" +
                    (display.RefreshRateHz > 0 ? $" · {display.RefreshRateHz} Hz" : "") +
                    (display.Primary ? TvT(" · Primary", " · Principale") : ""),
                TextWrapping = TextWrapping.Wrap
            });
            var roleBox = new ComboBox
            {
                Header = TvT("Display type", "Tipo di schermo"),
                MinWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(roleBox, $"Settings_ConsoleDisplay_{i}_Role");
            roleBox.Items.Add(new ComboBoxItem { Content = TvT("PC monitor", "Monitor PC"), Tag = ConsoleDisplaySelectionPolicy.MonitorRole });
            roleBox.Items.Add(new ComboBoxItem { Content = "TV", Tag = ConsoleDisplaySelectionPolicy.TvRole });
            roleBox.SelectedIndex = role == ConsoleDisplaySelectionPolicy.TvRole ? 1 : 0;

            var use = new Button
            {
                Content = string.Equals(selected, deviceId, StringComparison.OrdinalIgnoreCase)
                    ? TvT("Selected Console TV", "TV Console selezionata")
                    : TvT("Use this TV for Console", "Usa questa TV per Console"),
                IsEnabled = role == ConsoleDisplaySelectionPolicy.TvRole &&
                    !string.Equals(selected, deviceId, StringComparison.OrdinalIgnoreCase),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(use, $"Settings_ConsoleDisplay_{i}_Target");

            roleBox.SelectionChanged += (_, _) =>
            {
                string chosen = (roleBox.SelectedItem as ComboBoxItem)?.Tag as string
                    ?? ConsoleDisplaySelectionPolicy.MonitorRole;
                if (!ConsoleDisplaySelectionPolicy.ValidRole(chosen)) return;
                var data = SettingsStore.Current.Data;
                data.ConsoleDisplays ??= new List<ConsoleDisplayAssignment>();
                data.ConsoleDisplays.RemoveAll(a =>
                    string.Equals(a.DisplayId, deviceId, StringComparison.OrdinalIgnoreCase));
                data.ConsoleDisplays.Add(new ConsoleDisplayAssignment(deviceId, chosen));
                if (chosen != ConsoleDisplaySelectionPolicy.TvRole &&
                    string.Equals(data.ConsoleTargetDisplayId, deviceId, StringComparison.OrdinalIgnoreCase))
                    data.ConsoleTargetDisplayId = string.Empty;
                SettingsStore.Current.Save();
                // Defer rebuilding the picker until WinUI has finished raising
                // SelectionChanged for the ComboBox being removed from the tree.
                _dispatcher.TryEnqueue(() => RenderConsoleDisplays(snapshot));
            };
            use.Click += (_, _) =>
            {
                var data = SettingsStore.Current.Data;
                if (ConsoleDisplaySelectionPolicy.SelectTarget(
                    snapshot, data.ConsoleDisplays, deviceId) is null) return;
                data.ConsoleTargetDisplayId = deviceId;
                SettingsStore.Current.Save();
                RenderConsoleDisplays(snapshot);
            };
            panel.Children.Add(roleBox);
            panel.Children.Add(use);
            // Reuse the existing theme's subtle surface; no custom brush keys
            // or light/dark-specific inline colors are required for this control.
            var card = new Border { Child = panel, CornerRadius = new CornerRadius(10) };
            if (Application.Current.Resources.TryGetValue("PaRuXSurfaceSubtleBrush", out object? brush) &&
                brush is Microsoft.UI.Xaml.Media.Brush surface)
                card.Background = surface;
            ConsoleDisplaysList.Children.Add(card);
        }
    }
}
