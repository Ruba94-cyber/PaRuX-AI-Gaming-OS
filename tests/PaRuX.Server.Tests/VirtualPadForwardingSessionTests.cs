using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VirtualPadForwardingSessionTests
{
    private sealed class RawSource : IRawGamepadSource
    {
        public string DeviceId => @"HID\VID_1234&PID_5678\1";
        public bool Connected = true;
        public bool ThrowOnNextRead;
        public bool TryRead(out RawGamepadState? state)
        {
            if (ThrowOnNextRead) throw new ApplicationException("synthetic unexpected reader fault");
            state = Connected ? new RawGamepadState(new[] { true }, new[] { 0.5 }, -1) : null;
            return Connected;
        }
    }

    private sealed class Pad : IVirtualXboxPad
    {
        public readonly List<Xbox360Report> Reports = new();
        public int Disposals;
        public bool Submit(Xbox360Report value) { lock (Reports) Reports.Add(value); return true; }
        public void Dispose() => Interlocked.Increment(ref Disposals);
        public bool Pressed { get { lock (Reports) return Reports.Any(r => r.Buttons == Xbox360Report.A); } }
        public bool NeutralEnd { get { lock (Reports) return Reports.Count > 0 && Reports[^1] == default; } }
    }

    private sealed class Backend : IVirtualControllerBackend
    {
        public string Id => "synthetic";
        public int Starts;
        public bool ThrowOnCreate;
        public readonly Pad Pad = new();
        public IVirtualXboxPad? TryCreatePad(out string? error)
        {
            Starts++;
            if (ThrowOnCreate) throw new InvalidOperationException("synthetic unavailable driver");
            error = null;
            return Pad;
        }
    }

    private static PadMapping Map() => new() { Buttons = new() { [0] = Xbox360Report.A }, LeftXAxis = 0 };
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 300; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Forwarding never reached expected state.");
    }

    [Fact]
    public async Task Missing_input_isolation_prevents_any_driver_access()
    {
        var backend = new Backend();
        await using var session = new VirtualPadForwardingSession(backend, new RawSource(), () => false);
        Assert.False(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        Assert.Equal(0, backend.Starts);
    }

    [Fact]
    public async Task Explicit_stop_releases_buttons_and_only_our_pad()
    {
        var backend = new Backend();
        await using var session = new VirtualPadForwardingSession(backend, new RawSource(), () => true);
        Assert.True(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        await WaitUntil(() => backend.Pad.Pressed);
        Assert.False((await session.StopAsync()).Running);
        Assert.True(backend.Pad.NeutralEnd);
        Assert.Equal(1, backend.Pad.Disposals);
        Assert.Equal(1, backend.Starts);
    }

    [Fact]
    public async Task Disconnect_unplugs_and_does_not_allow_duplicate_start()
    {
        var reader = new RawSource();
        var backend = new Backend();
        await using var session = new VirtualPadForwardingSession(backend, reader, () => true);
        Assert.True(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        Assert.False(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        await WaitUntil(() => backend.Pad.Pressed);
        reader.Connected = false;
        await WaitUntil(() => backend.Pad.Disposals == 1);
        Assert.True(backend.Pad.NeutralEnd);
        Assert.Contains("disconnected", session.Status.Message, StringComparison.OrdinalIgnoreCase);
        await session.StopAsync();
    }

    [Fact]
    public async Task Loss_of_input_isolation_stops_forwarding()
    {
        bool isolated = true;
        var backend = new Backend();
        await using var session = new VirtualPadForwardingSession(backend, new RawSource(), () => isolated);
        Assert.True(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        await WaitUntil(() => backend.Pad.Pressed);
        isolated = false;
        await WaitUntil(() => backend.Pad.Disposals == 1);
        Assert.True(backend.Pad.NeutralEnd);
    }

    [Fact]
    public async Task Unavailable_virtual_driver_returns_error_without_starting_a_worker()
    {
        var backend = new Backend { ThrowOnCreate = true };
        await using var session = new VirtualPadForwardingSession(backend, new RawSource(), () => true);
        var outcome = session.Start(Map(), TimeSpan.FromMilliseconds(10));
        Assert.False(outcome.Running);
        Assert.Contains("unavailable", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.Status.Running);
        Assert.Equal(0, backend.Pad.Disposals);
    }

    [Fact]
    public async Task Unexpected_reader_fault_can_be_stopped_and_retried_without_a_stale_worker()
    {
        var backend = new Backend();
        var reader = new RawSource();
        await using var session = new VirtualPadForwardingSession(backend, reader, () => true);
        Assert.True(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        await WaitUntil(() => backend.Pad.Pressed);
        reader.ThrowOnNextRead = true;
        await WaitUntil(() => backend.Pad.Disposals == 1);
        var stopped = await session.StopAsync();
        Assert.False(stopped.Running);
        Assert.Contains("stopped after", stopped.Message, StringComparison.OrdinalIgnoreCase);
        reader.ThrowOnNextRead = false;
        Assert.True(session.Start(Map(), TimeSpan.FromMilliseconds(10)).Running);
        Assert.False((await session.StopAsync()).Running);
    }

    [Fact]
    public async Task Invalid_mapping_and_interval_do_not_create_a_virtual_pad()
    {
        var backend = new Backend();
        await using var session = new VirtualPadForwardingSession(backend, new RawSource(), () => true);
        var bad = Map(); bad.StickDeadzone = double.NaN;
        Assert.False(session.Start(bad, TimeSpan.FromMilliseconds(10)).Running);
        Assert.False(session.Start(Map(), TimeSpan.FromMilliseconds(2)).Running);
        Assert.Equal(0, backend.Starts);
    }
}
