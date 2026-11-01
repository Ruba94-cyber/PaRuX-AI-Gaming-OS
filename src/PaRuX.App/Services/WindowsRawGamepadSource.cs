using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Windows.Gaming.Input;

namespace PaRuX.Services;

/// <summary>Read-only, on-demand Windows.Gaming.Input metadata; no persistent hardware scanner.</summary>
public sealed record RawGamepadDeviceInfo(string Id, string Name, ushort VendorId, ushort ProductId,
    int ButtonCount, int AxisCount, int SwitchCount);

/// <summary>
/// Real Windows reader for a specifically chosen RawGameController. Windows reports axes in 0..1;
/// the PaRuX virtual-pad mapper expects -1..1 for sticks. Analog triggers must therefore use
/// PadMapping.TriggerAxisIsSigned=true for this reader. Switch 0 is used as the hat when present.
/// This reader never starts a virtual pad and never changes device configuration.
/// </summary>
public sealed class WindowsRawGamepadSource : IRawGamepadSource
{
    private readonly RawGameController _device;
    private readonly bool[] _buttons;
    private readonly GameControllerSwitchPosition[] _switches;
    private readonly double[] _axes;

    private WindowsRawGamepadSource(RawGameController device)
    {
        _device = device;
        DeviceId = device.NonRoamableId;
        _buttons = new bool[device.ButtonCount];
        _switches = new GameControllerSwitchPosition[device.SwitchCount];
        _axes = new double[device.AxisCount];
    }

    public string DeviceId { get; }

    public static IReadOnlyList<RawGamepadDeviceInfo> ListDevices()
    {
        var list = new List<RawGamepadDeviceInfo>();
        try
        {
            foreach (RawGameController device in RawGameController.RawGameControllers)
            {
                if (list.Count >= 16) break;
                if (!Valid(device)) continue;
                list.Add(new RawGamepadDeviceInfo(
                    device.NonRoamableId, device.DisplayName, device.HardwareVendorId,
                    device.HardwareProductId, device.ButtonCount, device.AxisCount, device.SwitchCount));
            }
        }
        catch (Exception) { /* device enumeration unavailable; return only verified entries */ }
        return list;
    }

    public static WindowsRawGamepadSource? TryOpen(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            foreach (RawGameController device in RawGameController.RawGameControllers)
                if (Valid(device) && string.Equals(device.NonRoamableId, id, StringComparison.Ordinal))
                    return new WindowsRawGamepadSource(device);
        }
        catch (Exception) { }
        return null;
    }

    public bool TryRead(out RawGamepadState? state)
    {
        state = null;
        try
        {
            // A removed gamepad can leave a stale WinRT object alive. Require its
            // exact non-roamable identity to remain in the active system snapshot.
            if (!RawGameController.RawGameControllers.Any(d =>
                    string.Equals(d.NonRoamableId, DeviceId, StringComparison.Ordinal)))
                return false;
            _device.GetCurrentReading(_buttons, _switches, _axes);
            double[] normalized = new double[_axes.Length];
            for (int i = 0; i < _axes.Length; i++)
                normalized[i] = double.IsFinite(_axes[i])
                    ? Math.Clamp(_axes[i] * 2.0 - 1.0, -1.0, 1.0)
                    : 0;
            int hat = _switches.Length > 0 ? Hat(_switches[0]) : -1;
            state = new RawGamepadState((bool[])_buttons.Clone(), normalized, hat);
            return true;
        }
        catch (Exception) { return false; }
    }

    // The documented WinRT values are Center=0, Up=1 ... UpLeft=8.
    internal static int Hat(GameControllerSwitchPosition position) =>
        position == GameControllerSwitchPosition.Center ? -1 : (int)position - 1 is >= 0 and <= 7
            ? (int)position - 1 : -1;

    private static bool Valid(RawGameController device)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(device.NonRoamableId) &&
                device.NonRoamableId.Length <= 300 &&
                device.ButtonCount is >= 0 and <= 64 &&
                device.AxisCount is >= 0 and <= 64 &&
                device.SwitchCount is >= 0 and <= 8;
        }
        catch (Exception) { return false; }
    }
}
