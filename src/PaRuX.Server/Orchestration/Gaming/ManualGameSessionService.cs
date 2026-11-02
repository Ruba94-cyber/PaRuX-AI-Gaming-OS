using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

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
    private readonly EpicGameDiscoveryProvider _epic;
    private readonly WorkloadOrchestrator _orchestrator;
    private readonly ILogger<ManualGameSessionService>? _logger;
    private readonly GameTitleStore? _titles;
    private readonly OwnedGameWindowPlacementService? _windowPlacement;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _monitorGate = new();
    private CancellationTokenSource? _monitorStop;
    private bool _disposed;

    public ManualGameSessionService(ManualGameLibrary library, WorkloadOrchestrator orchestrator,
        SteamGameDiscoveryProvider steam, ILogger<ManualGameSessionService>? logger = null,
        GogGameDiscoveryProvider? gog = null, EpicGameDiscoveryProvider? epic = null,
        GameTitleStore? titles = null, OwnedGameWindowPlacementService? windowPlacement = null)
    {
        _titles = titles;
        _windowPlacement = windowPlacement;
        _library = library;
        _orchestrator = orchestrator;
        _steam = steam;
        _gog = gog ?? new GogGameDiscoveryProvider();
        _epic = epic ?? new EpicGameDiscoveryProvider();
        _logger = logger;
    }

    // Share one verified Windows-local .exe policy across manual launches,
    // controller helpers and optional Gaming launchers. The old manual-game
    // check rejected linked .exe leaves but missed junctions in parent folders.
    internal static bool IsSafeLocalExecutable(string? path) =>
        CustomExecutableInputProvider.IsSafeHelperPath(path);

    public async Task<ManualGameLaunchResult> LaunchAsync(string gameId, CancellationToken ct,
        string? targetDisplayId = null)
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

            string? launchProblem = ValidateLaunchOptions(game);
            if (launchProblem != null)
                return new(false, false, launchProblem);

            // Applications (tools, front-ends, any other program) never change AI or Gaming state.
            if (string.Equals(game.Kind, ManualEntryKinds.Application, StringComparison.OrdinalIgnoreCase))
                return LaunchApplication(game);

            // A disconnected/reidentified TV must not start the game on the wrong
            // monitor. Revalidate the destination immediately before Gaming entry.
            if (targetDisplayId is { Length: > 0 } &&
                (_windowPlacement is null || !_windowPlacement.HasConnectedTarget(targetDisplayId)))
                return new(false, false,
                    "The selected game TV is unavailable. Check the connected displays in Settings; no game was launched.");

            bool entered = await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false);
            if (!entered)
                return new(false, false, "Gaming resources could not be prepared. No game was launched.");

            Process? process = null;
            bool started = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                // File may have moved while gaming resources were being released.
                // A failed recheck takes the normal no-process rollback path.
                string? changedFile = ValidateLaunchOptions(game);
                if (changedFile is not null) throw new InvalidOperationException(changedFile);
                string path = Path.GetFullPath(game.ExecutablePath);
                var info = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = string.IsNullOrEmpty(game.WorkingDirectory) ? Path.GetDirectoryName(path)! : game.WorkingDirectory,
                    Arguments = game.Arguments ?? string.Empty,
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
                    return new ManualGameLaunchResult(true, false, "Game started. Automatic return is unavailable because its exact process identity could not be verified; return to AI manually.") { StartState = GameStartStates.ProcessStarted };
                }

                _orchestrator.RegisterOwnedGameProcess(new WorkloadOrchestrator.OwnedGameProcess(
                    process.Id, process.StartTime.ToUniversalTime(), path, sessionVersion.Value));
                var stop = new CancellationTokenSource();
                CancellationTokenSource? previous;
                lock (_monitorGate)
                {
                    previous = _monitorStop;
                    _monitorStop = stop;
                }
                try { previous?.Cancel(); } catch (ObjectDisposedException) { }
                // This monitor never kills the process. Its cancellation only stops observation.
                _ = ObserveOwnedExitAsync(process, sessionVersion.Value, stop,
                    SessionTitleKey(game), DateTime.UtcNow);

                string placement = string.Empty;
                if (targetDisplayId is { Length: > 0 } && _windowPlacement is not null)
                {
                    // Bounded observation of only this exact process's one stable
                    // window. Never adopt child/launcher processes or foreground
                    // someone else's application. No game process is terminated.
                    placement = await _windowPlacement.TryPlaceAsync(
                        process, path, targetDisplayId, stop.Token).ConfigureAwait(false);
                }
                return new ManualGameLaunchResult(true, true,
                    "Game started. PaRuX monitors this exact process; early bootstrap exits require manual Return to AI.")
                {
                    WindowPlacement = placement
                };
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
                return new ManualGameLaunchResult(true, false, "Game started but automatic return is unavailable. Use Return to AI manually.") { StartState = GameStartStates.ProcessStarted };
            }
        }
        finally { _launchGate.Release(); }
    }

    /// <summary>Use the same fallback installation identity as the unified title catalog.
    /// Ordinary titles use their normalized names, but one-letter/symbol-only names
    /// would otherwise lose play time when launched through an emulator.</summary>
    internal static string SessionTitleKey(ManualGameDto game)
    {
        string source = string.IsNullOrEmpty(game.EmulatorId)
            ? GameInstallationSources.Manual : GameInstallationSources.Emulator;
        return GameTitleCatalog.KeyFor(game.Name,
            GameTitleCatalog.InstallationId(source, game.Id));
    }

    internal static bool ValidSteamAppId(string? appId) =>
        GameStoreLaunchIdPolicy.Steam(appId);

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

    /// <summary>
    /// Explicit owner-confirmed Epic Games Launcher protocol handoff for a freshly
    /// rediscovered installed title. Epic documents install-directory URL encoding.
    /// This opens Windows' user-registered URI handler, not a game .exe; ownership,
    /// activation success and final game process are NOT observable by PaRuX.
    /// </summary>
    public async Task<ManualGameLaunchResult> LaunchEpicAsync(string appId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EpicGameLaunchProtocol.IsValidGameId(appId))
            return new(false, false, "Invalid Epic app ID.");

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_orchestrator.CurrentState != "AiReady")
                return new(false, false, "Return to AI before starting another game.");

            var installed = await _epic.DiscoverAsync(ct).ConfigureAwait(false);
            DiscoveredGameDto? game = installed.FirstOrDefault(g =>
                g.ProviderId == "epic" && string.Equals(g.ProviderGameId, appId, StringComparison.Ordinal));
            if (game == null)
                return new(false, false, "The selected Epic game is not installed locally.");

            if (!EpicGameLaunchProtocol.TryBuildInstalledGameUri(game.InstallDirectory, out string? launchUri))
                return new(false, false, "The game's installation directory is no longer available or safe.");

            if (!EpicGameLaunchProtocol.IsWindowsProtocolRegistered())
                return new(false, false, "The Epic Games Launcher protocol is not registered in Windows.");

            if (!await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false))
                return new(false, false, "Gaming resources could not be prepared; Epic was not contacted.");

            try
            {
                ct.ThrowIfCancellationRequested();
                // Only the URI constructed by PaRuX from a fresh installed-game
                // manifest reaches the OS. The owner must approve this action in
                // desktop/TV UI. No shell command, executable or manifest flags.
                var info = new ProcessStartInfo
                {
                    FileName = launchUri!,
                    UseShellExecute = true
                };
                using Process? dispatched = Process.Start(info);
                // A URI activation may return null if an existing launcher accepts
                // it; absence of an owned process is not proof of failure/success.
                return new(true, false,
                    "Epic protocol activation requested. Windows controls the registered handler; the final game was not verified. Return to AI manually.");
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
                    ? "Windows did not dispatch the Epic protocol request. PaRuX returned to AI."
                    : "Windows did not dispatch the Epic request and AI recovery needs attention.");
            }
        }
        finally { _launchGate.Release(); }
    }

    internal static bool ValidGogGameId(string? gameId) =>
        GameStoreLaunchIdPolicy.Gog(gameId);

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

    /// <summary>Validate the typed game-file path recorded by new emulator
    /// imports, without executing, copying or parsing a game image.</summary>
    internal static bool IsSafeEmulatorGameFile(string? emulatorId, string? gameFilePath)
    {
        var definition = Emulators.EmulatorRegistry.Find(emulatorId);
        if (definition is null || string.IsNullOrWhiteSpace(gameFilePath) ||
            gameFilePath.Length > 1024 || gameFilePath.Any(char.IsControl))
            return false;
        var allowed = new HashSet<string>(definition.GameExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (string filename in definition.GameFileNames)
            allowed.Add(Path.GetExtension(filename));
        return GamingLocalFileSafetyPolicy.IsSafeRegularFile(gameFilePath, allowed) &&
               Emulators.EmulatorRegistry.IsGameFile(definition, gameFilePath);
    }

    /// <summary>Re-checks saved file and options at launch time.</summary>
    internal static string? ValidateLaunchOptions(ManualGameDto game)
    {
        if (!ManualGameLibrary.ValidArguments(game.Arguments ?? string.Empty))
            return "The saved arguments are not valid.";
        if (!string.IsNullOrWhiteSpace(game.GameFilePath) &&
            !IsSafeEmulatorGameFile(game.EmulatorId, game.GameFilePath))
            return "The imported emulator game file is missing, linked or unsupported. Re-import the game before launching.";
        if (!string.IsNullOrEmpty(game.WorkingDirectory) &&
            (!Path.IsPathFullyQualified(game.WorkingDirectory) || !ManualGameLibrary.IsSafeDirectory(game.WorkingDirectory)))
            return "The saved working directory is missing or linked.";
        return null;
    }

    private ManualGameLaunchResult LaunchApplication(ManualGameDto game)
    {
        try
        {
            string path = Path.GetFullPath(game.ExecutablePath);
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = string.IsNullOrEmpty(game.WorkingDirectory) ? Path.GetDirectoryName(path)! : game.WorkingDirectory,
                Arguments = game.Arguments ?? string.Empty,
                UseShellExecute = false
            });
            // The handle is released without touching the process: PaRuX does not own or monitor applications.
            return started is null
                ? new(false, false, "Windows did not start the application.")
                : new ManualGameLaunchResult(true, false, "Application started. PaRuX does not monitor it or change AI state.")
                    { StartState = GameStartStates.ProcessStarted };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(false, false, "The application could not be started.");
        }
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
    private async Task ObserveOwnedExitAsync(Process process, long sessionVersion, CancellationTokenSource stop,
        string titleKey, DateTime startedUtc)
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
            // Play time counts the time PaRuX actually observed the owned process, whether it exited or the
            // user returned to AI first. Very short runs (bootstrap stubs) are not play time.
            if (uptime.Elapsed >= MinimumMonitoredLifetime)
            {
                try { _titles?.AddSession(titleKey, startedUtc, uptime.Elapsed); }
                catch (OverflowException) { }
            }
            // The observer owns only the process instance it launched. Never
            // clear a later session's registered process from an old callback.
            _orchestrator.ClearOwnedGameProcess(sessionVersion);
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
