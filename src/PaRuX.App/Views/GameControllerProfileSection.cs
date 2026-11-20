using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Controller feel for one game: dead zones, trigger threshold and stick inversion. A game that has its own settings uses them;
/// every other game follows the shared profile from the Devices page. Removing the game's own settings goes back to the shared one.
/// </summary>
internal sealed class GameControllerProfileSection : StackPanel
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private readonly string _key;
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap };
    private readonly NumberBox _left = new() { Minimum = 0, Maximum = GamingControllerProfile.MaxDeadzone, SmallChange = 250, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _right = new() { Minimum = 0, Maximum = GamingControllerProfile.MaxDeadzone, SmallChange = 250, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _trigger = new() { Minimum = 0, Maximum = 255, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly CheckBox _invertLeft = new(), _invertRight = new();
    private readonly Button _save = new(), _remove = new();
    private GamingControllerProfile _shown = GamingControllerProfile.Default;

    public GameControllerProfileSection(string titleKey)
    {
        _key = titleKey;
        Spacing = 8;
        AutomationProperties.SetAutomationId(_source, "GameController_Source_" + _key);
        AutomationProperties.SetLiveSetting(_source, AutomationLiveSetting.Polite);
        Children.Add(new TextBlock
        {
            Text = T("Change how this game reads your controller. Games without their own settings follow the shared profile on the Devices page.",
                     "Cambia come questo gioco legge il controller. I giochi senza impostazioni proprie seguono il profilo condiviso nella pagina Dispositivi."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        Children.Add(_source);

        _left.Header = T("Left stick dead zone", "Zona morta stick sinistro");
        _right.Header = T("Right stick dead zone", "Zona morta stick destro");
        _trigger.Header = T("Trigger threshold (0-255)", "Soglia dei grilletti (0-255)");
        _invertLeft.Content = T("Invert left stick vertical", "Inverti verticale stick sinistro");
        _invertRight.Content = T("Invert right stick vertical", "Inverti verticale stick destro");
        Name(_left, "GameController_LeftDeadzone_" + _key);
        Name(_right, "GameController_RightDeadzone_" + _key);
        Name(_trigger, "GameController_Trigger_" + _key);
        Name(_invertLeft, "GameController_InvertLeft_" + _key);
        Name(_invertRight, "GameController_InvertRight_" + _key);
        foreach (UIElement e in new UIElement[] { _left, _right, _trigger, _invertLeft, _invertRight }) Children.Add(e);

        _save.Content = T("Use these settings for this game", "Usa queste impostazioni per questo gioco");
        _remove.Content = T("Go back to the shared profile", "Torna al profilo condiviso");
        AutomationProperties.SetAutomationId(_save, "GameController_Save_" + _key);
        AutomationProperties.SetAutomationId(_remove, "GameController_Remove_" + _key);
        _save.Click += async (_, _) => await SaveAsync();
        _remove.Click += async (_, _) => await RemoveAsync();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_save);
        row.Children.Add(_remove);
        Children.Add(row);
        Loaded += async (_, _) => await LoadAsync();
    }

    private static void Name(FrameworkElement e, string id)
    {
        AutomationProperties.SetAutomationId(e, id);
        if (e is NumberBox box && box.Header is string h) AutomationProperties.SetName(e, h);
    }

    private async Task LoadAsync()
    {
        var result = await GamingOrchestratorClient.Current.GetGameControllerProfileAsync(_key);
        if (!result.Succeeded || result.Value is null)
        {
            _source.Text = T("The controller settings are unavailable right now.", "Le impostazioni del controller non sono disponibili ora.");
            return;
        }
        Show(result.Value);
    }

    private void Show(GameControllerProfileEnvelope envelope)
    {
        _shown = envelope.Profile;
        _left.Value = envelope.Profile.LeftDeadzone;
        _right.Value = envelope.Profile.RightDeadzone;
        _trigger.Value = envelope.Profile.TriggerThreshold;
        _invertLeft.IsChecked = envelope.Profile.InvertLeftY;
        _invertRight.IsChecked = envelope.Profile.InvertRightY;
        bool own = envelope.Source == "game";
        _source.Text = own
            ? T("This game has its own controller settings.", "Questo gioco ha impostazioni del controller proprie.")
            : T("This game follows the shared profile.", "Questo gioco segue il profilo condiviso.");
        _remove.IsEnabled = own;
    }

    private static int Pick(double value, int fallback, int max) => (int)Math.Clamp(double.IsNaN(value) ? fallback : value, 0, max);

    private async Task SaveAsync()
    {
        var profile = _shown with
        {
            LeftDeadzone = Pick(_left.Value, GamingControllerProfile.DefaultLeftDeadzone, GamingControllerProfile.MaxDeadzone),
            RightDeadzone = Pick(_right.Value, GamingControllerProfile.DefaultRightDeadzone, GamingControllerProfile.MaxDeadzone),
            TriggerThreshold = Pick(_trigger.Value, GamingControllerProfile.DefaultTriggerThreshold, 255),
            InvertLeftY = _invertLeft.IsChecked == true,
            InvertRightY = _invertRight.IsChecked == true,
        };
        var result = await GamingOrchestratorClient.Current.SaveGameControllerProfileAsync(_key, profile);
        if (!result.Succeeded || result.Value is null)
        {
            _source.Text = T("These settings were not accepted. Check the values and try again.", "Queste impostazioni non sono state accettate. Controlla i valori e riprova.");
            return;
        }
        Show(result.Value);
    }

    private async Task RemoveAsync()
    {
        await GamingOrchestratorClient.Current.RemoveGameControllerProfileAsync(_key);
        await LoadAsync();
    }
}
