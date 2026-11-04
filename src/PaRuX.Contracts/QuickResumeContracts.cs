namespace PaRuX.Contracts;

/// <summary>An open application window that Quick Resume can pause (or has paused).</summary>
public sealed record QuickResumeAppDto(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    DateTime StartedUtc,
    bool Suspended,
    bool CanSuspend,
    string? Reason,
    int PausedChildCount = 0);

/// <summary>What the last automatic recovery (Core start-up or graceful shutdown) did with one journalled paused process.
/// Outcome: resumed | already-closed | failed.</summary>
public sealed record QuickResumeRecoveryDto(
    int ProcessId,
    string ProcessName,
    string Outcome,
    string Message,
    DateTime AtUtc,
    string Trigger);

public sealed record QuickResumeStatusDto(IReadOnlyList<QuickResumeAppDto> Apps, IReadOnlyList<QuickResumeRecoveryDto>? Recovery = null);

/// <summary>Identity of the process to act on: the id alone is not enough because Windows reuses process ids.</summary>
public sealed record QuickResumeActionRequest(int ProcessId, DateTime StartedUtc);

public sealed record QuickResumeActionResult(bool Succeeded, string Message);

/// <summary>
/// Pure safety rules for Quick Resume, the first-party replacement for Nyrna: pause an open application so it uses
/// no CPU, and bring it back exactly where it was. Only an ordinary application window may be paused: never Windows
/// itself, a security tool, a shell or PaRuX's own processes, because freezing those would freeze the user's session.
/// </summary>
public static class QuickResumePolicy
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm", "svchost",
        "fontdrvhost", "dwm", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        "sihost", "taskhostw", "ctfmon", "RuntimeBroker", "ApplicationFrameHost", "LogonUI", "consent", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "spoolsv", "conhost", "WUDFHost", "dllhost", "taskmgr",
        "PaRuX", "PaRuX.Server", "PaRuX.Cli", "PaRuX.UpdateHelper", "PaRuX.WinMlWorker", "llama-server", "claude"
    };

    public static bool IsProtected(string? processName) =>
        string.IsNullOrWhiteSpace(processName) || ProtectedNames.Contains(processName.Trim());

    /// <summary>Why this application must not be paused, or null when it may.</summary>
    public static string? RefusalReason(string? processName, string? windowTitle, bool sameSession, bool ownProcess)
    {
        if (ownProcess) return "This is a PaRuX process.";
        if (IsProtected(processName)) return "Windows and security components cannot be paused.";
        if (!sameSession) return "This application belongs to another Windows session.";
        if (string.IsNullOrWhiteSpace(windowTitle)) return "Only applications with a visible window can be paused.";
        return null;
    }

    /// <summary>The process may be acted on only if it is still the one the user saw (same id and start time, 2 s tolerance).</summary>
    public static bool SameProcess(DateTime expectedStartUtc, DateTime actualStartUtc) =>
        Math.Abs((expectedStartUtc.ToUniversalTime() - actualStartUtc.ToUniversalTime()).TotalSeconds) <= 2;
}
