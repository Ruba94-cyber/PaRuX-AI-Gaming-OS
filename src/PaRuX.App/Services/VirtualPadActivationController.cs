using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Windows.Gaming.Input;

namespace PaRuX.Services;

/// <summary>
/// Windows host of <see cref="VirtualPadForwardingService"/>: wires the real reader (RawGameController), the optional
/// ViGEmBus driver, the Core's HidHide isolation endpoints and RawGameController hot-plug events. Nothing is created
/// until the owner starts it from the guided panel; the hot-plug subscription exists only while forwarding is armed.
/// STATUS: auto-tested logic (service), host wiring NOT RUN against a real pad or driver (needs physical HW).
/// </summary>
internal sealed class VirtualPadActivationController
{
    public static VirtualPadActivationController Current { get; } = new();

    private readonly object _gate = new();
    private bool _subscribed;

    public VirtualPadForwardingService Service { get; }
    public GamePadProfileStore Profiles { get; }
    public event Action? Changed;

    private VirtualPadActivationController()
    {
        Profiles = new GamePadProfileStore(new RawPadMappingStore());
        Service = new VirtualPadForwardingService(
            new ViGEmBusClient(),
            id => WindowsRawGamepadSource.TryOpen(id),
            ViGEmBusClient.ProbeDriver,
            new CoreIsolation(),
            Profiles);
    }

    public static (bool Present, string? Message) ProbeDriver() => ViGEmBusClient.ProbeDriver();

    public static PadDeviceIdentity IdentityFor(RawGamepadDeviceInfo info) => new(
        info.Id, info.Name, info.VendorId, info.ProductId,
        WindowsRawGamepadSource.ListDevices().Count(d => d.VendorId == info.VendorId && d.ProductId == info.ProductId));

    public async Task<VirtualPadServiceStatus> StartAsync(RawGamepadDeviceInfo device, bool consentToHide, string? gameId)
    {
        Subscribe();
        try
        {
            return await Service.StartAsync(IdentityFor(device), consentToHide, gameId).ConfigureAwait(false);
        }
        finally
        {
            // Driver/mapping/isolation errors can throw before the service
            // returns a status. Never leave a global WinRT hot-plug listener
            // armed when forwarding isn't running or awaiting manual re-arm.
            // Conversely, don't lose removal events if a real pad remains live.
            if (Service.Status.Phase is not (VirtualPadPhase.Running or VirtualPadPhase.PausedRemoved))
                Unsubscribe();
            Changed?.Invoke();
        }
    }

    public async Task<VirtualPadServiceStatus> StopAsync()
    {
        try
        {
            return await Service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            // A failed Stop can leave the virtual pad alive; retaining the
            // removal listener in that state is safer than assuming cleanup
            // succeeded. Only detach after confirmed non-running state.
            if (Service.Status.Phase is not (VirtualPadPhase.Running or VirtualPadPhase.PausedRemoved))
                Unsubscribe();
            Changed?.Invoke();
        }
    }

    public async Task<VirtualPadServiceStatus> RearmAsync()
    {
        VirtualPadServiceStatus status = await Service.RearmAsync().ConfigureAwait(false);
        Changed?.Invoke();
        return status;
    }

    /// <summary>A game was launched by PaRuX: switch the active forwarding to that game's profile. Never starts forwarding.</summary>
    public async Task NotifyGameStartedAsync(string gameId)
    {
        try
        {
            await Service.OnGameStartedAsync(gameId).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception) { /* a profile switch must never break launching a game */ }
    }

    private void Subscribe()
    {
        lock (_gate)
        {
            if (_subscribed) return;
            RawGameController.RawGameControllerAdded += OnAdded;
            RawGameController.RawGameControllerRemoved += OnRemoved;
            _subscribed = true;
        }
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed) return;
            RawGameController.RawGameControllerAdded -= OnAdded;
            RawGameController.RawGameControllerRemoved -= OnRemoved;
            _subscribed = false;
        }
    }

    private void OnAdded(object? sender, RawGameController controller)
    {
        try { Service.OnDeviceAdded(controller.NonRoamableId); Changed?.Invoke(); }
        catch (Exception) { }
    }

    private async void OnRemoved(object? sender, RawGameController controller)
    {
        try
        {
            await Service.OnDeviceRemovedAsync(controller.NonRoamableId).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception) { /* the worker also ends by itself when the reader reports the pad gone */ }
    }

    private sealed class CoreIsolation : IPadIsolation
    {
        public async Task<PadIsolationOutcome> IsolateAsync(PadDeviceIdentity device, CancellationToken ct)
        {
            var result = await GamingOrchestratorClient.Current.IsolateVirtualPadAsync(new VirtualPadIsolateRequest(
                device.VendorId, device.ProductId, device.SameIdentityCount, true,
                Environment.ProcessPath ?? string.Empty), ct).ConfigureAwait(false);
            if (!result.Succeeded || result.Value is not { Success: true, InstancePath: { } path })
                return new(false, result.Error ?? result.Value?.Message ?? "The controller could not be hidden. Nothing was hidden.", null);
            return new(true, result.Value.Message, path);
        }

        public async Task<bool> IsStillIsolatedAsync(string instancePath, CancellationToken ct)
        {
            var result = await GamingOrchestratorClient.Current.GetVirtualPadIsolationAsync(instancePath, ct).ConfigureAwait(false);
            return result.Succeeded && result.Value is { Isolated: true };
        }
    }
}
