using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Explicit, reversible resolution / refresh-rate changes.
/// Safety model: only monitors in the current topology and modes the driver lists are accepted; the mode is
/// driver-tested first; the change is session-temporary and reverts by itself unless confirmed within the
/// timeout; a persisted restore record lets a crash or restart revert it; one change at a time; never while
/// Gaming owns the machine. HDR, VRR and colour settings are not touched.
/// </summary>
public sealed class GamingDisplayModeService : IDisposable
{
    internal static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(15);

    internal sealed record PendingRecord(string DisplayId, DisplayModeEntry Previous, DateTime DeadlineUtc);

    private readonly IDisplayModeApi _api;
    private readonly GamingDisplayTopologyService _topology;
    private readonly Func<bool> _gamingActive;
    private readonly string _recordPath;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private PendingRecord? _pending;
    private DisplayModeEntry? _applied;
    private Timer? _timer;
    private string _lastMessage = "No display change is pending.";

    public GamingDisplayModeService(GamingDisplayTopologyService topology, LocalRuntimeService runtime)
        : this(new WindowsDisplayModeApi(), topology, () => runtime.GamingMode,
            Path.Combine(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!, "display-revert.json"), DefaultConfirmTimeout) { }

    internal GamingDisplayModeService(IDisplayModeApi api, GamingDisplayTopologyService topology, Func<bool> gamingActive,
        string recordPath, TimeSpan timeout)
    {
        _api = api;
        _topology = topology;
        _gamingActive = gamingActive;
        _recordPath = recordPath;
        _timeout = timeout;
    }

    public GamingDisplayChangeStatusDto Status()
    {
        lock (_gate)
        {
            if (_pending is null) return new(false, null, 0, _lastMessage);
            int seconds = (int)Math.Max(0, Math.Ceiling((_pending.DeadlineUtc - DateTime.UtcNow).TotalSeconds));
            return new(true, _pending.DisplayId, seconds, _lastMessage);
        }
    }

    /// <summary>Distinct modes the driver lists for a known monitor (largest first, bounded).</summary>
    public IReadOnlyList<GamingDisplayModeOptionDto> Modes(string displayId)
    {
        string id = displayId ?? string.Empty;
        if (!_topology.Snapshot().Displays.Any(d => string.Equals(d.DisplayId, id, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That monitor is not part of the current display topology.");
        return _api.Supported(id)
            .Where(m => m.Width > 0 && m.Height > 0 && m.RefreshRateHz >= 24)
            .Select(m => new GamingDisplayModeOptionDto(m.Width, m.Height, m.RefreshRateHz))
            .Distinct()
            .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.RefreshRateHz)
            .Take(200)
            .ToArray();
    }

    public GamingDisplayChangeStatusDto Request(GamingDisplayModeChangeRequest request)
    {
        if (!OperatingSystem.IsWindows() && _api is WindowsDisplayModeApi)
            throw new InvalidOperationException("Display changes require a Windows desktop.");
        if (_gamingActive())
            throw new InvalidOperationException("Leave Gaming before changing the display mode.");
        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("Confirm or revert the pending display change first.");

            string id = request.DisplayId ?? string.Empty;
            if (!_topology.Snapshot().Displays.Any(d => string.Equals(d.DisplayId, id, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That monitor is not part of the current display topology.");

            DisplayModeEntry current = _api.Current(id)
                ?? throw new InvalidOperationException("Windows did not report the current mode of that monitor.");
            DisplayModeEntry target = _api.Supported(id)
                .Where(m => m.Width == request.Width && m.Height == request.Height && m.RefreshRateHz == request.RefreshRateHz)
                .OrderByDescending(m => m.BitsPerPixel == current.BitsPerPixel)
                .ThenByDescending(m => m.BitsPerPixel)
                .Select(m => m with { X = current.X, Y = current.Y })
                .FirstOrDefault()
                ?? throw new ArgumentException("That monitor does not list this resolution and refresh rate.");

            if (target.Width == current.Width && target.Height == current.Height && target.RefreshRateHz == current.RefreshRateHz)
            {
                _lastMessage = "The monitor already uses that mode.";
                return new(false, null, 0, _lastMessage);
            }
            if (!_api.Test(id, target))
                throw new InvalidOperationException("The display driver rejected that mode.");

            var record = new PendingRecord(id, current, DateTime.UtcNow + _timeout);
            SaveRecord(record);
            if (!_api.ApplyTemporary(id, target))
            {
                DeleteRecord();
                throw new InvalidOperationException("Windows could not apply that mode; nothing was changed.");
            }
            _pending = record;
            _applied = target;
            _timer?.Dispose();
            _timer = new Timer(_ => { try { Revert("The change was not confirmed in time and was reverted."); } catch { } },
                null, _timeout, Timeout.InfiniteTimeSpan);
            _lastMessage = "Display changed temporarily. Confirm to keep it; otherwise it reverts automatically.";
            return Status();
        }
    }

    /// <summary>Keeps the new mode: the session-temporary change becomes the monitor's saved default.</summary>
    public GamingDisplayChangeStatusDto Confirm()
    {
        lock (_gate)
        {
            if (_pending is null) throw new InvalidOperationException("There is no pending display change.");
            // A restore-only record loaded after a crash cannot be "confirmed".
            if (_applied is null)
                throw new InvalidOperationException("A previous display mode must be restored before applying a new one.");
            string id = _pending.DisplayId;
            if (!_api.ApplyPermanent(id, _applied))
            {
                // Keep both the restore journal and automatic timeout.
                _lastMessage = "Windows could not save that mode. It is still unconfirmed; revert or retry before the timeout.";
                return Status();
            }
            _timer?.Dispose();
            _timer = null;
            _pending = null;
            _applied = null;
            DeleteRecord();
            _lastMessage = "Display change confirmed and saved.";
            return new(false, null, 0, _lastMessage);
        }
    }

    public GamingDisplayChangeStatusDto Revert() => Revert("Display change reverted.");

    private GamingDisplayChangeStatusDto Revert(string message)
    {
        lock (_gate)
        {
            if (_pending is null) return new(false, null, 0, _lastMessage);
            _timer?.Dispose();
            _timer = null;
            if (!CanRestore(_pending) || !_api.ApplyTemporary(_pending.DisplayId, _pending.Previous))
            {
                // Keep the record: the next Revert call or the next start retries.
                _lastMessage = "Windows could not restore the previous mode yet. Try Revert again.";
                return new(true, _pending.DisplayId, 0, _lastMessage);
            }
            _pending = null;
            _applied = null;
            DeleteRecord();
            _lastMessage = message;
            return new(false, null, 0, _lastMessage);
        }
    }

    /// <summary>Called at startup: a record left by a crash during an unconfirmed change is reverted.</summary>
    public bool RecoverPendingAfterRestart()
    {
        lock (_gate)
        {
            if (_pending is not null) return false;
            PendingRecord? record = LoadRecord();
            if (record is null) return false;

            // A journal on disk is not authority to control an unknown display.
            // Keep it for diagnosis, but never apply a stale/invalid device mode.
            if (!CanRestore(record))
            {
                _pending = record;
                _lastMessage = "The saved display restore point does not match an available monitor/mode. The record was kept for recovery.";
                return false;
            }

            bool restored = _api.ApplyTemporary(record.DisplayId, record.Previous);
            if (restored)
            {
                DeleteRecord();
                _lastMessage = "A display change left unconfirmed by a previous run was reverted.";
                return true;
            }

            // Preserve in-memory pending state so the manual Revert action
            // retries the saved mode rather than silently reporting a no-op.
            _pending = record;
            _lastMessage = "Automatic display recovery failed; use Revert once the monitor is available.";
            return false;
        }
    }

    private bool CanRestore(PendingRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DisplayId) ||
            record.Previous is null ||
            record.Previous.Width <= 0 || record.Previous.Height <= 0 ||
            record.Previous.RefreshRateHz < 24)
            return false;
        if (!_topology.Snapshot().Displays.Any(d =>
                string.Equals(d.DisplayId, record.DisplayId, StringComparison.OrdinalIgnoreCase)))
            return false;
        return _api.Supported(record.DisplayId).Any(m =>
            m.Width == record.Previous.Width && m.Height == record.Previous.Height &&
            m.RefreshRateHz == record.Previous.RefreshRateHz &&
            m.BitsPerPixel == record.Previous.BitsPerPixel);
    }

    private void SaveRecord(PendingRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
            string temp = _recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record));
            File.Move(temp, _recordPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a restore record the change must not be made.
            throw new InvalidOperationException("The display restore point could not be saved; nothing was changed.");
        }
    }

    private PendingRecord? LoadRecord()
    {
        try
        {
            if (!File.Exists(_recordPath) || new FileInfo(_recordPath).Length > 4096) return null;
            return JsonSerializer.Deserialize<PendingRecord>(File.ReadAllText(_recordPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void DeleteRecord()
    {
        try { File.Delete(_recordPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
