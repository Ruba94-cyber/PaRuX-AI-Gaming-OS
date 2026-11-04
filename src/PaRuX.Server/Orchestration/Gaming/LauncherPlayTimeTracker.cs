using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Play time for games started through a store launcher (Steam / Epic / GOG / EA / Battle.net / Ubisoft /
/// Xbox). The launcher usually hands the request to an already-running client and exits first, so PaRuX has
/// no process handle to wait on. Instead it verifies the game process itself:
///   1. discovery: a process whose full image path lies inside the game's own install folder AND which was
///      created after the launch request (a pre-existing process, a reused PID or an unreadable image is never adopted);
///   2. tree: a direct child of a tracked process is adopted only when <see cref="OwnedGameChildExitGuard.Evaluate"/>
///      accepts it (parent PID match, created inside the verified parent's lifetime, readable identity);
///   3. while something tracked is alive only cheap per-PID identity checks run (no process scan during play);
///      a full snapshot is taken again only when a tracked process was seen to exit, and in the short end grace.
/// Credit = first verified start until the last verified process was last seen alive. It never kills, suspends
/// or modifies a process and never changes AI/Gaming state. Honest limits: a game whose image cannot be read
/// (elevated) is not credited; a detached grandchild whose parent already exited is not found; precision is
/// the polling interval.
/// </summary>
internal static class LauncherPlayTimeTracker
{
    internal sealed record ProcSnap(int Pid, int ParentPid, DateTime StartUtc, string? ImagePath);

    internal sealed record Options(
        TimeSpan DiscoveryWindow, TimeSpan DiscoveryInterval, TimeSpan PlayInterval, TimeSpan EndGrace)
    {
        public static Options Default { get; } = new(
            TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    internal sealed record Outcome(bool Observed, DateTime StartedUtc, TimeSpan Played, bool Cancelled);

    internal interface IProbe
    {
        /// <summary>One full snapshot of live processes (pid, parent, start, image path when readable).</summary>
        IReadOnlyList<ProcSnap> Snapshot(CancellationToken ct);
        /// <summary>True when that exact PID with that exact creation time is still running.</summary>
        bool IsAlive(int pid, DateTime startUtc);
    }

    private sealed class Tracked
    {
        public Tracked(ProcSnap snap) { Snap = snap; }
        public ProcSnap Snap { get; }
        public bool Alive { get; set; } = true;
        public DateTime LastAliveUtc { get; set; }
    }

    /// <summary>Case-insensitive "inside this folder" test on full paths (never a bare prefix of a sibling folder).</summary>
    internal static bool IsInside(string? imagePath, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(installDirectory)) return false;
        try
        {
            string dir = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string image = Path.GetFullPath(imagePath);
            return image.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static async Task<Outcome> TrackAsync(string installDirectory, DateTime launchRequestedUtc,
        IProbe probe, Func<DateTime> utcNow, Func<TimeSpan, CancellationToken, Task> delay, Options options,
        CancellationToken ct)
    {
        var tracked = new List<Tracked>();
        DateTime firstStart = default;
        DateTime lastAlive = default;
        try
        {
            // 1. Discovery.
            DateTime deadline = utcNow() + options.DiscoveryWindow;
            while (tracked.Count == 0)
            {
                ct.ThrowIfCancellationRequested();
                Adopt(probe.Snapshot(ct), tracked, installDirectory, launchRequestedUtc, utcNow());
                if (tracked.Count > 0) break;
                if (utcNow() >= deadline) return new Outcome(false, default, TimeSpan.Zero, false);
                await delay(options.DiscoveryInterval, ct).ConfigureAwait(false);
            }
            firstStart = tracked.Min(t => t.Snap.StartUtc);
            lastAlive = utcNow();
            foreach (Tracked t in tracked) t.LastAliveUtc = lastAlive;

            // 2. Play loop.
            DateTime? noneAliveSince = null;
            while (true)
            {
                await delay(options.PlayInterval, ct).ConfigureAwait(false);
                DateTime now = utcNow();
                bool exitSeen = false;
                foreach (Tracked t in tracked.Where(t => t.Alive))
                {
                    if (probe.IsAlive(t.Snap.Pid, t.Snap.StartUtc)) t.LastAliveUtc = now;
                    else { t.Alive = false; exitSeen = true; }
                }
                if (exitSeen || noneAliveSince is not null)
                {
                    // A process just exited (a child may have outlived it) or we are in the end grace
                    // (a relaunch into another executable of the same game): look once more.
                    Adopt(probe.Snapshot(ct), tracked, installDirectory, launchRequestedUtc, now);
                }
                if (tracked.Any(t => t.Alive))
                {
                    lastAlive = now;
                    noneAliveSince = null;
                    continue;
                }
                noneAliveSince ??= now;
                if (now - noneAliveSince.Value >= options.EndGrace) break;
            }
            return new Outcome(true, firstStart, lastAlive > firstStart ? lastAlive - firstStart : TimeSpan.Zero, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Return to AI / shutdown while the game still ran: credit what was observed.
            if (tracked.Count == 0) return new Outcome(false, default, TimeSpan.Zero, true);
            return new Outcome(true, firstStart, lastAlive > firstStart ? lastAlive - firstStart : TimeSpan.Zero, true);
        }
    }

    private static void Adopt(IReadOnlyList<ProcSnap> snapshot, List<Tracked> tracked, string installDirectory,
        DateTime launchRequestedUtc, DateTime now)
    {
        foreach (ProcSnap p in snapshot)
        {
            if (p.Pid <= 4 || p.StartUtc.Kind != DateTimeKind.Utc) continue;
            if (tracked.Any(t => t.Snap.Pid == p.Pid && t.Snap.StartUtc == p.StartUtc)) continue;

            bool root = p.StartUtc >= launchRequestedUtc.AddSeconds(-2) && IsInside(p.ImagePath, installDirectory);
            bool child = false;
            if (!root)
            {
                foreach (Tracked parent in tracked)
                {
                    if (parent.Snap.Pid != p.ParentPid) continue;
                    DateTime parentEnd = parent.Alive ? now : parent.LastAliveUtc.AddSeconds(2);
                    var candidate = new OwnedGameChildExitGuard.Candidate(p.Pid, p.ParentPid, p.StartUtc, Alive: true, Readable: true);
                    if (OwnedGameChildExitGuard.Evaluate(parent.Snap.Pid, parent.Snap.StartUtc,
                            parentEnd < parent.Snap.StartUtc ? parent.Snap.StartUtc : parentEnd,
                            new[] { candidate }, complete: true) == OwnedGameChildExitGuard.Result.LiveDirectChild)
                    {
                        child = true;
                        break;
                    }
                }
            }
            if (root || child) tracked.Add(new Tracked(p) { LastAliveUtc = now });
        }
    }

    /// <summary>Tracks, then credits the observed time to the title (sessions under five seconds are bootstrap noise).</summary>
    internal static async Task<Outcome> TrackAndCreditAsync(GameTitleStore? titles, string titleKey,
        string installDirectory, DateTime launchRequestedUtc, IProbe probe, Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay, Options options, CancellationToken ct)
    {
        Outcome outcome = await TrackAsync(installDirectory, launchRequestedUtc, probe, utcNow, delay, options, ct)
            .ConfigureAwait(false);
        if (outcome.Observed && outcome.Played >= TimeSpan.FromSeconds(5) && titles is not null && titleKey.Length > 0)
        {
            try { titles.AddSession(titleKey, outcome.StartedUtc, outcome.Played); }
            catch (OverflowException) { }
        }
        return outcome;
    }

    /// <summary>Production probe: Toolhelp snapshot for parent links, handle-based identity for start time and image.</summary>
    internal sealed class WindowsProbe : IProbe
    {
        public IReadOnlyList<ProcSnap> Snapshot(CancellationToken ct)
        {
            var result = new List<ProcSnap>();
            if (!OperatingSystem.IsWindows()) return result;
            IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
            if (snapshot == new IntPtr(-1)) return result;
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExecutableFile = string.Empty };
                if (!Process32FirstW(snapshot, ref entry)) return result;
                int count = 0;
                do
                {
                    if (ct.IsCancellationRequested || ++count > 65_536) break;
                    int pid = (int)entry.ProcessId;
                    if (pid > 4)
                    {
                        try
                        {
                            using Process process = Process.GetProcessById(pid);
                            if (!process.HasExited)
                                result.Add(new ProcSnap(pid, (int)entry.ParentProcessId,
                                    process.StartTime.ToUniversalTime(), ManualGameSessionService.ImagePathOf(process)));
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                            or NotSupportedException or UnauthorizedAccessException) { }
                    }
                    entry.ExecutableFile = string.Empty;
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                } while (Process32NextW(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
            return result;
        }

        public bool IsAlive(int pid, DateTime startUtc)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - startUtc).TotalSeconds) < 2;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                or NotSupportedException)
            {
                return false;
            }
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

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
