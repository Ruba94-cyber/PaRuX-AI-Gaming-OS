using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Explicit user-selected executable launch, built on the SAME Gaming orchestration.
/// Does not scan/kill/suspend arbitrary processes, mutate library entries, install
/// drivers or claim an unobserved child as a game. This is not a store/DRM launcher.
/// </summary>
public sealed class ManualGameSessionService : IDisposable
{
    private static readonly TimeSpan MinimumMonitoredLifetime = TimeSpan.FromSeconds(5);
    private readonly ManualGameLibrary _library;
    private readonly SteamGameDiscoveryProvider _steam;
    private readonly GogGameDiscoveryProvider _gog;
    private readonly WorkloadOrchestrator _orchestrator;
    private readonly ILogger<ManualGameSessionService>? _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _monitorGate = new();
    private CancellationTokenSource? _monitorStop;
    private bool _disposed;

    public ManualGameSessionService(ManualGameLibrary library, WorkloadOrchestrator orchestrator,
        SteamGameDiscoveryProvider steam, ILogger<ManualGameSessionService>? logger = null,
        GogGameDiscoveryProvider? gog = null)
    {
        _library = library;
        _orchestrator = orchestrator;
        _steam = steam;
        _gog = gog ?? new GogGameDiscoveryProvider();
        _logger = logger;
    }

    internal static bool IsSafeLocalExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string normalized = Path.GetFullPath(path);
            return File.Exists(normalized) &&
                (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<ManualGameLaunchResult> LaunchAsync(string gameId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(gameId))
            return new(false, false, "Choose a game from the local library.");

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ManualGameDto? game = _library.List().FirstOrDefault(
                g => string.Equals(g.Id, gameId, StringComparison.Ordinal));
            if (game == null)
                return new(false, false, "The selected library entry no longer exists.");
            if (!IsSafeLocalExecutable(game.ExecutablePath))
                return new(false, false, "The saved executable is missing, linked or unsafe to launch.");

            // No implicit switch away from another game, remote stream or failed transition.
            if (_orchestrator.CurrentState != "AiReady")
                return new(false, false, "Return to AI before starting another manual game.");

            bool entered = await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false);
            if (!entered)
                return new(false, false, "Gaming resources could not be prepared. No game was launched.");

            Process? process = null;
            bool started = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(game.ExecutablePath);
                var info = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                    UseShellExecute = false
                };
                process = new Process { StartInfo = info };
                if (!process.Start())
                    throw new InvalidOperationException("Windows did not start the requested executable.");
                started = true;

                // Process identity is verified using the actual handle returned by Windows:
                // PID + OS creation time + image path. Never substitute a window-title scan.
                bool verified = VerifyOwnedProcess(process, path);
                long? sessionVersion = _orchestrator.ActiveManualGameSessionVersion;
                if (!verified || sessionVersion == null)
                {
                    process.Dispose(); // releases our handle; it does NOT terminate the game
                    return new(true, false, "Game started. Automatic return is unavailable because its exact process identity could not be verified; return to AI manually.");
                }

                var stop = new CancellationTokenSource();
                CancellationTokenSource? previous;
                lock (_monitorGate)
                {
                    previous = _monitorStop;
                    _monitorStop = stop;
                }
                try { previous?.Cancel(); } catch (ObjectDisposedException) { }
                // This monitor never kills the process. Its cancellation only stops observation.
                _ = ObserveOwnedExitAsync(process, sessionVersion.Value, stop);
                return new(true, true, "Game started. PaRuX monitors this exact process; early bootstrap exits require manual Return to AI.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or InvalidOperationException or OperationCanceledException or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            {
                if (process != null && !started) process.Dispose();
                if (!started)
                {
                    using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    bool recovered;
                    try { recovered = await _orchestrator.ExitGamingSessionAsync(rollback.Token).ConfigureAwait(false); }
                    catch { recovered = false; }
                    return new(false, false, recovered
                        ? "The executable did not start. PaRuX returned to AI."
                        : "The executable did not start and AI recovery needs attention.");
                }
                _logger?.LogWarning("A started manual game could not be automatically monitored.");
                process?.Dispose();
                return new(true, false, "Game started but automatic return is unavailable. Use Return to AI manually.");
            }
        }
        finally { _launchGate.Release(); }
    }

    internal static bool ValidSteamAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.Length <= 15 && appId.All(char.IsAsciiDigit);

    /// <summary>
    /// Handoff to the user's already-installed official Steam client. The child
    /// process belongs to Steam, not PaRuX. Never infer it from windows or exit
    /// the Gaming session when the Steam bootstrapper finishes.
    /// </summary>
    public async Task<ManualGameLaunchResult> LaunchSteamAsync(string appId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ValidSteamAppId(appId))
            return new(false, false, "Invalid Steam app ID.");
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_orchestrator.CurrentState != "AiReady")
                return new(false, false, "Return to AI before starting another game.");

            var installed = await _steam.DiscoverAsync(ct).ConfigureAwait(false);
            if (!installed.Any(g => g.ProviderId == "steam" && g.ProviderGameId == appId))
                return new(false, false, "The selected Steam game is not installed locally.");

            string? steamExe = SteamInstallLocator.FindExecutable();
            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
                return new(false, false, "Steam is not installed in its expected location.");

            if (!await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false))
                return new(false, false, "Gaming resources could not be prepared; Steam was not launched.");

            try
            {
                ct.ThrowIfCancellationRequested();
                var info = new ProcessStartInfo
                {
                    FileName = steamExe,
                    WorkingDirectory = Path.GetDirectoryName(steamExe)!,
                    UseShellExecute = false
                };
                info.ArgumentList.Add("-applaunch");
                info.ArgumentList.Add(appId); // numeric validated, never launch options from manifests
                using var request = new Process { StartInfo = info };
                if (!request.Start())
                    throw new InvalidOperationException("Steam did not accept the launch process request.");
                return new(true, false,
                    "Launch request sent to Steam. PaRuX cannot verify the final game process; Return to AI manually when finished.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or InvalidOperationException or OperationCanceledException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                bool recovered;
                try { recovered = await _orchestrator.ExitGamingSessionAsync(rollback.Token).ConfigureAwait(false); }
                catch { recovered = false; }
                return new(false, false, recovered
                    ? "Steam did not accept the launch. PaRuX returned to AI."
                    : "Steam did not accept the launch and the return-to-AI operation needs attention.");
            }
        }
        finally { _launchGate.Release(); }
    }

    internal static bool ValidGogGameId(string? gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && gameId.Length <= 24 && gameId.All(char.IsAsciiDigit);

    /// <summary>
    /// Handoff to the user's GOG GALAXY client using a freshly rediscovered,
    /// installed numeric game ID and install directory. No game ownership,
    /// process monitoring, account/DRM bypass or automatic AI return is claimed.
    /// </summary>
    public async Task<ManualGameLaunchResult> LaunchGogAsync(string gameId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ValidGogGameId(gameId))
            return new(false, false, "Invalid GOG game ID.");

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_orchestrator.CurrentState != "AiReady")
                return new(false, false, "Return to AI before starting another game.");

            var installed = await _gog.DiscoverAsync(ct).ConfigureAwait(false);
            DiscoveredGameDto? game = installed.FirstOrDefault(g =>
                g.ProviderId == "gog" && g.ProviderGameId == gameId);
            if (game == null)
                return new(false, false, "The selected GOG game is not installed locally.");

            string? galaxyExe = GogGalaxyLocator.FindExecutable();
            if (string.IsNullOrWhiteSpace(galaxyExe) || !File.Exists(galaxyExe))
                return new(false, false, "GOG GALAXY is not installed in a supported local location.");

            if (!await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false))
                return new(false, false, "Gaming resources could not be prepared; GOG was not launched.");

            try
            {
                ct.ThrowIfCancellationRequested();
                var info = new ProcessStartInfo
                {
                    FileName = galaxyExe,
                    WorkingDirectory = Path.GetDirectoryName(galaxyExe)!,
                    UseShellExecute = false
                };
                info.ArgumentList.Add("/command=runGame");
                info.ArgumentList.Add("/gameId=" + gameId);
                info.ArgumentList.Add("/path=" + game.InstallDirectory);
                using var request = new Process { StartInfo = info };
                if (!request.Start())
                    throw new InvalidOperationException("GOG GALAXY did not accept the launch request.");

                return new(true, false,
                    "Launch request sent to GOG GALAXY. Return to AI manually after playing.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or InvalidOperationException or
                OperationCanceledException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                bool recovered;
                try { recovered = await _orchestrator.ExitGamingSessionAsync(rollback.Token).ConfigureAwait(false); }
                catch { recovered = false; }
                return new(false, false, recovered
                    ? "GOG GALAXY did not accept the launch. PaRuX returned to AI."
                    : "GOG GALAXY did not accept the launch and AI recovery needs attention.");
            }
        }
        finally { _launchGate.Release(); }
    }

    internal static bool VerifyOwnedProcess(Process process, string expectedPath)
    {
        try
        {
            return process.Id > 0 && process.StartTime != default &&
                !process.HasExited &&
                string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// A tiny launcher/bootstrap that exits immediately cannot prove that its spawned
    /// game also ended. Such short-lived processes never cause an automatic AI switch.
    /// </summary>
    private async Task ObserveOwnedExitAsync(Process process, long sessionVersion, CancellationTokenSource stop)
    {
        var uptime = Stopwatch.StartNew();
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            if (uptime.Elapsed < MinimumMonitoredLifetime || stop.IsCancellationRequested) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool restored = await _orchestrator.ExitManualGameSessionAfterProcessExitAsync(
                sessionVersion, timeout.Token).ConfigureAwait(false);
            if (!restored)
                _logger?.LogWarning("Manual game exit did not complete Gaming resource reconciliation.");
        }
        catch (OperationCanceledException) { /* user exited, host stopped or bounded recovery */ }
        catch (Exception)
        {
            _logger?.LogWarning("Manual game exit observation ended without confirming AI recovery.");
        }
        finally
        {
            process.Dispose();
            lock (_monitorGate)
            {
                if (ReferenceEquals(_monitorStop, stop)) _monitorStop = null;
            }
            stop.Dispose();
        }
    }

    /// <summary>Detaches from an owned game without affecting the Windows process.</summary>
    public void StopTracking()
    {
        CancellationTokenSource? active;
        lock (_monitorGate)
        {
            active = _monitorStop;
            _monitorStop = null;
        }
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>All explicit server Return-to-AI actions serialize against an in-progress
    /// manual game launch; the user can never release Gaming resources halfway through Start.</summary>
    public async Task<bool> ExitGamingAsync(CancellationToken ct)
    {
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopTracking();
            return await _orchestrator.ExitGamingSessionAsync(ct).ConfigureAwait(false);
        }
        finally { _launchGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTracking();
    }
}
