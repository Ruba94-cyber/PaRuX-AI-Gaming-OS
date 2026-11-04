using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>A verified child (or grandchild) of a paused application: id plus start time, because Windows reuses ids.</summary>
internal sealed record QuickResumeChild(int ProcessId, DateTime StartedUtc, string ProcessName);

/// <summary>
/// Finds the child processes of a paused application (launcher + game, game + crash handler). Selection is a pure
/// function over a (pid, parent pid) table so it is unit-testable; the table comes from a Toolhelp snapshot. A candidate
/// counts only if it started no earlier than its parent (guards against a reused parent id), is not a protected
/// Windows/security/PaRuX process and is not PaRuX itself.
/// </summary>
internal static class QuickResumeProcessTree
{
    internal const int MaxChildren = 64;

    internal static IReadOnlyList<QuickResumeChild> SelectDescendants(
        IReadOnlyList<(int Pid, int ParentPid)> table,
        int rootPid,
        DateTime rootStartedUtc,
        int selfPid,
        Func<int, (string Name, DateTime StartedUtc)?> identify)
    {
        var known = new Dictionary<int, DateTime> { [rootPid] = rootStartedUtc.ToUniversalTime() };
        var rejected = new HashSet<int>();
        var result = new List<QuickResumeChild>();
        bool grew = true;
        // Repeat until no new descendant is found: the table is unordered, so a grandchild can precede its parent.
        while (grew && result.Count < MaxChildren)
        {
            grew = false;
            foreach ((int pid, int parent) in table)
            {
                if (pid == rootPid || pid == selfPid || pid <= 4 || known.ContainsKey(pid) || rejected.Contains(pid)) continue;
                if (!known.TryGetValue(parent, out DateTime parentStart)) continue;
                (string Name, DateTime StartedUtc)? info = identify(pid);
                if (info is null) { rejected.Add(pid); continue; }
                DateTime start = info.Value.StartedUtc.ToUniversalTime();
                // Started before its "parent": the parent id was reused, so this is not really a child.
                if (start < parentStart.AddSeconds(-2) || QuickResumePolicy.IsProtected(info.Value.Name)) { rejected.Add(pid); continue; }
                known[pid] = start;
                result.Add(new QuickResumeChild(pid, start, info.Value.Name));
                grew = true;
                if (result.Count >= MaxChildren) break;
            }
        }
        return result.OrderBy(c => c.StartedUtc).ThenBy(c => c.ProcessId).ToList();
    }

    /// <summary>Descendants of a live process, via a Toolhelp snapshot.</summary>
    internal static IReadOnlyList<QuickResumeChild> FindDescendants(int rootPid, DateTime rootStartedUtc)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<QuickResumeChild>();
        return SelectDescendants(SnapshotTable(), rootPid, rootStartedUtc, Environment.ProcessId, Identify);
    }

    private static (string, DateTime)? Identify(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            return (p.ProcessName, p.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }

    private const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    private static IReadOnlyList<(int, int)> SnapshotTable()
    {
        var table = new List<(int, int)>();
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(), szExeFile = string.Empty };
            if (!Process32FirstW(snap, ref entry)) return table;
            do { table.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID)); }
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return table;
    }
}
