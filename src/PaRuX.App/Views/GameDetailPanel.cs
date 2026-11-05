using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

/// <summary>
/// Detail card of one title: installations, last played, play time, favourite, hidden, tags, collections, an optional
/// local cover, and for saved manual or emulator entries the program, arguments and emulator with edit and remove.
/// Removing an entry deletes only the library record after a confirmation; game files are never touched.
/// Starting a game goes through the host's existing launch-consent flow.
/// </summary>
internal sealed class GameDetailPanel : UserControl
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly Func<GameInstallationDto, Task> _launch;
    private readonly Func<Task> _refreshLibrary;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private GameTitleDto? _title;
    private GameInstallationDto? _chosen;
    private int _generation;
    private bool _console;
    private Button? _play;
    private bool _launching;

    // Labels use concrete brushes because the panel is built in C#. Track only
    // those references: switching themes must NEVER rebuild TextBox editors or
    // discard a draft tag, collection, launch-argument edit or keyboard focus.
    private readonly List<(TextBlock Text, bool Secondary)> _semanticLabels = new();
    private Border? _fallbackCoverSurface;
    private TextBlock? _fallbackCoverInitials;

    /// <summary>Raised after a successful local edit (favorite, hidden, tags, collections, cover, entry edit) so the list can refresh.</summary>
    internal event Action<GameTitleDto>? Changed;
    /// <summary>Raised after a manual entry was removed.</summary>
    internal event Action<string>? Removed;
    internal event Action? CloseRequested;
    internal event Action<string>? StatusMessage;

    internal GameDetailPanel(Func<GameInstallationDto, Task> launch, Func<Task> refreshLibrary)
    {
        _launch = launch;
        _refreshLibrary = refreshLibrary;
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Content = _body;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "GameDetail_Panel");
        AutomationProperties.SetName(this, T("Game details", "Dettagli del gioco"));
        ApplyBrushes();
        ActualThemeChanged += (_, _) => ApplyBrushes();
        KeyDown += (_, e) =>
        {
            // Desktop convenience only: in Console mode Escape/B keeps its shared "leave Console" meaning.
            if (!_console && e.Key == Windows.System.VirtualKey.Escape && Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseRequested?.Invoke();
            }
        };
    }

    internal GameTitleDto? Current => _title;

    internal void SetConsole(bool enabled)
    {
        if (_console == enabled) return;
        _console = enabled;
        if (_title is not null) _ = ShowAsync(_title);
    }

    private void ApplyBrushes()
    {
        Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
        BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
        foreach (var (label, secondary) in _semanticLabels)
            label.Foreground = GameUi.ThemeBrush(this,
                secondary ? "PaRuXTextSecondaryBrush" : "PaRuXTextPrimaryBrush");
        if (_fallbackCoverSurface is not null)
            _fallbackCoverSurface.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        if (_fallbackCoverInitials is not null)
            _fallbackCoverInitials.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
    }

    internal void Hide()
    {
        _generation++;
        _title = null;
        Visibility = Visibility.Collapsed;
        _body.Children.Clear();
        _semanticLabels.Clear();
        _fallbackCoverSurface = null;
        _fallbackCoverInitials = null;
    }

    internal bool FocusPrimary() => _play?.Focus(FocusState.Programmatic) ?? false;

    private double Body => _console ? 20 : 14;
    private double Small => _console ? 17 : 13;
    private double Target => _console ? 56 : 44;

    private TextBlock Label(string text, double? size = null, bool strong = false, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size ?? Body,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GameUi.ThemeBrush(this, secondary ? "PaRuXTextSecondaryBrush" : "PaRuXTextPrimaryBrush")
        };
        if (strong) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _semanticLabels.Add((block, secondary));
        return block;
    }

    /// <summary>Description, genres and developer from the optional online lookup (Steam store or Wikipedia), if it ran.</summary>
    private void AddCatalogueInfo(GameTitleDto title)
    {
        GameInfoDto? info = GameMetadataService.TryGetInfo(title.TitleKey);
        if (info is null) return;
        var facts = new List<string>();
        if (info.Genres.Count > 0) facts.Add(string.Join(", ", info.Genres));
        if (!string.IsNullOrWhiteSpace(info.Developer)) facts.Add(info.Developer);
        if (!string.IsNullOrWhiteSpace(info.Released)) facts.Add(info.Released);
        if (facts.Count > 0)
        {
            TextBlock line = Label(string.Join("  ·  ", facts), Small, secondary: true);
            AutomationProperties.SetAutomationId(line, "GameDetail_Info_Facts");
            _body.Children.Add(line);
        }
        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            TextBlock description = Label(info.Description, Body);
            AutomationProperties.SetAutomationId(description, "GameDetail_Info_Description");
            _body.Children.Add(description);
        }
        string source = info.Source switch
        {
            GameMetadataParsing.SourceSteam => "Steam",
            GameMetadataParsing.SourceIgdb => "IGDB",
            GameMetadataParsing.SourceSteamGridDb => "SteamGridDB",
            _ => "Wikipedia"
        };
        TextBlock credit = Label(T("Source: ", "Fonte: ") + source, Small, secondary: true);
        AutomationProperties.SetAutomationId(credit, "GameDetail_Info_Source");
        _body.Children.Add(credit);
    }

    /// <summary>Shows (or re-shows) a title. Safe to call again; a newer call supersedes the pending entry lookup.</summary>
    internal async Task ShowAsync(GameTitleDto title)
    {
        int generation = ++_generation;
        _title = title;
        _chosen = GameUi.ChooseInstallation(title);
        Visibility = Visibility.Visible;
        _body.Children.Clear();
        _semanticLabels.Clear();
        _fallbackCoverSurface = null;
        _fallbackCoverInitials = null;
        _body.Spacing = _console ? 14 : 10;
        string key = title.TitleKey;

        // header
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(BuildCover(title));
        var heading = Label(title.Name, _console ? 30 : 22, strong: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(heading, "GameDetail_Title");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = _console ? 20 : 14 },
            MinWidth = Target, MinHeight = Target, VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetAutomationId(close, "GameDetail_Close");
        AutomationProperties.SetName(close, T("Close details", "Chiudi i dettagli"));
        ToolTipService.SetToolTip(close, T("Close details", "Chiudi i dettagli"));
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        _body.Children.Add(header);
        AddCatalogueInfo(title);

        // play + start-with
        _play = new Button
        {
            Content = GameUi.PlayLabel(_chosen),
            MinHeight = Target,
            FontSize = Body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = GameUi.CanStart(_chosen)
        };
        if (GameUi.AccentButtonStyle is { } accent) _play.Style = accent;
        AutomationProperties.SetAutomationId(_play, "GameTitle_Play_" + key);
        AutomationProperties.SetName(_play, GameUi.PlayLabel(_chosen) + " " + title.Name);
        _play.Click += async (_, _) => await LaunchAsync();
        _body.Children.Add(_play);

        // A temporarily chosen alternate must never look like a silently saved new default.
        GameInstallationDto? savedDefault = title.Installations.FirstOrDefault(i =>
            string.Equals(i.InstallationId, title.DefaultInstallationId, StringComparison.Ordinal));
        TextBlock? alternateNotice = null;
        if (savedDefault is not null && _chosen is not null &&
            savedDefault.InstallationId != _chosen.InstallationId && GameUi.CanStart(_chosen))
        {
            alternateNotice = Label(
                T("Quick Play will use the available copy below. Your saved default is unchanged.",
                  "Avvio rapido userà la copia disponibile qui sotto. La preferenza salvata non cambia."),
                Small, secondary: true);
            AutomationProperties.SetAutomationId(alternateNotice, "GameDetail_AlternateNotice");
            _body.Children.Add(alternateNotice);
        }

        if (title.Installations.Count > 1)
        {
            var choice = new ComboBox { MinWidth = 260, Header = T("Start with", "Avvia con"), FontSize = Body, MinHeight = Target, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (GameInstallationDto installation in title.Installations)
            {
                string label = GameUi.SourceLabel(installation);
                if (!GameUi.CanStart(installation))
                    label += " · " + GameUi.StateLabel(installation.InstallState);
                choice.Items.Add(new ComboBoxItem { Content = label, Tag = installation });
            }
            int index = _chosen is null ? 0 : title.Installations.ToList().IndexOf(_chosen);
            choice.SelectedIndex = Math.Max(0, index);
            AutomationProperties.SetAutomationId(choice, "GameTitle_Installation_" + key);
            bool restoringSelection = false;
            Button playButton = _play;
            choice.SelectionChanged += async (_, _) =>
            {
                if (restoringSelection || generation != _generation ||
                    (choice.SelectedItem as ComboBoxItem)?.Tag is not GameInstallationDto picked ||
                    picked == _chosen) return;

                // Disable Play while the user preference is being saved. Failure
                // restores the prior selection instead of advertising false success.
                GameInstallationDto? previous = _chosen;
                choice.IsEnabled = false;
                playButton.IsEnabled = false;
                try
                {
                    var result = await GamingOrchestratorClient.Current
                        .SetTitleDefaultInstallationAsync(key, picked.InstallationId);
                    if (generation != _generation) return;
                    if (!result.Succeeded)
                    {
                        restoringSelection = true;
                        try { choice.SelectedIndex = Math.Max(0, title.Installations.ToList().IndexOf(previous)); }
                        finally { restoringSelection = false; }
                        StatusMessage?.Invoke(T("Could not save the preferred installation.",
                            "Impossibile salvare l'installazione preferita."));
                        return;
                    }

                    _chosen = picked;
                    playButton.Content = GameUi.PlayLabel(picked);
                    AutomationProperties.SetName(playButton, GameUi.PlayLabel(picked) + " " + title.Name);
                    if (alternateNotice is not null) alternateNotice.Visibility = Visibility.Collapsed;
                    Apply((_title ?? title) with { DefaultInstallationId = picked.InstallationId });
                }
                finally
                {
                    if (generation == _generation)
                    {
                        choice.IsEnabled = true;
                        playButton.IsEnabled = GameUi.CanStart(_chosen);
                    }
                }
            };
            _body.Children.Add(choice);
        }

        // favorite / hidden
        var states = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var favorite = new ToggleButton
        {
            Content = (title.Favorite ? "★ " : "☆ ") + T("Favorite", "Preferito"),
            IsChecked = title.Favorite, MinHeight = Target, FontSize = Body
        };
        AutomationProperties.SetAutomationId(favorite, "GameTitle_Favorite_" + key);
        AutomationProperties.SetName(favorite, T("Favorite: ", "Preferito: ") + title.Name);
        favorite.Click += async (_, _) =>
        {
            bool wanted = favorite.IsChecked == true;
            var result = await GamingOrchestratorClient.Current.SetTitleFavoriteAsync(key, wanted);
            if (!result.Succeeded)
            {
                favorite.IsChecked = !wanted;
                StatusMessage?.Invoke(T("The favorite could not be saved.", "Impossibile salvare il preferito."));
                return;
            }
            favorite.Content = (wanted ? "★ " : "☆ ") + T("Favorite", "Preferito");
            Apply(GameUi.WithState(_title ?? title, favorite: wanted));
        };
        var hidden = new ToggleButton
        {
            Content = T("Hidden from library", "Nascosto dalla libreria"),
            IsChecked = title.Hidden, MinHeight = Target, FontSize = Body
        };
        AutomationProperties.SetAutomationId(hidden, "GameDetail_Hidden");
        AutomationProperties.SetName(hidden, T("Hide from library: ", "Nascondi dalla libreria: ") + title.Name);
        hidden.Click += async (_, _) =>
        {
            bool wanted = hidden.IsChecked == true;
            var result = await GamingLibraryClient.Current.SetTitleHiddenAsync(key, wanted);
            if (!result.Succeeded)
            {
                hidden.IsChecked = !wanted;
                StatusMessage?.Invoke(T("The hidden state could not be saved.", "Impossibile salvare lo stato nascosto."));
                return;
            }
            Apply(GameUi.WithState(_title ?? title, hidden: wanted));
        };
        states.Children.Add(favorite);
        states.Children.Add(hidden);
        _body.Children.Add(states);

        // facts
        _body.Children.Add(Label(GameUi.PlaySummary(title), Small, secondary: true));
        if (title.AddedUtc is { } added)
            _body.Children.Add(Label(T("Added ", "Aggiunto il ") + added.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentUICulture), Small, secondary: true));

        // installations
        _body.Children.Add(Label(T("Installations", "Installazioni"), Body, strong: true));
        var installations = new StackPanel { Spacing = 4 };
        AutomationProperties.SetAutomationId(installations, "GameDetail_Installations");
        foreach (GameInstallationDto installation in title.Installations)
        {
            string platform = string.IsNullOrWhiteSpace(installation.Platform) ? "" : " · " + installation.Platform;
            installations.Children.Add(Label(
                GameUi.SourceLabel(installation) + platform + " · " + GameUi.StateLabel(installation.InstallState), Small));
        }
        _body.Children.Add(installations);

        // tags + collections
        _body.Children.Add(BuildListEditor(T("Tags (comma separated)", "Tag (separati da virgola)"), "GameTitle_Tags_" + key,
            title.Tags, 400, async list =>
            {
                var result = await GamingOrchestratorClient.Current.SetTitleTagsAsync(key, list);
                if (!result.Succeeded)
                {
                    StatusMessage?.Invoke(T("Tags were not saved: at most 20 tags of up to 40 characters.", "Tag non salvati: al massimo 20 tag da 40 caratteri."));
                    return false;
                }
                Apply(GameUi.WithState(_title ?? title, tags: list));
                return true;
            }));
        _body.Children.Add(BuildListEditor(T("Collections (comma separated)", "Collezioni (separate da virgola)"), "GameTitle_Collections_" + key,
            title.Collections, 500, async list =>
            {
                var result = await GamingOrchestratorClient.Current.SetTitleCollectionsAsync(key, list);
                if (!result.Succeeded)
                {
                    StatusMessage?.Invoke(T("Collections not saved: up to 12 names, each at most 40 characters.", "Collezioni non salvate: massimo 12 nomi da 40 caratteri."));
                    return false;
                }
                Apply(GameUi.WithState(_title ?? title, collections: list));
                return true;
            }));

        // cover
        _body.Children.Add(BuildCoverControls(title));

        // honest label for stores whose discovery has not been checked on a real install
        GameInstallationDto? unverifiedStore = title.Installations.FirstOrDefault(i => GameLibraryHome.IsUnverifiedStore(i.Source));
        if (unverifiedStore is not null)
        {
            TextBlock note = Label(GameLibraryHome.UnverifiedStoreNote(unverifiedStore.Source, GameUi.T("en", "it") == "it"), Small, secondary: true);
            AutomationProperties.SetAutomationId(note, "GameDetail_UnverifiedStoreNote");
            _body.Children.Add(note);
        }

        // save backup / restore and launch helpers (advanced, collapsed by default)
        _body.Children.Add(BuildAdvancedSection(T("Save games: back up and restore", "Salvataggi: backup e ripristino"),
            "GameDetail_Saves_Expander_" + key, new GameSaveBackupPanel(title.Name, "_" + key)));
        _body.Children.Add(BuildAdvancedSection(T("Launch helpers (advanced)", "Helper di avvio (avanzato)"),
            "GameDetail_Hooks_Expander_" + key, new GameHooksSection(key)));

        // launch details of saved entries (read after the shell is visible)
        var entries = new StackPanel { Spacing = 8 };
        _body.Children.Add(entries);
        List<GameInstallationDto> managed = title.Installations
            .Where(i => i.Source is GameInstallationSources.Manual or GameInstallationSources.Emulator).ToList();
        List<GameInstallationDto> stores = title.Installations.Except(managed).ToList();
        foreach (GameInstallationDto store in stores)
            entries.Children.Add(Label(GameUi.SourceLabel(store) + ": " + T(
                "managed by the store. PaRuX starts it through the store and does not edit it.",
                "gestito dallo store. PaRuX lo avvia tramite lo store e non lo modifica."), Small, secondary: true));
        if (managed.Count == 0) return;

        Dictionary<string, ManualGameDto> saved = new(StringComparer.Ordinal);
        try
        {
            var listed = await GamingOrchestratorClient.Current.ListManualGamesAsync();
            if (listed.Succeeded && listed.Value is not null)
                foreach (ManualGameDto game in listed.Value) saved[game.Id] = game;
        }
        catch (Exception) { /* The details below then say the entry could not be read. */ }
        if (generation != _generation) return;
        foreach (GameInstallationDto installation in managed)
            entries.Children.Add(BuildEntryCard(title, installation, saved.GetValueOrDefault(installation.SourceGameId)));
    }

    private UIElement BuildAdvancedSection(string header, string automationId, UIElement content)
    {
        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = Target,
        };
        AutomationProperties.SetAutomationId(expander, automationId);
        AutomationProperties.SetName(expander, header);
        return expander;
    }

    private void Apply(GameTitleDto updated)
    {
        _title = updated;
        Changed?.Invoke(updated);
    }

    private UIElement BuildCover(GameTitleDto title)
    {
        const double size = 88;
        var cover = new Grid { Width = size * (_console ? 1.3 : 1), Height = size * (_console ? 1.3 : 1) };
        var surface = new Border { CornerRadius = new CornerRadius(8), Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush") };
        cover.Children.Add(surface);
        bool image = false;
        string? path = GameCoverStore.TryGetPath(title.TitleKey);
        if (path is not null)
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 260 };
                bitmap.UriSource = new Uri(path);
                surface.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
                image = true;
            }
            catch (Exception) { }
        }
        if (!image)
        {
            var initials = new TextBlock
            {
                Text = GameUi.Initials(title.Name), FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _fallbackCoverSurface = surface;
            _fallbackCoverInitials = initials;
            cover.Children.Add(initials);
        }
        return cover;
    }

    private UIElement BuildCoverControls(GameTitleDto title)
    {
        string key = title.TitleKey;
        var panel = new StackPanel { Spacing = 6 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var status = Label(T("Optional local cover: PNG, JPG or WebP up to 5 MB. A copy is kept inside PaRuX; nothing is downloaded.",
                             "Copertina locale facoltativa: PNG, JPG o WebP fino a 5 MB. Una copia resta dentro PaRuX; non viene scaricato nulla."),
                          Small, secondary: true);
        AutomationProperties.SetAutomationId(status, "GameDetail_Cover_Status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var choose = new Button { Content = T("Choose cover…", "Scegli copertina…"), MinHeight = Target, FontSize = Body };
        AutomationProperties.SetAutomationId(choose, "GameDetail_CoverChoose");
        choose.Click += async (_, _) =>
        {
            string? file = await PickImageAsync();
            if (file is null) return;
            GameCoverResult result = GameCoverStore.Set(key, file);
            if (result == GameCoverResult.Saved)
            {
                status.Text = T("Cover saved.", "Copertina salvata.");
                Apply(_title ?? title);
                if (_title is not null) await ShowAsync(_title);
                return;
            }
            status.Text = result switch
            {
                GameCoverResult.TooLarge => T("That image is larger than 5 MB.", "Quell'immagine supera 5 MB."),
                GameCoverResult.UnsupportedType => T("Only PNG, JPG and WebP images are accepted.", "Sono accettate solo immagini PNG, JPG e WebP."),
                GameCoverResult.NotFound => T("That file could not be found.", "Impossibile trovare il file."),
                _ => T("The cover could not be saved.", "Impossibile salvare la copertina."),
            };
        };
        row.Children.Add(choose);
        if (GameCoverStore.TryGetPath(key) is not null)
        {
            var remove = new Button { Content = T("Remove cover", "Rimuovi copertina"), MinHeight = Target, FontSize = Body };
            AutomationProperties.SetAutomationId(remove, "GameDetail_CoverRemove");
            remove.Click += async (_, _) =>
            {
                if (!GameCoverStore.Remove(key)) { status.Text = T("The cover could not be removed.", "Impossibile rimuovere la copertina."); return; }
                Apply(_title ?? title);
                if (_title is not null) await ShowAsync(_title);
            };
            row.Children.Add(remove);
        }
        panel.Children.Add(row);
        panel.Children.Add(status);
        return panel;
    }

    private static async Task<string?> PickImageAsync()
    {
        var picker = new FileOpenPicker();
        foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private UIElement BuildListEditor(string header, string automationId, IReadOnlyList<string> values, int maxLength,
        Func<List<string>, Task<bool>> save)
    {
        var box = new TextBox
        {
            Header = header, Text = string.Join(", ", values), MaxLength = maxLength,
            FontSize = Body, MinHeight = Target, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(box, automationId);
        string saved = box.Text;
        async Task CommitAsync()
        {
            if (box.Text == saved) return;
            var parsed = (box.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (await save(parsed)) saved = box.Text ?? string.Empty;
        }
        box.LostFocus += async (_, _) => await CommitAsync();
        var keyboard = ConsoleKeyboardFlyout.Create(box, T("Done", "Fine"), T("Backspace", "Cancella"), T("Clear", "Svuota"), T("Space", "Spazio"));
        box.KeyDown += async (_, e) =>
        {
            if (_console && e.Key is Windows.System.VirtualKey.GamepadA or Windows.System.VirtualKey.GamepadY)
            {
                keyboard.ShowAt(box);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await CommitAsync();
            }
        };
        return box;
    }

    private async Task LaunchAsync()
    {
        if (_launching || _chosen is null || _play is null) return;
        _launching = true;
        _play.IsEnabled = false;
        try { await _launch(_chosen); }
        finally
        {
            _launching = false;
            if (_play is not null) _play.IsEnabled = GameUi.CanStart(_chosen);
        }
    }

    // ------------------------------------------------------------------ saved entry (program, arguments, emulator)

    private UIElement BuildEntryCard(GameTitleDto title, GameInstallationDto installation, ManualGameDto? entry)
    {
        string id = installation.SourceGameId;
        var content = new StackPanel { Spacing = 6 };
        var expander = new Expander
        {
            Header = T("Launch details · ", "Dettagli di avvio · ") + GameUi.SourceLabel(installation),
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "GameDetail_LaunchDetails_" + id);

        if (entry is null)
        {
            content.Children.Add(Label(T("This library entry could not be read. Refresh the library and try again.",
                                         "Impossibile leggere questa voce della libreria. Aggiorna la libreria e riprova."), Small));
            return expander;
        }

        void Show()
        {
            content.Children.Clear();
            content.Children.Add(Fact(T("Program", "Programma"), entry.ExecutablePath));
            content.Children.Add(Fact(T("Arguments", "Argomenti"), string.IsNullOrWhiteSpace(entry.Arguments) ? T("(none)", "(nessuno)") : entry.Arguments));
            content.Children.Add(Fact(T("Working folder", "Cartella di lavoro"),
                string.IsNullOrWhiteSpace(entry.WorkingDirectory) ? T("(the program's folder)", "(la cartella del programma)") : entry.WorkingDirectory));
            if (!string.IsNullOrWhiteSpace(entry.EmulatorId))
                content.Children.Add(Fact(T("Emulator", "Emulatore"),
                    (string.IsNullOrWhiteSpace(installation.EmulatorName) ? entry.EmulatorId : installation.EmulatorName)
                    + (string.IsNullOrWhiteSpace(entry.Platform) ? "" : " · " + entry.Platform)));
            if (!string.IsNullOrWhiteSpace(entry.GameFilePath))
                content.Children.Add(Fact(T("Game file", "File di gioco"), entry.GameFilePath));
            content.Children.Add(Label(T("PaRuX always asks for confirmation before it starts this program.",
                                         "PaRuX chiede sempre conferma prima di avviare questo programma."), Small, secondary: true));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var edit = new Button { Content = T("Edit entry", "Modifica voce"), MinHeight = Target, FontSize = Body };
            AutomationProperties.SetAutomationId(edit, "GameDetail_Edit_" + id);
            edit.Click += (_, _) => ShowEditor();
            var remove = new Button { Content = T("Remove from library", "Rimuovi dalla libreria"), MinHeight = Target, FontSize = Body };
            AutomationProperties.SetAutomationId(remove, "GameDetail_Remove_" + id);
            remove.Click += async (_, _) => await RemoveAsync(title, entry);
            actions.Children.Add(edit);
            actions.Children.Add(remove);
            content.Children.Add(actions);
        }

        void ShowEditor()
        {
            content.Children.Clear();
            TextBox name = Field(T("Name", "Nome"), entry.Name, 200, "GameDetail_Edit_Name_" + id);
            TextBox exe = Field(T("Program (.exe)", "Programma (.exe)"), entry.ExecutablePath, 1024, "GameDetail_Edit_Path_" + id);
            TextBox args = Field(T("Arguments", "Argomenti"), entry.Arguments, 1024, "GameDetail_Edit_Arguments_" + id);
            TextBox work = Field(T("Working folder", "Cartella di lavoro"), entry.WorkingDirectory, 1024, "GameDetail_Edit_Folder_" + id);
            var status = Label(string.Empty, Small);
            AutomationProperties.SetAutomationId(status, "GameDetail_Edit_Status_" + id);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var save = new Button { Content = T("Save", "Salva"), MinHeight = Target, FontSize = Body };
            if (GameUi.AccentButtonStyle is { } accent) save.Style = accent;
            AutomationProperties.SetAutomationId(save, "GameDetail_Edit_Save_" + id);
            var cancel = new Button { Content = T("Cancel", "Annulla"), MinHeight = Target, FontSize = Body };
            AutomationProperties.SetAutomationId(cancel, "GameDetail_Edit_Cancel_" + id);
            cancel.Click += (_, _) => Show();
            save.Click += async (_, _) =>
            {
                save.IsEnabled = false;
                try
                {
                    var result = await GamingLibraryClient.Current.UpdateManualGameAsync(id, new UpdateManualGameRequest(
                        name.Text.Trim(), exe.Text.Trim(), args.Text, work.Text.Trim(), entry.EmulatorId, entry.Platform));
                    if (!result.Succeeded || result.Value is null)
                    {
                        status.Text = result.Error ?? T("The entry could not be saved.", "Impossibile salvare la voce.");
                        return;
                    }
                    entry = result.Value;
                    Show();
                    StatusMessage?.Invoke(T("Entry saved.", "Voce salvata."));
                    await _refreshLibrary();
                    Changed?.Invoke(_title ?? title);
                }
                finally { save.IsEnabled = true; }
            };
            foreach (UIElement element in new UIElement[] { name, exe, args, work, status })
                content.Children.Add(element);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(save);
            row.Children.Add(cancel);
            content.Children.Add(row);
        }

        Show();
        return expander;
    }

    private TextBox Field(string header, string value, int maxLength, string automationId)
    {
        var box = new TextBox { Header = header, Text = value, MaxLength = maxLength, FontSize = Body, MinHeight = Target, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private UIElement Fact(string name, string value)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(Label(name, Small, secondary: true));
        var text = Label(value, Body);
        text.IsTextSelectionEnabled = true;
        panel.Children.Add(text);
        return panel;
    }

    private async Task RemoveAsync(GameTitleDto title, ManualGameDto entry)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Remove from library?", "Rimuovere dalla libreria?"),
            Content = T($"\"{entry.Name}\" will be removed from the PaRuX library only. The game files on this PC are not deleted.",
                        $"\"{entry.Name}\" verrà rimosso solo dalla libreria di PaRuX. I file del gioco su questo PC non vengono eliminati."),
            PrimaryButtonText = T("Remove", "Rimuovi"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = await GamingOrchestratorClient.Current.RemoveManualGameAsync(entry.Id);
        if (!result.Succeeded)
        {
            StatusMessage?.Invoke(T("The entry could not be removed.", "Impossibile rimuovere la voce."));
            return;
        }
        StatusMessage?.Invoke(T("Removed from the library. Game files were not touched.", "Rimosso dalla libreria. I file del gioco non sono stati toccati."));
        await _refreshLibrary();
        Removed?.Invoke(title.TitleKey);
    }
}
