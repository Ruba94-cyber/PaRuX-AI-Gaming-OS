using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only Windows CCD color capability probe. A modern WCG desktop is NOT HDR.
/// Never changes display modes, color settings, EDID, refresh, VRR or system registry.
/// Unknown/driver-refused data never authorizes an HDR-required launch.
/// </summary>
internal static class GamingHdrDisplayProbe
{
    private const uint ActivePaths = 2;
    private const int InsufficientBuffer = 122;
    private const uint GetSourceName = 1;
    private const uint GetAdvancedColorInfo = 9;
    // Win11 24H2+ CCD: distinguishes SDR / WCG / HDR.
    private const uint GetAdvancedColorInfo2 = 15;
    private const int MaxPaths = 32, MaxModes = 128;
    private const int NativeModeInfoSafeStrideBytes = 128;

    internal sealed record ColorState(bool? HdrSupported, bool? HdrEnabled, string ColorMode)
    {
        public static ColorState Unknown { get; } = new(null, null, "unknown");
    }

    /// <summary>Pure classifier: v2 explicitly disambiguates HDR from WCG.</summary>
    internal static ColorState FromV2(uint flags, int activeColorMode)
    {
        bool supported = (flags & (1u << 4)) != 0; // highDynamicRangeSupported
        if (!supported) return new(false, false, activeColorMode == 1 ? "wcg" : "sdr");
        return activeColorMode switch
        {
            0 => new(true, false, "sdr"),
            1 => new(true, false, "wcg"),
            2 => new(true, true, "hdr"),
            _ => ColorState.Unknown
        };
    }

    /// <summary>
    /// Old AdvancedColorInfo conflates HDR with WCG, so only report that
    /// advanced color is ON. Never claim that HDR is enabled from this API.
    /// </summary>
    internal static ColorState FromV1(uint flags)
    {
        bool supported = (flags & 1u) != 0;
        if (!supported) return ColorState.Unknown;
        return new(null, null, (flags & 2u) != 0 ? "advanced-color-unknown" : "unknown");
    }

    public static ColorState Read(string displayId)
    {
        if (!OperatingSystem.IsWindows() ||
            !ConsoleDisplaySelectionPolicy.ValidDisplayId(displayId))
            return ColorState.Unknown;

        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(ActivePaths, out uint pathCount,
                        out uint modeCount) != 0 ||
                    pathCount is 0 or > MaxPaths || modeCount > MaxModes)
                    return ColorState.Unknown;

                var paths = new DisplayPath[pathCount];
                IntPtr modes = IntPtr.Zero;
                try
                {
                    // Mode buffers are never read here. Reserve more than the
                    // documented native MODE_INFO slot to bound layout risk.
                    modes = Marshal.AllocHGlobal(checked((int)modeCount * NativeModeInfoSafeStrideBytes));
                    int rc = QueryDisplayConfig(ActivePaths, ref pathCount,
                        paths, ref modeCount, modes, IntPtr.Zero);
                    if (rc == InsufficientBuffer) continue;
                    if (rc != 0) return ColorState.Unknown;

                    (Luid Adapter, uint Id)? target = null;
                    for (int i = 0; i < pathCount; i++)
                    {
                        DisplayPath path = paths[i];
                        var source = new SourceName
                        {
                            Header = new DeviceInfoHeader
                            {
                                Type = GetSourceName,
                                Size = (uint)Marshal.SizeOf<SourceName>(),
                                Adapter = path.Source.Adapter,
                                Id = path.Source.Id
                            },
                            DeviceName = string.Empty
                        };
                        if (DisplayConfigGetSourceName(ref source) != 0 ||
                            !string.Equals(source.DeviceName.TrimEnd('\0'),
                                displayId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Multiple output targets for a mirrored GDI display are
                        // ambiguous. Fail rather than attribute HDR to the wrong TV.
                        if (target is not null) return ColorState.Unknown;
                        target = (path.Target.Adapter, path.Target.Id);
                    }
                    if (target is null) return ColorState.Unknown;
                    DeviceInfoHeader header = new()
                    {
                        Type = GetAdvancedColorInfo2,
                        Size = (uint)Marshal.SizeOf<ColorInfo2>(),
                        Adapter = target.Value.Adapter,
                        Id = target.Value.Id
                    };
                    var v2 = new ColorInfo2 { Header = header };
                    if (DisplayConfigGetColorInfo2(ref v2) == 0)
                        return FromV2(v2.Flags, v2.ActiveColorMode);

                    header.Type = GetAdvancedColorInfo;
                    header.Size = (uint)Marshal.SizeOf<ColorInfo1>();
                    var v1 = new ColorInfo1 { Header = header };
                    return DisplayConfigGetColorInfo1(ref v1) == 0
                        ? FromV1(v1.Flags) : ColorState.Unknown;
                }
                finally { if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or OverflowException or OutOfMemoryException
            or ArgumentException or System.ComponentModel.Win32Exception)
        { }
        return ColorState.Unknown;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid Adapter;
        public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator;
        public uint ScanLineOrdering;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        public SourceInfo Source;
        public TargetInfo Target;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ColorInfo1
    {
        public DeviceInfoHeader Header;
        public uint Flags, ColorEncoding, BitsPerChannel;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ColorInfo2
    {
        public DeviceInfoHeader Header;
        public uint Flags, ColorEncoding, BitsPerChannel;
        public int ActiveColorMode;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, IntPtr modes, IntPtr topologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetSourceName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetColorInfo1(ref ColorInfo1 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetColorInfo2(ref ColorInfo2 info);
}
