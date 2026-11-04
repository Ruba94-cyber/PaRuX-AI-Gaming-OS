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
    private const int ErrorElevationRequired = 740;

    private static readonly TimeSpan MinimumMonitoredLifetime = TimeSpan.FromSeconds(5);
    private readonly ManualGameLibrary _library;
    private readonly SteamGameDiscoveryProvider _steam;
    private readonly GogGameDiscoveryProvider _gog;
    private readonly EpicGameDiscoveryProvider _epic;
    private readonly WorkloadOrchestrator _orchestrator;
    private readonly ILogger<ManualGameSessionService>? _logger;
    private readonly GameTitleStore? _titles;
    private readonly OwnedGameWindowPlacementService? _windowPlacement;
    private readonly ExtraStoreCatalog _extra;
    private readonly GameLaunchHookService? _hooks;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _monitorGate = new();
    private CancellationTokenSource? _monitorStop;
    private CancellationTokenSource? _launcherTrackStop;
    private bool _disposed;

    public ManualGameSessionService(ManualGameLibrary library, WorkloadOrchestrator orchestrator,
        SteamGameDiscoveryProvider steam, ILogger<ManualGameSessionService>? logger = null,
        GogGameDiscoveryProvider? gog = null, EpicGameDiscoveryProvider? epic = null,
        GameTitleStore? titles = null, OwnedGameWindowPlacementService? windowPlacement = null,
        ExtraStoreCatalog? extraStores = null, GameLaunchHookService? hooks = null)
    {
        _hooks = hooks;
        _extra = extraStores ?? new ExtraStoreCatalog();
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

            await RunHookAsync(GameHookPhase.Pre, SessionTitleKey(game), ct).ConfigureAwait(false);
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
                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("Windows did not start the requested executable.");
                }
                catch (System.ComponentModel.Win32Exception elevation) when (elevation.NativeErrorCode == ErrorElevationRequired)
                {
                    // The executable's manifest demands administrator rights (typical for repacked and anti-cheat
                    // titles). Windows' own UAC prompt is the consent step: it names the program and the user
                    // can decline (error 1223 then rolls back to AI through the normal failure path below).
                    process.Dispose();
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = path,
                            WorkingDirectory = info.WorkingDirectory,
                            Arguments = info.Arguments,
                            UseShellExecute = true,
                            Verb = "runas"
                        }
                    };
                    // The UAC prompt waits for the user; bound it so AI recovery is never held hostage by an unanswered prompt.
                    Process elevated = process;
                    bool elevatedStarted;
                    Task<bool> startTask = Task.Run(() => elevated.Start());
                    try { elevatedStarted = await startTask.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        // Windows keeps the UAC prompt open. If the user approves it later, the game starts into an AI session:
                        // set the AI aside again so the state matches what is really running.
                        _ = startTask.ContinueWith(async t =>
                        {
                            if (t.IsCompletedSuccessfully && t.Result)
                                try { await _orchestrator.EnterGamingSessionAsync(null, CancellationToken.None).ConfigureAwait(false); } catch { }
                        }, TaskScheduler.Default);
                        throw new InvalidOperationException("Windows is still waiting for administrator approval of this game.");
                    }
                    if (!elevatedStarted)
                        throw new InvalidOperationException("Windows did not start the requested executable.");
                }
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

                // Capture OS start time while the owned handle is alive. A PID
                // alone is insufficient for exit/child-process identity checks.
                DateTime verifiedStartUtc = process.StartTime.ToUniversalTime();
                _orchestrator.RegisterOwnedGameProcess(new WorkloadOrchestrator.OwnedGameProcess(
                    process.Id, verifiedStartUtc, path, sessionVersion.Value));
                var stop = new CancellationTokenSource();
                // The exit observer can complete synchronously for an
                // instantly terminating game and dispose this CTS before the
                // window-placement await below. Capture the immutable token
                // now: accessing stop.Token after Dispose throws, but a
                // captured CancellationToken remains safe to inspect.
                CancellationToken observationToken = stop.Token;
                CancellationTokenSource? previous;
                lock (_monitorGate)
                {
                    previous = _monitorStop;
                    _monitorStop = stop;
                }
                try { previous?.Cancel(); } catch (ObjectDisposedException) { }
                // This monitor never kills the process. Its cancellation only stops observation.
                _ = ObserveOwnedExitAsync(process, sessionVersion.Value, verifiedStartUtc, stop,
                    SessionTitleKey(game), DateTime.UtcNow);

                string placement = string.Empty;
                if (targetDisplayId is { Length: > 0 } && _windowPlacement is not null)
                {
                    // Bounded observation of only this exact process's one stable
                    // window. Never adopt child/launcher processes or foreground
                    // someone else's application. No game process is terminated.
                    placement = await _windowPlacement.TryPlaceAsync(
                        process, path, targetDisplayId, observationToken).ConfigureAwait(false);
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
                    _logger?.LogWarning("Manual game launch failed before the process started: {Type}: {Reason}", ex.GetType().Name, ex.Message);
                    using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    bool recovered;
                    try { recovered = await _orchestrator.ExitGamingSessionAsync(rollback.Token).ConfigureAwait(false); }
                    catch { recovered = false; }
                    return new(false, false, recovered
                        ? "The executable did not start (" + ex.Message + "). PaRuX returned to AI."
                        : "The executable did not start (" + ex.Message + ") and AI recovery needs attention.");
                }
                _logger?.LogWarning("A started manual game could not be automatically monitored.");
                process?.Dispose();
                return new ManualGameLaunchResult(true, false, "Game started but automatic return is unavailable. Use Return to AI manually.") { StartState = GameStartStates.ProcessStarted };
            }
        }
        finally { _launchGate.Release(); }
    }

    /// <summary>
    /// After a store handoff the launcher usually exits (or was already running), so there is no handle to wait on.
    /// Observe the game's own verified process tree (see <see cref="LauncherPlayTimeTracker"/>) and credit play time;
    /// observation only, never an action against a process, and cancelled by Return to AI / shutdown.
    /// </summary>
    private static string HookKey(DiscoveredGameDto game) => GameTitleCatalog.KeyFor(game.Name,
        GameTitleCatalog.InstallationId(game.ProviderId.ToLowerInvariant(), game.ProviderGameId));

    /// <summary>Off-by-default per-game helper (see <see cref="GameLaunchHookStore"/>). A hook outcome never blocks or fails a launch.</summary>
    private async Task RunHookAsync(GameHookPhase phase, string titleKey, CancellationToken ct)
    {
        if (_hooks is null || titleKey.Length == 0) return;
        try
        {
            GameHookRunResult result = await _hooks.RunAsync(phase, titleKey, ct).ConfigureAwait(false);
            if (result.Ran || result.Reason is "failed" or "unsafe-path")
                _logger?.LogInformation("Launch hook {Phase}: {Reason}", phase, result.Reason);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void BeginLauncherPlayTimeTracking(DiscoveredGameDto game)
    {
        if (_titles is null || string.IsNullOrWhiteSpace(game.InstallDirectory)) return;
        string key = GameTitleCatalog.KeyFor(game.Name,
            GameTitleCatalog.InstallationId(game.ProviderId.ToLowerInvariant(), game.ProviderGameId));
        var stop = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_monitorGate) { previous = _launcherTrackStop; _launcherTrackStop = stop; }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        DateTime requested = DateTime.UtcNow;
        string install = game.InstallDirectory;
        _ = Task.Run(async () =>
        {
            try
            {
                await LauncherPlayTimeTracker.TrackAndCreditAsync(_titles, key, install, requested,
                    new LauncherPlayTimeTracker.WindowsProbe(), () => DateTime.UtcNow,
                    (d, t) => Task.Delay(d, t), LauncherPlayTimeTracker.Options.Default, stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogInformation("Launcher play-time observation ended without a result: {Type}", ex.GetType().Name);
            }
            finally
            {
                lock (_monitorGate) { if (ReferenceEquals(_launcherTrackStop, stop)) _launcherTrackStop = null; }
                stop.Dispose();
            }
        });
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

    /// <summary>
    /// Read-only official-store install checks can overlap another Gaming
    /// entry. Cancel the manifest/registry read when PaRuX leaves AiReady,
    /// and never authorize a handoff from a stale snapshot. We don't infer
    /// ownership of any Steam/Epic/GOG child process here.
    /// </summary>
    internal static async Task<IReadOnlyList<DiscoveredGameDto>?> GuardedStoreLaunchDiscoveryAsync(
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredGameDto>>> discover,
        Func<bool> aiReady, CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(aiReady);
        caller.ThrowIfCancellationRequested();
        if (!aiReady()) return null;
        using var hardOff = new GamingHardOffWatch(() => !aiReady(), caller);
        try
        {
            IReadOnlyList<DiscoveredGameDto> items = await discover(hardOff.Token).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            return hardOff.Triggered || !aiReady() ? null : items;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested &&
            (hardOff.Triggered || !aiReady()))
        {
            return null;
        }
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

            var installed = await GuardedStoreLaunchDiscoveryAsync(
                _steam.DiscoverAsync, () => _orchestrator.CurrentState == "AiReady", ct)
                .ConfigureAwait(false);
            if (installed is null)
                return new(false, false, "Gaming began while Steam games were being checked. No launch was requested.");
            if (!installed.Any(g => g.ProviderId == "steam" && g.ProviderGameId == appId))
                return new(false, false, "The selected Steam game is not installed locally.");

            string? steamExe = SteamInstallLocator.FindExecutable();
            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
                return new(false, false, "Steam is not installed in its expected location.");

            await RunHookAsync(GameHookPhase.Pre, HookKey(installed.First(g => g.ProviderId == "steam" && g.ProviderGameId == appId)), ct).ConfigureAwait(false);
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
                BeginLauncherPlayTimeTracking(installed.First(g => g.ProviderId == "steam" && g.ProviderGameId == appId));
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

            var installed = await GuardedStoreLaunchDiscoveryAsync(
                _epic.DiscoverAsync, () => _orchestrator.CurrentState == "AiReady", ct)
                .ConfigureAwait(false);
            if (installed is null)
                return new(false, false, "Gaming began while Epic games were being checked. No launch was requested.");
            DiscoveredGameDto? game = installed.FirstOrDefault(g =>
                g.ProviderId == "epic" && string.Equals(g.ProviderGameId, appId, StringComparison.Ordinal));
            if (game == null)
                return new(false, false, "The selected Epic game is not installed locally.");

            if (!EpicGameLaunchProtocol.TryBuildInstalledGameUri(game.InstallDirectory, out string? launchUri))
                return new(false, false, "The game's installation directory is no longer available or safe.");

            if (!EpicGameLaunchProtocol.IsWindowsProtocolRegistered())
                return new(false, false, "The Epic Games Launcher protocol is not registered in Windows.");

            await RunHookAsync(GameHookPhase.Pre, HookKey(game), ct).ConfigureAwait(false);
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
                BeginLauncherPlayTimeTracking(game);
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

            var installed = await GuardedStoreLaunchDiscoveryAsync(
                _gog.DiscoverAsync, () => _orchestrator.CurrentState == "AiReady", ct)
                .ConfigureAwait(false);
            if (installed is null)
                return new(false, false, "Gaming began while GOG games were being checked. No launch was requested.");
            DiscoveredGameDto? game = installed.FirstOrDefault(g =>
                g.ProviderId == "gog" && g.ProviderGameId == gameId);
            if (game == null)
                return new(false, false, "The selected GOG game is not installed locally.");

            string? galaxyExe = GogGalaxyLocator.FindExecutable();
            if (string.IsNullOrWhiteSpace(galaxyExe) || !File.Exists(galaxyExe))
                return new(false, false, "GOG GALAXY is not installed in a supported local location.");

            await RunHookAsync(GameHookPhase.Pre, HookKey(game), ct).ConfigureAwait(false);
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

                BeginLauncherPlayTimeTracking(game);
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

    /// <summary>
    /// Handoff to EA app, Battle.net, Ubisoft Connect or the Xbox app through the client's own registered URI protocol
    /// for a freshly rediscovered installed game. No executable, command line, credential or account access; the
    /// final game process is observed only by <see cref="LauncherPlayTimeTracker"/> (play time), never owned.
    /// </summary>
    public async Task<ManualGameLaunchResult> LaunchExtraStoreAsync(string providerId, string gameId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IExtraStoreProvider? provider = _extra.Find(providerId);
        if (provider is null || !GameStoreLaunchIdPolicy.SupportsOfficialLaunch(provider.Id, gameId))
            return new(false, false, "This store or game ID cannot be launched through its official protocol.");

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_orchestrator.CurrentState != "AiReady")
                return new(false, false, "Return to AI before starting another game.");
            var installed = await GuardedStoreLaunchDiscoveryAsync(
                provider.DiscoverAsync, () => _orchestrator.CurrentState == "AiReady", ct).ConfigureAwait(false);
            if (installed is null)
                return new(false, false, "Gaming began while games were being checked. No launch was requested.");
            DiscoveredGameDto? game = installed.FirstOrDefault(g => string.Equals(g.ProviderGameId, gameId, StringComparison.Ordinal));
            if (game is null)
                return new(false, false, "The selected game is not installed locally.");
            if (!provider.TryBuildLaunchUri(game, out string uri))
                return new(false, false, "The game cannot be launched through its official protocol.");
            await RunHookAsync(GameHookPhase.Pre, HookKey(game), ct).ConfigureAwait(false);
            if (!await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false))
                return new(false, false, "Gaming resources could not be prepared; the launcher was not contacted.");
            try
            {
                ct.ThrowIfCancellationRequested();
                using Process? dispatched = Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                BeginLauncherPlayTimeTracking(game);
                return new(true, false,
                    "Protocol launch requested through the store client. The final game was not verified; Return to AI manually.");
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
                    ? "Windows did not dispatch the store protocol request. PaRuX returned to AI."
                    : "Windows did not dispatch the store request and AI recovery needs attention.");
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
            string workingDirectory = string.IsNullOrEmpty(game.WorkingDirectory) ? Path.GetDirectoryName(path)! : game.WorkingDirectory;
            Process? started;
            try
            {
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = path, WorkingDirectory = workingDirectory, Arguments = game.Arguments ?? string.Empty, UseShellExecute = false
                });
            }
            catch (System.ComponentModel.Win32Exception elevation) when (elevation.NativeErrorCode == ErrorElevationRequired)
            {
                // Same rule as for games: Windows' UAC prompt is the consent step for a program that demands administrator rights.
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = path, WorkingDirectory = workingDirectory, Arguments = game.Arguments ?? string.Empty,
                    UseShellExecute = true, Verb = "runas"
                });
            }
            using Process? owned = started;
            // The handle is released without touching the process: PaRuX does not own or monitor applications.
            return owned is null
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    /// <summary>Executable path of a process. MainModule needs VM-read rights that Windows refuses for an elevated game; the
    /// limited-query right below is allowed across integrity levels, so elevated games can be verified and monitored too.</summary>
    internal static string? ImagePathOf(Process process)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var name = new System.Text.StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    internal static bool VerifyOwnedProcess(Process process, string expectedPath)
    {
        try
        {
            return process.Id > 0 && process.StartTime != default &&
                !process.HasExited &&
                string.Equals(Path.GetFullPath(ImagePathOf(process) ?? string.Empty),
                    Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Even a verified game executable can be a bootstrapper that launches a
    /// child, stays up for several seconds, and then exits. Never release Gaming
    /// resources merely because that parent exited while a direct child may
    /// still be playing. This one-shot Windows check does not adopt, poll, move,
    /// suspend, or terminate any child. Uncertainty requires manual Return.
    /// </summary>
    private async Task ObserveOwnedExitAsync(Process process, long sessionVersion,
        DateTime verifiedStartUtc, CancellationTokenSource stop, string titleKey, DateTime startedUtc)
    {
        var uptime = Stopwatch.StartNew();
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return;
            if (uptime.Elapsed < MinimumMonitoredLifetime)
            {
                _orchestrator.ReportManualReturnRequired(sessionVersion, "bootstrap-exited-early");
                return;
            }

            OwnedGameChildExitGuard.Result children = OwnedGameChildExitGuard.InspectAtParentExit(
                process.Id, verifiedStartUtc, DateTime.UtcNow, stop.Token);
            if (children != OwnedGameChildExitGuard.Result.NoLiveDirectChild)
            {
                _orchestrator.ReportManualReturnRequired(sessionVersion,
                    children == OwnedGameChildExitGuard.Result.LiveDirectChild
                        ? "child-still-active" : "child-check-inconclusive");
                _logger?.LogInformation(
                    "Automatic Return to AI suppressed: child-process check is {Result}. Return to AI manually after closing the game.",
                    children);
                return;
            }
            if (stop.IsCancellationRequested) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool restored = await _orchestrator.ExitManualGameSessionAfterProcessExitAsync(
                sessionVersion, timeout.Token).ConfigureAwait(false);
            if (!restored)
                _logger?.LogWarning("Manual game exit did not complete Gaming resource reconciliation.");
            else
                await RunHookAsync(GameHookPhase.Post, titleKey, CancellationToken.None).ConfigureAwait(false);
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
            if (uptime.Elapsed >= MinimumMonitoredLifetime && titleKey.Length > 0)
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

    /// <summary>
    /// Quick Resume brought the AI back while an owned game was paused. When that game is resumed, Gaming is entered again and
    /// the exact process is registered and observed like a fresh launch, so closing it later returns the AI automatically.
    /// Returns false (nothing registered) when the process is gone or is no longer the one PaRuX paused.
    /// </summary>
    internal async Task<bool> ResumeOwnedSessionAsync(int processId, DateTime startedUtc, CancellationToken ct)
    {
        if (!await _orchestrator.EnterManualGameSessionAsync(ct).ConfigureAwait(false)) return false;
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; }
        try
        {
            string? path = ImagePathOf(process);
            if (path is null || process.HasExited || !QuickResumePolicy.SameProcess(startedUtc, process.StartTime.ToUniversalTime())) { process.Dispose(); return false; }
            long? version = _orchestrator.ActiveManualGameSessionVersion;
            if (version is null) { process.Dispose(); return false; }
            DateTime verifiedStartUtc = process.StartTime.ToUniversalTime();
            _orchestrator.RegisterOwnedGameProcess(new WorkloadOrchestrator.OwnedGameProcess(process.Id, verifiedStartUtc, path, version.Value));
            var stop = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (_monitorGate) { previous = _monitorStop; _monitorStop = stop; }
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            _ = ObserveOwnedExitAsync(process, version.Value, verifiedStartUtc, stop, string.Empty, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            process.Dispose();
            return false;
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
        CancellationTokenSource? launcher;
        lock (_monitorGate) { launcher = _launcherTrackStop; }
        try { launcher?.Cancel(); } catch (ObjectDisposedException) { }
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
