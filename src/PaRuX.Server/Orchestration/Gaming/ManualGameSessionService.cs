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
    private readonly WorkloadOrchestrator _orchestrator;
    private readonly ILogger<ManualGameSessionService>? _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _monitorGate = new();
    private CancellationTokenSource? _monitorStop;
    private bool _disposed;

    public ManualGameSessionService(ManualGameLibrary library, WorkloadOrchestrator orchestrator,
        ILogger<ManualGameSessionService>? logger = null)
    {
        _library = library;
        _orchestrator = orchestrator;
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
                lock (_monitorGate)
                {
                    _monitorStop?.Cancel();
                    _monitorStop = stop;
                }
                // This monitor never kills the process. Its cancellation only stops observation.
                _ = ObserveOwnedExitAsync(process, sessionVersion.Value, stop);
                return new(true, true, "Game started. PaRuX will return to AI when this owned process exits normally.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or InvalidOperationException or OperationCanceledException)
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
        lock (_monitorGate)
        {
            CancellationTokenSource? active = _monitorStop;
            _monitorStop = null;
            try { active?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTracking();
    }
}
