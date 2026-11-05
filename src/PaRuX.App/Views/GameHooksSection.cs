using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

/// <summary>
/// Per-game launch helpers (advanced, OFF by default): one explicitly chosen local .exe that runs before the game starts and/or after
/// it closed. The Core owns every safety rule (safe local .exe only, bounded arguments, 1-60 s timeout, never during Gaming); this
/// panel only collects the choice, asks for confirmation that names the exact program, and relays the Core's refusal in plain words.
/// </summary>
internal sealed class GameHooksSection : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly string _titleKey;
    private readonly ToggleSwitch _enabled = new() { OnContent = "", OffContent = "" };
    private readonly TextBox _prePath = new() { MaxLength = 260 }, _preArgs = new() { MaxLength = 1024 }, _preTimeout = new() { MaxLength = 2, Text = "15" };
    private readonly TextBox _postPath = new() { MaxLength = 260 }, _postArgs = new() { MaxLength = 1024 }, _postTimeout = new() { MaxLength = 2, Text = "15" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new(), _clear = new();
    private bool _loading = true;

    internal GameHooksSection(string titleKey)
    {
        _titleKey = titleKey;
        Spacing = 8;
        AutomationProperties.SetAutomationId(this, "GameHooks_Panel_" + titleKey);
        Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Optional: run one program you choose before this game starts and/or after it closes (for example to close an overlay or set a display profile). " +
                     "It is off until you turn it on below. Only a local .exe is accepted (no scripts, no shortcuts, no network paths); it runs with your normal rights, " +
                     "is stopped when its timeout ends, and never runs while Gaming is active.",
                     "Facoltativo: esegui un programma che scegli tu prima dell'avvio di questo gioco e/o dopo la chiusura (per esempio per chiudere un overlay o impostare un profilo schermo). " +
                     "Resta spento finché non lo attivi qui sotto. È accettato solo un .exe locale (niente script, collegamenti o percorsi di rete); gira con i tuoi permessi normali, " +
                     "viene fermato allo scadere del timeout e non parte mai mentre è attiva la modalità Gaming.")
        });

        _enabled.Header = T("Allow launch helpers (for all games)", "Consenti gli helper di avvio (per tutti i giochi)");
        AutomationProperties.SetAutomationId(_enabled, "GameHooks_Enabled_" + titleKey);
        AutomationProperties.SetName(_enabled, T("Allow launch helpers for all games", "Consenti gli helper di avvio per tutti i giochi"));
        _enabled.Toggled += async (_, _) => await OnEnabledToggledAsync();
        Children.Add(_enabled);

        AddPhase(T("Before launch", "Prima dell'avvio"), "Pre", _prePath, _preArgs, _preTimeout);
        AddPhase(T("After the game closes", "Dopo la chiusura del gioco"), "Post", _postPath, _postArgs, _postTimeout);

        _save.Content = T("Save helpers", "Salva gli helper");
        _save.MinHeight = 44;
        AutomationProperties.SetAutomationId(_save, "GameHooks_Save_" + titleKey);
        _save.Click += async (_, _) => await SaveAsync();
        _clear.Content = T("Remove helpers of this game", "Rimuovi gli helper di questo gioco");
        _clear.MinHeight = 44;
        AutomationProperties.SetAutomationId(_clear, "GameHooks_Clear_" + titleKey);
        _clear.Click += async (_, _) => await ClearAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_save);
        buttons.Children.Add(_clear);
        Children.Add(buttons);

        AutomationProperties.SetAutomationId(_status, "GameHooks_Status_" + titleKey);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        Loaded += async (_, _) => await LoadAsync();
    }

    private void AddPhase(string heading, string phase, TextBox path, TextBox args, TextBox timeout)
    {
        Children.Add(new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        path.Header = T("Program (.exe)", "Programma (.exe)");
        AutomationProperties.SetAutomationId(path, $"GameHooks_{phase}Path_{_titleKey}");
        var browse = new Button { Content = T("Choose program...", "Scegli programma..."), MinHeight = 44 };
        AutomationProperties.SetAutomationId(browse, $"GameHooks_{phase}Browse_{_titleKey}");
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) path.Text = file.Path;
            }
            catch (Exception) { _status.Text = T("Could not open the file picker.", "Impossibile aprire la selezione file."); }
        };
        args.Header = T("Arguments (optional)", "Argomenti (facoltativi)");
        AutomationProperties.SetAutomationId(args, $"GameHooks_{phase}Args_{_titleKey}");
        timeout.Header = T("Timeout in seconds (1-60)", "Timeout in secondi (1-60)");
        AutomationProperties.SetAutomationId(timeout, $"GameHooks_{phase}Timeout_{_titleKey}");
        Children.Add(path);
        Children.Add(browse);
        Children.Add(args);
        Children.Add(timeout);
    }

    private async Task LoadAsync()
    {
        try
        {
            var state = await GamingLibraryClient.Current.GetGameHooksAsync();
            if (!state.Succeeded || state.Value is null)
            {
                _status.Text = T("Launch helper settings are unavailable right now.", "Le impostazioni degli helper di avvio non sono disponibili in questo momento.");
                return;
            }
            _enabled.IsOn = state.Value.Enabled;
            if (state.Value.Hooks.TryGetValue(_titleKey, out GameHooksDto? hooks))
            {
                Fill(hooks.Pre, _prePath, _preArgs, _preTimeout);
                Fill(hooks.Post, _postPath, _postArgs, _postTimeout);
            }
            _status.Text = state.Value.Enabled
                ? T("Launch helpers are on.", "Gli helper di avvio sono attivi.")
                : T("Launch helpers are off. Nothing runs before or after any game.", "Gli helper di avvio sono spenti. Nulla viene eseguito prima o dopo i giochi.");
        }
        catch (Exception) { _status.Text = T("Launch helper settings could not be read.", "Impossibile leggere le impostazioni degli helper."); }
        finally { _loading = false; }
    }

    private static void Fill(GameHookSpec? spec, TextBox path, TextBox args, TextBox timeout)
    {
        if (spec is null) return;
        path.Text = spec.Path;
        args.Text = spec.Arguments;
        timeout.Text = spec.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task OnEnabledToggledAsync()
    {
        if (_loading) return;
        bool wanted = _enabled.IsOn;
        if (wanted)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Allow launch helpers?", "Consentire gli helper di avvio?"),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = T("PaRuX will be allowed to run the programs you attach to games, before launch and after the game closes. You choose each program yourself; nothing runs until you save one. You can turn this off at any time.",
                             "PaRuX potrà eseguire i programmi che colleghi ai giochi, prima dell'avvio e dopo la chiusura. Scegli tu ogni programma; nulla parte finché non ne salvi uno. Puoi spegnerlo in qualsiasi momento.")
                },
                PrimaryButtonText = T("Allow", "Consenti"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { SetToggle(false); return; }
        }
        var result = await GamingLibraryClient.Current.SetGameHooksEnabledAsync(wanted);
        if (!result.Succeeded)
        {
            SetToggle(!wanted);
            _status.Text = T("The switch could not be saved: ", "Impossibile salvare l'interruttore: ") + (result.Error ?? "");
            return;
        }
        _status.Text = wanted
            ? T("Launch helpers are on.", "Gli helper di avvio sono attivi.")
            : T("Launch helpers are off. Nothing runs before or after any game.", "Gli helper di avvio sono spenti. Nulla viene eseguito prima o dopo i giochi.");
    }

    private void SetToggle(bool value)
    {
        _loading = true;
        try { _enabled.IsOn = value; }
        finally { _loading = false; }
    }

    private async Task SaveAsync()
    {
        bool italian = GameUi.T("en", "it") == "it";
        var (pre, preError) = GameSaveHookPresenter.ParseHookForm(_prePath.Text, _preArgs.Text, _preTimeout.Text, italian);
        var (post, postError) = GameSaveHookPresenter.ParseHookForm(_postPath.Text, _postArgs.Text, _postTimeout.Text, italian);
        if (preError is not null || postError is not null)
        {
            _status.Text = (preError is not null ? T("Before launch: ", "Prima dell'avvio: ") + preError : T("After close: ", "Dopo la chiusura: ") + postError);
            return;
        }
        if (pre is null && post is null)
        {
            _status.Text = T("Choose a program first, or use Remove to clear this game's helpers.", "Scegli prima un programma, oppure usa Rimuovi per cancellare gli helper del gioco.");
            return;
        }
        var hooks = new GameHooksDto(pre, post);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Save these helpers?", "Salvare questi helper?"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = GameSaveHookPresenter.HookConfirmationText(hooks, italian) },
            PrimaryButtonText = T("Save", "Salva"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _status.Text = T("Nothing was saved.", "Nulla è stato salvato.");
            return;
        }
        _save.IsEnabled = false;
        try
        {
            var result = await GamingLibraryClient.Current.SetTitleHooksAsync(_titleKey, hooks);
            _status.Text = result.Succeeded
                ? (_enabled.IsOn
                    ? T("Helpers saved.", "Helper salvati.")
                    : T("Helpers saved, but they stay inactive until you allow launch helpers above.", "Helper salvati, ma restano inattivi finché non consenti gli helper di avvio qui sopra."))
                : T("The Core refused these helpers, nothing was saved: ", "Il Core ha rifiutato questi helper, nulla è stato salvato: ") + (result.Error ?? "");
        }
        finally { _save.IsEnabled = true; }
    }

    private async Task ClearAsync()
    {
        var result = await GamingLibraryClient.Current.SetTitleHooksAsync(_titleKey, new GameHooksDto(null, null));
        if (result.Succeeded)
        {
            foreach (TextBox box in new[] { _prePath, _preArgs, _postPath, _postArgs }) box.Text = string.Empty;
            _preTimeout.Text = _postTimeout.Text = "15";
            _status.Text = T("This game's helpers were removed.", "Gli helper di questo gioco sono stati rimossi.");
        }
        else _status.Text = T("The helpers could not be removed: ", "Impossibile rimuovere gli helper: ") + (result.Error ?? "");
    }
}
