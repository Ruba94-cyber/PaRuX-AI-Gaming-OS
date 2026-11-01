using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Captures the OS-reported desktop monitors on demand. No EDID serial numbers,
/// display setting writes, driver calls, long-running polling or user tracking.
/// </summary>
public sealed class GamingDisplayTopologyService
{
    private const uint PrimaryMonitor = 1;
    private const int MaxMonitors = 16;
    private const int CurrentSettings = -1;
    private readonly Func<IReadOnlyList<GamingDisplayModeDto>> _read;

    public GamingDisplayTopologyService() : this(ReadWindowsTopology) { }
    internal GamingDisplayTopologyService(Func<IReadOnlyList<GamingDisplayModeDto>> read) =>
        _read = read ?? throw new ArgumentNullException(nameof(read));

    public GamingDisplaySnapshotDto Snapshot()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Display topology requires a Windows desktop.", Array.Empty<GamingDisplayModeDto>());
        try
        {
            var modes = NormalizeModes(_read());
            return modes.Length == 0
                ? new(false, "Windows did not expose an active desktop monitor.", Array.Empty<GamingDisplayModeDto>())
                : new(true, "Current display topology only. HDR, VRR and pending changes are not verified.", modes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or InvalidOperationException)
        {
            return new(false, "Display topology is temporarily unavailable.", Array.Empty<GamingDisplayModeDto>());
        }
    }

    internal static GamingDisplayModeDto[] NormalizeModes(IEnumerable<GamingDisplayModeDto> readings) =>
        readings.Take(MaxMonitors).Where(ValidMode)
            .GroupBy(x => x.DisplayId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.Primary).ThenBy(x => x.X).ThenBy(x => x.Y)
            .ToArray();

    internal static bool ValidMode(GamingDisplayModeDto mode) =>
        !string.IsNullOrWhiteSpace(mode.DisplayId) && mode.DisplayId.Length <= 64 &&
        mode.Width is > 0 and <= 32768 && mode.Height is > 0 and <= 32768 &&
        mode.RefreshRateHz is >= 0 and <= 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    // DEVMODEW: the 16-byte display union is expanded as position X/Y,
    // orientation and fixed output. Query only frequency; never change modes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PixelsWidth;
        public uint PixelsHeight;
        public uint DisplayFlags;
        public uint Frequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DevMode settings);

    private static IReadOnlyList<GamingDisplayModeDto> ReadWindowsTopology()
    {
        var result = new List<GamingDisplayModeDto>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr handle, IntPtr hdc, ref Rect bounds, IntPtr context) =>
            {
                if (result.Count >= MaxMonitors) return false;
                var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfo(handle, ref info) || string.IsNullOrEmpty(info.Device))
                    return true;

                int width = info.Monitor.Right - info.Monitor.Left;
                int height = info.Monitor.Bottom - info.Monitor.Top;
                var settings = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
                int hz = 0; // 0 means Windows did not report a reliable rate.
                if (EnumDisplaySettings(info.Device, CurrentSettings, ref settings) &&
                    settings.Frequency is >= 2 and <= 1000)
                    hz = (int)settings.Frequency;

                result.Add(new GamingDisplayModeDto(info.Device, (info.Flags & PrimaryMonitor) != 0,
                    info.Monitor.Left, info.Monitor.Top, width, height, hz));
                return true;
            }, IntPtr.Zero);
        return result;
    }
}
