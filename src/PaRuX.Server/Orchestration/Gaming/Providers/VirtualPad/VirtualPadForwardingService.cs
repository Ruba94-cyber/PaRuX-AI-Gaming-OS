using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>The physical pad the owner picked. Vendor/product and the number of connected pads with that identity feed
/// the fail-closed HID binding check; the device id is only used to open the reader and to look up mappings.</summary>
public sealed record PadDeviceIdentity(string DeviceId, string Name, ushort VendorId, ushort ProductId, int SameIdentityCount);

public sealed record PadIsolationOutcome(bool Success, string Message, string? InstancePath);

/// <summary>Double-input prevention, as the host provides it (the app talks to the Core's HidHide endpoints).</summary>
public interface IPadIsolation
{
    /// <summary>Hides the pad after PROVING the controller-to-HID binding. Must fail closed.</summary>
    Task<PadIsolationOutcome> IsolateAsync(PadDeviceIdentity device, CancellationToken ct);
    /// <summary>Live check that the pad is still hidden. Any failure must be reported as false.</summary>
    Task<bool> IsStillIsolatedAsync(string instancePath, CancellationToken ct);
}

public enum VirtualPadPhase { Idle, Starting, Running, PausedRemoved, Failed }

public sealed record VirtualPadServiceStatus(
    VirtualPadPhase Phase, string Message, bool CanRearm, string? GameId, PadMappingSource MappingSource);

public sealed class VirtualPadServiceOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(8);
    public int MaxStartAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan IsolationRefresh { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>An isolation confirmation older than this no longer counts (fail closed if the checker stalls).</summary>
    public TimeSpan IsolationMaxAge { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// Owner-driven activation of one virtual Xbox pad for one physical pad. It never starts by itself: every start needs an
/// explicit call (the guided panel), the driver must be present, the pad must be isolated (hidden with consent and a
/// proven binding), and a mapping must exist. Stop and removal neutralise and unplug the virtual pad (through
/// <see cref="VirtualPadForwardingSession"/>); a returning pad only offers a re-arm. Game starts switch the mapping.
/// STATUS: logic fully auto-tested with fakes; NEEDS PHYSICAL HW (real pad, ViGEmBus, HidHide) for end-to-end proof.
/// </summary>
public sealed class VirtualPadForwardingService : IAsyncDisposable
{
    private readonly IVirtualControllerBackend _backend;
    private readonly Func<string, IRawGamepadSource?> _openSource;
    private readonly Func<(bool Present, string? Message)> _driverProbe;
    private readonly IPadIsolation _isolation;
    private readonly GamePadProfileStore _profiles;
    private readonly VirtualPadServiceOptions _options;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly object _gate = new();

    private VirtualPadForwardingSession? _session;
    private IsolationMonitor? _monitor;
    private PadDeviceIdentity? _device;
    private string? _isolatedPath;
    private string? _gameId;
    private string? _mappingFingerprint;
    private PadMappingSource _source = PadMappingSource.None;
    private VirtualPadPhase _phase = VirtualPadPhase.Idle;
    private string _message = "Not active.";
    private bool _rearmOffered;

    public VirtualPadForwardingService(IVirtualControllerBackend backend, Func<string, IRawGamepadSource?> openSource,
        Func<(bool Present, string? Message)> driverProbe, IPadIsolation isolation, GamePadProfileStore profiles,
        VirtualPadServiceOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _openSource = openSource ?? throw new ArgumentNullException(nameof(openSource));
        _driverProbe = driverProbe ?? throw new ArgumentNullException(nameof(driverProbe));
        _isolation = isolation ?? throw new ArgumentNullException(nameof(isolation));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _options = options ?? new VirtualPadServiceOptions();
    }

    public VirtualPadServiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                // A worker that ended by itself (pad unplugged, isolation lost, I/O fault) must not read as "running".
                if (_phase == VirtualPadPhase.Running && _session is { } s && !s.Status.Running)
                {
                    string end = s.Status.Message;
                    if (end.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
                    {
                        _phase = VirtualPadPhase.PausedRemoved;
                        _message = "Controller disconnected; the virtual pad was neutralised and removed. Reconnect it and re-arm.";
                    }
                    else
                    {
                        _phase = VirtualPadPhase.Failed;
                        _message = end;
                    }
                }
                return new(_phase, _message,
                    _phase == VirtualPadPhase.PausedRemoved && _rearmOffered, _gameId, _source);
            }
        }
    }

    private void Set(VirtualPadPhase phase, string message)
    {
        lock (_gate) { _phase = phase; _message = message; }
    }

    // ------------------------------------------------------------------ start / stop

    /// <param name="consentToHide">Explicit owner consent to hide the physical pad (HidHide). Without it nothing starts,
    /// because forwarding next to a visible physical pad would double every input.</param>
    public async Task<VirtualPadServiceStatus> StartAsync(PadDeviceIdentity device, bool consentToHide, string? gameId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ct.IsCancellationRequested) { Set(VirtualPadPhase.Idle, "Start cancelled."); return Status; }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status.Phase is VirtualPadPhase.Running or VirtualPadPhase.Starting)
                return Status with { Message = "Stop the current forwarding first." };
            if (await TearDownAsync(ct).ConfigureAwait(false) is { Running: true } stuck)
            {
                Set(VirtualPadPhase.Failed, stuck.Message);
                return Status;
            }
            Set(VirtualPadPhase.Starting, "Starting.");

            var (present, driverMessage) = SafeProbe();
            if (!present)
            {
                Set(VirtualPadPhase.Failed, driverMessage ??
                    "The virtual controller driver (ViGEmBus) is not installed. PaRuX never installs it; get it from its official project page and install it yourself.");
                return Status;
            }

            ResolvedPadMapping resolved = _profiles.Resolve(device.DeviceId, gameId);
            if (resolved.Mapping is null)
            {
                Set(VirtualPadPhase.Failed, resolved.Error ?? "Save a mapping for this controller first.");
                return Status;
            }

            string? path = _isolatedPath is not null && SameDevice(_device, device) ? _isolatedPath : null;
            if (path is null || !await SafeIsolatedAsync(path, ct).ConfigureAwait(false))
            {
                path = null;
                if (!consentToHide)
                {
                    Set(VirtualPadPhase.Failed,
                        "The physical controller must be hidden from games first (otherwise every input is doubled). Consent to hide it, or use a setup that already ignores it.");
                    return Status;
                }
                PadIsolationOutcome outcome;
                try { outcome = await _isolation.IsolateAsync(device, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { outcome = new(false, "The isolation step failed unexpectedly.", null); }
                if (!outcome.Success || outcome.InstancePath is null)
                {
                    Set(VirtualPadPhase.Failed, outcome.Message);
                    return Status;
                }
                path = outcome.InstancePath;
            }

            lock (_gate) { _device = device; _isolatedPath = path; _gameId = gameId; }
            return await LaunchAsync(device, path, resolved, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TearDownAsync().ConfigureAwait(false);
            Set(VirtualPadPhase.Idle, "Start cancelled.");
            return Status;
        }
        finally { _op.Release(); }
    }

    private async Task<VirtualPadServiceStatus> LaunchAsync(PadDeviceIdentity device, string isolatedPath,
        ResolvedPadMapping resolved, CancellationToken ct)
    {
        IRawGamepadSource? source = SafeOpen(device.DeviceId);
        if (source is null)
        {
            Set(VirtualPadPhase.Failed, "The physical controller is not available.");
            return Status;
        }

        var monitor = new IsolationMonitor(_isolation, isolatedPath, _options);
        if (!await monitor.PrimeAsync(ct).ConfigureAwait(false))
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
            Set(VirtualPadPhase.Failed, "Physical input isolation was not confirmed.");
            return Status;
        }

        VirtualPadForwardingStatus started = new(false, "Not started.");
        VirtualPadForwardingSession? session = null;
        for (int attempt = 1; attempt <= Math.Max(1, _options.MaxStartAttempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            session = new VirtualPadForwardingSession(_backend, source, monitor.IsReady);
            started = session.Start(resolved.Mapping!, _options.Interval, ct);
            if (started.Running) break;
            await session.DisposeAsync().ConfigureAwait(false);
            session = null;
            if (!IsTransient(started.Message) || attempt == _options.MaxStartAttempts) break;
            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
        }

        if (session is null)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
            Set(VirtualPadPhase.Failed, started.Message);
            return Status;
        }

        lock (_gate)
        {
            _session = session;
            _monitor = monitor;
            _source = resolved.Source;
            _mappingFingerprint = Fingerprint(resolved.Mapping!);
            _rearmOffered = false;
            _phase = VirtualPadPhase.Running;
            _message = resolved.Source == PadMappingSource.GameProfile
                ? "Forwarding with the game profile."
                : "Forwarding with the controller mapping.";
        }
        return Status;
    }

    /// <summary>Explicit owner stop. The virtual pad gets a neutral report and is unplugged by the session.</summary>
    public async Task<VirtualPadServiceStatus> StopAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            VirtualPadForwardingStatus? ended = await TearDownAsync(ct).ConfigureAwait(false);
            lock (_gate) { _rearmOffered = false; }
            Set(VirtualPadPhase.Idle, ended is { Running: true }
                ? ended.Message
                : "Stopped. The virtual pad was neutralised and removed. The physical pad stays hidden until you restore visibility (after leaving Gaming).");
            return Status;
        }
        finally { _op.Release(); }
    }

    /// <returns>null or a stopped status when clean; a Running status when the worker did not stop in time (the session
    /// and its monitor are then KEPT so that no second pad is ever created next to a possibly live one).</returns>
    private async Task<VirtualPadForwardingStatus?> TearDownAsync(CancellationToken ct = default)
    {
        VirtualPadForwardingSession? session; IsolationMonitor? monitor;
        lock (_gate) { session = _session; monitor = _monitor; _session = null; _monitor = null; }
        VirtualPadForwardingStatus? ended = null;
        if (session is not null)
        {
            ended = await session.StopAsync(ct).ConfigureAwait(false);
            if (ended.Running)
            {
                lock (_gate) { _session = session; _monitor = monitor; }
                return ended;
            }
            await session.DisposeAsync().ConfigureAwait(false);
        }
        if (monitor is not null) await monitor.DisposeAsync().ConfigureAwait(false);
        return ended;
    }

    // ------------------------------------------------------------------ hot-plug

    /// <summary>Windows reported the pad as removed: neutralise, unplug the virtual pad, pause. Never keeps a ghost pad.</summary>
    public async Task OnDeviceRemovedAsync(string deviceId)
    {
        await _op.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsActiveDevice(deviceId)) return;
            var ended = await TearDownAsync().ConfigureAwait(false);
            lock (_gate) { _rearmOffered = false; }
            Set(VirtualPadPhase.PausedRemoved, ended is { Running: true }
                ? "Controller removed, but the virtual pad did not stop in time. Do not start another."
                : "Controller removed; the virtual pad was neutralised and removed. Reconnect it to re-arm.");
        }
        finally { _op.Release(); }
    }

    /// <summary>The pad came back: only OFFERS a re-arm; forwarding never resumes by itself.</summary>
    public void OnDeviceAdded(string deviceId)
    {
        lock (_gate)
        {
            _ = Status; // lets a worker that already ended on its own become "paused" first
            if (_phase != VirtualPadPhase.PausedRemoved || _device is null ||
                !string.Equals(_device.DeviceId, deviceId, StringComparison.Ordinal)) return;
            _rearmOffered = true;
            _message = "Controller is back. Press Re-arm to resume forwarding.";
        }
    }

    /// <summary>Explicit owner re-arm after a removal. Re-uses the earlier hiding only if it is still in place; it never
    /// hides anything by itself (no consent is assumed).</summary>
    public async Task<VirtualPadServiceStatus> RearmAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PadDeviceIdentity? device; string? path; string? game;
            lock (_gate)
            {
                _ = Status;
                if (_phase != VirtualPadPhase.PausedRemoved || !_rearmOffered || _device is null || _isolatedPath is null)
                    return Status with { Message = "Nothing to re-arm." };
                device = _device; path = _isolatedPath; game = _gameId;
            }
            if (await TearDownAsync(ct).ConfigureAwait(false) is { Running: true } stuck)
            { Set(VirtualPadPhase.Failed, stuck.Message); return Status; }
            Set(VirtualPadPhase.Starting, "Re-arming.");
            var (present, driverMessage) = SafeProbe();
            if (!present) { Set(VirtualPadPhase.Failed, driverMessage ?? "The virtual controller driver is not available."); return Status; }
            if (!await SafeIsolatedAsync(path, ct).ConfigureAwait(false))
            {
                Set(VirtualPadPhase.Failed, "The pad is no longer hidden from games. Start again from the guided steps and consent to hide it.");
                return Status;
            }
            ResolvedPadMapping resolved = _profiles.Resolve(device.DeviceId, game);
            if (resolved.Mapping is null) { Set(VirtualPadPhase.Failed, resolved.Error ?? "No mapping is saved for this controller."); return Status; }
            return await LaunchAsync(device, path, resolved, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TearDownAsync().ConfigureAwait(false);
            Set(VirtualPadPhase.PausedRemoved, "Re-arm cancelled.");
            return Status;
        }
        finally { _op.Release(); }
    }

    // ------------------------------------------------------------------ per-game profiles

    /// <summary>A game started: remember it and, if forwarding is active, switch to that game's profile (or the device
    /// mapping when it has none). Does nothing unless the owner already armed forwarding.</summary>
    public Task<VirtualPadServiceStatus> OnGameStartedAsync(string gameId, CancellationToken ct = default) =>
        SwitchGameAsync(string.IsNullOrWhiteSpace(gameId) ? null : gameId, ct);

    public Task<VirtualPadServiceStatus> OnGameStoppedAsync(CancellationToken ct = default) => SwitchGameAsync(null, ct);

    private async Task<VirtualPadServiceStatus> SwitchGameAsync(string? gameId, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PadDeviceIdentity? device; string? path; VirtualPadPhase phase;
            lock (_gate) { _gameId = gameId; device = _device; path = _isolatedPath; phase = _phase; }
            if (device is null || path is null) return Status;
            ResolvedPadMapping resolved = _profiles.Resolve(device.DeviceId, gameId);
            if (phase == VirtualPadPhase.Running && Status.Phase == VirtualPadPhase.Running)
            {
                if (resolved.Mapping is null) return Status; // keep the working mapping rather than dropping input
                bool changed;
                lock (_gate) { changed = Fingerprint(resolved.Mapping) != _mappingFingerprint; }
                if (!changed) return Status;
                if (await TearDownAsync(ct).ConfigureAwait(false) is { Running: true } stuck)
                { Set(VirtualPadPhase.Failed, stuck.Message); return Status; }
                return await LaunchAsync(device, path, resolved, ct).ConfigureAwait(false);
            }
            return Status;
        }
        finally { _op.Release(); }
    }

    // ------------------------------------------------------------------ helpers

    private bool IsActiveDevice(string deviceId)
    {
        lock (_gate)
            return _device is not null && string.Equals(_device.DeviceId, deviceId, StringComparison.Ordinal) &&
                   _phase is VirtualPadPhase.Running or VirtualPadPhase.Starting or VirtualPadPhase.Failed;
    }

    private static bool SameDevice(PadDeviceIdentity? a, PadDeviceIdentity b) =>
        a is not null && string.Equals(a.DeviceId, b.DeviceId, StringComparison.Ordinal);

    private (bool Present, string? Message) SafeProbe()
    {
        try { return _driverProbe(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        { return (false, "The virtual controller driver could not be checked."); }
    }

    private IRawGamepadSource? SafeOpen(string deviceId)
    {
        try { return _openSource(deviceId); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException
            or ObjectDisposedException or UnauthorizedAccessException) { return null; }
    }

    private async Task<bool> SafeIsolatedAsync(string path, CancellationToken ct)
    {
        try { return await _isolation.IsStillIsolatedAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>Transient driver conditions worth a bounded retry; everything else (driver absent, isolation,
    /// unreadable pad, invalid mapping) is final.</summary>
    internal static bool IsTransient(string message) =>
        message.Contains("free slot", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("another program", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Stop the previous", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not be used", StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(PadMapping mapping) => JsonSerializer.Serialize(mapping);

    public async ValueTask DisposeAsync()
    {
        await _op.WaitAsync().ConfigureAwait(false);
        try { await TearDownAsync().ConfigureAwait(false); }
        finally { _op.Release(); }
    }

    /// <summary>Keeps a fresh "isolated" confirmation for the session's synchronous gate; stale or failed means NOT ready.</summary>
    private sealed class IsolationMonitor : IAsyncDisposable
    {
        private readonly IPadIsolation _isolation;
        private readonly string _path;
        private readonly VirtualPadServiceOptions _options;
        private readonly CancellationTokenSource _cts = new();
        private long _okAtTicks = long.MinValue;
        private volatile bool _ok;
        private Task? _loop;

        public IsolationMonitor(IPadIsolation isolation, string path, VirtualPadServiceOptions options)
        { _isolation = isolation; _path = path; _options = options; }

        public bool IsReady() => _ok &&
            (System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref _okAtTicks)) <
            _options.IsolationMaxAge.TotalSeconds * System.Diagnostics.Stopwatch.Frequency;

        public async Task<bool> PrimeAsync(CancellationToken ct)
        {
            if (!await CheckAsync(ct).ConfigureAwait(false)) return false;
            _loop = Task.Run(RunAsync);
            return true;
        }

        private async Task<bool> CheckAsync(CancellationToken ct)
        {
            bool ok;
            try { ok = await _isolation.IsStillIsolatedAsync(_path, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
            if (ok) Interlocked.Exchange(ref _okAtTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            _ok = ok;
            return ok;
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(_options.IsolationRefresh, _cts.Token).ConfigureAwait(false);
                    await CheckAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _ok = false;
            _cts.Cancel();
            if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (Exception) { } }
            _cts.Dispose();
        }
    }
}
