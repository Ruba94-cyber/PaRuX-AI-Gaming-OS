using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Single opt-in, bounded post-start action against the ONE stable top-level game
/// window of the process that PaRuX directly launched and independently verified.
/// No launcher handoffs, child-process adoption, global SendInput, blind Alt+Tab,
/// arbitrary-title matching, thread-input attachment or elevation.
/// Windows may decline foreground permission or ignore posted key/mouse messages;
/// successful posting is never represented as confirmed fullscreen/game response.
/// </summary>
public sealed class OwnedGamePostLaunchService
{
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftDown = 0x0201;
    private const uint WmLeftUp = 0x0202;
    private const uint VkReturn = 0x0D;
    private const uint MkLeft = 0x0001;
    private const int MaxWindowObservations = 18;

    internal sealed record Candidate(
        IntPtr Handle, int Pid, bool Visible, bool HasOwner, bool ToolWindow,
        bool Minimized, bool Enabled);

    internal static IntPtr SelectUniqueWindow(IEnumerable<Candidate> candidates, int expectedPid)
    {
        // Captionless/borderless and maximized game windows are legitimate here,
        // unlike a WINDOWED-TV-placement request. Splash screens, tool/popup
        // windows, multiple competing windows and a foreign PID are not.
        Candidate[] eligible = candidates.Where(w =>
            w.Handle != IntPtr.Zero && w.Pid == expectedPid &&
            w.Visible && !w.HasOwner && !w.ToolWindow && !w.Minimized && w.Enabled)
            .Take(2).ToArray();
        return eligible.Length == 1 ? eligible[0].Handle : IntPtr.Zero;
    }

    /// <summary>Safe client-center coordinate for a single opt-in window-targeted click.</summary>
    internal static (int X, int Y)? CenterPoint(int clientWidth, int clientHeight)
    {
        // WM_MOUSE* encodes signed 16-bit client coordinates in lParam.
        if (clientWidth is < 2 or > 32767 || clientHeight is < 2 or > 32767)
            return null;
        return (clientWidth / 2, clientHeight / 2);
    }

    internal static IntPtr MouseLParam(int x, int y) =>
        new(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x)));

    public async Task<string> TryApplyAsync(Process owned, string verifiedImagePath,
        GamePostLaunchSettings? requested, CancellationToken stop)
    {
        if (requested?.Requested != true) return GamePostLaunchStates.NotRequested;
        if (!OperatingSystem.IsWindows() || !requested.BringToForeground ||
            requested.DelayMs is < 0 or > 8000)
            return GamePostLaunchStates.NotConfirmed;

        try
        {
            if (requested.DelayMs > 0)
                await Task.Delay(requested.DelayMs, stop).ConfigureAwait(false);

            IntPtr previous = IntPtr.Zero;
            int stable = 0;
            for (int attempt = 0; attempt < MaxWindowObservations; attempt++)
            {
                stop.ThrowIfCancellationRequested();
                if (!ManualGameSessionService.VerifyOwnedProcess(owned, verifiedImagePath))
                    return GamePostLaunchStates.NotConfirmed;

                IntPtr candidate = SelectUniqueWindow(FindWindows(owned.Id), owned.Id);
                stable = candidate != IntPtr.Zero && candidate == previous ? stable + 1 : 0;
                previous = candidate;
                if (stable >= 2)
                {
                    if (!StillOwned(owned, verifiedImagePath, candidate))
                        return GamePostLaunchStates.NotConfirmed;

                    // Windows alone decides whether this program can become the foreground
                    // owner. Do not bypass its foreground/UIPI policy or click another app.
                    if (GetForegroundWindow() != candidate && !SetForegroundWindow(candidate))
                        return GamePostLaunchStates.ForegroundDenied;
                    await Task.Delay(110, stop).ConfigureAwait(false);
                    if (!StillOwned(owned, verifiedImagePath, candidate) ||
                        GetForegroundWindow() != candidate)
                        return GamePostLaunchStates.ForegroundDenied;

                    bool fullscreenPosted = false;
                    if (requested.RequestAltEnterFullscreen)
                    {
                        // Targeted window messages, not system-wide keyboard injection.
                        // Alt+Enter is a TOGGLE: only an explicitly enabled per-game profile
                        // may post it. The game's renderer may ignore this request.
                        bool keyDownPosted = PostMessage(candidate, WmSysKeyDown, (UIntPtr)VkReturn,
                            new IntPtr(unchecked((int)0x20000001u)));
                        // Always attempt the matching release, including after a failed press.
                        bool keyUpPosted = PostMessage(candidate, WmSysKeyUp, (UIntPtr)VkReturn,
                            new IntPtr(unchecked((int)0xE0000001u)));
                        if (!keyDownPosted || !keyUpPosted)
                            return GamePostLaunchStates.NotConfirmed;
                        fullscreenPosted = true;
                    }

                    if (requested.ClickClientCenter)
                    {
                        // Fullscreen can replace the HWND, or the user can Alt+Tab between
                        // the two actions. Neither condition authorizes a blind click.
                        if (fullscreenPosted) await Task.Delay(150, stop).ConfigureAwait(false);
                        if (!StillOwned(owned, verifiedImagePath, candidate) ||
                            GetForegroundWindow() != candidate ||
                            !GetClientRect(candidate, out WinRect rect))
                            return fullscreenPosted ? GamePostLaunchStates.FullscreenRequested
                                : GamePostLaunchStates.NotConfirmed;
                        (int X, int Y)? center = CenterPoint(rect.Right - rect.Left, rect.Bottom - rect.Top);
                        if (center is null)
                            return fullscreenPosted ? GamePostLaunchStates.FullscreenRequested
                                : GamePostLaunchStates.NotConfirmed;

                        IntPtr point = MouseLParam(center.Value.X, center.Value.Y);
                        if (!PostMessage(candidate, WmMouseMove, UIntPtr.Zero, point))
                            return fullscreenPosted ? GamePostLaunchStates.FullscreenRequested
                                : GamePostLaunchStates.NotConfirmed;
                        bool mouseDownPosted = PostMessage(candidate, WmLeftDown, (UIntPtr)MkLeft, point);
                        // Never intentionally leave the target window holding a posted mouse button.
                        bool mouseUpPosted = PostMessage(candidate, WmLeftUp, UIntPtr.Zero, point);
                        if (!mouseDownPosted || !mouseUpPosted)
                            return fullscreenPosted ? GamePostLaunchStates.FullscreenRequested
                                : GamePostLaunchStates.NotConfirmed;

                        return fullscreenPosted ? GamePostLaunchStates.FullscreenAndClickPosted
                            : GamePostLaunchStates.ClickPosted;
                    }
                    return fullscreenPosted ? GamePostLaunchStates.FullscreenRequested
                        : GamePostLaunchStates.ForegroundConfirmed;
                }

                await Task.Delay(200, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or Win32Exception or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException or ArgumentException) { }

        return GamePostLaunchStates.NotConfirmed;
    }

    private static bool StillOwned(Process process, string verifiedImagePath, IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) ||
            !ManualGameSessionService.VerifyOwnedProcess(process, verifiedImagePath))
            return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid != (uint)process.Id || GetWindow(window, GwOwner) != IntPtr.Zero ||
            !IsWindowVisible(window) || IsIconic(window) || !IsWindowEnabled(window))
            return false;
        return (GetWindowLong(window, GwlExStyle) & WsExToolWindow) == 0 &&
            SelectUniqueWindow(FindWindows(process.Id), process.Id) == window;
    }

    private static IReadOnlyList<Candidate> FindWindows(int expectedPid)
    {
        var list = new List<Candidate>();
        bool overflow = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != (uint)expectedPid) return true;
            if (list.Count >= 16) { overflow = true; return false; }
            list.Add(new Candidate(window, (int)pid, IsWindowVisible(window),
                GetWindow(window, GwOwner) != IntPtr.Zero,
                (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0,
                IsIconic(window), IsWindowEnabled(window)));
            return true;
        }, IntPtr.Zero);
        return overflow ? Array.Empty<Candidate>() : list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr lparam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr value);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out WinRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
