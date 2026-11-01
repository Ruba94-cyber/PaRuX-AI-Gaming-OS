using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>One concrete display mode of one monitor, including its desktop position.</summary>
internal sealed record DisplayModeEntry(int Width, int Height, int RefreshRateHz, int BitsPerPixel, int X, int Y);

/// <summary>Seam over the Windows display-settings API so the apply/confirm/revert logic is testable.</summary>
internal interface IDisplayModeApi
{
    DisplayModeEntry? Current(string displayId);
    IReadOnlyList<DisplayModeEntry> Supported(string displayId);
    /// <summary>Validates a mode with the driver without applying it.</summary>
    bool Test(string displayId, DisplayModeEntry mode);
    /// <summary>Applies a mode for the current session only (not saved to the registry).</summary>
    bool ApplyTemporary(string displayId, DisplayModeEntry mode);
    /// <summary>Saves a mode as the monitor's default.</summary>
    bool ApplyPermanent(string displayId, DisplayModeEntry mode);
}

internal sealed class WindowsDisplayModeApi : IDisplayModeApi
{
    private const int CurrentSettings = -1;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsTest = 0x2;
    private const uint FieldPosition = 0x20;
    private const uint FieldBits = 0x40000;
    private const uint FieldWidth = 0x80000;
    private const uint FieldHeight = 0x100000;
    private const uint FieldFrequency = 0x400000;
    private const int Successful = 0;
    private const int MaxModes = 4096;

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

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DevMode settings);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode settings, IntPtr hwnd, uint flags, IntPtr param);

    private static DevMode NewDevMode() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };

    private static DisplayModeEntry ToEntry(DevMode m) => new(
        (int)m.PixelsWidth, (int)m.PixelsHeight, (int)m.Frequency, (int)m.BitsPerPel, m.PositionX, m.PositionY);

    public DisplayModeEntry? Current(string displayId)
    {
        DevMode m = NewDevMode();
        return EnumDisplaySettings(displayId, CurrentSettings, ref m) ? ToEntry(m) : null;
    }

    public IReadOnlyList<DisplayModeEntry> Supported(string displayId)
    {
        var list = new List<DisplayModeEntry>();
        for (int i = 0; i < MaxModes; i++)
        {
            DevMode m = NewDevMode();
            if (!EnumDisplaySettings(displayId, i, ref m)) break;
            list.Add(ToEntry(m));
        }
        return list;
    }

    private static int Change(string displayId, DisplayModeEntry mode, uint flags)
    {
        DevMode m = NewDevMode();
        if (!EnumDisplaySettings(displayId, CurrentSettings, ref m)) return -1;
        m.PixelsWidth = (uint)mode.Width;
        m.PixelsHeight = (uint)mode.Height;
        m.Frequency = (uint)mode.RefreshRateHz;
        m.BitsPerPel = (uint)mode.BitsPerPixel;
        m.PositionX = mode.X;
        m.PositionY = mode.Y;
        m.Fields = FieldWidth | FieldHeight | FieldFrequency | FieldBits | FieldPosition;
        return ChangeDisplaySettingsEx(displayId, ref m, IntPtr.Zero, flags, IntPtr.Zero);
    }

    public bool Test(string displayId, DisplayModeEntry mode) => Change(displayId, mode, CdsTest) == Successful;
    public bool ApplyTemporary(string displayId, DisplayModeEntry mode) => Change(displayId, mode, 0) == Successful;
    public bool ApplyPermanent(string displayId, DisplayModeEntry mode) => Change(displayId, mode, CdsUpdateRegistry) == Successful;
}
