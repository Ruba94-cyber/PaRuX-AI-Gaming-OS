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
        private CancellationTokenSource? _rumbleLoop;

        /// <summary>The last vibration forwarded to the physical controller, for diagnostics.</summary>
        public (ushort Low, ushort High) LastRumble { get; private set; }

        /// <summary>True while the controller reports a motor, so the panel can say whether game vibration can reach it.</summary>
        public bool PadHasRumble { get; private set; }

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
                if (!status.Running) { _session = null; return status; }
                PadHasRumble = source.HasRumble;
                StartRumbleLoop(source);
                return status;
            }
        }

        /// <summary>Forwards what a game asks of the shim's vibration to the physical controller, about 25 times a second.</summary>
        private void StartRumbleLoop(WindowsRawGamepadSource source)
        {
            _rumbleLoop?.Cancel();
            var cts = _rumbleLoop = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                (ushort Low, ushort High) sent = (0, 0);
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        (ushort Low, ushort High) wanted = SharedMemoryXInputBackend.ReadRumble(0);
                        // A held rumble is re-sent every pass so the 250 ms effect does not run out; silence is sent once.
                        if (wanted.Low != 0 || wanted.High != 0 || sent.Low != 0 || sent.High != 0)
                        {
                            await source.RumbleAsync(wanted.Low, wanted.High).ConfigureAwait(false);
                            sent = wanted;
                            LastRumble = wanted;
                        }
                        await Task.Delay(40, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { try { await source.RumbleAsync(0, 0).ConfigureAwait(false); } catch (Exception) { } }
            });
        }

        public async Task<VirtualPadForwardingStatus> StopAsync()
        {
            _rumbleLoop?.Cancel();
            VirtualPadForwardingSession? session;
            lock (_gate) session = _session;
            if (session is null) return new(false, "Not active.");
            VirtualPadForwardingStatus status = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) if (!status.Running) _session = null;
            return status;
        }
    }
}
