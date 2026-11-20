using System;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services
{
    /// <summary>
    /// Runs the driverless virtual Xbox pad: one physical controller, the owner's saved mapping, published through shared memory
    /// for the XInput shim. It needs neither ViGEmBus nor HidHide, so no hiding consent is involved; it starts only on the owner's
    /// click, stops on the owner's click or when the controller disconnects, and never touches a game's files.
    /// </summary>
    public sealed class XInputShimBridge
    {
        public static XInputShimBridge Current { get; } = new XInputShimBridge();
        private readonly object _gate = new();
        private VirtualPadForwardingSession? _session;

        private XInputShimBridge() { }

        public bool Running { get { lock (_gate) return _session?.Status.Running == true; } }

        public string Message { get { lock (_gate) return _session?.Status.Message ?? "Not active."; } }

        public VirtualPadForwardingStatus Start(string deviceId, PadMapping mapping)
        {
            lock (_gate)
            {
                if (_session?.Status.Running == true) return new(true, "Already running.");
                WindowsRawGamepadSource? source = WindowsRawGamepadSource.TryOpen(deviceId);
                if (source is null) return new(false, "The controller is not available.");
                // With the shim there is no second, physical XInput device to hide, so isolation is not needed.
                _session = new VirtualPadForwardingSession(new SharedMemoryXInputBackend(), source, () => true);
                VirtualPadForwardingStatus status = _session.Start(mapping, TimeSpan.FromMilliseconds(8));
                if (!status.Running) _session = null;
                return status;
            }
        }

        public async Task<VirtualPadForwardingStatus> StopAsync()
        {
            VirtualPadForwardingSession? session;
            lock (_gate) session = _session;
            if (session is null) return new(false, "Not active.");
            VirtualPadForwardingStatus status = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) if (!status.Running) _session = null;
            return status;
        }
    }
}
