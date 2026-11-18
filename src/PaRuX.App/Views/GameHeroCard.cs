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
    private readonly Border _backdrop = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14) };
    private readonly Border _scrim = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14) };
    private readonly Border _cover = new() { Width = 200, Height = 250, CornerRadius = new CornerRadius(10) };
    private readonly Grid _layout = new() { ColumnSpacing = 24, RowSpacing = 16, Margin = new Thickness(24) };
    private readonly StackPanel _text = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapRowPanel _buttons = new() { ColumnSpacing = 10, RowSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
    private bool _console;
    private bool _compact;
    private bool _stacked;
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

        _text.Children.Add(_kicker);
        _text.Children.Add(_name);
        _text.Children.Add(_summary);
        _text.Children.Add(_source);
        _buttons.Children.Add(_play);
        _buttons.Children.Add(_details);
        _text.Children.Add(_buttons);

        _layout.Children.Add(_cover);
        _layout.Children.Add(_text);
        _frame.Child = new Grid { Children = { _backdrop, _scrim, _layout } };
        UpdateLayoutMode(false, false);
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 560;
            bool stacked = e.NewSize.Width < 380;
            if (compact != _compact || stacked != _stacked) UpdateLayoutMode(compact, stacked);
        };
        ApplyBrushes();
        ActualThemeChanged += (_, _) => ApplyBrushes();
    }

    private void UpdateLayoutMode(bool compact, bool stacked)
    {
        _compact = compact;
        _stacked = stacked;
        _layout.Margin = new Thickness(compact ? 16 : 24);
        _layout.ColumnSpacing = compact ? 14 : 24;
        _layout.ColumnDefinitions.Clear();
        _layout.RowDefinitions.Clear();
        if (stacked)
        {
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(_cover, 0);
            Grid.SetRow(_cover, 0);
            Grid.SetColumn(_text, 0);
            Grid.SetRow(_text, 1);
        }
        else
        {
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_cover, 0);
            Grid.SetColumn(_cover, 0);
            Grid.SetRow(_text, 0);
            Grid.SetColumn(_text, 1);
        }
        _cover.HorizontalAlignment = HorizontalAlignment.Left;
        _cover.Width = stacked ? 120 : compact ? 132 : _console ? 228 : 192;
        _cover.Height = _cover.Width * 4 / 3;
        _name.FontSize = compact ? 26 : _console ? 40 : 34;
    }

    private void ApplyBrushes()
    {
        _frame.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
        _frame.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
        UpdateBackdropTheme();
        _kicker.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        _name.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _summary.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        _source.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        if (_cover.Background is not ImageBrush)
        {
            _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
            if (_cover.Child is TextBlock initials) initials.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        }
    }

    private void UpdateBackdropTheme()
    {
        bool highContrast = GameUi.IsHighContrast(this);
        _backdrop.Visibility = _scrim.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        _backdrop.Opacity = ActualTheme == ElementTheme.Light ? 0.14 : 0.30;
        if (_frame.Background is not SolidColorBrush surface) return;

        var fadingSurface = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        fadingSurface.GradientStops.Add(new GradientStop { Color = surface.Color, Offset = 0.44 });
        fadingSurface.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0, surface.Color.R, surface.Color.G, surface.Color.B), Offset = 1
        });
        _scrim.Background = fadingSurface;
    }

    internal void SetConsole(bool enabled)
    {
        _console = enabled;
        _summary.FontSize = enabled ? 20 : 14;
        _source.FontSize = enabled ? 17 : 13;
        _kicker.FontSize = enabled ? 17 : 13;
        _play.MinHeight = _details.MinHeight = enabled ? 56 : 44;
        _play.FontSize = _details.FontSize = enabled ? 20 : 14;
        UpdateLayoutMode(_compact, _stacked);
    }

    internal GameTitleDto? Current => _title;

    internal bool FocusPlay() => _play.Focus(FocusState.Programmatic);

    /// <summary>Shows the title (or hides the hero for null). The cover is the user's or the cached one; the lettered fallback otherwise.</summary>
    internal void Show(GameTitleDto? title)
    {
        _title = title;
        if (title is null)
        {
            Visibility = Visibility.Collapsed;
            _cover.Background = _backdrop.Background = null;
            return;
        }
        Visibility = Visibility.Visible;
        _name.Text = title.Name;
        _summary.Text = GameUi.PlaySummary(title);
        _source.Text = GameUi.SourceSummary(title);
        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        _play.Content = GameUi.PlayLabel(chosen);
        _play.IsEnabled = GameUi.CanStart(chosen);
        AutomationProperties.SetName(_play, GameUi.PlayLabel(chosen) + " " + title.Name);
        AutomationProperties.SetName(_details, T("Details of ", "Dettagli di ") + title.Name);

        ShowFallback(title.Name);
        string? path = GameCoverStore.TryGetPath(title.TitleKey);
        if (path is null) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            bitmap.ImageFailed += (_, _) =>
            {
                // A recycled hero must not replace the new title's cover after an old decode fails.
                if (_cover.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, bitmap))
                    ShowFallback(_title?.Name ?? string.Empty);
            };
            _cover.Child = null;
            _cover.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            _backdrop.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            bitmap.UriSource = new Uri(path);
        }
        catch (Exception) { ShowFallback(title.Name); }
    }

    private void ShowFallback(string name)
    {
        _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        _backdrop.Background = null;
        _cover.Child = new TextBlock
        {
            Text = GameUi.Initials(name),
            FontSize = 48,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush")
        };
    }
}
