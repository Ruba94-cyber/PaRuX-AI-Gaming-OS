using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// One read-only Toolhelp snapshot when the exact directly launched game process
/// exits. A parent PID alone is not ownership: candidates must have started
/// during the verified parent's lifetime. A surviving direct child prevents the
/// automatic AI/Console return, even if the parent ran longer than the bootstrap
/// grace period. Unknown/privileged processes fail closed to manual Return.
///
/// This is NOT process-tree ownership. If intermediate descendants already
/// exited, Windows may no longer expose their ancestry. We never claim that
/// an absence of live direct children proves there is no detached grandchild.
/// </summary>
internal static class OwnedGameChildExitGuard
{
    internal enum Result { NoLiveDirectChild, LiveDirectChild, Inconclusive }

    internal sealed record Candidate(int Pid, int ParentPid, DateTime? StartUtc, bool Alive, bool Readable);

    /// <summary>Pure conservative filter. A single unreadable matching child
    /// may be the game, so automatic Return is forbidden. Pre-existing PID
    /// reuse is excluded using the verified parent process creation time.</summary>
    internal static Result Evaluate(int verifiedParentPid, DateTime parentStartedUtc,
        DateTime parentExitedUtc, IEnumerable<Candidate>? children, bool complete)
    {
        if (!complete || verifiedParentPid <= 0 || parentStartedUtc.Kind != DateTimeKind.Utc ||
            parentExitedUtc.Kind != DateTimeKind.Utc || parentExitedUtc < parentStartedUtc ||
            children is null)
            return Result.Inconclusive;

        bool uncertain = false;
        foreach (Candidate child in children)
        {
            if (child.ParentPid != verifiedParentPid || child.Pid == verifiedParentPid || !child.Alive)
                continue;
            if (!child.Readable || child.StartUtc is null || child.StartUtc.Value.Kind != DateTimeKind.Utc)
            {
                uncertain = true;
                continue;
            }
            // A reused PID or stale kernel parent reference from a process that
            // predates this specific launch must not be counted as its child.
            DateTime created = child.StartUtc.Value;
            if (created < parentStartedUtc.AddSeconds(-2) ||
                created > parentExitedUtc.AddSeconds(2))
                continue;
            return Result.LiveDirectChild;
        }
        return uncertain ? Result.Inconclusive : Result.NoLiveDirectChild;
    }

    internal static Result InspectAtParentExit(int parentPid, DateTime startUtc,
        DateTime exitUtc, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || parentPid <= 0 || cancellationToken.IsCancellationRequested)
            return Result.Inconclusive;

        // One bounded snapshot at confirmed parent exit, never a worker
        // scanning processes during the game or an action against them.
        IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return Result.Inconclusive;
        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExecutableFile = string.Empty
            };
            var candidates = new List<Candidate>();
            if (!Process32FirstW(snapshot, ref entry))
                return Marshal.GetLastWin32Error() == 18
                    ? Evaluate(parentPid, startUtc, exitUtc, candidates, complete: true)
                    : Result.Inconclusive;

            int count = 0;
            do
            {
                if (cancellationToken.IsCancellationRequested || ++count > 65_536)
                    return Result.Inconclusive;
                if (entry.ParentProcessId == (uint)parentPid &&
                    entry.ProcessId is > 0 and <= int.MaxValue &&
                    entry.ProcessId != entry.ParentProcessId)
                {
                    int pid = (int)entry.ProcessId;
                    try
                    {
                        using Process child = Process.GetProcessById(pid);
                        if (!child.HasExited)
                            candidates.Add(new Candidate(pid, parentPid,
                                child.StartTime.ToUniversalTime(), Alive: true, Readable: true));
                    }
                    catch (ArgumentException)
                    {
                        // The child exited between the snapshot and the handle read.
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or
                        NotSupportedException or UnauthorizedAccessException)
                    {
                        // Never infer a safe game exit from an unreadable live
                        // candidate or unconfirmed process identity.
                        candidates.Add(new Candidate(pid, parentPid,
                            StartUtc: null, Alive: true, Readable: false));
                    }
                }
                entry.ExecutableFile = string.Empty;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32NextW(snapshot, ref entry));

            if (Marshal.GetLastWin32Error() != 18) return Result.Inconclusive;
            return Evaluate(parentPid, startUtc, exitUtc, candidates, complete: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or
            ArgumentException or NotSupportedException)
        {
            return Result.Inconclusive;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
