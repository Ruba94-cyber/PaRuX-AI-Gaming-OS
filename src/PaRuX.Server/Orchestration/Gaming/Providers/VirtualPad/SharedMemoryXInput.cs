using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>
/// The byte layout PaRuX and the native XInput shim (native/xinput-shim/parux_xinput.c) share. Keep both in step: the shim reads
/// this section, so a change here is a change of <see cref="Version"/>.
/// </summary>
public static class XInputSectionLayout
{
    public const string SectionName = "Local\\PaRuX.XInput.v1";
    public const uint Magic = 0x31495850;   // 'PXI1'
    public const uint Version = 1;
    public const int Slots = 4;
    public const int HeaderSize = 16;
    public const int SlotSize = 28;
    public const int TotalSize = HeaderSize + Slots * SlotSize;

    public static int SlotOffset(int slot) => HeaderSize + slot * SlotSize;

    public static void WriteHeader(Span<byte> section, uint heartbeatMs, uint enabledMask)
    {
        BitConverter.TryWriteBytes(section[0..4], Magic);
        BitConverter.TryWriteBytes(section[4..8], Version);
        BitConverter.TryWriteBytes(section[8..12], heartbeatMs);
        BitConverter.TryWriteBytes(section[12..16], enabledMask);
    }

    public static void WriteSlot(Span<byte> section, int slot, uint packet, bool connected, Xbox360Report r)
    {
        Span<byte> s = section.Slice(SlotOffset(slot), SlotSize);
        BitConverter.TryWriteBytes(s[0..4], packet);
        BitConverter.TryWriteBytes(s[4..8], connected ? 1u : 0u);
        BitConverter.TryWriteBytes(s[8..10], r.Buttons);
        s[10] = r.LeftTrigger;
        s[11] = r.RightTrigger;
        BitConverter.TryWriteBytes(s[12..14], r.LeftX);
        BitConverter.TryWriteBytes(s[14..16], r.LeftY);
        BitConverter.TryWriteBytes(s[16..18], r.RightX);
        BitConverter.TryWriteBytes(s[18..20], r.RightY);
    }

    /// <summary>The rumble strengths the game last asked for (the shim writes them; PaRuX forwards them to the physical pad).</summary>
    public static (ushort Low, ushort High) ReadRumble(ReadOnlySpan<byte> section, int slot)
    {
        ReadOnlySpan<byte> s = section.Slice(SlotOffset(slot), SlotSize);
        return (BitConverter.ToUInt16(s[20..22]), BitConverter.ToUInt16(s[22..24]));
    }

    public static void ClearRumble(Span<byte> section, int slot) => section.Slice(SlotOffset(slot) + 20, 4).Clear();
}

/// <summary>
/// A driverless virtual Xbox pad: instead of a kernel bus driver (ViGEmBus) it publishes the pad state in a shared-memory
/// section that the PaRuX XInput shim, placed in a game's own folder, serves to the game. Games that read XInput see an Xbox pad;
/// games that read DirectInput or raw HID do not (that is what a kernel driver would add). Nothing is installed system-wide.
/// </summary>
public sealed class SharedMemoryXInputBackend : IVirtualControllerBackend
{
    private static readonly object Gate = new();
    private static MemoryMappedFile? _file;
    private static MemoryMappedViewAccessor? _view;
    private static Timer? _heartbeat;
    private static uint _mask;
    private readonly string _sectionName;

    public SharedMemoryXInputBackend() : this(XInputSectionLayout.SectionName) { }

    /// <summary>A different name lets tests run beside a real PaRuX without touching its section.</summary>
    public SharedMemoryXInputBackend(string sectionName) => _sectionName = sectionName;

    public string Id => "parux-xinput-shim";

    public IVirtualXboxPad? TryCreatePad(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) { error = "The XInput shim backend is Windows only."; return null; }
        lock (Gate)
        {
            try
            {
                if (_file is null)
                {
                    _file = MemoryMappedFile.CreateOrOpen(_sectionName, XInputSectionLayout.TotalSize);
                    _view = _file.CreateViewAccessor(0, XInputSectionLayout.TotalSize);
                    _mask = 0;
                    PublishHeader();
                    _heartbeat = new Timer(_ => { lock (Gate) PublishHeader(); }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
                }
                for (int slot = 0; slot < XInputSectionLayout.Slots; slot++)
                {
                    if ((_mask & (1u << slot)) != 0) continue;
                    _mask |= 1u << slot;
                    PublishHeader();
                    return new Pad(this, slot);
                }
                error = "All four virtual pad slots are in use.";
                ReleaseIfIdle();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                error = "The shared section for the XInput shim could not be created.";
                ReleaseIfIdle();
                return null;
            }
        }
    }

    /// <summary>
    /// The vibration a game last asked for on a slot (the shim writes it), or (0, 0) when no pad is published. The bridge forwards it to
    /// the physical controller. Reading never changes the section.
    /// </summary>
    public static (ushort Low, ushort High) ReadRumble(int slot)
    {
        if (slot < 0 || slot >= XInputSectionLayout.Slots) return (0, 0);
        lock (Gate)
        {
            if (_view is null) return (0, 0);
            var buffer = new byte[XInputSectionLayout.TotalSize];
            _view.ReadArray(0, buffer, 0, buffer.Length);
            return XInputSectionLayout.ReadRumble(buffer, slot);
        }
    }

    private static void PublishHeader()
    {
        if (_view is null) return;
        Span<byte> header = stackalloc byte[XInputSectionLayout.HeaderSize];
        XInputSectionLayout.WriteHeader(header, unchecked((uint)Environment.TickCount), _mask);
        _view.WriteArray(0, header.ToArray(), 0, header.Length);
    }

    private static void ReleaseIfIdle()
    {
        if (_mask != 0) return;
        _heartbeat?.Dispose(); _heartbeat = null;
        // A stale heartbeat is how the shim knows PaRuX is gone, so leave the header zeroed rather than keep it fresh.
        try { _view?.WriteArray(0, new byte[XInputSectionLayout.HeaderSize], 0, XInputSectionLayout.HeaderSize); } catch (Exception) { }
        _view?.Dispose(); _view = null;
        _file?.Dispose(); _file = null;
    }

    private void Release(int slot)
    {
        lock (Gate)
        {
            try
            {
                if (_view is not null)
                {
                    var blank = new byte[XInputSectionLayout.SlotSize];
                    _view.WriteArray(XInputSectionLayout.SlotOffset(slot), blank, 0, blank.Length);
                }
            }
            catch (Exception) { }
            _mask &= ~(1u << slot);
            PublishHeader();
            ReleaseIfIdle();
        }
    }

    private sealed class Pad : IVirtualXboxPad
    {
        private readonly SharedMemoryXInputBackend _owner;
        private readonly int _slot;
        private uint _packet;
        private Xbox360Report _last;
        private bool _disposed;

        public Pad(SharedMemoryXInputBackend owner, int slot) { _owner = owner; _slot = slot; }

        public bool Submit(Xbox360Report report)
        {
            lock (Gate)
            {
                if (_disposed || _view is null) return false;
                if (_packet == 0 || !report.Equals(_last)) _packet++;   // the packet number only moves when the state does, as in real XInput
                _last = report;
                var scratch = new byte[XInputSectionLayout.TotalSize];
                XInputSectionLayout.WriteSlot(scratch, _slot, _packet, connected: true, report);
                // Keep the rumble the game asked for: only the 20 state bytes of this slot are rewritten.
                int offset = XInputSectionLayout.SlotOffset(_slot);
                _view.WriteArray(offset, scratch, offset, 20);
                return true;
            }
        }

        /// <summary>The rumble the game last requested for this pad, to be forwarded to the physical controller.</summary>
        public (ushort Low, ushort High) ReadRumble()
        {
            lock (Gate)
            {
                if (_disposed || _view is null) return (0, 0);
                var buffer = new byte[XInputSectionLayout.TotalSize];
                _view.ReadArray(0, buffer, 0, buffer.Length);
                return XInputSectionLayout.ReadRumble(buffer, _slot);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Release(_slot);
        }
    }
}
