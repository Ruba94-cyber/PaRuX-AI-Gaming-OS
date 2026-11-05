using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaRuX.Contracts;

namespace PaRuX.Views;

/// <summary>
/// The Playnite / Hydra style filter rail of the Games home: library views (all, favourites, recently played), install state,
/// store and genre, each with a count. It owns no state of its own; the home passes the facets and the active filter in and
/// receives the user's choice back, so the toolbar and this rail can never disagree. Every entry has a stable AutomationId and
/// is an ordinary toggle button (keyboard, controller and screen-reader reachable, at least 44 px tall).
/// </summary>
internal sealed class GameSideFilterPanel : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly StackPanel _entries = new() { Spacing = 4 };
    private readonly List<ToggleButton> _buttons = new();
    private bool _console;
    private bool _updating;

    internal event Action<string>? StoreChosen;          // "" = all stores
    internal event Action<string>? InstalledChosen;      // "", "installed", "notinstalled"
    internal event Action<bool>? FavoritesChosen;
    internal event Action<bool>? RecentChosen;
    internal event Action<string>? GenreChosen;          // "" = all genres
    /// <summary>Raised once after the choice events of one click, so the host reloads exactly once.</summary>
    internal event Action? Applied;

    internal GameSideFilterPanel()
    {
        Spacing = 6;
        AutomationProperties.SetAutomationId(this, "GameSide_Panel");
        AutomationProperties.SetName(this, T("Library filters", "Filtri della libreria"));
        var heading = new TextBlock { Text = T("Filters", "Filtri"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 18 };
        AutomationProperties.SetAutomationId(heading, "GameSide_Heading");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        Children.Add(heading);
        Children.Add(_entries);
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
    }

    internal void SetConsole(bool enabled)
    {
        _console = enabled;
        foreach (ToggleButton b in _buttons) { b.FontSize = enabled ? 20 : 14; b.MinHeight = enabled ? 56 : 44; }
    }

    /// <summary>Rebuilds the entries from the facets. Called after every successful load; selection is shown from <paramref name="filter"/>.</summary>
    internal void Update(GameHomeFacets facets, GameHomeFilter filter)
    {
        _updating = true;
        try
        {
            _entries.Children.Clear();
            _buttons.Clear();
            Section(T("Library", "Libreria"));
            Entry("GameSide_All", T("All games", "Tutti i giochi"), facets.Total, filter.IsEmpty, on => { ClearAll(); });
            Entry("GameSide_Favorites", "★ " + T("Favorites", "Preferiti"), facets.Favorites, filter.FavoritesOnly, on => FavoritesChosen?.Invoke(on));
            Entry("GameSide_Recent", T("Recently played", "Giocati di recente"), facets.RecentlyPlayed, filter.RecentlyPlayedOnly, on => RecentChosen?.Invoke(on));
            Section(T("Install state", "Stato installazione"));
            Entry("GameSide_Installed", T("Installed", "Installati"), facets.Installed, filter.Installed == true, on => InstalledChosen?.Invoke(on ? "installed" : ""));
            Entry("GameSide_NotInstalled", T("Not installed", "Non installati"), facets.NotInstalled, filter.Installed == false, on => InstalledChosen?.Invoke(on ? "notinstalled" : ""));
            if (facets.Stores.Count > 0)
            {
                Section(T("Store", "Store"));
                foreach (var store in facets.Stores)
                {
                    string source = store.Key;
                    string label = StoreLabel(source) + (GameLibraryHome.IsUnverifiedStore(source) ? " · " + T("unverified", "non verificato") : "");
                    Entry("GameSide_Store_" + source, label, store.Value, filter.Source == source, on => StoreChosen?.Invoke(on ? source : ""));
                }
            }
            if (facets.Genres.Count > 0)
            {
                Section(T("Genre", "Genere"));
                foreach (var genre in facets.Genres)
                {
                    string name = genre.Key;
                    Entry("GameSide_Genre_" + IdPart(name), name, genre.Value,
                        string.Equals(filter.Genre, name, StringComparison.CurrentCultureIgnoreCase), on => GenreChosen?.Invoke(on ? name : ""));
                }
            }
        }
        finally { _updating = false; }
        SetConsole(_console);
    }

    private void ClearAll()
    {
        StoreChosen?.Invoke("");
        InstalledChosen?.Invoke("");
        FavoritesChosen?.Invoke(false);
        RecentChosen?.Invoke(false);
        GenreChosen?.Invoke("");
    }

    /// <summary>Genre ids are letters and digits only so they stay stable and valid whatever the catalogue returns.</summary>
    internal static string IdPart(string text) => new string(text.Where(char.IsLetterOrDigit).ToArray());

    internal static string StoreLabel(string source) => source switch
    {
        GameInstallationSources.Steam => "Steam",
        GameInstallationSources.Epic => "Epic Games",
        GameInstallationSources.Gog => "GOG",
        GameInstallationSources.Ea => "EA app",
        GameInstallationSources.BattleNet => "Battle.net",
        GameInstallationSources.Ubisoft => "Ubisoft Connect",
        GameInstallationSources.Xbox => "Xbox",
        GameInstallationSources.Emulator => T("Emulator", "Emulatore"),
        _ => T("Local on this PC", "Locale su questo PC"),
    };

    private void Section(string text)
    {
        var block = new TextBlock { Text = text, Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0), FontSize = _console ? 17 : 12 };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level4);
        _entries.Children.Add(block);
    }

    private void Entry(string id, string label, int count, bool selected, Action<bool> chosen)
    {
        var button = new ToggleButton
        {
            Content = $"{label}  ({count})",
            IsChecked = selected,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 44,
        };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, $"{label}, {count}");
        button.Click += (_, _) =>
        {
            if (_updating) return;
            chosen(button.IsChecked == true);
            Applied?.Invoke();
        };
        _buttons.Add(button);
        _entries.Children.Add(button);
    }
}
