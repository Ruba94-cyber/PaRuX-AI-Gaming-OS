using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

public sealed class ManualGameLibraryView : UserControl
{
    private readonly TextBox _name = new();
    private readonly TextBox _path = new();
    private readonly StackPanel _games = new() { Spacing = 8 };
    private readonly StackPanel _discoveredGames = new() { Spacing = 8 };
    private readonly TextBlock _discoveryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _steamRefresh = new();
    private readonly Button _epicRefresh = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    private readonly Button _browse = new();
    private bool _busy;
    private readonly TextBox _filter = new();
    private List<ManualGameDto> _allGames = new();
    private int _page;
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    public ManualGameLibraryView()
    {
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new TextBlock { Text = T("Game library", "Libreria giochi"), FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = T("Add local games and launch them only after explicit confirmation. PaRuX never installs them here.", "Aggiungi giochi locali e avviali solo dopo una conferma esplicita. PaRuX non li installa da questa schermata."), TextWrapping = TextWrapping.Wrap });
        _name.Header = T("Game name", "Nome del gioco");
        _name.MaxLength = 200;
        AutomationProperties.SetAutomationId(_name, "GameLibrary_Name_TextBox");
        _path.Header = T("Local executable (.exe)", "Eseguibile locale (.exe)");
        _path.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_path, "GameLibrary_Path_TextBox");
        _browse.Content = T("Browse", "Sfoglia");
        AutomationProperties.SetAutomationId(_browse, "GameLibrary_Browse_Button");
        _browse.Click += Browse_Click;
        _add.Content = T("Add game", "Aggiungi gioco");
        AutomationProperties.SetAutomationId(_add, "GameLibrary_Add_Button");
        _add.Click += Add_Click;
        root.Children.Add(_name); root.Children.Add(_path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_browse); buttons.Children.Add(_add); root.Children.Add(buttons);
        AutomationProperties.SetAutomationId(_status, "GameLibrary_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        root.Children.Add(_status);
        _filter.Header = T("Filter by name", "Filtra per nome");
        AutomationProperties.SetAutomationId(_filter, "GameLibrary_Filter_TextBox");
        _filter.TextChanged += (_, _) => { _page = 0; RenderGames(); };
        root.Children.Add(_filter); root.Children.Add(_games);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _previous.Content = T("Previous", "Precedente"); _next.Content = T("Next", "Successiva");
        _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderGames(); } };
        _next.Click += (_, _) => { _page++; RenderGames(); };
        pages.Children.Add(_previous); pages.Children.Add(_next); root.Children.Add(pages);

        root.Children.Add(new TextBlock
        {
            Text = T("Installed store games", "Giochi degli store installati"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Read-only discovery of local Steam and Epic installations. Nothing is launched, installed or modified.", "Rilevamento di sola lettura delle installazioni Steam ed Epic locali. Nessun gioco viene avviato, installato o modificato."),
            TextWrapping = TextWrapping.Wrap
        });
        _steamRefresh.Content = T("Detect Steam games", "Rileva giochi Steam");
        AutomationProperties.SetAutomationId(_steamRefresh, "GameLibrary_Steam_Refresh_Button");
        _steamRefresh.Click += async (_, _) => await RefreshSteamAsync();
        root.Children.Add(_steamRefresh);
        _epicRefresh.Content = T("Detect Epic games", "Rileva giochi Epic");
        AutomationProperties.SetAutomationId(_epicRefresh, "GameLibrary_Epic_Refresh_Button");
        _epicRefresh.Click += async (_, _) => await RefreshEpicAsync();
        root.Children.Add(_epicRefresh);
        AutomationProperties.SetAutomationId(_discoveryStatus, "GameLibrary_Steam_Status_Text");
        AutomationProperties.SetLiveSetting(_discoveryStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_discoveryStatus);
        root.Children.Add(_discoveredGames);

        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public async void OnShown() => await RefreshAsync();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var file = await picker.PickSingleFileAsync();
            if (file != null) _path.Text = file.Path;
        }
        catch (Exception) { _status.Text = T("Could not open the file picker.", "Impossibile aprire la selezione file."); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(_name.Text, _path.Text));
            if (!result.Succeeded) { _status.Text = T("Could not add the game. Check the name, local .exe path, or whether it is already listed.", "Impossibile aggiungere il gioco. Controlla nome, percorso .exe locale o eventuali duplicati."); return; }
            _name.Text = ""; _path.Text = "";
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not add the game.", "Impossibile aggiungere il gioco."); }
        finally { SetBusy(false); }
    }

    private Task RefreshSteamAsync() => RefreshDetectedAsync(
        "Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());

    private Task RefreshEpicAsync() => RefreshDetectedAsync(
        "Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());

    private async Task RefreshDetectedAsync(
        string provider,
        Func<Task<GamingCallResult<List<DiscoveredGameDto>>>> fetch)
    {
        if (_busy) return;
        SetBusy(true);
        _discoveredGames.Children.Clear();
        _discoveryStatus.Text = T($"Detecting installed {provider} games…", $"Rilevamento giochi {provider} installati…");
        try
        {
            GamingCallResult<List<DiscoveredGameDto>> result = await fetch();
            if (!result.Succeeded || result.Value is null)
            {
                _discoveryStatus.Text = T($"{provider} discovery unavailable. No files were changed.", $"Rilevamento {provider} non disponibile. Nessun file è stato modificato.");
                return;
            }
            _discoveryStatus.Text = result.Value.Count == 0
                ? T($"No installed {provider} games found.", $"Nessun gioco {provider} installato rilevato.")
                : T($"Found {result.Value.Count} {provider} games (showing up to 100).", $"Rilevati {result.Value.Count} giochi {provider} (massimo 100 visualizzati).");
            foreach (DiscoveredGameDto game in result.Value.Take(100))
            {
                var item = new StackPanel { Spacing = 2, Padding = new Thickness(12) };
                item.Children.Add(new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                item.Children.Add(new TextBlock { Text = provider + " #" + game.ProviderGameId, TextWrapping = TextWrapping.Wrap });
                _discoveredGames.Children.Add(item);
            }
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T($"{provider} discovery failed. No files were changed.", $"Rilevamento {provider} non riuscito. Nessun file è stato modificato.");
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await LoadAsync(); }
        catch (Exception) { _status.Text = T("Could not load the library.", "Impossibile caricare la libreria."); }
        finally { SetBusy(false); }
    }

    private async Task LoadAsync()
    {
        _status.Text = T("Loading…", "Caricamento…");
        var result = await GamingOrchestratorClient.Current.ListManualGamesAsync();
        if (!result.Succeeded || result.Value == null) { _status.Text = T("Could not load the library. Try opening it again.", "Impossibile caricare la libreria. Prova a riaprirla."); return; }
        _allGames = result.Value;
        _page = 0;
        RenderGames();
    }

    private void RenderGames()
    {
        _games.Children.Clear();
        var matches = _allGames.Where(game => game.Name.Contains(_filter.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (_page * 100 >= matches.Count) _page = Math.Max(0, (matches.Count - 1) / 100);
        int first = matches.Count == 0 ? 0 : _page * 100 + 1;
        int last = Math.Min(matches.Count, (_page + 1) * 100);
        _status.Text = matches.Count == 0 ? T("No matching games.", "Nessun gioco corrispondente.") :
            T($"Showing {first}–{last} of {matches.Count}.", $"Mostrati {first}–{last} di {matches.Count}.");
        _previous.IsEnabled = _page > 0; _next.IsEnabled = last < matches.Count;
        foreach (ManualGameDto game in matches.Skip(_page * 100).Take(100))
        {
            var card = new StackPanel { Spacing = 4, Padding = new Thickness(12) };
            card.Children.Add(new TextBlock { Text = game.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = game.ExecutablePath, TextWrapping = TextWrapping.Wrap });
            var launch = new Button
            {
                Content = T("Play", "Gioca"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(launch, T("Launch ", "Avvia ") + game.Name);
            AutomationProperties.SetAutomationId(launch, "GameLibrary_Launch_" + game.Id);
            launch.Click += async (_, _) => await LaunchAsync(game);
            card.Children.Add(launch);
            var remove = new Button { Content = T("Remove from list", "Rimuovi dalla lista"), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(remove, T("Remove ", "Rimuovi ") + game.Name);
            AutomationProperties.SetAutomationId(remove, "GameLibrary_Remove_" + game.Id);
            remove.Click += async (_, _) => await RemoveAsync(game);
            card.Children.Add(remove); _games.Children.Add(card);
        }
    }

    private async Task LaunchAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch selected game?", "Avviare il gioco selezionato?"),
                Content = T(
                    "PaRuX will pause local AI resources and start only this saved executable. It will not install software, suspend the game, or forcibly close it. Automatic return is enabled only if Windows confirms the owned game process.",
                    "PaRuX sospenderà le risorse AI locali e avvierà solo questo eseguibile salvato. Non installerà software, non sospenderà il gioco né lo chiuderà forzatamente. Il ritorno automatico è disponibile solo quando Windows conferma il processo del gioco avviato."),
                PrimaryButtonText = T("Launch", "Avvia"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await GamingOrchestratorClient.Current.LaunchManualGameAsync(game.Id);
            if (!result.Succeeded || result.Value is not { Launched: true } launch)
            {
                _status.Text = T(
                    "Could not launch the game. Check Gaming state and the executable.",
                    "Impossibile avviare il gioco. Controlla lo stato Gaming e l'eseguibile.");
                return;
            }
            _status.Text = launch.AutomaticReturnOnExit
                ? T("Game started. Automatic return to AI is monitoring the owned process.",
                    "Gioco avviato. Il ritorno automatico all'AI monitora il processo avviato.")
                : T("Game started. Return to AI manually when finished.",
                    "Gioco avviato. Torna manualmente all'AI al termine.");
        }
        catch (Exception)
        {
            _status.Text = T("Could not launch the game.", "Impossibile avviare il gioco.");
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = T("Remove game?", "Rimuovere il gioco?"), Content = T("This removes only the entry; game files stay on disk.", "Viene rimossa solo la voce; i file del gioco restano sul disco."), PrimaryButtonText = T("Remove", "Rimuovi"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RemoveManualGameAsync(game.Id);
            if (!result.Succeeded) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); return; }
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value) { _busy = value; _add.IsEnabled = !value; _browse.IsEnabled = !value; _steamRefresh.IsEnabled = !value; _epicRefresh.IsEnabled = !value; _games.IsHitTestVisible = !value; }
}
