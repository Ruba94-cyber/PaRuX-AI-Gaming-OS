using System.Diagnostics;
using System.Text.Json;
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
    private readonly WindowsNativeSuspendProvider _provider = new();
    private readonly object _gate = new();
    private readonly string _journalPath;

    private readonly WorkloadOrchestrator? _orchestrator;

    public QuickResumeService(WorkloadOrchestrator orchestrator)
        : this(PaRuXDataRoot.Combine("quick-resume", "paused.json"), orchestrator) { }

    internal QuickResumeService(string journalPath, WorkloadOrchestrator? orchestrator = null)
    {
        _journalPath = journalPath;
        _orchestrator = orchestrator;
    }

    /// <summary>ReturnedToAi: the paused process was the game PaRuX started, so AI was given back while it is paused
    /// and is set aside again when the game resumes (the Xbox-style quick switch between a game and the AI).</summary>
    private sealed record Entry(int ProcessId, DateTime StartedUtc, string ProcessName, bool ReturnedToAi = false);

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
                    bool paused = journal.Any(e => e.ProcessId == process.Id);
                    if (string.IsNullOrWhiteSpace(title) && !paused) continue;
                    if (!TryStart(process, out DateTime started)) continue;
                    string? reason = QuickResumePolicy.RefusalReason(process.ProcessName, title.Length > 0 ? title : "paused",
                        process.SessionId == session, process.Id == self);
                    if (reason is not null && !paused) continue;
                    if (reason is null && !paused && !CanOpenForSuspend(process.Id))
                        reason = "This application runs with administrator rights, so PaRuX cannot pause it unless PaRuX is also elevated.";
                    apps.Add(new QuickResumeAppDto(process.Id, process.ProcessName, title, started, paused, reason is null, reason));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return new QuickResumeStatusDto(apps.OrderByDescending(a => a.Suspended).ThenBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool IsAlive(Entry entry)
    {
        try
        {
            using Process process = Process.GetProcessById(entry.ProcessId);
            return !process.HasExited && TryStart(process, out DateTime started) && QuickResumePolicy.SameProcess(entry.StartedUtc, started);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

    public async Task<QuickResumeActionResult> SuspendAsync(QuickResumeActionRequest request, CancellationToken ct)
    {
        Process process;
        try { process = Process.GetProcessById(request.ProcessId); }
        catch (ArgumentException) { return new(false, "The application is no longer running."); }
        using (process)
        {
            if (!TryStart(process, out DateTime started) || !QuickResumePolicy.SameProcess(request.StartedUtc, started))
                return new(false, "The application changed since the list was loaded. Refresh and try again.");
            string? reason = QuickResumePolicy.RefusalReason(process.ProcessName, process.MainWindowTitle,
                process.SessionId == Process.GetCurrentProcess().SessionId, process.Id == Environment.ProcessId);
            if (reason is not null) return new(false, reason);
            lock (_gate)
            {
                if (ReadJournal().Any(e => e.ProcessId == process.Id)) return new(true, "The application is already paused.");
            }
            if (!await _provider.SuspendProcessAsync(process.Id, ct).ConfigureAwait(false))
                return new(false, "Windows refused to pause this application (it may run with administrator rights).");
            bool returnedToAi = false;
            if (_orchestrator is not null && _orchestrator.IsOwnedGame(process.Id, started) && _orchestrator.GetStatus().IsGamingActive)
            {
                // The paused game no longer needs the GPU: give the AI back while it waits, without closing it.
                try { returnedToAi = await _orchestrator.ExitGamingSessionAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { returnedToAi = false; }
            }
            lock (_gate)
            {
                List<Entry> journal = ReadJournal();
                journal.Add(new Entry(process.Id, started, process.ProcessName, returnedToAi));
                WriteJournal(journal);
            }
            return new(true, process.ProcessName + (returnedToAi
                ? " is paused and the AI is available again. Resume it from Quick Resume."
                : " is paused. Resume it from Quick Resume."));
        }
    }

    public async Task<QuickResumeActionResult> ResumeAsync(QuickResumeActionRequest request, CancellationToken ct)
    {
        Entry? entry;
        lock (_gate) entry = ReadJournal().FirstOrDefault(e => e.ProcessId == request.ProcessId);
        if (entry is null || !QuickResumePolicy.SameProcess(entry.StartedUtc, request.StartedUtc))
            return new(false, "PaRuX did not pause this application, so it will not resume it.");
        if (!IsAlive(entry))
        {
            Forget(entry.ProcessId);
            return new(true, "The application had already closed.");
        }
        bool aiSetAside = false;
        if (entry.ReturnedToAi && _orchestrator is not null)
        {
            // The game gets the GPU back before it runs again; the AI is set aside, like leaving it for a game on a console.
            try { aiSetAside = await _orchestrator.EnterGamingSessionAsync(null, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { aiSetAside = false; }
        }
        if (!await _provider.ResumeProcessAsync(entry.ProcessId, ct).ConfigureAwait(false))
            return new(false, "Windows refused to resume this application.");
        Forget(entry.ProcessId);
        return new(true, entry.ProcessName + (aiSetAside ? " is running again; the AI was set aside for it." : " is running again."));
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
