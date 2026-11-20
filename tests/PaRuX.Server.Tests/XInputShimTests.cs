using System.Runtime.InteropServices;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The driverless virtual pad end to end: the real native shim DLL (built by scripts/build-xinput-shim.ps1) is loaded into this
/// process and asked for pad state while PaRuX's shared-memory backend publishes it. No game, no driver, no second process.
/// The test uses the shim's own section name, so it stands down when a real PaRuX already owns that section.
/// </summary>
[Collection("XInputShimSection")]
public sealed class XInputShimTests
{
    private const uint Success = 0, NotConnected = 1167, Empty = 4306;

    [StructLayout(LayoutKind.Sequential)] private struct XState { public uint Packet; public ushort Buttons; public byte Lt, Rt; public short Lx, Ly, Rx, Ry; }
    [StructLayout(LayoutKind.Sequential)] private struct XVib { public ushort Left, Right; }
    [StructLayout(LayoutKind.Sequential)] private struct XCaps { public byte Type, SubType; public ushort Flags; public XState Pad; public XVib Vib; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetStateFn(uint index, ref XState state);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetStateFn(uint index, ref XVib vib);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetCapsFn(uint index, uint flags, ref XCaps caps);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetKeystrokeFn(uint index, uint reserved, IntPtr key);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private static string? ShimPath()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "artifacts", "xinput-shim", "x64", "parux_xinput.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static bool SectionOwnedElsewhere()
    {
        try { using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(XInputSectionLayout.SectionName); return true; }
        catch (FileNotFoundException) { return false; }
        catch (Exception) { return false; }
    }

    [Fact]
    public void The_layout_matches_the_native_structs_byte_for_byte()
    {
        Assert.Equal(16 + 4 * 28, XInputSectionLayout.TotalSize);
        byte[] section = new byte[XInputSectionLayout.TotalSize];
        XInputSectionLayout.WriteHeader(section, 1234, 0b0101);
        Assert.Equal(0x31495850u, BitConverter.ToUInt32(section, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(section, 4));
        Assert.Equal(1234u, BitConverter.ToUInt32(section, 8));
        Assert.Equal(5u, BitConverter.ToUInt32(section, 12));

        XInputSectionLayout.WriteSlot(section, 2, 9, true, new Xbox360Report(0x1010, 200, 100, -5, 6, 32767, -32768));
        int o = XInputSectionLayout.SlotOffset(2);
        Assert.Equal(9u, BitConverter.ToUInt32(section, o));
        Assert.Equal(1u, BitConverter.ToUInt32(section, o + 4));
        Assert.Equal((ushort)0x1010, BitConverter.ToUInt16(section, o + 8));
        Assert.Equal(200, section[o + 10]);
        Assert.Equal(100, section[o + 11]);
        Assert.Equal((short)-5, BitConverter.ToInt16(section, o + 12));
        Assert.Equal((short)-32768, BitConverter.ToInt16(section, o + 18));
    }

    [Fact]
    public void Slots_are_handed_out_once_and_come_back_when_a_pad_is_disposed()
    {
        if (SectionOwnedElsewhere()) return;
        var backend = new SharedMemoryXInputBackend();
        var pads = new List<IVirtualXboxPad>();
        for (int i = 0; i < 4; i++) pads.Add(backend.TryCreatePad(out _)!);
        Assert.All(pads, Assert.NotNull);
        Assert.Null(backend.TryCreatePad(out string? full));
        Assert.Contains("in use", full);

        pads[1].Dispose();
        IVirtualXboxPad? again = backend.TryCreatePad(out _);
        Assert.NotNull(again);
        foreach (IVirtualXboxPad p in pads.Append(again!)) p.Dispose();
        Assert.False(SectionOwnedElsewhere(), "the section is released when the last pad goes");
    }

    [Fact]
    public void The_real_shim_serves_the_published_pad_and_forwards_to_windows_otherwise()
    {
        string? dll = ShimPath();
        if (dll is null || !Environment.Is64BitProcess || SectionOwnedElsewhere()) return;   // the shim was not built on this PC
        IntPtr module = LoadLibraryW(dll);
        Assert.NotEqual(IntPtr.Zero, module);
        var getState = Marshal.GetDelegateForFunctionPointer<GetStateFn>(GetProcAddress(module, "XInputGetState"));
        var getStateEx = Marshal.GetDelegateForFunctionPointer<GetStateFn>(GetProcAddress(module, (IntPtr)100));
        var setState = Marshal.GetDelegateForFunctionPointer<SetStateFn>(GetProcAddress(module, "XInputSetState"));
        var getCaps = Marshal.GetDelegateForFunctionPointer<GetCapsFn>(GetProcAddress(module, "XInputGetCapabilities"));
        var getKey = Marshal.GetDelegateForFunctionPointer<GetKeystrokeFn>(GetProcAddress(module, "XInputGetKeystroke"));

        // PaRuX is not running: the shim must behave like Windows' own XInput (a pad if one is XInput, else "not connected").
        var idle = new XState();
        uint passThrough = getState(0, ref idle);
        Assert.True(passThrough is Success or NotConnected, "pass-through answered " + passThrough);

        var backend = new SharedMemoryXInputBackend();
        IVirtualXboxPad pad = backend.TryCreatePad(out _)!;
        try
        {
            Assert.True(pad.Submit(new Xbox360Report(Xbox360Report.A | Xbox360Report.DpadUp, 255, 17, -1000, 2000, 32767, -32768)));
            var state = new XState();
            Assert.Equal(Success, getState(0, ref state));
            Assert.Equal((ushort)(Xbox360Report.A | Xbox360Report.DpadUp), state.Buttons);
            Assert.Equal(255, state.Lt);
            Assert.Equal(17, state.Rt);
            Assert.Equal((short)-1000, state.Lx);
            Assert.Equal((short)2000, state.Ly);
            Assert.Equal(short.MaxValue, state.Rx);
            Assert.Equal(short.MinValue, state.Ry);
            uint packetBefore = state.Packet;

            // Same state again: the packet number must not move (games use it to skip unchanged reads); a new state moves it.
            pad.Submit(new Xbox360Report(Xbox360Report.A | Xbox360Report.DpadUp, 255, 17, -1000, 2000, 32767, -32768));
            getState(0, ref state);
            Assert.Equal(packetBefore, state.Packet);
            pad.Submit(new Xbox360Report(Xbox360Report.B, 0, 0, 0, 0, 0, 0));
            getState(0, ref state);
            Assert.True(state.Packet > packetBefore);

            // The Ex entry point (ordinal 100) serves the same pad; capabilities say "gamepad"; keystrokes are empty.
            var ex = new XState();
            Assert.Equal(Success, getStateEx(0, ref ex));
            Assert.Equal(Xbox360Report.B, ex.Buttons);
            var caps = new XCaps();
            Assert.Equal(Success, getCaps(0, 0, ref caps));
            Assert.Equal(1, caps.Type);
            Assert.Equal(1, caps.SubType);
            Assert.Equal(Empty, getKey(0, 0, IntPtr.Zero));

            // A game's rumble request lands in the section for PaRuX to forward.
            var vib = new XVib { Left = 40000, Right = 12345 };
            Assert.Equal(Success, setState(0, ref vib));

            // A slot PaRuX does not serve still goes to Windows.
            var other = new XState();
            Assert.True(getState(1, ref other) is Success or NotConnected);
        }
        finally { pad.Dispose(); }

        // After the pad is gone the shim is a plain pass-through again.
        var after = new XState();
        Assert.True(getState(0, ref after) is Success or NotConnected);
        Assert.NotEqual(0xFFFFFFFFu, after.Packet);
    }

    [Fact]
    public void A_game_that_passes_a_null_pointer_gets_an_error_not_a_crash()
    {
        string? dll = ShimPath();
        if (dll is null || !Environment.Is64BitProcess) return;
        IntPtr module = LoadLibraryW(dll);
        var getKey = Marshal.GetDelegateForFunctionPointer<GetKeystrokeFn>(GetProcAddress(module, "XInputGetKeystroke"));
        var getStateRaw = Marshal.GetDelegateForFunctionPointer<NullStateFn>(GetProcAddress(module, "XInputGetState"));
        Assert.Equal(160u, getStateRaw(0, IntPtr.Zero));   // ERROR_BAD_ARGUMENTS, the answer real XInput gives
        Assert.NotNull(getKey);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint NullStateFn(uint index, IntPtr state);
}
