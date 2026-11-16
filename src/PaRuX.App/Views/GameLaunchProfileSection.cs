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
/// Compact per-game launch profile (display, fullscreen preference, performance profile, controller; resolution and launch
/// arguments under Advanced). The Core owns every validation rule: this panel only collects values and shows the Core's
/// refusal inline. Plain WinUI controls, so the controller and the Console on-screen keyboard work as everywhere else.
/// Fullscreen is stored as a preference only and the panel says so.
/// </summary>
internal sealed class GameLaunchProfileSection : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly string _key;
    private readonly double _body, _target;
    private readonly ComboBox _display = new(), _performance = new(), _controller = new(), _postPreset = new();
    private readonly CheckBox _fullscreen = new();
    private readonly CheckBox _postFocus = new(), _postAltEnter = new(), _postClick = new();
    private readonly TextBox _postDelay = new() { MaxLength = 4, Text = "1200" };
    private readonly TextBox _width = new() { MaxLength = 5 }, _height = new() { MaxLength = 5 }, _refresh = new() { MaxLength = 4 };
    private readonly TextBox _args = new() { MaxLength = 2048 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new(), _reset = new(), _retry = new() { Visibility = Visibility.Collapsed };
    private GameLaunchProfileDto _loaded = new();
    private bool _busy, _loaded_ok, _syncingPostPreset;

    internal GameLaunchProfileSection(string gameKey, bool console)
    {
        _key = gameKey;
        _body = console ? 20 : 14;
        _target = console ? 56 : 44;
        Spacing = 8;
        AutomationProperties.SetAutomationId(this, "GameLaunchProfile_Panel_" + gameKey);

        Children.Add(Note(T("Optional launch preferences for this game. Empty means: start it exactly as saved in the library.",
                            "Preferenze di avvio facoltative per questo gioco. Vuoto significa: avvia come salvato nella libreria.")));

        Setup(_display, T("Display", "Schermo"), "Display");
        Setup(_performance, T("Performance profile", "Profilo prestazioni"), "Performance");
        foreach (string p in GameLaunchProfileForm.PerformanceProfiles)
            _performance.Items.Add(new ComboBoxItem { Content = PerformanceLabel(p), Tag = p });
        Setup(_controller, T("Controller", "Controller"), "Controller");
        foreach (string c in GameLaunchProfileForm.ControllerPresets)
            _controller.Items.Add(new ComboBoxItem { Content = ControllerLabel(c), Tag = c });

        // Quick Playnite-like one-click startup preset; advanced checkboxes below
        // still allow inspecting/editing the exact actions and launch wait.
        Setup(_postPreset, T("After-launch assistance", "Assistenza dopo l'avvio"), "PostPreset");
        foreach (var (tag, en, it) in new[]
        {
            ("none", "None (default)", "Nessuna (predefinita)"),
            ("focus", "Bring game to foreground", "Porta il gioco in primo piano"),
            ("fullscreen", "Foreground + Alt+Enter fullscreen toggle", "Primo piano + commutazione Alt+Invio"),
            ("click", "Foreground + one center click", "Primo piano + un click centrale"),
            ("all", "Foreground + Alt+Enter + click", "Primo piano + Alt+Invio + click")
        })
            _postPreset.Items.Add(new ComboBoxItem { Content = T(en, it), Tag = tag });
        _postPreset.SelectionChanged += (_, _) => ApplyPostPreset();
        Select(_postPreset, "none");

        _fullscreen.Content = T("Fullscreen (saved as a preference)", "Schermo intero (salvato come preferenza)");
        _fullscreen.MinHeight = _target; _fullscreen.FontSize = _body;
        AutomationProperties.SetAutomationId(_fullscreen, "GameLaunchProfile_Fullscreen_" + gameKey);
        AutomationProperties.SetName(_fullscreen, T("Fullscreen preference", "Preferenza schermo intero"));
        Children.Add(_fullscreen);
        var fullscreenNote = Note(T("PaRuX cannot force fullscreen on an arbitrary program: it is saved as a preference and is not applied when the game starts. Use the game's own setting or a launch argument.",
                                    "PaRuX non può forzare lo schermo intero su un programma qualsiasi: viene salvato come preferenza e non è applicato all'avvio del gioco. Usa l'impostazione del gioco o un argomento di avvio."));
        AutomationProperties.SetAutomationId(fullscreenNote, "GameLaunchProfile_FullscreenNote_" + gameKey);
        Children.Add(fullscreenNote);

        // Advanced: resolution and arguments.
        var advanced = new StackPanel { Spacing = 8 };
        Field(_width, T("Width (px)", "Larghezza (px)"), "Width"); Field(_height, T("Height (px)", "Altezza (px)"), "Height");
        Field(_refresh, T("Refresh rate (Hz, optional)", "Frequenza (Hz, facoltativa)"), "Refresh");
        var resolution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        resolution.Children.Add(_width); resolution.Children.Add(_height); resolution.Children.Add(_refresh);
        advanced.Children.Add(resolution);
        Field(_args, T("Launch arguments (separated by spaces; {width} {height} {refresh} allowed)",
                       "Argomenti di avvio (separati da spazi; ammessi {width} {height} {refresh})"), "Args");
        _args.HorizontalAlignment = HorizontalAlignment.Stretch;
        advanced.Children.Add(_args);
        var expander = new Expander
        {
            Header = T("Advanced: resolution and launch arguments", "Avanzate: risoluzione e argomenti di avvio"),
            Content = advanced, IsExpanded = false, MinHeight = _target,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "GameLaunchProfile_Advanced_" + gameKey);
        Children.Add(expander);

        // Explicit per-game startup assistance. No action is enabled by default,
        // and the Core will require an exact process + uniquely verified HWND.
        var postPanel = new StackPanel { Spacing = 6 };
        void AddPostAction(CheckBox check, string label, string id)
        {
            check.Content = label;
            check.MinHeight = _target;
            check.FontSize = _body;
            AutomationProperties.SetAutomationId(check, "GameLaunchProfile_" + id + "_" + gameKey);
            postPanel.Children.Add(check);
        }
        AddPostAction(_postFocus,
            T("Bring this game to the foreground after launch", "Porta il gioco in primo piano dopo l'avvio"),
            "PostFocus");
        AddPostAction(_postAltEnter,
            T("Request fullscreen once (Alt+Enter toggle)", "Richiedi schermo intero una volta (Alt+Invio, commutazione)"),
            "PostAltEnter");
        AddPostAction(_postClick,
            T("Send one click to the center of the game window", "Invia un click al centro della finestra del gioco"),
            "PostClick");
        _postFocus.Checked += (_, _) => { EnablePostChildren(); UpdatePostPreset(); };
        _postFocus.Unchecked += (_, _) =>
        {
            _postAltEnter.IsChecked = false;
            _postClick.IsChecked = false;
            EnablePostChildren();
            UpdatePostPreset();
        };
        _postAltEnter.Checked += (_, _) => UpdatePostPreset();
        _postAltEnter.Unchecked += (_, _) => UpdatePostPreset();
        _postClick.Checked += (_, _) => UpdatePostPreset();
        _postClick.Unchecked += (_, _) => UpdatePostPreset();
        Field(_postDelay, T("Wait after launch (milliseconds, 0–8000)", "Attesa dopo l'avvio (millisecondi, 0–8000)"), "PostDelay");
        postPanel.Children.Add(_postDelay);
        postPanel.Children.Add(Note(T(
            "Only for directly launched games with one verified window. Alt+Enter may be ignored or toggle an already-fullscreen game back to windowed. A click can activate an in-game control. No actions on store/Playnite handoffs, other apps, or elevated windows.",
            "Solo per giochi avviati direttamente con una finestra verificata. Alt+Invio può essere ignorato o riportare un gioco già a schermo intero in finestra. Il click può attivare un comando nel gioco. Nessuna azione su passaggi a store/Playnite, altre app o finestre elevate.")));
        var postExpander = new Expander
        {
            Header = T("After launch (advanced, opt-in)", "Dopo l'avvio (avanzate, facoltative)"),
            Content = postPanel, IsExpanded = false, MinHeight = _target,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(postExpander, "GameLaunchProfile_PostLaunch_" + gameKey);
        Children.Add(postExpander);
        EnablePostChildren();

        _save.Content = T("Save launch profile", "Salva il profilo di avvio");
        _reset.Content = T("Reset to defaults", "Ripristina predefiniti");
        foreach (var b in new[] { _save, _reset, _retry }) { b.MinHeight = _target; b.FontSize = _body; }
        AutomationProperties.SetAutomationId(_save, "GameLaunchProfile_Save_" + gameKey);
        AutomationProperties.SetAutomationId(_reset, "GameLaunchProfile_Reset_" + gameKey);
        _save.Click += async (_, _) => await SaveAsync(reset: false);
        _reset.Click += async (_, _) => await SaveAsync(reset: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_save); buttons.Children.Add(_reset);
        Children.Add(buttons);

        _status.FontSize = _body;
        AutomationProperties.SetAutomationId(_status, "GameLaunchProfile_Status_" + gameKey);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        _retry.Content = T("Retry loading", "Riprova a caricare");
        AutomationProperties.SetAutomationId(_retry, "GameLaunchProfile_Retry_" + gameKey);
        _retry.Click += async (_, _) => await LoadAsync();
        Children.Add(_retry);

        SetEditable(false);
        Loaded += async (_, _) => { if (!_loaded_ok) await LoadAsync(); };
    }

    private TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = _body - 2, Opacity = 0.8 };

    private void Setup(ComboBox box, string header, string id)
    {
        box.Header = header; box.FontSize = _body; box.MinHeight = _target; box.MinWidth = 260;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetAutomationId(box, "GameLaunchProfile_" + id + "_" + _key);
        AutomationProperties.SetName(box, header);
        Children.Add(box);
    }

    private void Field(TextBox box, string header, string id)
    {
        box.Header = header; box.FontSize = _body; box.MinHeight = _target;
        AutomationProperties.SetAutomationId(box, "GameLaunchProfile_" + id + "_" + _key);
        AutomationProperties.SetName(box, header);
    }

    private static string PerformanceLabel(string p) => p switch
    {
        "performance" => T("Performance", "Prestazioni"),
        "quality" => T("Quality", "Qualità"),
        "quiet" => T("Quiet", "Silenzioso"),
        _ => T("Balanced", "Bilanciato")
    };

    private static string ControllerLabel(string c) => c switch
    {
        "xbox" => "Xbox",
        "playstation" => "PlayStation",
        "switch" => "Switch",
        "generic" => T("Generic gamepad", "Gamepad generico"),
        "keyboard_mouse" => T("Keyboard and mouse", "Tastiera e mouse"),
        _ => T("Automatic", "Automatico")
    };

    private void ApplyPostPreset()
    {
        if (_syncingPostPreset || !_loaded_ok || _busy) return;
        string value = (string?)(_postPreset.SelectedItem as ComboBoxItem)?.Tag ?? "none";
        _syncingPostPreset = true;
        try
        {
            _postFocus.IsChecked = value != "none";
            _postAltEnter.IsChecked = value is "fullscreen" or "all";
            _postClick.IsChecked = value is "click" or "all";
        }
        finally { _syncingPostPreset = false; }
        EnablePostChildren();
    }

    private void UpdatePostPreset()
    {
        if (_syncingPostPreset) return;
        _syncingPostPreset = true;
        try
        {
            bool focus = _postFocus.IsChecked == true;
            bool alt = _postAltEnter.IsChecked == true;
            bool click = _postClick.IsChecked == true;
            string tag = !focus ? "none" : alt && click ? "all" : alt ? "fullscreen" : click ? "click" : "focus";
            Select(_postPreset, tag);
        }
        finally { _syncingPostPreset = false; }
    }

    private void EnablePostChildren()
    {
        bool enabled = _loaded_ok && !_busy;
        _postAltEnter.IsEnabled = enabled && _postFocus.IsChecked == true;
        _postClick.IsEnabled = enabled && _postFocus.IsChecked == true;
    }

    private void SetEditable(bool on)
    {
        foreach (Control c in new Control[] { _display, _performance, _controller, _postPreset, _fullscreen, _width, _height, _refresh, _args, _postFocus, _postDelay, _save, _reset })
            c.IsEnabled = on && !_busy;
        EnablePostChildren();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = GameUi.ThemeBrush(this, error ? "PaRuXDangerTextBrush" : "PaRuXTextSecondaryBrush");
    }

    private async Task LoadAsync()
    {
        _retry.Visibility = Visibility.Collapsed;
        ShowStatus(T("Loading...", "Caricamento..."), false);
        var profileTask = GamingOrchestratorClient.Current.GetLaunchProfileAsync(_key);
        var displayTask = GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
        var profile = await profileTask;
        var displays = await displayTask;
        if (!profile.Succeeded || profile.Value is null)
        {
            ShowStatus(T("The launch profile could not be loaded: ", "Impossibile caricare il profilo di avvio: ") + profile.Error, true);
            _retry.Visibility = Visibility.Visible;
            return;
        }
        _loaded = profile.Value;

        _display.Items.Clear();
        _display.Items.Add(new ComboBoxItem { Content = T("No preference", "Nessuna preferenza"), Tag = "" });
        if (displays.Succeeded && displays.Value is { Supported: true } snapshot)
            foreach (GamingDisplayModeDto d in snapshot.Displays)
                _display.Items.Add(new ComboBoxItem
                {
                    Content = (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.DisplayId : d.FriendlyName) + $" · {d.Width}x{d.Height}",
                    Tag = d.DisplayId
                });
        string saved = _loaded.DisplayId ?? "";
        if (saved.Length > 0 && !_display.Items.OfType<ComboBoxItem>().Any(i => (string?)i.Tag == saved))
            _display.Items.Add(new ComboBoxItem { Content = T("Saved display (not connected)", "Schermo salvato (non collegato)"), Tag = saved });
        Select(_display, saved);
        Select(_performance, _loaded.PerformanceProfile);
        Select(_controller, _loaded.ControllerPreset);
        _fullscreen.IsChecked = _loaded.Fullscreen == true;
        _width.Text = _loaded.Width?.ToString() ?? "";
        _height.Text = _loaded.Height?.ToString() ?? "";
        _refresh.Text = _loaded.RefreshHz?.ToString() ?? "";
        _args.Text = GameLaunchProfileForm.JoinArgs(_loaded.LaunchArgs);
        GamePostLaunchDto post = _loaded.PostLaunch ?? new();
        _postFocus.IsChecked = post.BringToForeground;
        _postAltEnter.IsChecked = post.RequestAltEnterFullscreen;
        _postClick.IsChecked = post.ClickClientCenter;
        _postDelay.Text = post.DelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdatePostPreset();
        _loaded_ok = true;
        SetEditable(true);
        ShowStatus("", false);
    }

    private static void Select(ComboBox box, string tag)
    {
        var match = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag)
                    ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
        box.SelectedItem = match;
    }

    private async Task SaveAsync(bool reset)
    {
        if (_busy || !_loaded_ok) return;
        GameLaunchProfileDto next = new()
        {
            Streaming = _loaded.Streaming,
            AiConcurrency = string.IsNullOrEmpty(_loaded.AiConcurrency) ? "hard_off" : _loaded.AiConcurrency
        };
        if (!reset)
        {
            if (!GameLaunchProfileForm.TryParseOptionalInt(_width.Text, out int? w) ||
                !GameLaunchProfileForm.TryParseOptionalInt(_height.Text, out int? h) ||
                !GameLaunchProfileForm.TryParseOptionalInt(_refresh.Text, out int? hz))
            {
                ShowStatus(T("Width, height and refresh rate must be whole numbers.", "Larghezza, altezza e frequenza devono essere numeri interi."), true);
                return;
            }
            string display = (string?)(_display.SelectedItem as ComboBoxItem)?.Tag ?? "";
            next.DisplayId = display.Length == 0 ? null : display;
            next.Width = w; next.Height = h; next.RefreshHz = hz;
            next.Fullscreen = _fullscreen.IsChecked == true ? true : null;
            next.ControllerPreset = (string?)(_controller.SelectedItem as ComboBoxItem)?.Tag ?? "auto";
            next.PerformanceProfile = (string?)(_performance.SelectedItem as ComboBoxItem)?.Tag ?? "balanced";
            next.LaunchArgs = GameLaunchProfileForm.ParseArgs(_args.Text);
            if (!int.TryParse(_postDelay.Text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int postDelay) ||
                postDelay is < 0 or > 8000)
            {
                ShowStatus(T("Post-launch delay must be a number from 0 to 8000 ms.",
                    "L'attesa dopo l'avvio deve essere un numero da 0 a 8000 ms."), true);
                return;
            }
            next.PostLaunch = new GamePostLaunchDto
            {
                BringToForeground = _postFocus.IsChecked == true,
                RequestAltEnterFullscreen = _postAltEnter.IsChecked == true,
                ClickClientCenter = _postClick.IsChecked == true,
                DelayMs = postDelay
            };
        }
        _busy = true; SetEditable(true);
        ShowStatus(T("Saving...", "Salvataggio..."), false);
        try
        {
            var result = await GamingOrchestratorClient.Current.SaveLaunchProfileAsync(_key, next);
            if (!result.Succeeded)
            {
                // The Core's reason (for example a rejected argument) is shown as written, nothing was stored.
                ShowStatus(T("Not saved: ", "Non salvato: ") + result.Error, true);
                return;
            }
            _busy = false;
            _loaded_ok = false;
            await LoadAsync();
            ShowStatus(reset ? T("Launch profile reset.", "Profilo di avvio ripristinato.") : T("Launch profile saved.", "Profilo di avvio salvato."), false);
        }
        finally { _busy = false; SetEditable(_loaded_ok); }
    }
}
