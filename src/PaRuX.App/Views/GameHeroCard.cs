using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// The hero of the Games home: the game the user played last, large, with one-click Play and Details. It only appears when
/// the library has real play history (it never invents a recommendation). Play raises <see cref="PlayRequested"/> and the host
/// runs the same launch-consent flow as every tile; nothing starts by itself.
/// </summary>
internal sealed class GameHeroCard : UserControl
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly Border _frame = new();
    private readonly Border _cover = new() { Width = 132, Height = 176, CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _kicker = new() { FontSize = 13, Opacity = 0.8 };
    private readonly TextBlock _name = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 13 };
    private readonly Button _play = new() { MinHeight = 44, MinWidth = 140 };
    private readonly Button _details = new() { MinHeight = 44, MinWidth = 120 };
    private GameTitleDto? _title;

    internal event Action<GameTitleDto>? PlayRequested;
    internal event Action<GameTitleDto>? DetailsRequested;

    internal GameHeroCard()
    {
        Visibility = Visibility.Collapsed;
        Content = _frame;
        _frame.CornerRadius = new CornerRadius(14);
        _frame.BorderThickness = new Thickness(1);
        _frame.Padding = new Thickness(20);
        AutomationProperties.SetAutomationId(this, "GameHome_Hero");
        AutomationProperties.SetName(this, T("Continue playing", "Continua a giocare"));

        _kicker.Text = T("CONTINUE PLAYING", "CONTINUA A GIOCARE");
        AutomationProperties.SetAutomationId(_name, "GameHome_Hero_Title");
        AutomationProperties.SetHeadingLevel(_name, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(_summary, "GameHome_Hero_Summary");
        AutomationProperties.SetAutomationId(_source, "GameHome_Hero_Source");
        AutomationProperties.SetAutomationId(_play, "GameHome_Hero_Play");
        AutomationProperties.SetAutomationId(_details, "GameHome_Hero_Details");
        _details.Content = T("Details", "Dettagli");
        if (GameUi.AccentButtonStyle is { } accent) _play.Style = accent;
        _play.Click += (_, _) => { if (_title is not null) PlayRequested?.Invoke(_title); };
        _details.Click += (_, _) => { if (_title is not null) DetailsRequested?.Invoke(_title); };

        var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_kicker);
        text.Children.Add(_name);
        text.Children.Add(_summary);
        text.Children.Add(_source);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_play);
        buttons.Children.Add(_details);
        text.Children.Add(buttons);

        var layout = new Grid { ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(_cover);
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        _frame.Child = layout;
        ApplyBrushes();
        ActualThemeChanged += (_, _) => ApplyBrushes();
    }

    private void ApplyBrushes()
    {
        _frame.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
        _frame.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
        _kicker.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        _name.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _summary.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        _source.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        if (_cover.Child is null && _cover.Background is not ImageBrush) _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
    }

    internal void SetConsole(bool enabled)
    {
        _name.FontSize = enabled ? 38 : 28;
        _summary.FontSize = enabled ? 20 : 14;
        _source.FontSize = enabled ? 17 : 13;
        _kicker.FontSize = enabled ? 17 : 13;
        _play.MinHeight = _details.MinHeight = enabled ? 56 : 44;
        _play.FontSize = _details.FontSize = enabled ? 20 : 14;
        _cover.Width = enabled ? 172 : 132;
        _cover.Height = enabled ? 230 : 176;
    }

    internal GameTitleDto? Current => _title;

    internal bool FocusPlay() => _play.Focus(FocusState.Programmatic);

    /// <summary>Shows the title (or hides the hero for null). The cover is the user's or the cached one; the lettered fallback otherwise.</summary>
    internal void Show(GameTitleDto? title)
    {
        _title = title;
        if (title is null) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        _name.Text = title.Name;
        _summary.Text = GameUi.PlaySummary(title);
        _source.Text = GameUi.SourceSummary(title);
        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        _play.Content = GameUi.PlayLabel(chosen);
        _play.IsEnabled = GameUi.CanStart(chosen);
        AutomationProperties.SetName(_play, GameUi.PlayLabel(chosen) + " " + title.Name);
        AutomationProperties.SetName(_details, T("Details of ", "Dettagli di ") + title.Name);

        _cover.Child = null;
        _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        string? path = GameCoverStore.TryGetPath(title.TitleKey);
        bool image = false;
        if (path is not null)
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 300 };
                bitmap.UriSource = new Uri(path);
                _cover.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
                image = true;
            }
            catch (Exception) { /* a damaged cover falls back to the lettered tile */ }
        }
        if (!image)
            _cover.Child = new TextBlock
            {
                Text = GameUi.Initials(title.Name),
                FontSize = 40,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush")
            };
    }
}
