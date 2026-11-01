using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

/// <summary>
/// Answer to "can PaRuX present a virtual Xbox controller?". It is a read-only look at Windows for an installed
/// virtual-bus driver. PaRuX never installs a driver silently and does not forward input to it yet, so
/// <see cref="CanForwardInput"/> stays false: the answer exists so the UI can explain the optional step honestly.
/// </summary>
public sealed record VirtualControllerStatus(
    string Backend, bool DriverInstalled, bool CanForwardInput, string Message);

public static class VirtualControllerProbe
{
    // Device-interface class of the ViGEmBus virtual gamepad bus driver (published in its client library).
    private static readonly Guid ViGEmBusInterface = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    public static VirtualControllerStatus Probe() => Probe(OperatingSystem.IsWindows() ? IsViGEmBusPresent : () => false);

    internal static VirtualControllerStatus Probe(Func<bool> driverPresent)
    {
        bool installed;
        try { installed = driverPresent(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or ExternalException)
        { installed = false; }

        return installed
            ? new("ViGEmBus", true, false,
                "A virtual controller driver (ViGEmBus) is installed. PaRuX does not forward input to it yet, so it stays unused.")
            : new("ViGEmBus", false, false,
                "No virtual controller driver was found. It is optional: PaRuX works with real controllers without it, and never installs drivers on its own.");
    }

    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private static bool IsViGEmBusPresent()
    {
        Guid guid = ViGEmBusInterface;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            return SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
