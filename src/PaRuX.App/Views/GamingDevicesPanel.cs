using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Views;

/// <summary>
/// Display-mode change and controller-profile panel of the Games workspace. Every action is explicit:
/// a display change is temporary until confirmed (the Core reverts it by itself), calibration samples
/// the resting sticks once, and nothing here polls. Timers exist only while a display change is pending
/// or while the owner-started virtual-controller forwarding is running.
/// </summary>
internal sealed class GamingDevicesPanel : StackPanel
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private readonly ComboBox _modes = new() { MinWidth = 280 };
    private readonly Button _loadModes = new();
    private readonly Button _apply = new();
    private readonly Button _confirm = new();
    private readonly Button _revert = new();
    private readonly TextBlock _displayStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _displayId;
    private int _secondsLeft;

    private readonly NumberBox _leftDeadzone = new() { Minimum = 0, Maximum = GamingControllerProfile.MaxDeadzone, SmallChange = 250, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _rightDeadzone = new() { Minimum = 0, Maximum = GamingControllerProfile.MaxDeadzone, SmallChange = 250, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _trigger = new() { Minimum = 0, Maximum = 254, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _slot = new() { Minimum = 1, Maximum = 4, Value = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly CheckBox _invertLeft = new();
    private readonly CheckBox _invertRight = new();
    private readonly CheckBox _swapAb = new();
    private readonly Button _save = new();
    private readonly Button _reset = new();
    private readonly TextBlock _controllerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _rawDevices = new() { MinWidth = 280 };
    private readonly TextBlock _rawDeviceStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly NumberBox _rawA = RawIndex(0);
    private readonly NumberBox _rawB = RawIndex(1);
    private readonly NumberBox _rawLx = RawIndex(-1);
    private readonly NumberBox _rawLy = RawIndex(-1);
    private readonly NumberBox _rawRx = RawIndex(-1);
    private readonly NumberBox _rawRy = RawIndex(-1);
    private readonly NumberBox _rawLt = RawIndex(-1);
    private readonly NumberBox _rawRt = RawIndex(-1);
    private readonly NumberBox _rawDeadzone = new() { Minimum = 0, Maximum = 0.6, Value = 0.1, SmallChange = 0.02, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly CheckBox _rawInvertY = new() { IsChecked = true };
    private readonly CheckBox _rawDpad = new() { IsChecked = true };

    // Full mapping (beyond A/B): every Xbox button, digital triggers, X inversion and where the hat goes.
    private readonly NumberBox _rawX = RawIndex(-1);
    private readonly NumberBox _rawY = RawIndex(-1);
    private readonly NumberBox _rawLb = RawIndex(-1);
    private readonly NumberBox _rawRb = RawIndex(-1);
    private readonly NumberBox _rawBack = RawIndex(-1);
    private readonly NumberBox _rawStart = RawIndex(-1);
    private readonly NumberBox _rawL3 = RawIndex(-1);
    private readonly NumberBox _rawR3 = RawIndex(-1);
    private readonly NumberBox _rawGuide = RawIndex(-1);
    private readonly NumberBox _rawLtButton = RawIndex(-1);
    private readonly NumberBox _rawRtButton = RawIndex(-1);
    private readonly CheckBox _rawInvertX = new() { IsChecked = false };
    private readonly ComboBox _rawHatTarget = new();
    private readonly ComboBox _gameChoice = new() { MinWidth = 260 };

    // Guided virtual-controller activation.
    private readonly CheckBox _hideConsent = new();
    private readonly TextBlock _guidedStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepDevice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepDriver = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepIsolation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepTest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly HyperlinkButton _driverLink = new() { Visibility = Visibility.Collapsed };
    private readonly HyperlinkButton _hidHideLink = new() { Visibility = Visibility.Collapsed };
    private Button? _startForwarding, _stopForwarding, _rearmForwarding;
    private readonly DispatcherTimer _guidedTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private (NumberBox Box, ushort Bit)[] ButtonBoxes => new[]
    {
        (_rawA, Xbox360Report.A), (_rawB, Xbox360Report.B), (_rawX, Xbox360Report.X), (_rawY, Xbox360Report.Y),
        (_rawLb, Xbox360Report.LeftShoulder), (_rawRb, Xbox360Report.RightShoulder),
        (_rawBack, Xbox360Report.Back), (_rawStart, Xbox360Report.Start),
        (_rawL3, Xbox360Report.LeftThumb), (_rawR3, Xbox360Report.RightThumb), (_rawGuide, Xbox360Report.Guide)
    };

    private static NumberBox RawIndex(int value) => new()
    {
        Minimum = -1, Maximum = 63, Value = value, SmallChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };
    private static int? SelectedIndex(NumberBox n) =>
        double.IsNaN(n.Value) || n.Value < 0 ? null : (int)Math.Clamp(Math.Round(n.Value), 0, 63);

    public GamingDevicesPanel()
    {
        Spacing = 8;
        BuildDisplaySection();
        BuildControllerSection();
        _countdown.Tick += async (_, _) => await OnCountdownAsync();
        _guidedTimer.Tick += (_, _) => RefreshGuided();
        Loaded += (_, _) =>
        {
            VirtualPadActivationController.Current.Changed += OnPadStateChanged;
            RefreshGuided();
        };
        Unloaded += (_, _) =>
        {
            _countdown.Stop();
            _guidedTimer.Stop();
            VirtualPadActivationController.Current.Changed -= OnPadStateChanged;
        };
    }

    private void OnPadStateChanged() => DispatcherQueue?.TryEnqueue(RefreshGuided);

    private static Button MakeButton(string text, string id, Func<Task> action)
    {
        var b = new Button { Content = text, MinHeight = 36 };
        AutomationProperties.SetAutomationId(b, id);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    private void BuildDisplaySection()
    {
        Children.Add(new TextBlock
        {
            Text = T("Display mode (resolution and refresh rate)", "Modo schermo (risoluzione e frequenza)"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = T("A change is temporary: confirm it, or PaRuX restores the previous mode after 15 seconds. HDR and VRR are never changed.",
                     "La modifica è temporanea: confermala, altrimenti PaRuX ripristina il modo precedente dopo 15 secondi. HDR e VRR non vengono mai modificati."),
            TextWrapping = TextWrapping.Wrap
        });
        AutomationProperties.SetAutomationId(_modes, "GameDevices_DisplayModes_ComboBox");
        AutomationProperties.SetName(_modes, T("Available display modes", "Modi schermo disponibili"));

        var loadModes = MakeButton(T("Load primary display modes", "Carica i modi dello schermo principale"),
            "GameDevices_DisplayModes_Load_Button", LoadModesAsync);
        var apply = MakeButton(T("Apply temporarily", "Applica temporaneamente"), "GameDevices_Display_Apply_Button", ApplyAsync);
        var confirm = MakeButton(T("Keep this mode", "Mantieni questo modo"), "GameDevices_Display_Confirm_Button", ConfirmAsync);
        var revert = MakeButton(T("Revert now", "Ripristina ora"), "GameDevices_Display_Revert_Button", RevertAsync);
        confirm.Visibility = Visibility.Collapsed;
        revert.Visibility = Visibility.Collapsed;
        _confirmButton = confirm;
        _revertButton = revert;
        _applyButton = apply;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(loadModes);
        row.Children.Add(_modes);
        row.Children.Add(apply);
        row.Children.Add(confirm);
        row.Children.Add(revert);
        Children.Add(row);
        AutomationProperties.SetAutomationId(_displayStatus, "GameDevices_Display_Status_Text");
        AutomationProperties.SetLiveSetting(_displayStatus, AutomationLiveSetting.Polite);
        Children.Add(_displayStatus);
    }

    private Button? _confirmButton, _revertButton, _applyButton;

    private async Task LoadModesAsync()
    {
        _displayStatus.Text = T("Reading display modes…", "Lettura dei modi schermo…");
        try
        {
            var topology = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            var primary = topology.Value?.Displays.FirstOrDefault(d => d.Primary) ?? topology.Value?.Displays.FirstOrDefault();
            if (!topology.Succeeded || primary is null)
            {
                _displayStatus.Text = T("Windows display information is unavailable.", "Informazioni sugli schermi Windows non disponibili.");
                return;
            }
            var modes = await GamingOrchestratorClient.Current.GetDisplayModesAsync(primary.DisplayId);
            if (!modes.Succeeded || modes.Value is not { Count: > 0 } list)
            {
                _displayStatus.Text = T("The driver did not list selectable modes.", "Il driver non ha elencato modi selezionabili.");
                return;
            }
            _displayId = primary.DisplayId;
            _modes.Items.Clear();
            foreach (var mode in list)
                _modes.Items.Add(new ComboBoxItem
                {
                    Content = $"{mode.Width} × {mode.Height} @ {mode.RefreshRateHz} Hz",
                    Tag = mode
                });
            _modes.SelectedIndex = list.FindIndex(m => m.Width == primary.Width && m.Height == primary.Height && m.RefreshRateHz == primary.RefreshRateHz) is var i and >= 0 ? i : 0;
            _displayStatus.Text = T($"{list.Count} modes available for the primary display.", $"{list.Count} modi disponibili per lo schermo principale.");
        }
        catch (Exception)
        {
            _displayStatus.Text = T("Could not read display modes.", "Impossibile leggere i modi schermo.");
        }
    }

    private async Task ApplyAsync()
    {
        if (_displayId is null || (_modes.SelectedItem as ComboBoxItem)?.Tag is not GamingDisplayModeOptionDto mode)
        {
            _displayStatus.Text = T("Load and select a mode first.", "Carica e seleziona prima un modo.");
            return;
        }
        try
        {
            var result = await GamingOrchestratorClient.Current.RequestDisplayChangeAsync(
                new GamingDisplayModeChangeRequest(_displayId, mode.Width, mode.Height, mode.RefreshRateHz));
            if (!result.Succeeded || result.Value is null)
            {
                _displayStatus.Text = T("The change was not applied. Leave Gaming, confirm or revert any pending change, and choose a listed mode.",
                                        "La modifica non è stata applicata. Esci dal Gaming, conferma o ripristina eventuali modifiche in sospeso e scegli un modo elencato.");
                return;
            }
            ShowPending(result.Value);
        }
        catch (Exception)
        {
            _displayStatus.Text = T("Could not change the display mode.", "Impossibile cambiare il modo schermo.");
        }
    }

    private async Task ConfirmAsync()
    {
        var result = await GamingOrchestratorClient.Current.ConfirmDisplayChangeAsync();
        _countdown.Stop();
        SetPendingUi(false);
        _displayStatus.Text = result.Succeeded && result.Value is not null
            ? result.Value.Message
            : T("Nothing to confirm.", "Niente da confermare.");
    }

    private async Task RevertAsync()
    {
        var result = await GamingOrchestratorClient.Current.RevertDisplayChangeAsync();
        _countdown.Stop();
        SetPendingUi(result.Value?.Pending == true);
        _displayStatus.Text = result.Value?.Message ?? T("Could not revert.", "Impossibile ripristinare.");
    }

    private void ShowPending(GamingDisplayChangeStatusDto status)
    {
        if (!status.Pending)
        {
            SetPendingUi(false);
            _displayStatus.Text = status.Message;
            return;
        }
        _secondsLeft = status.SecondsRemaining;
        SetPendingUi(true);
        UpdatePendingText();
        _countdown.Start();
    }

    private void UpdatePendingText() => _displayStatus.Text = T(
        $"Keep this mode? Reverting automatically in {_secondsLeft} s.",
        $"Mantenere questo modo? Ripristino automatico tra {_secondsLeft} s.");

    private async Task OnCountdownAsync()
    {
        if (--_secondsLeft > 0) { UpdatePendingText(); return; }
        _countdown.Stop();
        // The Core owns the revert timer; ask it for the truth instead of assuming.
        var status = await GamingOrchestratorClient.Current.GetDisplayChangeAsync();
        SetPendingUi(status.Value?.Pending == true);
        _displayStatus.Text = status.Value?.Message ?? T("The previous mode was restored.", "Il modo precedente è stato ripristinato.");
    }

    private void SetPendingUi(bool pending)
    {
        if (_confirmButton is not null) _confirmButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (_revertButton is not null) _revertButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (_applyButton is not null) _applyButton.IsEnabled = !pending;
        if (pending) _confirmButton?.Focus(FocusState.Programmatic);
    }

    private void BuildControllerSection()
    {
        Children.Add(new TextBlock
        {
            Text = T("Controller profile (diagnostics and Console navigation)", "Profilo controller (diagnostica e navigazione Console)"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        Children.Add(new TextBlock
        {
            Text = T("Saved locally. It is never injected into games and does not create a virtual controller.",
                     "Salvato in locale. Non viene iniettato nei giochi e non crea un controller virtuale."),
            TextWrapping = TextWrapping.Wrap
        });

        _leftDeadzone.Header = T("Left stick deadzone", "Deadzone levetta sinistra");
        _rightDeadzone.Header = T("Right stick deadzone", "Deadzone levetta destra");
        _trigger.Header = T("Trigger threshold", "Soglia grilletti");
        _slot.Header = T("Controller slot", "Slot controller");
        _invertLeft.Content = T("Invert left stick Y", "Inverti Y levetta sinistra");
        _invertRight.Content = T("Invert right stick Y", "Inverti Y levetta destra");
        _swapAb.Content = T("Swap A and B", "Scambia A e B");
        AutomationProperties.SetAutomationId(_leftDeadzone, "GameDevices_LeftDeadzone_NumberBox");
        AutomationProperties.SetAutomationId(_rightDeadzone, "GameDevices_RightDeadzone_NumberBox");
        AutomationProperties.SetAutomationId(_trigger, "GameDevices_TriggerThreshold_NumberBox");

        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var n in new UIElement[] { _leftDeadzone, _rightDeadzone, _trigger, _slot }) numbers.Children.Add(n);
        Children.Add(numbers);
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var c in new UIElement[] { _invertLeft, _invertRight, _swapAb }) toggles.Children.Add(c);
        Children.Add(toggles);

        var load = MakeButton(T("Load saved profile", "Carica profilo salvato"), "GameDevices_Controller_Load_Button", LoadProfileAsync);
        var calibrate = MakeButton(T("Calibrate (leave sticks untouched)", "Calibra (non toccare le levette)"), "GameDevices_Controller_Calibrate_Button", CalibrateAsync);
        var save = MakeButton(T("Save profile", "Salva profilo"), "GameDevices_Controller_Save_Button", SaveProfileAsync);
        var reset = MakeButton(T("Reset to defaults", "Ripristina predefiniti"), "GameDevices_Controller_Reset_Button", ResetProfileAsync);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in new[] { load, calibrate, save, reset }) actions.Children.Add(b);
        Children.Add(actions);
        AutomationProperties.SetAutomationId(_controllerStatus, "GameDevices_Controller_Status_Text");
        AutomationProperties.SetLiveSetting(_controllerStatus, AutomationLiveSetting.Polite);
        Children.Add(_controllerStatus);

        Children.Add(new TextBlock
        {
            Text = T("Other controllers (RawGameController)", "Altri controller (RawGameController)"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        Children.Add(new TextBlock
        {
            Text = T("Find and sample Windows-compatible HID devices on demand. Nothing is injected into games; a virtual Xbox pad needs a separately installed driver and confirmed input isolation.",
                "Rileva e campiona i dispositivi HID compatibili su richiesta. Nessun input viene iniettato nei giochi; un controller Xbox virtuale richiede un driver separato e l'isolamento verificato dell'input."),
            TextWrapping = TextWrapping.Wrap
        });
        _rawDevices.Header = T("Physical controller", "Controller fisico");
        AutomationProperties.SetAutomationId(_rawDevices, "GameDevices_RawController_ComboBox");
        var detectRaw = MakeButton(T("Find controllers", "Trova controller"),
            "GameDevices_Raw_Detect_Button", DetectRawAsync);
        var readRaw = MakeButton(T("Read once", "Leggi una volta"),
            "GameDevices_Raw_Read_Button", SampleRawAsync);
        var rawActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rawActions.Children.Add(detectRaw);
        rawActions.Children.Add(_rawDevices);
        rawActions.Children.Add(readRaw);
        Children.Add(rawActions);
        AutomationProperties.SetAutomationId(_rawDeviceStatus, "GameDevices_Raw_Status_Text");
        AutomationProperties.SetLiveSetting(_rawDeviceStatus, AutomationLiveSetting.Polite);
        Children.Add(_rawDeviceStatus);

        Children.Add(new TextBlock
        {
            Text = T("Raw controller mapping — manual, -1 disables a control", "Mappatura controller — manuale, -1 disabilita un controllo"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _rawA.Header = "A"; _rawB.Header = "B";
        _rawLx.Header = T("Left X", "X sinistra"); _rawLy.Header = T("Left Y", "Y sinistra");
        _rawRx.Header = T("Right X", "X destra"); _rawRy.Header = T("Right Y", "Y destra");
        _rawLt.Header = "LT"; _rawRt.Header = "RT";
        _rawDeadzone.Header = T("Stick deadzone", "Deadzone levette");
        _rawInvertY.Content = T("Invert vertical axes", "Inverti assi verticali");
        _rawDpad.Content = T("Use first hat as D-pad", "Usa prima croce direzionale");
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var stickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var triggerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (NumberBox n in new[] { _rawA, _rawB }) buttonRow.Children.Add(n);
        foreach (NumberBox n in new[] { _rawLx, _rawLy, _rawRx, _rawRy }) stickRow.Children.Add(n);
        foreach (NumberBox n in new[] { _rawLt, _rawRt, _rawDeadzone }) triggerRow.Children.Add(n);
        Children.Add(buttonRow);
        Children.Add(stickRow);
        Children.Add(triggerRow);
        var rawFlags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rawFlags.Children.Add(_rawInvertY);
        rawFlags.Children.Add(_rawDpad);
        Children.Add(rawFlags);
        var loadMapping = MakeButton(T("Load mapping", "Carica mappatura"),
            "GameDevices_RawMapping_Load_Button", LoadRawMappingAsync);
        var saveMapping = MakeButton(T("Save mapping", "Salva mappatura"),
            "GameDevices_RawMapping_Save_Button", SaveRawMappingAsync);
        var mappingActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        mappingActions.Children.Add(loadMapping);
        mappingActions.Children.Add(saveMapping);
        Children.Add(mappingActions);
        Children.Add(BuildFullMappingExpander());
        Children.Add(BuildGameProfileRow());

        Children.Add(new TextBlock
        {
            Text = T("Physical input recovery (HidHide)", "Recupero input fisico (HidHide)"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        Children.Add(new TextBlock
        {
            Text = T("If PaRuX closed unexpectedly after hiding a controller, check recovery before using a virtual pad. This never modifies unrelated HidHide settings.",
                "Se PaRuX si è chiuso dopo aver nascosto un controller, verifica il ripristino prima di usare un pad virtuale. Non modifica impostazioni HidHide estranee a PaRuX."),
            TextWrapping = TextWrapping.Wrap
        });
        var checkIsolation = MakeButton(T("Check recovery", "Controlla ripristino"),
            "GameDevices_HidHide_Status_Button", CheckHidHideAsync);
        var restoreIsolation = MakeButton(T("Restore PaRuX changes", "Ripristina modifiche PaRuX"),
            "GameDevices_HidHide_Restore_Button", RestoreHidHideAsync);
        var restoreActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        restoreActions.Children.Add(checkIsolation);
        restoreActions.Children.Add(restoreIsolation);
        Children.Add(restoreActions);
        AutomationProperties.SetAutomationId(_rawA, "GameDevices_RawMapping_A_Index");
        AutomationProperties.SetAutomationId(_rawB, "GameDevices_RawMapping_B_Index");
        AutomationProperties.SetAutomationId(_rawLx, "GameDevices_RawMapping_LeftX_Index");
        AutomationProperties.SetAutomationId(_rawDeadzone, "GameDevices_RawMapping_Deadzone");
        AutomationProperties.SetAutomationId(_rawDpad, "GameDevices_RawMapping_HatEnabled_CheckBox");
        AutomationProperties.SetAutomationId(_rawInvertY, "GameDevices_RawMapping_InvertY_CheckBox");
        BuildGuidedSection();
        ApplyToUi(GamingControllerProfile.Default);
    }

    private Task DetectRawAsync()
    {
        _rawDevices.Items.Clear();
        var list = WindowsRawGamepadSource.ListDevices();
        foreach (RawGamepadDeviceInfo device in list)
            _rawDevices.Items.Add(new ComboBoxItem
            {
                Content = $"{device.Name} (VID {device.VendorId:X4}, PID {device.ProductId:X4})",
                Tag = device.Id
            });
        _rawDevices.SelectedIndex = list.Count > 0 ? 0 : -1;
        _rawDeviceStatus.Text = list.Count > 0
            ? T($"{list.Count} physical controllers listed. Choose one and click Read once.",
                 $"{list.Count} controller fisici rilevati. Scegline uno e premi Leggi una volta.")
            : T("No compatible raw controllers were found.", "Nessun controller raw compatibile rilevato.");
        return Task.CompletedTask;
    }

    private Task SampleRawAsync()
    {
        string? selected = (_rawDevices.SelectedItem as ComboBoxItem)?.Tag as string;
        var source = selected is null ? null : WindowsRawGamepadSource.TryOpen(selected);
        if (source is null || !source.TryRead(out var sample) || sample is null)
        {
            _rawDeviceStatus.Text = T("The selected controller is no longer available.",
                "Il controller selezionato non è più disponibile.");
            return Task.CompletedTask;
        }
        string pressed = string.Join(", ", sample.Buttons.Select((pressed, index) => (pressed, index))
            .Where(x => x.pressed).Take(16).Select(x => x.index.ToString(CultureInfo.InvariantCulture)));
        string axes = string.Join(", ", sample.Axes.Take(12)
            .Select((value, index) => $"{index}:{value.ToString("F2", CultureInfo.CurrentCulture)}"));
        _rawDeviceStatus.Text = T(
            $"Read-only sample — pressed button indices [{pressed}], axis indices/values [{axes}], hat {sample.HatDirection}.",
            $"Lettura manuale — indici pulsanti premuti [{pressed}], indici/valori assi [{axes}], direzione {sample.HatDirection}.");
        return Task.CompletedTask;
    }

    private async Task CheckHidHideAsync()
    {
        try
        {
            var status = await GamingOrchestratorClient.Current.GetHidHideStatusAsync();
            _rawDeviceStatus.Text = status.Succeeded && status.Value is { } value
                ? value.RecoveryRequired
                    ? T("Recovery is pending. Leave Gaming and use Restore PaRuX changes.",
                        "Ripristino necessario. Esci dal Gaming e usa Ripristina modifiche PaRuX.")
                    : value.Available
                        ? T("HidHide available; no PaRuX recovery pending.",
                            "HidHide disponibile; nessun ripristino PaRuX in sospeso.")
                        : T("HidHide is not installed; optional virtual mapping unavailable.",
                            "HidHide non installato; mappatura virtuale opzionale non disponibile.")
                : T("Could not check HidHide.", "Impossibile controllare HidHide.");
        }
        catch (Exception)
        {
            _rawDeviceStatus.Text = T("Could not check HidHide.", "Impossibile controllare HidHide.");
        }
    }

    private async Task RestoreHidHideAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Restore PaRuX controller visibility changes?", "Ripristinare la visibilità dei controller modificata da PaRuX?"),
            Content = T("Only changes recorded in PaRuX's recovery journal will be reversed. This operation is refused during Gaming.",
                "Verranno annullate solo le modifiche registrate nel journal di PaRuX. Durante il Gaming l'operazione viene rifiutata."),
            PrimaryButtonText = T("Restore", "Ripristina"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var result = await GamingOrchestratorClient.Current.RecoverHidHideAsync();
            _rawDeviceStatus.Text = result.Succeeded
                ? T("PaRuX controller visibility recovery completed.", "Ripristino della visibilità dei controller PaRuX completato.")
                : T("Recovery incomplete or refused. The journal was kept; check HidHide before using virtual input.",
                    "Ripristino incompleto o rifiutato. Journal preservato; controlla HidHide prima di usare input virtuale.");
        }
        catch (Exception)
        {
            _rawDeviceStatus.Text = T("Could not restore controllers; check HidHide manually.",
                "Impossibile ripristinare i controller; controlla HidHide manualmente.");
        }
    }

    private UIElement BuildFullMappingExpander()
    {
        _rawX.Header = "X"; _rawY.Header = "Y";
        _rawLb.Header = "LB"; _rawRb.Header = "RB";
        _rawBack.Header = T("Back", "Indietro"); _rawStart.Header = T("Start", "Start");
        _rawL3.Header = "L3"; _rawR3.Header = "R3"; _rawGuide.Header = T("Guide", "Guida");
        _rawLtButton.Header = T("LT as button", "LT come pulsante");
        _rawRtButton.Header = T("RT as button", "RT come pulsante");
        _rawInvertX.Content = T("Invert horizontal axes", "Inverti assi orizzontali");
        _rawHatTarget.Header = T("Hat switch goes to", "La croce direzionale va a");
        _rawHatTarget.Items.Add(new ComboBoxItem { Content = T("D-pad", "Croce direzionale"), Tag = PadHatTarget.Dpad });
        _rawHatTarget.Items.Add(new ComboBoxItem { Content = T("Left stick", "Levetta sinistra"), Tag = PadHatTarget.LeftStick });
        _rawHatTarget.Items.Add(new ComboBoxItem { Content = T("Right stick", "Levetta destra"), Tag = PadHatTarget.RightStick });
        _rawHatTarget.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_rawX, "GameDevices_RawMapping_X_Index");
        AutomationProperties.SetAutomationId(_rawY, "GameDevices_RawMapping_Y_Index");
        AutomationProperties.SetAutomationId(_rawLb, "GameDevices_RawMapping_LB_Index");
        AutomationProperties.SetAutomationId(_rawRb, "GameDevices_RawMapping_RB_Index");
        AutomationProperties.SetAutomationId(_rawBack, "GameDevices_RawMapping_Back_Index");
        AutomationProperties.SetAutomationId(_rawStart, "GameDevices_RawMapping_Start_Index");
        AutomationProperties.SetAutomationId(_rawL3, "GameDevices_RawMapping_L3_Index");
        AutomationProperties.SetAutomationId(_rawR3, "GameDevices_RawMapping_R3_Index");
        AutomationProperties.SetAutomationId(_rawGuide, "GameDevices_RawMapping_Guide_Index");
        AutomationProperties.SetAutomationId(_rawLtButton, "GameDevices_RawMapping_LtButton_Index");
        AutomationProperties.SetAutomationId(_rawRtButton, "GameDevices_RawMapping_RtButton_Index");
        AutomationProperties.SetAutomationId(_rawInvertX, "GameDevices_RawMapping_InvertX_CheckBox");
        AutomationProperties.SetAutomationId(_rawHatTarget, "GameDevices_RawMapping_HatTarget_ComboBox");

        var rowA = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (NumberBox n in new[] { _rawX, _rawY, _rawLb, _rawRb }) rowA.Children.Add(n);
        var rowB = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (NumberBox n in new[] { _rawBack, _rawStart, _rawL3, _rawR3, _rawGuide }) rowB.Children.Add(n);
        var rowC = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowC.Children.Add(_rawLtButton);
        rowC.Children.Add(_rawRtButton);
        rowC.Children.Add(_rawHatTarget);
        rowC.Children.Add(_rawInvertX);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = T("Physical button numbers from \"Read once\". -1 leaves a control unmapped. Two Xbox buttons cannot share one physical button.",
                     "Numeri dei pulsanti fisici da \"Leggi una volta\". -1 lascia il controllo non mappato. Due pulsanti Xbox non possono usare lo stesso pulsante fisico."),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(rowA);
        body.Children.Add(rowB);
        body.Children.Add(rowC);
        var expander = new Expander
        {
            Header = T("All buttons, triggers and hat (advanced)", "Tutti i pulsanti, grilletti e croce (avanzato)"),
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(expander, "GameDevices_RawMapping_Full_Expander");
        return expander;
    }

    private UIElement BuildGameProfileRow()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = T("Per-game profile — used automatically when PaRuX starts that game; other games use this controller's mapping above.",
                     "Profilo per gioco — usato automaticamente quando PaRuX avvia quel gioco; gli altri giochi usano la mappatura del controller qui sopra."),
            TextWrapping = TextWrapping.Wrap
        });
        _gameChoice.Header = T("Game", "Gioco");
        AutomationProperties.SetAutomationId(_gameChoice, "GameDevices_GameProfile_Game_ComboBox");
        var load = MakeButton(T("List games", "Elenca giochi"), "GameDevices_GameProfile_ListGames_Button", LoadGameChoicesAsync);
        var loadProfile = MakeButton(T("Load game profile", "Carica profilo gioco"), "GameDevices_GameProfile_Load_Button", LoadGameProfileAsync);
        var saveProfile = MakeButton(T("Save as game profile", "Salva come profilo gioco"), "GameDevices_GameProfile_Save_Button", SaveGameProfileAsync);
        var deleteProfile = MakeButton(T("Delete game profile", "Elimina profilo gioco"), "GameDevices_GameProfile_Delete_Button", DeleteGameProfileAsync);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(load);
        row.Children.Add(_gameChoice);
        row.Children.Add(loadProfile);
        row.Children.Add(saveProfile);
        row.Children.Add(deleteProfile);
        panel.Children.Add(row);
        return panel;
    }

    private async Task LoadGameChoicesAsync()
    {
        try
        {
            var games = await GamingOrchestratorClient.Current.ListManualGamesAsync();
            _gameChoice.Items.Clear();
            _gameChoice.Items.Add(new ComboBoxItem { Content = T("(none — controller mapping)", "(nessuno — mappatura del controller)"), Tag = null });
            if (games.Succeeded && games.Value is { } list)
                foreach (var game in list)
                    _gameChoice.Items.Add(new ComboBoxItem { Content = game.Name, Tag = game.Id });
            _gameChoice.SelectedIndex = 0;
            _rawDeviceStatus.Text = T("Games listed. Pick one to load or save its profile.",
                "Giochi elencati. Sceglierne uno per caricare o salvare il suo profilo.");
        }
        catch (Exception)
        {
            _rawDeviceStatus.Text = T("Could not list games.", "Impossibile elencare i giochi.");
        }
    }

    private string? SelectedGameId => (_gameChoice.SelectedItem as ComboBoxItem)?.Tag as string;
    private string? SelectedDeviceId => (_rawDevices.SelectedItem as ComboBoxItem)?.Tag as string;

    private Task LoadGameProfileAsync()
    {
        if (SelectedDeviceId is not { } device || SelectedGameId is not { } game)
        {
            _rawDeviceStatus.Text = T("Select a controller and a game first.", "Seleziona prima un controller e un gioco.");
            return Task.CompletedTask;
        }
        PadMapping? loaded = VirtualPadActivationController.Current.Profiles.Load(game, device, out string? error);
        if (error is not null)
            _rawDeviceStatus.Text = T("The game profile could not be loaded; its file was preserved.",
                "Impossibile caricare il profilo del gioco; il file è stato preservato.");
        else if (loaded is null)
            _rawDeviceStatus.Text = T("No profile for this game yet; it uses the controller mapping.",
                "Nessun profilo per questo gioco; usa la mappatura del controller.");
        else
        {
            ApplyRawMapping(loaded);
            _rawDeviceStatus.Text = T("Game profile loaded. No virtual controller was started.",
                "Profilo del gioco caricato. Nessun controller virtuale è stato avviato.");
        }
        return Task.CompletedTask;
    }

    private Task SaveGameProfileAsync()
    {
        if (SelectedDeviceId is not { } device || SelectedGameId is not { } game)
        {
            _rawDeviceStatus.Text = T("Select a controller and a game first.", "Seleziona prima un controller e un gioco.");
            return Task.CompletedTask;
        }
        PadMapping? mapping = BuildRawMapping(out string? problem);
        if (mapping is null) { _rawDeviceStatus.Text = problem; return Task.CompletedTask; }
        bool saved = VirtualPadActivationController.Current.Profiles.Save(game, device, mapping, out _);
        _rawDeviceStatus.Text = saved
            ? T("Game profile saved locally. It applies when PaRuX starts that game with forwarding armed.",
                "Profilo del gioco salvato in locale. Si applica quando PaRuX avvia quel gioco con l'inoltro attivo.")
            : T("Invalid mapping or the saved file is damaged; nothing was overwritten.",
                "Mappatura non valida o file danneggiato; nessun dato è stato sovrascritto.");
        return Task.CompletedTask;
    }

    private Task DeleteGameProfileAsync()
    {
        if (SelectedDeviceId is not { } device || SelectedGameId is not { } game)
        {
            _rawDeviceStatus.Text = T("Select a controller and a game first.", "Seleziona prima un controller e un gioco.");
            return Task.CompletedTask;
        }
        _rawDeviceStatus.Text = VirtualPadActivationController.Current.Profiles.Delete(game, device)
            ? T("Game profile removed; the game uses the controller mapping.", "Profilo del gioco rimosso; il gioco usa la mappatura del controller.")
            : T("The game profile could not be removed.", "Impossibile rimuovere il profilo del gioco.");
        return Task.CompletedTask;
    }

    private Task LoadRawMappingAsync()
    {
        if (SelectedDeviceId is not { } id)
        {
            _rawDeviceStatus.Text = T("Select a physical controller first.", "Seleziona prima un controller fisico.");
            return Task.CompletedTask;
        }
        var loaded = new RawPadMappingStore().Load(id, out string? error);
        if (error is not null)
        {
            _rawDeviceStatus.Text = T("The saved mapping could not be loaded; its file was preserved.",
                "Impossibile caricare la mappatura; il file originale è stato preservato.");
            return Task.CompletedTask;
        }
        if (loaded is null)
        {
            _rawDeviceStatus.Text = T("No mapping has been saved for this controller.",
                "Nessuna mappatura salvata per questo controller.");
            return Task.CompletedTask;
        }
        ApplyRawMapping(loaded);
        _rawDeviceStatus.Text = T("Mapping loaded. No virtual controller was started.",
            "Mappatura caricata. Nessun controller virtuale è stato avviato.");
        return Task.CompletedTask;
    }

    private void ApplyRawMapping(PadMapping loaded)
    {
        foreach (var (box, bit) in ButtonBoxes)
        {
            var hit = loaded.Buttons.Where(x => x.Value == bit).Select(x => (int?)x.Key).FirstOrDefault();
            box.Value = hit ?? -1;
        }
        _rawLx.Value = loaded.LeftXAxis ?? -1;
        _rawLy.Value = loaded.LeftYAxis ?? -1;
        _rawRx.Value = loaded.RightXAxis ?? -1;
        _rawRy.Value = loaded.RightYAxis ?? -1;
        _rawLt.Value = loaded.LeftTriggerAxis ?? -1;
        _rawRt.Value = loaded.RightTriggerAxis ?? -1;
        _rawLtButton.Value = loaded.LeftTriggerButton ?? -1;
        _rawRtButton.Value = loaded.RightTriggerButton ?? -1;
        _rawDeadzone.Value = loaded.StickDeadzone;
        _rawInvertY.IsChecked = loaded.InvertLeftY && loaded.InvertRightY;
        _rawInvertX.IsChecked = loaded.InvertLeftX && loaded.InvertRightX;
        _rawDpad.IsChecked = loaded.HatIsDpad;
        int target = Math.Clamp((int)loaded.HatTarget, 0, _rawHatTarget.Items.Count - 1);
        _rawHatTarget.SelectedIndex = target;
    }

    /// <summary>Reads every mapping control. Returns null with a user-readable reason when the entries conflict.</summary>
    private PadMapping? BuildRawMapping(out string? problem)
    {
        problem = null;
        var mapping = new PadMapping
        {
            LeftXAxis = SelectedIndex(_rawLx), LeftYAxis = SelectedIndex(_rawLy),
            RightXAxis = SelectedIndex(_rawRx), RightYAxis = SelectedIndex(_rawRy),
            LeftTriggerAxis = SelectedIndex(_rawLt), RightTriggerAxis = SelectedIndex(_rawRt),
            LeftTriggerButton = SelectedIndex(_rawLtButton), RightTriggerButton = SelectedIndex(_rawRtButton),
            TriggerAxisIsSigned = true, // Windows RawGameController axes were transformed from 0..1 to -1..1.
            InvertLeftY = _rawInvertY.IsChecked == true,
            InvertRightY = _rawInvertY.IsChecked == true,
            InvertLeftX = _rawInvertX.IsChecked == true,
            InvertRightX = _rawInvertX.IsChecked == true,
            HatIsDpad = _rawDpad.IsChecked == true,
            HatTarget = (_rawHatTarget.SelectedItem as ComboBoxItem)?.Tag is PadHatTarget t ? t : PadHatTarget.Dpad,
            StickDeadzone = double.IsNaN(_rawDeadzone.Value) ? double.NaN : _rawDeadzone.Value
        };
        var used = new HashSet<int>();
        foreach (var (box, bit) in ButtonBoxes)
        {
            if (SelectedIndex(box) is not { } index) continue;
            if (!used.Add(index))
            {
                problem = T("Two Xbox buttons cannot use the same physical button.", "Due pulsanti Xbox non possono usare lo stesso pulsante fisico.");
                return null;
            }
            mapping.Buttons[index] = bit;
        }
        if (mapping.Validate() is not null)
        {
            problem = T("Invalid mapping values.", "Valori di mappatura non validi.");
            return null;
        }
        return mapping;
    }

    private Task SaveRawMappingAsync()
    {
        if (SelectedDeviceId is not { } id)
        {
            _rawDeviceStatus.Text = T("Select a physical controller first.", "Seleziona prima un controller fisico.");
            return Task.CompletedTask;
        }
        PadMapping? mapping = BuildRawMapping(out string? problem);
        if (mapping is null) { _rawDeviceStatus.Text = problem; return Task.CompletedTask; }
        bool saved = new RawPadMappingStore().Save(id, mapping, out _);
        _rawDeviceStatus.Text = saved
            ? T("Mapping saved locally. Virtual forwarding is still off.", "Mappatura salvata in locale. L'inoltro virtuale resta disattivato.")
            : T("Invalid mapping or saved file is damaged; nothing was overwritten.",
                "Mappatura non valida o file danneggiato; nessun dato è stato sovrascritto.");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------- guided virtual-controller activation

    private void BuildGuidedSection()
    {
        Children.Add(new TextBlock
        {
            Text = T("Guided virtual controller (optional)", "Controller virtuale guidato (opzionale)"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        Children.Add(new TextBlock
        {
            Text = T("For pads Windows games do not understand. Every step is yours to take: PaRuX never installs a driver, never hides a controller without your consent, and never starts by itself. Experimental: not yet verified with a physical pad.",
                     "Per controller che i giochi Windows non riconoscono. Ogni passo è tuo: PaRuX non installa mai un driver, non nasconde un controller senza il tuo consenso e non parte da solo. Sperimentale: non ancora verificato con un controller fisico."),
            TextWrapping = TextWrapping.Wrap
        });

        // 1. Detect the pad (uses the controller list above).
        var detect = MakeButton(T("1. Find controllers", "1. Trova controller"), "GameDevices_Guided_Detect_Button", async () =>
        {
            await DetectRawAsync();
            _stepDevice.Text = SelectedDeviceId is null
                ? T("No compatible controller found. Connect it and try again.", "Nessun controller compatibile. Collegalo e riprova.")
                : T("Controller selected in the list above. Choose a different one there if needed.", "Controller selezionato nell'elenco qui sopra. Sceglierne un altro lì se serve.");
        });
        AddGuidedRow(detect, _stepDevice, "GameDevices_Guided_Step1_Text");

        // 2. Virtual driver: detect only; if absent, explain and link, never install.
        var driver = MakeButton(T("2. Check virtual driver", "2. Verifica driver virtuale"), "GameDevices_Guided_Driver_Button", () =>
        {
            var (present, message) = VirtualPadActivationController.ProbeDriver();
            _driverLink.Visibility = present ? Visibility.Collapsed : Visibility.Visible;
            _stepDriver.Text = present
                ? T("The virtual controller driver (ViGEmBus) is installed.", "Il driver del controller virtuale (ViGEmBus) è installato.")
                : T("The virtual controller driver (ViGEmBus) was not found. It is a separate, optional driver from its official project. PaRuX will not install it: if you want it, download and install it yourself, then check again.",
                    "Il driver del controller virtuale (ViGEmBus) non è stato trovato. È un driver separato e opzionale del suo progetto ufficiale. PaRuX non lo installa: se lo vuoi, scaricalo e installalo tu, poi ricontrolla.");
            return Task.CompletedTask;
        });
        _driverLink.Content = T("Official ViGEmBus project page", "Pagina ufficiale del progetto ViGEmBus");
        _driverLink.NavigateUri = new Uri("https://github.com/nefarius/ViGEmBus");
        AutomationProperties.SetAutomationId(_driverLink, "GameDevices_Guided_DriverLink");
        AddGuidedRow(driver, _stepDriver, "GameDevices_Guided_Step2_Text");
        Children.Add(_driverLink);

        // 3. Optional HidHide isolation with explicit consent.
        var hidHide = MakeButton(T("3. Check HidHide", "3. Verifica HidHide"), "GameDevices_Guided_HidHide_Button", CheckHidHideForGuidedAsync);
        _hidHideLink.Content = T("Official HidHide project page", "Pagina ufficiale del progetto HidHide");
        _hidHideLink.NavigateUri = new Uri("https://github.com/nefarius/HidHide");
        AutomationProperties.SetAutomationId(_hidHideLink, "GameDevices_Guided_HidHideLink");
        AddGuidedRow(hidHide, _stepIsolation, "GameDevices_Guided_Step3_Text");
        Children.Add(_hidHideLink);
        _hideConsent.Content = T("I agree that PaRuX hides this physical controller from games with HidHide (recorded, and undone with \"Restore PaRuX changes\" below). Without this, forwarding does not start, because every input would be doubled.",
                                 "Acconsento che PaRuX nasconda questo controller fisico ai giochi con HidHide (registrato e annullabile con \"Ripristina modifiche PaRuX\" qui sotto). Senza questo l'inoltro non parte, perché ogni input sarebbe doppio.");
        AutomationProperties.SetAutomationId(_hideConsent, "GameDevices_Guided_HideConsent_CheckBox");
        Children.Add(_hideConsent);

        // 4. Start / stop / re-arm.
        _startForwarding = MakeButton(T("4. Start forwarding", "4. Avvia inoltro"), "GameDevices_Guided_Start_Button", StartForwardingAsync);
        _stopForwarding = MakeButton(T("Stop forwarding", "Ferma inoltro"), "GameDevices_Guided_Stop_Button", StopForwardingAsync);
        _rearmForwarding = MakeButton(T("Re-arm after reconnect", "Riarma dopo la riconnessione"), "GameDevices_Guided_Rearm_Button", RearmForwardingAsync);
        _stopForwarding.IsEnabled = false;
        _rearmForwarding.Visibility = Visibility.Collapsed;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(_startForwarding);
        controls.Children.Add(_stopForwarding);
        controls.Children.Add(_rearmForwarding);
        Children.Add(controls);

        // 5. Test input.
        var test = MakeButton(T("5. Test input", "5. Prova input"), "GameDevices_Guided_Test_Button", TestInputAsync);
        AddGuidedRow(test, _stepTest, "GameDevices_Guided_Step5_Text");

        AutomationProperties.SetAutomationId(_guidedStatus, "GameDevices_Guided_Status_Text");
        AutomationProperties.SetLiveSetting(_guidedStatus, AutomationLiveSetting.Polite);
        _guidedStatus.Text = T("Not active.", "Non attivo.");
        Children.Add(_guidedStatus);
    }

    private void AddGuidedRow(Button button, TextBlock text, string textId)
    {
        AutomationProperties.SetAutomationId(text, textId);
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(button);
        Children.Add(row);
        Children.Add(text);
    }

    private async Task CheckHidHideForGuidedAsync()
    {
        try
        {
            var status = await GamingOrchestratorClient.Current.GetHidHideStatusAsync();
            if (!status.Succeeded || status.Value is not { } value)
            {
                _stepIsolation.Text = T("Could not check HidHide.", "Impossibile controllare HidHide.");
                return;
            }
            _hidHideLink.Visibility = value.Available ? Visibility.Collapsed : Visibility.Visible;
            _stepIsolation.Text = value.RecoveryRequired
                ? T("A previous PaRuX hiding has not been restored yet. Use \"Restore PaRuX changes\" below first.",
                    "Un precedente nascondimento di PaRuX non è stato ancora ripristinato. Usa prima \"Ripristina modifiche PaRuX\" qui sotto.")
                : value.Available
                    ? T("HidHide is installed. PaRuX will only hide this controller after proving which device it is, and only with your consent.",
                        "HidHide è installato. PaRuX nasconderà questo controller solo dopo aver dimostrato quale dispositivo è, e solo con il tuo consenso.")
                    : T("HidHide is not installed. It is a separate, optional driver from its official project; PaRuX will not install it. Without it, forwarding cannot start safely.",
                        "HidHide non è installato. È un driver separato e opzionale del suo progetto ufficiale; PaRuX non lo installa. Senza di esso l'inoltro non può partire in sicurezza.");
        }
        catch (Exception)
        {
            _stepIsolation.Text = T("Could not check HidHide.", "Impossibile controllare HidHide.");
        }
    }

    private RawGamepadDeviceInfo? SelectedDeviceInfo()
    {
        if (SelectedDeviceId is not { } id) return null;
        return WindowsRawGamepadSource.ListDevices().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
    }

    private async Task StartForwardingAsync()
    {
        if (SelectedDeviceInfo() is not { } device)
        {
            _guidedStatus.Text = T("Choose an available controller first (step 1).", "Scegli prima un controller disponibile (passo 1).");
            return;
        }
        _guidedStatus.Text = T("Starting…", "Avvio…");
        var status = await VirtualPadActivationController.Current.StartAsync(device, _hideConsent.IsChecked == true, SelectedGameId);
        ShowGuided(status);
    }

    private async Task StopForwardingAsync() => ShowGuided(await VirtualPadActivationController.Current.StopAsync());

    private async Task RearmForwardingAsync() => ShowGuided(await VirtualPadActivationController.Current.RearmAsync());

    private Task TestInputAsync()
    {
        if (SelectedDeviceId is not { } id || WindowsRawGamepadSource.TryOpen(id) is not { } source ||
            !source.TryRead(out var sample) || sample is null)
        {
            _stepTest.Text = T("The selected controller is not available.", "Il controller selezionato non è disponibile.");
            return Task.CompletedTask;
        }
        var resolved = VirtualPadActivationController.Current.Profiles.Resolve(id, SelectedGameId);
        if (resolved.Mapping is null)
        {
            _stepTest.Text = T("Save a mapping for this controller first.", "Salva prima una mappatura per questo controller.");
            return Task.CompletedTask;
        }
        var report = GenericPadMapper.Map(sample, resolved.Mapping);
        string phase = VirtualPadActivationController.Current.Service.Status.Phase == VirtualPadPhase.Running
            ? T("Forwarding is active: this is what the virtual pad sends.", "L'inoltro è attivo: questo è ciò che invia il pad virtuale.")
            : T("Forwarding is off: preview only, nothing is sent.", "L'inoltro è spento: solo anteprima, nulla viene inviato.");
        _stepTest.Text = $"{phase} Buttons 0x{report.Buttons:X4}, LT {report.LeftTrigger}, RT {report.RightTrigger}, " +
                         $"L({report.LeftX},{report.LeftY}) R({report.RightX},{report.RightY}). " +
                         T("Hold a button, then press Test input again.", "Tieni premuto un pulsante, poi premi di nuovo Prova input.");
        return Task.CompletedTask;
    }

    private void ShowGuided(VirtualPadServiceStatus status)
    {
        string phase = status.Phase switch
        {
            VirtualPadPhase.Running => T("Active", "Attivo"),
            VirtualPadPhase.Starting => T("Starting", "Avvio"),
            VirtualPadPhase.PausedRemoved => T("Paused (controller removed)", "In pausa (controller rimosso)"),
            VirtualPadPhase.Failed => T("Stopped", "Fermo"),
            _ => T("Off", "Spento")
        };
        _guidedStatus.Text = $"{phase}: {status.Message}";
        if (_startForwarding is not null)
            _startForwarding.IsEnabled = status.Phase is not (VirtualPadPhase.Running or VirtualPadPhase.Starting);
        if (_stopForwarding is not null)
            _stopForwarding.IsEnabled = status.Phase is VirtualPadPhase.Running or VirtualPadPhase.Starting or VirtualPadPhase.PausedRemoved;
        if (_rearmForwarding is not null)
            _rearmForwarding.Visibility = status.CanRearm ? Visibility.Visible : Visibility.Collapsed;
        // The only periodic work: notice a worker that ended by itself (unplugged pad, lost isolation).
        if (status.Phase is VirtualPadPhase.Running or VirtualPadPhase.Starting) _guidedTimer.Start();
        else _guidedTimer.Stop();
    }

    private void RefreshGuided() => ShowGuided(VirtualPadActivationController.Current.Service.Status);

    private void ApplyToUi(GamingControllerProfile p)
    {
        _leftDeadzone.Value = p.LeftDeadzone;
        _rightDeadzone.Value = p.RightDeadzone;
        _trigger.Value = p.TriggerThreshold;
        _invertLeft.IsChecked = p.InvertLeftY;
        _invertRight.IsChecked = p.InvertRightY;
        _swapAb.IsChecked = p.ButtonRemap.TryGetValue(0x1000, out ushort b) && b == 0x2000
                            && p.ButtonRemap.TryGetValue(0x2000, out ushort a) && a == 0x1000;
    }

    private GamingControllerProfile FromUi() => new()
    {
        LeftDeadzone = (int)Math.Round(double.IsNaN(_leftDeadzone.Value) ? GamingControllerProfile.DefaultLeftDeadzone : _leftDeadzone.Value),
        RightDeadzone = (int)Math.Round(double.IsNaN(_rightDeadzone.Value) ? GamingControllerProfile.DefaultRightDeadzone : _rightDeadzone.Value),
        TriggerThreshold = (int)Math.Round(double.IsNaN(_trigger.Value) ? GamingControllerProfile.DefaultTriggerThreshold : _trigger.Value),
        InvertLeftY = _invertLeft.IsChecked == true,
        InvertRightY = _invertRight.IsChecked == true,
        ButtonRemap = _swapAb.IsChecked == true
            ? new Dictionary<ushort, ushort> { [0x1000] = 0x2000, [0x2000] = 0x1000 }
            : new Dictionary<ushort, ushort>()
    };

    private async Task LoadProfileAsync()
    {
        var result = await GamingOrchestratorClient.Current.GetControllerProfileAsync();
        if (!result.Succeeded || result.Value is null)
        {
            _controllerStatus.Text = T("The controller profile is unavailable.", "Il profilo controller non è disponibile.");
            return;
        }
        ApplyToUi(result.Value.Profile);
        _controllerStatus.Text = result.Value.LoadError is null
            ? T("Profile loaded.", "Profilo caricato.")
            : T("The saved profile was unreadable; defaults are shown and the file was not overwritten.",
                "Il profilo salvato non era leggibile; sono mostrati i valori predefiniti e il file non è stato sovrascritto.");
    }

    private async Task CalibrateAsync()
    {
        _controllerStatus.Text = T("Calibrating… keep the sticks at rest.", "Calibrazione… lascia le levette a riposo.");
        int slot = (int)Math.Clamp(double.IsNaN(_slot.Value) ? 1 : _slot.Value, 1, 4) - 1;
        var result = await GamingOrchestratorClient.Current.CalibrateControllerAsync(slot);
        if (!result.Succeeded || result.Value is null)
        {
            _controllerStatus.Text = T("No readable XInput controller in that slot, or Gaming is active.",
                                       "Nessun controller XInput leggibile in quello slot, oppure il Gaming è attivo.");
            return;
        }
        _leftDeadzone.Value = result.Value.LeftDeadzone;
        _rightDeadzone.Value = result.Value.RightDeadzone;
        _controllerStatus.Text = T("Recommended deadzones filled in. Save the profile to keep them.",
                                   "Deadzone consigliate inserite. Salva il profilo per mantenerle.");
    }

    private async Task SaveProfileAsync()
    {
        var result = await GamingOrchestratorClient.Current.SaveControllerProfileAsync(FromUi());
        _controllerStatus.Text = result.Succeeded
            ? T("Profile saved.", "Profilo salvato.")
            : T("The profile was not saved. Check the values.", "Profilo non salvato. Controlla i valori.");
    }

    private async Task ResetProfileAsync()
    {
        var result = await GamingOrchestratorClient.Current.ResetControllerProfileAsync();
        ApplyToUi(GamingControllerProfile.Default);
        _controllerStatus.Text = result.Succeeded
            ? T("Defaults restored.", "Valori predefiniti ripristinati.")
            : T("Defaults shown, but the saved file could not be removed.", "Mostrati i predefiniti, ma il file salvato non è stato rimosso.");
    }
}
