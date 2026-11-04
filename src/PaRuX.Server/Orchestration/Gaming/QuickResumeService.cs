using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Pauses and resumes an open application (Nyrna-style) and remembers what it paused in a small journal under the PaRuX
/// data folder, so a restart of PaRuX still shows what is paused and can bring it back. Only an application PaRuX itself
/// paused can be resumed through it, and every action re-checks the process id AND its start time.
/// </summary>
public sealed class QuickResumeService
{
    private readonly IGameSuspendProvider _provider;
    private readonly Func<int, DateTime, IReadOnlyList<QuickResumeChild>> _childFinder;
    private readonly List<QuickResumeRecoveryDto> _recovery = new();
    private readonly object _gate = new();
    // Pause and resume run one at a time: NtSuspendProcess counts, so two overlapping pauses would need two resumes.
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly string _journalPath;

    private readonly WorkloadOrchestrator? _orchestrator;
    private readonly ManualGameSessionService? _sessions;

    public QuickResumeService(WorkloadOrchestrator orchestrator, ManualGameSessionService sessions)
        : this(PaRuXDataRoot.Combine("quick-resume", "paused.json"), orchestrator, sessions) { }

    internal QuickResumeService(string journalPath, WorkloadOrchestrator? orchestrator = null, ManualGameSessionService? sessions = null,
        IGameSuspendProvider? provider = null, Func<int, DateTime, IReadOnlyList<QuickResumeChild>>? childFinder = null)
    {
        _journalPath = journalPath;
        _provider = provider ?? new WindowsNativeSuspendProvider();
        _childFinder = childFinder ?? QuickResumeProcessTree.FindDescendants;
        _orchestrator = orchestrator;
        _sessions = sessions;
    }

    /// <summary>ReturnedToAi: the paused process was the game PaRuX started, so AI was given back while it is paused
    /// and is set aside again when the game resumes (the Xbox-style quick switch between a game and the AI).</summary>
    private sealed record Entry(int ProcessId, DateTime StartedUtc, string ProcessName, bool ReturnedToAi = false,
        List<QuickResumeChild>? Children = null);

    private List<Entry> ReadJournal()
    {
        try
        {
            return File.Exists(_journalPath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_journalPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void WriteJournal(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        string tmp = _journalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, _journalPath, overwrite: true);
    }

    private static bool TryStart(Process process, out DateTime startedUtc)
    {
        try { startedUtc = process.StartTime.ToUniversalTime(); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { startedUtc = default; return false; }
    }

    /// <summary>Open application windows, plus anything PaRuX paused earlier that is still alive.</summary>
    public QuickResumeStatusDto List()
    {
        var apps = new List<QuickResumeAppDto>();
        List<QuickResumeRecoveryDto> recovery;
        lock (_gate) recovery = _recovery.ToList();
        int self = Environment.ProcessId;
        int session = Process.GetCurrentProcess().SessionId;
        List<Entry> journal;
        lock (_gate)
        {
            journal = ReadJournal();
            // Forget entries whose process ended or was replaced by another one with the same id.
            int before = journal.Count;
            journal.RemoveAll(e => !IsAlive(e));
            if (journal.Count != before) WriteJournal(journal);
        }

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string title = process.MainWindowTitle ?? string.Empty;
                    Entry? pausedEntry = journal.FirstOrDefault(e => e.ProcessId == process.Id);
                    bool paused = pausedEntry is not null;
                    if (string.IsNullOrWhiteSpace(title) && !paused) continue;
                    if (!TryStart(process, out DateTime started)) continue;
                    string? reason = QuickResumePolicy.RefusalReason(process.ProcessName, title.Length > 0 ? title : "paused",
                        process.SessionId == session, process.Id == self);
                    if (reason is not null && !paused) continue;
                    if (reason is null && !paused && !CanOpenForSuspend(process.Id))
                        reason = "This application runs with administrator rights, so PaRuX cannot pause it unless PaRuX is also elevated.";
                    apps.Add(new QuickResumeAppDto(process.Id, process.ProcessName, title, started, paused, reason is null, reason, pausedEntry?.Children?.Count ?? 0));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return new QuickResumeStatusDto(apps.OrderByDescending(a => a.Suspended).ThenBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).ToList(), recovery);
    }

    private static bool IsAlive(Entry entry) => IsAlive(entry.ProcessId, entry.StartedUtc) || (entry.Children?.Any(c => IsAlive(c.ProcessId, c.StartedUtc)) ?? false);

    private static bool IsAlive(int pid, DateTime startedUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && TryStart(process, out DateTime started) && QuickResumePolicy.SameProcess(startedUtc, started);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; } // the id now belongs to a process we may not inspect
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool CanOpenForSuspend(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr handle = OpenProcess(0x0800, false, pid);
        if (handle == IntPtr.Zero) return false;
        CloseHandle(handle);
        return true;
    }

    public Task<QuickResumeActionResult> SuspendAsync(QuickResumeActionRequest request, CancellationToken ct) =>
        SuspendAsync(request, ct, requireWindow: true);

    /// <param name="requireWindow">Always true for the public API; tests with window-less throw-away processes pass false.
    /// The deny list, session, own-process and identity checks still apply.</param>
    internal async Task<QuickResumeActionResult> SuspendAsync(QuickResumeActionRequest request, CancellationToken ct, bool requireWindow)
    {
        await _actions.WaitAsync(ct).ConfigureAwait(false);
        try { return await SuspendCoreAsync(request, ct, requireWindow).ConfigureAwait(false); }
        finally { _actions.Release(); }
    }

    private async Task<QuickResumeActionResult> SuspendCoreAsync(QuickResumeActionRequest request, CancellationToken ct, bool requireWindow)
    {
        Process process;
        try { process = Process.GetProcessById(request.ProcessId); }
        catch (ArgumentException) { return new(false, "The application is no longer running."); }
        using (process)
        {
            if (!TryStart(process, out DateTime started) || !QuickResumePolicy.SameProcess(request.StartedUtc, started))
                return new(false, "The application changed since the list was loaded. Refresh and try again.");
            string? reason = QuickResumePolicy.RefusalReason(process.ProcessName, requireWindow ? process.MainWindowTitle : "(no window required)",
                process.SessionId == Process.GetCurrentProcess().SessionId, process.Id == Environment.ProcessId);
            if (reason is not null) return new(false, reason);
            lock (_gate)
            {
                if (ReadJournal().Any(e => e.ProcessId == process.Id)) return new(true, "The application is already paused.");
            }
            // Journal the intent BEFORE pausing: if PaRuX dies between the two steps, recovery finds the entry and a
            // resume of a process that was never suspended is a harmless no-op.
            lock (_gate)
            {
                List<Entry> intent = ReadJournal();
                intent.Add(new Entry(process.Id, started, process.ProcessName));
                WriteJournal(intent);
            }
            if (!await _provider.SuspendProcessAsync(process.Id, ct).ConfigureAwait(false))
            {
                Forget(process.Id);
                return new(false, "Windows refused to pause this application (it may run with administrator rights).");
            }
            bool returnedToAi = false;
            if (_orchestrator is not null && _orchestrator.IsOwnedGame(process.Id, started) && _orchestrator.GetStatus().IsGamingActive)
            {
                // The paused game no longer needs the GPU: give the AI back while it waits, without closing it.
                try { returnedToAi = await _orchestrator.ExitGamingSessionAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { returnedToAi = false; }
            }

            // Process group: pause the verified children too (the parent is already frozen, so it cannot spawn more).
            // Children are journalled before they are paused, for the same crash-safety reason as above.
            List<QuickResumeChild> candidates = SafeFindChildren(process.Id, started);
            Entry planned = new(process.Id, started, process.ProcessName, returnedToAi, candidates.Count > 0 ? candidates : null);
            lock (_gate) ReplaceEntry(planned);
            var paused = new List<QuickResumeChild>();
            foreach (QuickResumeChild child in candidates)
            {
                if (!IsAlive(child.ProcessId, child.StartedUtc)) continue;
                if (await _provider.SuspendProcessAsync(child.ProcessId, ct).ConfigureAwait(false)) paused.Add(child);
            }
            int skipped = candidates.Count - paused.Count;
            lock (_gate) ReplaceEntry(planned with { Children = paused.Count > 0 ? paused : null });
            string group = paused.Count > 0 ? " (with " + paused.Count + " related process" + (paused.Count == 1 ? "" : "es") + ")" : string.Empty;
            string note = skipped > 0 ? " " + skipped + " related process" + (skipped == 1 ? " was" : "es were") + " not paused (closed or protected)." : string.Empty;
            return new(true, process.ProcessName + group + (returnedToAi
                ? " is paused and the AI is available again. Resume it from Quick Resume."
                : " is paused. Resume it from Quick Resume.") + note);
        }
    }

    private List<QuickResumeChild> SafeFindChildren(int pid, DateTime started)
    {
        try { return _childFinder(pid, started).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(); }
    }

    private void ReplaceEntry(Entry entry)
    {
        List<Entry> journal = ReadJournal();
        journal.RemoveAll(e => e.ProcessId == entry.ProcessId);
        journal.Add(entry);
        WriteJournal(journal);
    }

    public async Task<QuickResumeActionResult> ResumeAsync(QuickResumeActionRequest request, CancellationToken ct)
    {
        await _actions.WaitAsync(ct).ConfigureAwait(false);
        try { return await ResumeCoreAsync(request, ct).ConfigureAwait(false); }
        finally { _actions.Release(); }
    }

    private async Task<QuickResumeActionResult> ResumeCoreAsync(QuickResumeActionRequest request, CancellationToken ct)
    {
        Entry? entry;
        lock (_gate) entry = ReadJournal().FirstOrDefault(e => e.ProcessId == request.ProcessId);
        if (entry is null || !QuickResumePolicy.SameProcess(entry.StartedUtc, request.StartedUtc))
            return new(false, "PaRuX did not pause this application, so it will not resume it.");
        (QuickResumeActionResult result, _) = await ResumeEntryAsync(entry, restoreAiSetAside: true, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Resumes the children, then the main process, each only if its id and start time still match the journal.
    /// Whatever could not be resumed stays in the journal (so it is never silently forgotten); the rest is removed.</summary>
    private async Task<(QuickResumeActionResult Result, string Outcome)> ResumeEntryAsync(Entry entry, bool restoreAiSetAside, CancellationToken ct)
    {
        bool parentAlive = IsAlive(entry.ProcessId, entry.StartedUtc);
        var failedChildren = new List<QuickResumeChild>();
        int resumedChildren = 0;
        foreach (QuickResumeChild child in entry.Children ?? new List<QuickResumeChild>())
        {
            if (!IsAlive(child.ProcessId, child.StartedUtc)) continue; // gone, or the id now belongs to another process
            if (await _provider.ResumeProcessAsync(child.ProcessId, ct).ConfigureAwait(false)) resumedChildren++;
            else failedChildren.Add(child);
        }
        if (!parentAlive && failedChildren.Count == 0)
        {
            Forget(entry.ProcessId);
            return (new(true, resumedChildren > 0 ? "The application had already closed; its related processes were resumed." : "The application had already closed."),
                resumedChildren > 0 ? "resumed" : "already-closed");
        }
        bool aiSetAside = false;
        if (parentAlive)
        {
            if (restoreAiSetAside && entry.ReturnedToAi && _sessions is not null)
            {
                // The game gets the GPU back before it runs again; the AI is set aside, like leaving it for a game on a console.
                // The exact process is registered again, so closing the game later returns the AI automatically.
                try { aiSetAside = await _sessions.ResumeOwnedSessionAsync(entry.ProcessId, entry.StartedUtc, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { aiSetAside = false; }
            }
            if (!await _provider.ResumeProcessAsync(entry.ProcessId, ct).ConfigureAwait(false))
            {
                lock (_gate) ReplaceEntry(entry with { Children = failedChildren.Count > 0 ? failedChildren : null });
                return (new(false, "Windows refused to resume this application."), "failed");
            }
        }
        if (failedChildren.Count > 0)
        {
            lock (_gate) ReplaceEntry(entry with { Children = failedChildren });
            return (new(false, entry.ProcessName + " is running again, but " + failedChildren.Count + " related process"
                + (failedChildren.Count == 1 ? "" : "es") + " could not be resumed. Try Resume again."), "failed");
        }
        Forget(entry.ProcessId);
        string group = resumedChildren > 0 ? " (with " + resumedChildren + " related process" + (resumedChildren == 1 ? "" : "es") + ")" : string.Empty;
        return (new(true, entry.ProcessName + group + (aiSetAside ? " is running again; the AI was set aside for it." : " is running again.")), "resumed");
    }

    /// <summary>
    /// Crash recovery and graceful shutdown: resume everything PaRuX itself journalled as paused and whose identity (id +
    /// start time) still verifies. Nothing else is touched. The AI is not set aside again (PaRuX is starting up or going
    /// away, so no game session is registered). The outcome per entry is kept for the status list.
    /// </summary>
    public async Task<IReadOnlyList<QuickResumeRecoveryDto>> RecoverAsync(string trigger, CancellationToken ct)
    {
        await _actions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Entry> entries;
            lock (_gate) entries = ReadJournal();
            var outcomes = new List<QuickResumeRecoveryDto>();
            foreach (Entry entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                (QuickResumeActionResult result, string outcome) = await ResumeEntryAsync(entry, restoreAiSetAside: false, ct).ConfigureAwait(false);
                string message = outcome == "resumed"
                    ? (trigger == "startup" ? "Resumed after PaRuX restarted: " : "Resumed because PaRuX closed: ") + result.Message
                    : result.Message;
                outcomes.Add(new QuickResumeRecoveryDto(entry.ProcessId, entry.ProcessName, outcome, message, DateTime.UtcNow, trigger));
            }
            if (outcomes.Count > 0)
            {
                lock (_gate)
                {
                    _recovery.Clear();
                    _recovery.AddRange(outcomes);
                }
            }
            return outcomes;
        }
        finally { _actions.Release(); }
    }

    public async Task<QuickResumeActionResult> ResumeAllAsync(CancellationToken ct)
    {
        List<Entry> entries;
        lock (_gate) entries = ReadJournal();
        int resumed = 0, failed = 0;
        foreach (Entry entry in entries)
        {
            QuickResumeActionResult result = await ResumeAsync(new QuickResumeActionRequest(entry.ProcessId, entry.StartedUtc), ct).ConfigureAwait(false);
            if (result.Succeeded) resumed++; else failed++;
        }
        return new(failed == 0, resumed + " resumed" + (failed > 0 ? ", " + failed + " could not be resumed." : "."));
    }

    private void Forget(int pid)
    {
        lock (_gate)
        {
            List<Entry> journal = ReadJournal();
            if (journal.RemoveAll(e => e.ProcessId == pid) > 0) WriteJournal(journal);
        }
    }
}

/// <summary>
/// Quick Resume crash recovery: when Core starts, anything PaRuX itself left paused (journal + verified identity) is
/// resumed, so a crash or kill of PaRuX never strands a frozen game. On graceful shutdown the same is done, because
/// nothing would be left to resume it. The outcome is visible in the Quick Resume list (Recovery).
/// </summary>
public sealed class QuickResumeRecoveryHostedService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly QuickResumeService _service;
    private readonly Microsoft.Extensions.Logging.ILogger<QuickResumeRecoveryHostedService>? _logger;

    public QuickResumeRecoveryHostedService(QuickResumeService service, Microsoft.Extensions.Logging.ILogger<QuickResumeRecoveryHostedService>? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync("startup", cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => RunAsync("shutdown", cancellationToken);

    private async Task RunAsync(string trigger, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); // bounded: never delay start-up or shutdown for long
            IReadOnlyList<QuickResumeRecoveryDto> outcomes = await _service.RecoverAsync(trigger, timeout.Token).ConfigureAwait(false);
            foreach (QuickResumeRecoveryDto o in outcomes)
                _logger?.LogInformation("Quick Resume {Trigger} recovery: {Process} -> {Outcome}", trigger, o.ProcessName, o.Outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Quick Resume {Trigger} recovery did not complete", trigger);
        }
    }
}

public static class QuickResumeEndpoints
{
    public static void MapQuickResumeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/quick-resume", (QuickResumeService service) => Results.Ok(service.List()));

        app.MapPost("/api/v1/gaming/quick-resume/suspend", async (QuickResumeActionRequest request, QuickResumeService service, CancellationToken ct) =>
        {
            QuickResumeActionResult result = await service.SuspendAsync(request, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/quick-resume/resume", async (QuickResumeActionRequest request, QuickResumeService service, CancellationToken ct) =>
        {
            QuickResumeActionResult result = await service.ResumeAsync(request, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/quick-resume/resume-all", async (QuickResumeService service, CancellationToken ct) =>
            Results.Ok(await service.ResumeAllAsync(ct))).RequireDesktopOwner();
    }
}
