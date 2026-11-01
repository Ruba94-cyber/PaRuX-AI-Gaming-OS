using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// One bounded, opt-in placement attempt for a top-level WINDOWED UI belonging
/// to the exact process PaRuX just started and verified. Never touches foreign
/// windows, child processes, Steam/Epic/GOG handoffs or exclusive fullscreen.
/// Never activates a window or changes system monitor, resolution or HDR state.
/// </summary>
public sealed class OwnedGameWindowPlacementService
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int GwOwner = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const long WsCaption = 0x00C00000L;
    private const long WsExToolWindow = 0x00000080L;
    private readonly GamingDisplayTopologyService _topology;

    public OwnedGameWindowPlacementService(GamingDisplayTopologyService topology) => _topology = topology;

    internal sealed record WindowCandidate(IntPtr Handle, int Pid, bool Visible, bool HasOwner,
        bool ToolWindow, bool Minimized, bool Maximized, bool Captioned);

    internal static IntPtr SelectUniqueWindow(IEnumerable<WindowCandidate> candidates, int expectedPid)
    {
        // A splash screen, emulator settings window, or secondary process is not
        // a safe target. If multiple eligible windows exist, refuse to guess.
        var matches = candidates.Where(x => x.Handle != IntPtr.Zero && x.Pid == expectedPid &&
            x.Visible && !x.HasOwner && !x.ToolWindow && !x.Minimized && !x.Maximized && x.Captioned)
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Handle : IntPtr.Zero;
    }

    internal static (int X, int Y)? CenterUnchangedWindow(
        GamingDisplayModeDto target, int width, int height)
    {
        // Never resize a game, change graphics mode, or move an oversized
        // window partly off the selected TV (negative desktop coordinates work).
        if (target.Width <= 0 || target.Height <= 0 || width <= 0 || height <= 0 ||
            width > target.Width || height > target.Height) return null;
        long x = (long)target.X + (target.Width - width) / 2;
        long y = (long)target.Y + (target.Height - height) / 2;
        return x is >= int.MinValue and <= int.MaxValue &&
            y is >= int.MinValue and <= int.MaxValue ? ((int)x, (int)y) : null;
    }

    /// <summary>Fail before launching if the requested destination is no longer
    /// connected. Only the current OS topology can authorize positioning.</summary>
    public bool HasConnectedTarget(string? displayId) =>
        OperatingSystem.IsWindows() &&
        ConsoleDisplaySelectionPolicy.ValidDisplayId(displayId) &&
        _topology.Snapshot() is { Supported: true } topology &&
        topology.Displays.Any(d => string.Equals(d.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a truthful bounded outcome, never throws for a GUI that
    /// is absent, running fullscreen, or refusing repositioning. No periodic
    /// service, global shell hooks, input injection or focus stealing.</summary>
    public async Task<string> TryPlaceAsync(Process owned, string verifiedImagePath,
        string displayId, CancellationToken stop)
    {
        if (!HasConnectedTarget(displayId)) return "display-unavailable";
        if (!OperatingSystem.IsWindows()) return "unsupported";

        IntPtr previousWindow = IntPtr.Zero;
        int stableCount = 0;
        try
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (stop.IsCancellationRequested) return "not-confirmed";
                if (!ManualGameSessionService.VerifyOwnedProcess(owned, verifiedImagePath))
                    return "not-confirmed";
                GamingDisplaySnapshotDto topology = _topology.Snapshot();
                GamingDisplayModeDto? destination = topology.Displays.FirstOrDefault(d =>
                    string.Equals(d.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
                if (!topology.Supported || destination is null) return "display-unavailable";

                IntPtr candidate = SelectUniqueWindow(FindWindows(owned.Id), owned.Id);
                if (candidate != IntPtr.Zero && candidate == previousWindow)
                    stableCount++;
                else
                    stableCount = 0;
                previousWindow = candidate;

                // A single transient splash/dialog must not be treated as the
                // game's main window; demand one stable candidate across 2 reads.
                if (candidate != IntPtr.Zero && stableCount >= 1)
                {
                    if (!ManualGameSessionService.VerifyOwnedProcess(owned, verifiedImagePath) ||
                        SelectUniqueWindow(FindWindows(owned.Id), owned.Id) != candidate)
                        return "not-confirmed";
                    if (!GetWindowRect(candidate, out WinRect rect))
                        return "not-confirmed";
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    var centered = CenterUnchangedWindow(destination, width, height);
                    if (!centered.HasValue) return "unsupported-window";
                    if (rect.Left == centered.Value.X && rect.Top == centered.Value.Y)
                        return "already-on-tv";
                    // Keep size/z-order/focus exactly as before. Win32 may refuse
                    // foreign-integrity/elevated windows: report, never escalate.
                    return SetWindowPos(candidate, IntPtr.Zero, centered.Value.X,
                        centered.Value.Y, 0, 0, SwpNoSize | SwpNoZOrder |
                        SwpNoActivate | SwpNoOwnerZOrder)
                        ? "placed" : "not-confirmed";
                }
                await Task.Delay(250, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return "not-confirmed"; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or ObjectDisposedException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException)
        {
            return "not-confirmed";
        }
        return "unsupported-window";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr userData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr userData);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    private static IReadOnlyList<WindowCandidate> FindWindows(int pid)
    {
        var list = new List<WindowCandidate>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint ownerPid);
            if (ownerPid != (uint)pid) return true;
            // Bounded observation: don't enumerate arbitrary child processes
            // or resolve titles and executable paths of unrelated windows.
            if (list.Count >= 16) return false;
            int style = GetWindowLong(window, GwlStyle);
            int exStyle = GetWindowLong(window, GwlExStyle);
            list.Add(new WindowCandidate(window, (int)ownerPid, IsWindowVisible(window),
                GetWindow(window, GwOwner) != IntPtr.Zero,
                (exStyle & WsExToolWindow) != 0, IsIconic(window),
                IsZoomed(window), (style & WsCaption) == WsCaption));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
