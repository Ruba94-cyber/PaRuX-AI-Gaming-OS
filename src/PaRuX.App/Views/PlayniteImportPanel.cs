using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// "Bring your Playnite library": finds the Playnite library of this PC, shows what PaRuX can take over (programs and
/// emulator games) and what it leaves to its own store discovery, then imports only after a separate click. Playnite's
/// files are only read (a private copy is opened by the Core); covers are copied by the same validated cover store the
/// rest of the library uses.
/// </summary>
internal sealed class PlayniteImportPanel : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly Func<Task> _refreshLibrary;
    private readonly Button _look = new() { MinHeight = 44 };
    private readonly Button _import = new() { MinHeight = 44, Visibility = Visibility.Collapsed };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private bool _busy;

    internal PlayniteImportPanel(Func<Task> refreshLibrary)
    {
        _refreshLibrary = refreshLibrary;
        Spacing = 10;
        AutomationProperties.SetAutomationId(this, "GameImport_Playnite_Panel");
        var heading = new TextBlock
        {
            Text = T("Bring your Playnite library", "Porta qui la tua libreria Playnite"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = T(
                "PaRuX reads your Playnite library without changing it, then adds your programs and emulator games with their covers, favourites and play time. Games from Steam, Epic or GOG are found by PaRuX itself.",
                "PaRuX legge la tua libreria Playnite senza modificarla e aggiunge i tuoi programmi e giochi per emulatore con copertine, preferiti e tempo di gioco. I giochi di Steam, Epic o GOG li trova PaRuX da solo."),
        };
        _look.Content = T("Look for my Playnite library", "Cerca la mia libreria Playnite");
        AutomationProperties.SetAutomationId(_look, "GameImport_Playnite_Look_Button");
        _look.Click += async (_, _) => await LookAsync();
        _import.Style = GameUi.AccentButtonStyle;
        AutomationProperties.SetAutomationId(_import, "GameImport_Playnite_Import_Button");
        _import.Click += async (_, _) => await ImportAsync();
        AutomationProperties.SetAutomationId(_summary, "GameImport_Playnite_Summary");
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        Children.Add(heading);
        Children.Add(intro);
        Children.Add(_look);
        Children.Add(_summary);
        Children.Add(_list);
        Children.Add(_import);
    }

    private int _importable;

    private async Task LookAsync()
    {
        if (_busy) return;
        _busy = true;
        _look.IsEnabled = false;
        _import.Visibility = Visibility.Collapsed;
        _list.Children.Clear();
        _summary.Text = T("Looking…", "Ricerca in corso…");
        try
        {
            var response = await GamingLibraryClient.Current.PreviewPlayniteLibraryAsync();
            if (!response.Succeeded || response.Value is null)
            {
                _summary.Text = T("No Playnite library could be read on this PC. Nothing was changed.",
                    "Non è stato possibile leggere una libreria Playnite su questo PC. Non è stato modificato nulla.");
                return;
            }
            List<PlayniteGamePreviewDto> games = response.Value;
            _importable = games.Count(g => g.Importable);
            int stores = games.Count(g => !g.Importable && g.Kind == "store");
            int other = games.Count - _importable - stores;
            _summary.Text = T(
                $"Found {games.Count} games: {_importable} can be imported, {stores} come from store libraries that PaRuX finds itself, {other} cannot be started from PaRuX.",
                $"Trovati {games.Count} giochi: {_importable} importabili, {stores} provengono da store che PaRuX trova da solo, {other} non avviabili da PaRuX.");
            foreach (PlayniteGamePreviewDto game in games.OrderByDescending(g => g.Importable).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Take(60))
            {
                string state = game.Importable ? T("will be imported", "sarà importato") : game.SkipReason;
                _list.Children.Add(new TextBlock
                {
                    Text = game.Name + " · " + state,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = game.Importable ? 1 : 0.65,
                });
            }
            if (_importable > 0)
            {
                _import.Content = T($"Import {_importable} games into PaRuX", $"Importa {_importable} giochi in PaRuX");
                _import.Visibility = Visibility.Visible;
            }
        }
        finally { _busy = false; _look.IsEnabled = true; }
    }

    private async Task ImportAsync()
    {
        if (_busy || _importable == 0) return;
        _busy = true;
        _import.IsEnabled = false;
        try
        {
            var response = await GamingLibraryClient.Current.ImportPlayniteLibraryAsync(consent: true);
            if (!response.Succeeded || response.Value is not { } result)
            {
                _summary.Text = T("The import failed; nothing was added.", "Importazione non riuscita; non è stato aggiunto nulla.");
                return;
            }
            int covers = 0;
            foreach (PlayniteImportedGameDto game in result.Games)
                if (!string.IsNullOrEmpty(game.CoverPath) && GameCoverStore.Set(game.TitleKey, game.CoverPath) == GameCoverResult.Saved) covers++;
            _summary.Text = T(
                $"Imported {result.Imported} games with {covers} covers ({result.AlreadyInLibrary} were already in PaRuX, {result.Skipped} skipped). Playnite was not changed and nothing was started.",
                $"Importati {result.Imported} giochi con {covers} copertine ({result.AlreadyInLibrary} erano già in PaRuX, {result.Skipped} saltati). Playnite non è stato modificato e nulla è stato avviato.");
            _import.Visibility = Visibility.Collapsed;
            _list.Children.Clear();
            await _refreshLibrary();
        }
        finally { _busy = false; _import.IsEnabled = true; }
    }
}
