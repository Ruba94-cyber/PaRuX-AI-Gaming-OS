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
    internal static IReadOnlyList<GamingControllerStateDto> ReadStates(
        GamingControllerProfile? profile = null,
        Func<int, ControllerProfileMath.RawPad?>? readSlot = null)
    {
        if (readSlot is null && !OperatingSystem.IsWindows()) return Array.Empty<GamingControllerStateDto>();
        profile ??= GamingControllerProfile.Default;
        readSlot ??= ReadNativeSlot;
        var states = new List<GamingControllerStateDto>(MaxSlots);
        try
        {
            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (readSlot(slot) is not { } raw) continue;
                states.Add(ControllerProfileMath.Apply(slot, raw, profile));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<GamingControllerStateDto>();
        }
        return states;
    }

    private static ControllerProfileMath.RawPad? ReadNativeSlot(int slot)
    {
        if (GetState((uint)slot, out State value) != Success) return null;
        Gamepad g = value.Gamepad;
        return new ControllerProfileMath.RawPad(g.Buttons, g.LeftTrigger, g.RightTrigger, g.LeftX, g.LeftY, g.RightX, g.RightY);
    }

    /// <summary>
    /// Explicit, bounded calibration: samples one slot's resting sticks (about 0.6 s, user-initiated,
    /// never a background poll) and recommends deadzones. Returns null when the slot is absent or
    /// not enough samples could be read.
    /// </summary>
    internal static async Task<(int Left, int Right)?> CalibrateAsync(
        int slot, CancellationToken ct,
        Func<int, ControllerProfileMath.RawPad?>? readSlot = null,
        int samples = 30, TimeSpan? interval = null)
    {
        if (slot is < 0 or >= MaxSlots) return null;
        if (readSlot is null && !OperatingSystem.IsWindows()) return null;
        readSlot ??= ReadNativeSlot;
        var left = new List<(short, short)>(samples);
        var right = new List<(short, short)>(samples);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (readSlot(slot) is not { } raw) return null;
                left.Add((raw.LeftX, raw.LeftY));
                right.Add((raw.RightX, raw.RightY));
                if (i + 1 < samples) await Task.Delay(interval ?? TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        int? l = ControllerProfileMath.RecommendDeadzone(left);
        int? r = ControllerProfileMath.RecommendDeadzone(right);
        return l is { } lv && r is { } rv ? (lv, rv) : null;
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
