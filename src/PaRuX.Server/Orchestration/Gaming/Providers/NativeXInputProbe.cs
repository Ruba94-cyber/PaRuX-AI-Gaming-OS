using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

/// <summary>
/// Built-in, read-only XInput probe. No virtual controller, driver, remapping,
/// injection or polling thread. Called only when provider status is requested.
/// </summary>
internal static class NativeXInputProbe
{
    private const uint Success = 0;
    internal const int MaxSlots = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint index, out State state);

    internal static IReadOnlyList<int> ConnectedSlots()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<int>();
        try
        {
            return ConnectedSlots(slot => GetState((uint)slot, out _));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>Query slots once, only when explicitly requested. No raw HID keys,
    /// device serial numbers, polling thread or virtual input injection.</summary>
    internal static IReadOnlyList<GamingControllerStateDto> ReadStates()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<GamingControllerStateDto>();
        var states = new List<GamingControllerStateDto>(MaxSlots);
        try
        {
            for (uint slot = 0; slot < MaxSlots; slot++)
            {
                if (GetState(slot, out State value) != Success) continue;
                var (lx, ly) = NormalizeStick(value.Gamepad.LeftX, value.Gamepad.LeftY, 7849);
                var (rx, ry) = NormalizeStick(value.Gamepad.RightX, value.Gamepad.RightY, 8689);
                states.Add(new GamingControllerStateDto(
                    (int)slot, value.Gamepad.Buttons, lx, ly, rx, ry,
                    value.Gamepad.LeftTrigger / 255f,
                    value.Gamepad.RightTrigger / 255f));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<GamingControllerStateDto>();
        }
        return states;
    }

    /// <summary>Radial deadzone normalization (not applied to Windows input itself).
    /// It exists for consistent diagnostics and is independently testable.</summary>
    internal static (float X, float Y) NormalizeStick(short x, short y, int deadzone)
    {
        if (deadzone is < 0 or >= 32767)
            throw new ArgumentOutOfRangeException(nameof(deadzone));
        double length = Math.Sqrt((double)x * x + (double)y * y);
        if (length <= deadzone) return (0, 0);
        double magnitude = Math.Min(1.0, (length - deadzone) / (32767.0 - deadzone));
        return ((float)(x / length * magnitude), (float)(y / length * magnitude));
    }

    internal static IReadOnlyList<int> ConnectedSlots(Func<int, uint> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var slots = new List<int>(MaxSlots);
        for (int slot = 0; slot < MaxSlots; slot++)
            if (query(slot) == Success) slots.Add(slot);
        return slots;
    }
}
