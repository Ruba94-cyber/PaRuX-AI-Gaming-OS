using System.Runtime.InteropServices;

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

    internal static IReadOnlyList<int> ConnectedSlots(Func<int, uint> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var slots = new List<int>(MaxSlots);
        for (int slot = 0; slot < MaxSlots; slot++)
            if (query(slot) == Success) slots.Add(slot);
        return slots;
    }
}
