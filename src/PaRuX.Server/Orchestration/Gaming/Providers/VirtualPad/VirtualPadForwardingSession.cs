using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>One explicitly selected physical pad source; its Windows reader is host-owned.</summary>
public interface IRawGamepadSource
{
    string DeviceId { get; }
    bool TryRead(out RawGamepadState? state);
}

public sealed record VirtualPadForwardingStatus(bool Running, string Message);

/// <summary>
/// Optional, one-source/one-virtual-pad forwarding. A caller must confirm physical input
/// isolation (e.g. trusted HidHide state) before starting and every sample. This class
/// never scans devices, installs drivers or automatically activates during Gaming.
/// </summary>
public sealed class VirtualPadForwardingSession : IAsyncDisposable
{
    private readonly IVirtualControllerBackend _backend;
    private readonly IRawGamepadSource _source;
    private readonly Func<bool> _isolationReady;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task? _worker;
    private string _message = "Not active.";

    public VirtualPadForwardingSession(IVirtualControllerBackend backend, IRawGamepadSource source, Func<bool> isolationReady)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _isolationReady = isolationReady ?? throw new ArgumentNullException(nameof(isolationReady));
    }

    public VirtualPadForwardingStatus Status
    {
        get { lock (_gate) return new(_worker is { IsCompleted: false }, _message); }
    }

    public VirtualPadForwardingStatus Start(PadMapping mapping, TimeSpan interval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Validate() is { } error) return new(false, error);
        if (interval < TimeSpan.FromMilliseconds(8) || interval > TimeSpan.FromMilliseconds(100))
            return new(false, "Sampling interval must be between 8 and 100 ms.");
        if (string.IsNullOrWhiteSpace(_source.DeviceId) || _source.DeviceId.Length > 300)
            return new(false, "Select one physical pad.");
        if (ct.IsCancellationRequested) return new(false, "Start cancelled.");
        lock (_gate)
        {
            if (_worker is not null) return new(false, "Stop the previous session first.");
            // A failed driver query or unplugged WinRT device must report an
            // unavailable controller, not crash a UI command or leak a pad.
            bool isolated;
            RawGamepadState? first;
            try
            {
                isolated = _isolationReady();
                if (!isolated) return new(false, "Physical input isolation was not confirmed.");
                if (!_source.TryRead(out first) || first is null)
                    return new(false, "The physical pad could not be read.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or ObjectDisposedException or
                System.Runtime.InteropServices.COMException)
            {
                return new(false, "Physical controller or isolation state is unavailable.");
            }
            IVirtualXboxPad? pad;
            try { pad = _backend.TryCreatePad(out string? problem);
                if (pad is null) return new(false, problem ?? "Virtual device unavailable."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or ObjectDisposedException or DllNotFoundException or
                EntryPointNotFoundException or System.Runtime.InteropServices.COMException)
            {
                return new(false, "Virtual controller backend is unavailable.");
            }
            // Do not let UI edits mutate an active game's mapping.
            var chosen = new PadMapping
            {
                Buttons = new Dictionary<int, ushort>(mapping.Buttons),
                LeftXAxis = mapping.LeftXAxis, LeftYAxis = mapping.LeftYAxis,
                RightXAxis = mapping.RightXAxis, RightYAxis = mapping.RightYAxis,
                InvertLeftY = mapping.InvertLeftY, InvertRightY = mapping.InvertRightY,
                LeftTriggerAxis = mapping.LeftTriggerAxis, RightTriggerAxis = mapping.RightTriggerAxis,
                LeftTriggerButton = mapping.LeftTriggerButton, RightTriggerButton = mapping.RightTriggerButton,
                TriggerAxisIsSigned = mapping.TriggerAxisIsSigned,
                StickDeadzone = mapping.StickDeadzone, HatIsDpad = mapping.HatIsDpad
            };
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _message = "Owner-enabled forwarding.";
            _worker = Task.Run(() => ForwardAsync(pad, chosen, first, interval, _cancel.Token));
            return new(true, _message);
        }
    }

    private async Task ForwardAsync(IVirtualXboxPad pad, PadMapping mapping, RawGamepadState first, TimeSpan interval, CancellationToken ct)
    {
        string outcome = "Forwarding stopped.";
        try
        {
            if (!pad.Submit(default)) { outcome = "Virtual pad refused its neutral report."; return; }
            RawGamepadState state = first;
            while (!ct.IsCancellationRequested)
            {
                if (!_isolationReady()) { outcome = "Physical input isolation lost."; break; }
                if (!pad.Submit(GenericPadMapper.Map(state, mapping))) { outcome = "Virtual pad refused a report."; break; }
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (!_source.TryRead(out RawGamepadState? next) || next is null)
                { outcome = "Physical controller disconnected."; break; }
                state = next;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = "Stopped by owner."; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            ObjectDisposedException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException)
        { outcome = "Controller I/O failed."; }
        finally
        {
            try { pad.Submit(default); } catch (Exception) { }
            try { pad.Dispose(); } catch (Exception) { }
            lock (_gate) _message = outcome;
        }
    }

    public async Task<VirtualPadForwardingStatus> StopAsync(CancellationToken ct = default)
    {
        Task? task;
        lock (_gate)
        {
            task = _worker;
            if (task is null) return new(false, _message);
            _cancel?.Cancel();
        }
        try { await task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (TimeoutException) { return new(true, "Stop timeout; do not create another pad."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return new(true, "Stop was interrupted; device ownership remains until cleanup."); }
        catch (Exception ex)
        {
            // The worker may fault with an unexpected source/driver exception.
            // Its finally block already neutralizes and disposes the owned
            // virtual pad. Retire the faulted task so a later explicit Start
            // is possible; never silently keep the session marked running.
            lock (_gate) _message = "Controller worker stopped after " + ex.GetType().Name + ".";
        }
        lock (_gate)
        {
            if (ReferenceEquals(task, _worker))
            {
                _worker = null;
                _cancel?.Dispose();
                _cancel = null;
            }
            return new(false, _message);
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
