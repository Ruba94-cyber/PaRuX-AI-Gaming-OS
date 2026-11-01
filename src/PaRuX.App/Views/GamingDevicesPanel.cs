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
/// the resting sticks once, and nothing here polls. The only timer exists while a change is pending.
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
        Unloaded += (_, _) => _countdown.Stop();
    }

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
        _rawLx.Header = "Left X"; _rawLy.Header = "Left Y";
        _rawRx.Header = "Right X"; _rawRy.Header = "Right Y";
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
        AutomationProperties.SetAutomationId(_rawA, "GameDevices_RawMapping_A_Index");
        AutomationProperties.SetAutomationId(_rawB, "GameDevices_RawMapping_B_Index");
        AutomationProperties.SetAutomationId(_rawLx, "GameDevices_RawMapping_LeftX_Index");
        AutomationProperties.SetAutomationId(_rawDeadzone, "GameDevices_RawMapping_Deadzone");
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

    private Task LoadRawMappingAsync()
    {
        if ((_rawDevices.SelectedItem as ComboBoxItem)?.Tag is not string id)
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
        _rawA.Value = loaded.Buttons.FirstOrDefault(x => x.Value == Xbox360Report.A).Value == Xbox360Report.A
            ? loaded.Buttons.First(x => x.Value == Xbox360Report.A).Key : -1;
        _rawB.Value = loaded.Buttons.FirstOrDefault(x => x.Value == Xbox360Report.B).Value == Xbox360Report.B
            ? loaded.Buttons.First(x => x.Value == Xbox360Report.B).Key : -1;
        _rawLx.Value = loaded.LeftXAxis ?? -1;
        _rawLy.Value = loaded.LeftYAxis ?? -1;
        _rawRx.Value = loaded.RightXAxis ?? -1;
        _rawRy.Value = loaded.RightYAxis ?? -1;
        _rawLt.Value = loaded.LeftTriggerAxis ?? -1;
        _rawRt.Value = loaded.RightTriggerAxis ?? -1;
        _rawDeadzone.Value = loaded.StickDeadzone;
        _rawInvertY.IsChecked = loaded.InvertLeftY && loaded.InvertRightY;
        _rawDpad.IsChecked = loaded.HatIsDpad;
        _rawDeviceStatus.Text = T("Mapping loaded. No virtual controller was started.",
            "Mappatura caricata. Nessun controller virtuale è stato avviato.");
        return Task.CompletedTask;
    }

    private Task SaveRawMappingAsync()
    {
        if ((_rawDevices.SelectedItem as ComboBoxItem)?.Tag is not string id)
        {
            _rawDeviceStatus.Text = T("Select a physical controller first.", "Seleziona prima un controller fisico.");
            return Task.CompletedTask;
        }
        int? buttonA = SelectedIndex(_rawA), buttonB = SelectedIndex(_rawB);
        if (buttonA is not null && buttonA == buttonB)
        {
            _rawDeviceStatus.Text = T("A and B cannot use the same physical button.", "A e B non possono usare lo stesso pulsante.");
            return Task.CompletedTask;
        }
        var mapping = new PadMapping
        {
            LeftXAxis = SelectedIndex(_rawLx), LeftYAxis = SelectedIndex(_rawLy),
            RightXAxis = SelectedIndex(_rawRx), RightYAxis = SelectedIndex(_rawRy),
            LeftTriggerAxis = SelectedIndex(_rawLt), RightTriggerAxis = SelectedIndex(_rawRt),
            TriggerAxisIsSigned = true, // Windows RawGameController axes were transformed from 0..1 to -1..1.
            InvertLeftY = _rawInvertY.IsChecked == true,
            InvertRightY = _rawInvertY.IsChecked == true,
            HatIsDpad = _rawDpad.IsChecked == true,
            StickDeadzone = double.IsNaN(_rawDeadzone.Value) ? double.NaN : _rawDeadzone.Value
        };
        if (buttonA is { } a) mapping.Buttons[a] = Xbox360Report.A;
        if (buttonB is { } b) mapping.Buttons[b] = Xbox360Report.B;
        bool saved = new RawPadMappingStore().Save(id, mapping, out _);
        _rawDeviceStatus.Text = saved
            ? T("Mapping saved locally. Virtual forwarding is still off.", "Mappatura salvata in locale. L'inoltro virtuale resta disattivato.")
            : T("Invalid mapping or saved file is damaged; nothing was overwritten.",
                "Mappatura non valida o file danneggiato; nessun dato è stato sovrascritto.");
        return Task.CompletedTask;
    }

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
