using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>One virtual Xbox 360 pad. Disposing it unplugs the pad.</summary>
public interface IVirtualXboxPad : IDisposable
{
    bool Submit(Xbox360Report report);
}

/// <summary>An optional, replaceable way to present a virtual pad to Windows. Nothing starts it automatically.</summary>
public interface IVirtualControllerBackend
{
    string Id { get; }
    /// <returns>A plugged-in pad, or null with a user-readable reason.</returns>
    IVirtualXboxPad? TryCreatePad(out string? error);
}

/// <summary>
/// Client for the optional ViGEmBus virtual gamepad bus driver (installed by the user, never by PaRuX).
/// STATUS: SOURCE IMPLEMENTED, NOT RUN. It could not be exercised here: it needs the driver and a Windows session
/// that allows plugging in devices. Every failure is reported and returns null; it never throws for an absent driver.
///
/// Portions adapted from ViGEmClient (https://github.com/nefarius/ViGEmClient, MIT License, Copyright (c) Benjamin
/// Höglinger-Stelzer) and the IOCTL contract in ViGEmBus's BusShared.h (BSD-3-Clause, Copyright (c) Nefarius Software
/// Solutions e.U.). Both upstream projects are archived and no longer maintained. See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class ViGEmBusClient : IVirtualControllerBackend
{
    public string Id => "vigembus";

    // IOCTL contract (CTL_CODE(FILE_DEVICE_BUS_EXTENDER, function, METHOD_BUFFERED, FILE_WRITE_DATA)).
    internal const uint DeviceBusExtender = 0x2A;
    internal const uint FileWriteAccess = 2;
    internal static uint Ioctl(uint function) => (DeviceBusExtender << 16) | (FileWriteAccess << 14) | (function << 2);
    internal static readonly uint PluginTarget = Ioctl(0x801);
    internal static readonly uint UnplugTarget = Ioctl(0x802);
    internal static readonly uint CheckVersion = Ioctl(0x803);
    internal static readonly uint WaitDeviceReady = Ioctl(0x804);
    internal static readonly uint XusbSubmitReport = Ioctl(0x801 + 0x201);

    internal const uint CommonVersion = 1;
    internal const int Xbox360Wired = 0;
    internal const int MaxTargets = 16;
    private const uint ErrorIoPending = 997;
    private const uint ErrorInvalidParameter = 87;

    // --- wire structures (all little-endian, no padding beyond what the fields need) -----------------------
    internal static byte[] PluginRequest(uint serial) => Pack(16, serial, Xbox360Wired, 0, 0);
    internal static byte[] UnplugRequest(uint serial) => Pack(8, serial);
    internal static byte[] VersionRequest() => Pack(8, CommonVersion);
    internal static byte[] WaitReadyRequest(uint serial) => Pack(8, serial);

    internal static byte[] SubmitRequest(uint serial, Xbox360Report r)
    {
        var buffer = new byte[20];
        BitConverter.GetBytes(20u).CopyTo(buffer, 0);
        BitConverter.GetBytes(serial).CopyTo(buffer, 4);
        BitConverter.GetBytes(r.Buttons).CopyTo(buffer, 8);
        buffer[10] = r.LeftTrigger;
        buffer[11] = r.RightTrigger;
        BitConverter.GetBytes(r.LeftX).CopyTo(buffer, 12);
        BitConverter.GetBytes(r.LeftY).CopyTo(buffer, 14);
        BitConverter.GetBytes(r.RightX).CopyTo(buffer, 16);
        BitConverter.GetBytes(r.RightY).CopyTo(buffer, 18);
        return buffer;
    }

    // Pack: first value is the structure size; remaining values are 32-bit fields, except the trailing vendor/product
    // ids of the plug-in request which are two zero 16-bit fields (written as zeros inside the final 32-bit slot).
    private static byte[] Pack(uint size, params uint[] fields)
    {
        var buffer = new byte[size];
        BitConverter.GetBytes(size).CopyTo(buffer, 0);
        for (int i = 0; i < fields.Length && 4 + i * 4 + 4 <= size; i++)
            BitConverter.GetBytes(fields[i]).CopyTo(buffer, 4 + i * 4);
        return buffer;
    }

    public IVirtualXboxPad? TryCreatePad(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) { error = "Virtual controllers need Windows."; return null; }
        try
        {
            SafeFileHandle? bus = OpenBus(out error);
            if (bus is null) return null;
            if (!Control(bus, CheckVersion, VersionRequest()))
            {
                bus.Dispose();
                error = "The installed virtual controller driver is a different version than PaRuX expects.";
                return null;
            }
            for (uint serial = 1; serial <= MaxTargets; serial++)
            {
                if (!Control(bus, PluginTarget, PluginRequest(serial))) continue;
                // Drivers from v1.17 keep this request pending until the pad can take reports; older ones answer
                // ERROR_INVALID_PARAMETER, which is treated as "ready" (same rule as the reference client).
                if (!Control(bus, WaitDeviceReady, WaitReadyRequest(serial)) && Marshal.GetLastWin32Error() != ErrorInvalidParameter)
                {
                    Control(bus, UnplugTarget, UnplugRequest(serial));
                    continue;
                }
                return new Pad(bus, serial);
            }
            bus.Dispose();
            error = "The virtual controller driver has no free slot.";
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception or IOException)
        {
            error = "The virtual controller driver could not be used.";
            return null;
        }
    }

    private sealed class Pad : IVirtualXboxPad
    {
        private readonly SafeFileHandle _bus;
        private readonly uint _serial;
        private bool _disposed;
        public Pad(SafeFileHandle bus, uint serial) { _bus = bus; _serial = serial; }

        public bool Submit(Xbox360Report report) => !_disposed && Control(_bus, XusbSubmitReport, SubmitRequest(_serial, report));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Control(_bus, UnplugTarget, UnplugRequest(_serial)); } finally { _bus.Dispose(); }
        }
    }

    // --- native plumbing ------------------------------------------------------------------------------------

    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileShareReadWrite = 3, OpenExisting = 3, FileAttributeNormal = 0x80, FileFlagOverlapped = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public uint CbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    private static readonly Guid BusInterface = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle device, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static SafeFileHandle? OpenBus(out string? error)
    {
        error = "The virtual controller driver (ViGEmBus) is not installed.";
        Guid guid = BusInterface;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data); index++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required is 0 or > 4096) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on 64-bit, 6 on 32-bit; the path follows at offset 4.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    SafeFileHandle handle = CreateFile(path, GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting,
                        FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
                    if (handle.IsInvalid) { error = "The virtual controller driver could not be opened (is another program using it?)."; continue; }
                    error = null;
                    return handle;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>One overlapped IOCTL, waited to completion (the handle is opened overlapped, like the reference client).</summary>
    private static bool Control(SafeFileHandle bus, uint code, byte[] input)
    {
        IntPtr overlapped = Marshal.AllocHGlobal(32);
        for (int i = 0; i < 32; i++) Marshal.WriteByte(overlapped, i, 0);
        IntPtr evt = CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        try
        {
            Marshal.WriteIntPtr(overlapped, 24, evt); // OVERLAPPED.hEvent on 64-bit (offset 16 on 32-bit)
            if (IntPtr.Size == 4) { Marshal.WriteIntPtr(overlapped, 24, IntPtr.Zero); Marshal.WriteIntPtr(overlapped, 16, evt); }
            bool ok = DeviceIoControl(bus, code, input, (uint)input.Length, IntPtr.Zero, 0, out _, overlapped);
            if (!ok && (uint)Marshal.GetLastWin32Error() != ErrorIoPending) return false;
            return GetOverlappedResult(bus, overlapped, out _, true);
        }
        finally
        {
            if (evt != IntPtr.Zero) CloseHandle(evt);
            Marshal.FreeHGlobal(overlapped);
        }
    }
}
