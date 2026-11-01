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
            _timer?.Dispose();
            _timer = null;
            string id = _pending.DisplayId;
            DisplayModeEntry kept = _applied ?? _pending.Previous;
            bool saved = _api.ApplyPermanent(id, kept);
            _pending = null;
            _applied = null;
            DeleteRecord();
            _lastMessage = saved
                ? "Display change confirmed and saved."
                : "Display change confirmed for this session; Windows could not save it as the default.";
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
            if (!_api.ApplyTemporary(_pending.DisplayId, _pending.Previous))
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
        PendingRecord? record = LoadRecord();
        if (record is null) return false;
        bool restored = _api.ApplyTemporary(record.DisplayId, record.Previous);
        if (restored) DeleteRecord();
        lock (_gate)
            _lastMessage = restored
                ? "A display change left unconfirmed by a previous run was reverted."
                : "An unconfirmed display change could not be reverted automatically; use Revert.";
        return restored;
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
