using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

public class GameDownloadException : Exception
{
    public GameDownloadException(string message) : base(message) { }
}

/// <summary>
/// Native, modular download manager for files the user is entitled to fetch (installers, patches, mods, media):
/// HTTP(S) with resume, optional SHA-256 check, bandwidth limit, queue with priority and concurrency, free-space and
/// size checks, history, and automatic pause while Gaming. A bundled, opt-in in-process BitTorrent transport handles
/// validated user-provided v1 torrents (multifile output packed as ZIP); qBittorrent remains optional. No built-in catalogue.
/// It never starts a downloaded file; opening or installing it is a separate, explicit step.
/// </summary>
public sealed class GameDownloadManager : IGamingResumable, IDisposable
{
    public const int MaxHistory = 200;
    public const long DefaultMaxFileBytes = 200L * 1024 * 1024 * 1024;
    internal const long FreeSpaceMarginBytes = 256L * 1024 * 1024;
    private const string PartExtension = ".parux-part";

    private readonly object _gate = new();
    private readonly List<GameDownloadJob> _entries = new();
    private readonly HttpClient _http;
    private readonly Dictionary<string, IGameDownloadTransport> _transports = new(StringComparer.Ordinal);
    private readonly ExternalTorrentClientTransport? _torrent;
    private readonly EmbeddedTorrentTransport? _nativeTorrent;
    private readonly PortableGameInstallService? _portableInstaller;
    private readonly Func<bool> _gamingActive;
    private readonly Func<string, long> _freeBytes;
    private readonly string? _historyPath;
    private readonly NativeTorrentRecoveryStore? _nativeRecovery;
    private readonly TimeSpan[] _retryDelays;
    private readonly BandwidthLimiter _limiter = new();
    private int _maxConcurrent = 2;
    private long _maxFileBytes = DefaultMaxFileBytes;
    private bool _disposed;

    public GameDownloadManager(LocalRuntimeService runtime, ExternalTorrentClientTransport torrent)
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
               { Timeout = Timeout.InfiniteTimeSpan },
            () => runtime.GamingMode, DefaultFreeBytes,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-downloads.json"),
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30) }, torrent) { }

    internal GameDownloadManager(LocalRuntimeService runtime, ExternalTorrentClientTransport torrent,
        EmbeddedTorrentTransport nativeTorrent)
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
               { Timeout = Timeout.InfiniteTimeSpan },
            () => runtime.GamingMode, DefaultFreeBytes,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-downloads.json"),
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30) },
            torrent, nativeTorrent) { }

    internal GameDownloadManager(LocalRuntimeService runtime, ExternalTorrentClientTransport torrent,
        EmbeddedTorrentTransport nativeTorrent, PortableGameInstallService portableInstaller)
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
               { Timeout = Timeout.InfiniteTimeSpan },
            () => runtime.GamingMode, DefaultFreeBytes,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-downloads.json"),
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30) },
            torrent, nativeTorrent, portableInstaller) { }

    internal GameDownloadManager(HttpClient http, Func<bool> gamingActive, Func<string, long> freeBytes,
        string? historyPath, TimeSpan[] retryDelays, ExternalTorrentClientTransport? torrent = null,
        EmbeddedTorrentTransport? nativeTorrent = null, PortableGameInstallService? portableInstaller = null)
    {
        _http = http;
        _gamingActive = gamingActive;
        _freeBytes = freeBytes;
        _historyPath = historyPath;
        _retryDelays = retryDelays;
        _transports[GameDownloadKinds.Http] = new HttpGameDownloadTransport(http);
        _torrent = torrent;
        _nativeTorrent = nativeTorrent;
        _portableInstaller = portableInstaller;
        _nativeRecovery = historyPath is not null && nativeTorrent is not null
            ? new NativeTorrentRecoveryStore(historyPath + ".torrent.dpapi") : null;
        IGameDownloadTransport? initialTorrent = nativeTorrent ?? (IGameDownloadTransport?)torrent;
        if (initialTorrent is not null)
        {
            _transports[GameDownloadKinds.Magnet] = initialTorrent;
            _transports[GameDownloadKinds.TorrentFile] = initialTorrent;
        }
        LoadHistory();
        LoadNativeTorrentRecovery();
    }

    internal int MaxConcurrent { get => _maxConcurrent; set => _maxConcurrent = Math.Clamp(value, 1, 8); }
    internal long MaxFileBytes { get => _maxFileBytes; set => _maxFileBytes = Math.Max(1, value); }

    public void SetBandwidthLimit(long bytesPerSecond) => _limiter.SetLimit(Math.Max(0, bytesPerSecond));

    public IReadOnlyList<GameDownloadDto> List()
    {
        lock (_gate) return _entries.Select(ToDto).OrderByDescending(d => d.CreatedUtc).ToArray();
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Read-only post-download handoff to the existing local package inspector.
    /// Only the exact published file in this process may be inspected, not an arbitrary
    /// path supplied by the HTTP caller. The original completion digest is rechecked:
    /// integrity does not imply publisher trust, licensing or installer safety.
    /// </summary>
    public async Task<GameInstallerInspectionResult> InspectCompletedAsync(
        string id, GameInstallerInspectionService inspector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (_gamingActive()) throw new InvalidOperationException("Leave Gaming before inspecting a download.");
        string path;
        string checksum;
        lock (_gate)
        {
            GameDownloadJob? entry = Find(id);
            if (entry is null || entry.State != GameDownloadStates.Completed ||
                string.IsNullOrWhiteSpace(entry.PublishedPath) ||
                string.IsNullOrWhiteSpace(entry.VerifiedSha256))
                throw new InvalidOperationException("This download is not available for inspection in the current session.");
            path = entry.PublishedPath;
            checksum = entry.VerifiedSha256;
        }
        GameInstallerInspectionResult inspected = await inspector.InspectAsync(
            new GameInstallerInspectionRequest(path), ct).ConfigureAwait(false);
        if (!string.Equals(inspected.Sha256, checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The completed download changed since its original hash was recorded.");
        return inspected with
        {
            Advisory = inspected.Advisory +
                " The inspected bytes still match this session's original download; this is not a publisher authenticity check."
        };
    }

    /// <summary>
    /// Explicit, copy-only handoff from a completed job to the existing staging transaction.
    /// A checksum the user supplied BEFORE downloading is mandatory: the self-computed
    /// transfer digest is never accepted as a stand-in for an independent expected value.
    /// Neither this path nor StageAsync executes, unpacks or installs anything.
    /// </summary>
    public Task<GamePackageStageResult> StageCompletedAsync(
        string id, GamePackageStageService staging, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ct.ThrowIfCancellationRequested();
        if (_gamingActive()) throw new InvalidOperationException("Leave Gaming before staging a download.");
        string path;
        string publisherSha;
        lock (_gate)
        {
            GameDownloadJob? entry = Find(id);
            if (entry is null || entry.State != GameDownloadStates.Completed ||
                !entry.IntegrityChecked || string.IsNullOrWhiteSpace(entry.PublishedPath) ||
                entry.ExpectedSha256 is null || entry.VerifiedSha256 is null ||
                !string.Equals(entry.ExpectedSha256, entry.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Only downloads completed in this session with an independently supplied SHA-256 may be staged.");
            path = entry.PublishedPath;
            publisherSha = entry.ExpectedSha256;
        }
        return staging.StageAsync(new GamePackageStageRequest(path, publisherSha), ct);
    }

    public GameDownloadDto Enqueue(GameDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string kind = GameDownloadKinds.Normalize(request.Kind)
            ?? throw new GameDownloadException("The download kind must be http, magnet or torrentfile.");
        // Kind-specific validation of the source. Nothing below logs or persists the raw source.
        Uri? url = null;
        string target, source, defaultName;
        string? infoHash = null;
        switch (kind)
        {
            case GameDownloadKinds.Magnet:
                GameMagnetLink magnet = GameDownloadSourceValidator.ValidateMagnet(request.Url);
                target = magnet.Normalized;
                infoHash = magnet.InfoHashHex;
                source = "magnet/" + magnet.InfoHashHex;
                defaultName = magnet.DisplayName ?? magnet.InfoHashHex;
                break;
            case GameDownloadKinds.TorrentFile:
                GameTorrentFile torrentFile = GameDownloadSourceValidator.ValidateTorrentFile(request.Url);
                target = torrentFile.FullPath;
                infoHash = torrentFile.InfoHashHex;
                source = "torrent-file/" + (SafeFileName(Path.GetFileName(torrentFile.FullPath)) ?? "file");
                defaultName = torrentFile.Name;
                break;
            default:
                url = ValidateUrl(request.Url);
                target = url.AbsoluteUri;
                source = url.Host + url.AbsolutePath;
                defaultName = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
                break;
        }
        if (kind != GameDownloadKinds.Http && _nativeTorrent is null && (_torrent is null || !_torrent.IsEnabled))
            throw new GameDownloadException("Torrent transfers require the integrated engine or an explicitly configured client.");
        // qBittorrent is an explicit override; otherwise torrent bytes come directly
        // from the bundled MonoTorrent engine. Never switch a queued job mid-transfer.
        IGameDownloadTransport? selected = kind == GameDownloadKinds.Http ? null :
            _torrent?.IsEnabled == true ? _torrent : _nativeTorrent;
        string directory = ValidateDirectory(request.DestinationDirectory);
        string name = SafeFileName(request.FileName) ?? SafeFileName(defaultName) ?? "download";
        string? sha = NormalizeSha(request.ExpectedSha256);
        if (request.ExpectedSha256 is { Length: > 0 } && sha is null) throw new GameDownloadException("The SHA-256 must be 64 hexadecimal characters.");
        if (request.AutoInstallPortable)
        {
            if (_portableInstaller is null || sha is null ||
                !string.Equals(Path.GetExtension(name), ".zip", StringComparison.OrdinalIgnoreCase) ||
                !PortableGameInstallService.ValidPortableEntryPoint(request.PortableExecutableRelativePath) ||
                string.IsNullOrWhiteSpace(request.PortableGameTitle) ||
                request.PortableGameTitle.Length > 200 || request.PortableGameTitle.Any(char.IsControl))
                throw new GameDownloadException(
                    "Auto-deploy requires a ZIP, a separate independent SHA-256 and a valid user-chosen portable game executable/title.");
        }
        else if (request.PortableExecutableRelativePath is not null || request.PortableGameTitle is not null)
            throw new GameDownloadException("Portable game parameters require explicit AutoInstallPortable opt-in.");
        if (_gamingActive()) throw new GameDownloadException("Downloads are paused while a game is running.");

        lock (_gate)
        {
            // Gaming can start after the first admission check but before
            // acquiring the queue lock. Refuse rather than accept a stale job.
            if (_gamingActive()) throw new GameDownloadException("Downloads are paused while a game is running.");
            if (_entries.Count(e => !GameDownloadStates.IsTerminal(e.State)) >= 200) throw new GameDownloadException("Too many active downloads.");
            // Two active jobs for the same target file would corrupt each other's part file.
            if (_entries.Any(e => !GameDownloadStates.IsTerminal(e.State) &&
                                  string.Equals(e.PartPath, Path.Combine(directory, name + PartExtension), StringComparison.OrdinalIgnoreCase)))
                throw new GameDownloadException("A download to that file is already in progress.");
            var entry = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), Kind = kind, InfoHash = infoHash, ChosenTransport = selected, Url = url, Target = target, Source = source, FileName = name, Directory = directory,
                ExpectedSha256 = sha, Priority = Math.Clamp(request.Priority, -10, 10),
                AutoInstallPortable = request.AutoInstallPortable,
                PortableExecutableRelativePath = request.AutoInstallPortable ? request.PortableExecutableRelativePath : null,
                PortableGameTitle = request.AutoInstallPortable ? request.PortableGameTitle?.Trim() : null,
                PostInstallState = request.AutoInstallPortable ? GamePostInstallStates.Pending : GamePostInstallStates.None,
            };
            _entries.Add(entry);
            SaveNativeTorrentRecovery();
            Pump();
            return ToDto(entry);
        }
    }

    public bool Pause(string id)
    {
        lock (_gate)
        {
            GameDownloadJob? e = Find(id);
            if (e is null || GameDownloadStates.IsTerminal(e.State)) return false;
            e.UserPause = true;
            e.Reason = "Paused by you.";
            if (e.State == GameDownloadStates.Running) e.Run?.Cancel(); else e.State = GameDownloadStates.Paused;
            SaveNativeTorrentRecovery();
            return true;
        }
    }

    public bool Resume(string id)
    {
        lock (_gate)
        {
            GameDownloadJob? e = Find(id);
            if (e is null || e.State != GameDownloadStates.Paused) return false;
            if (_gamingActive()) { e.Reason = "A game is running; the download will continue afterwards."; return false; }
            e.UserPause = false;
            e.Reason = null;
            e.State = GameDownloadStates.Queued;
            SaveNativeTorrentRecovery();
            Pump();
            return true;
        }
    }

    /// <summary>Cancels a job and deletes its partial file, or removes a finished job from the history.</summary>
    public bool CancelOrRemove(string id)
    {
        lock (_gate)
        {
            GameDownloadJob? e = Find(id);
            if (e is null) return false;
            if (GameDownloadStates.IsTerminal(e.State))
            {
                // Publication may have completed just before the post-install
                // worker starts. Cancel the worker EVEN when the install is
                // still Pending so "Remove history" cannot cause an invisible
                // later extract/registration from an already-removed job.
                if (e.AutoInstallPortable && e.PostInstallState is
                    (GamePostInstallStates.Pending or GamePostInstallStates.Installing))
                {
                    e.PostInstallState = GamePostInstallStates.Interrupted;
                    e.PostInstallMessage = "Portable installation cancelled by the owner; the archive was retained.";
                    e.InstallerRun?.Cancel();
                    e.Run?.Cancel();
                }
                _entries.Remove(e);
                SaveHistory();
                return true;
            }
            bool running = e.State == GameDownloadStates.Running;
            e.State = GameDownloadStates.Cancelled;
            e.Reason = "Cancelled by you.";
            e.FinishedUtc = DateTime.UtcNow;
            e.Run?.Cancel();
            if (!running) CleanPartial(e);
            IGameDownloadTransport? owner = e.ChosenTransport;
            if (owner is null) _transports.TryGetValue(e.Kind, out owner);
            if (owner is not null && (owner is EmbeddedTorrentTransport || e.ExternalId is not null))
                _ = AbandonExternalAsync(owner, e);
            SaveHistory();
            Pump();
            return true;
        }
    }

    /// <summary>Called when Gaming ends: jobs paused only because of the game are queued again.</summary>
    public Task<int> ResumeGamingPausedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_gamingActive()) return Task.FromResult(0);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            // A new Gaming entry can race the check before the queue lock.
            // Never report that downloads were resumed after hard-off.
            if (_gamingActive()) return Task.FromResult(0);
            var paused = _entries.Where(e => e.State == GameDownloadStates.Paused && !e.UserPause).ToList();
            foreach (GameDownloadJob e in paused) { e.State = GameDownloadStates.Queued; e.Reason = null; }
            Pump();
            return Task.FromResult(paused.Count);
        }
    }

    /// <summary>Test seam and shutdown helper: waits until no job is running.</summary>
    internal async Task WhenIdleAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
                if (!_entries.Any(e => e.State == GameDownloadStates.Running || (e.State == GameDownloadStates.Queued && !_gamingActive()))) return;
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ scheduling

    private void Pump()
    {
        if (_disposed || _gamingActive()) return;
        int running = _entries.Count(e => e.State == GameDownloadStates.Running);
        foreach (GameDownloadJob next in _entries.Where(e => e.State == GameDownloadStates.Queued)
                     .OrderByDescending(e => e.Priority).ThenBy(e => e.CreatedUtc).ToList())
        {
            if (running >= _maxConcurrent) break;
            next.State = GameDownloadStates.Running;
            next.Reason = null;
            var run = new CancellationTokenSource();
            next.Run = run;
            // Cancel/Pause may finish and dispose next.Run before Task.Run
            // executes. Capture a stable token now, not from the mutable slot.
            CancellationToken token = run.Token;
            running++;
            _ = Task.Run(() => RunAsync(next, token));
        }
    }

    private async Task RunAsync(GameDownloadJob e, CancellationToken ct)
    {
        string? failure = null;
        string? externalPause = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_gamingActive())
                throw new OperationCanceledException("Gaming started before the download worker ran.");
            for (int attempt = 0; ; attempt++)
            {
                try { await TransferAsync(e, ct).ConfigureAwait(false); break; }
                catch (HttpRequestException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
                { await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false); }
                catch (IOException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested && !DiskIsFull(e))
                { await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false); }
            }
            // Deployment happens only after the existing SHA-256 verified
            // non-overwriting publication. An install failure never deletes the
            // downloaded archive or relabels valid bytes as a failed download.
            if (e.AutoInstallPortable)
                await RunAutoPortableInstallAsync(e, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* pause, cancel or Gaming: handled below */ }
        catch (GameDownloadPausedException ex) { externalPause = ex.Message; }
        catch (GameDownloadException ex) { failure = ex.Message; }
        catch (HttpRequestException) when (e.Kind != GameDownloadKinds.Http) { failure = "The torrent client could not be reached."; }
        catch (HttpRequestException ex)
        {
            failure = ex.StatusCode is { } status ? $"The server answered HTTP {(int)status}." : "The server could not be reached.";
        }
        catch (IOException) { failure = "The file could not be written."; }
        catch (UnauthorizedAccessException) { failure = "The destination folder is not writable."; }

        lock (_gate)
        {
            e.Run?.Dispose();
            e.Run = null;
            if (e.State == GameDownloadStates.Cancelled) { CleanPartial(e); }
            else if (failure is not null)
            {
                e.State = GameDownloadStates.Failed;
                e.Reason = failure;
                e.FinishedUtc = DateTime.UtcNow;
                if (e.AutoInstallPortable)
                {
                    e.PostInstallState = GamePostInstallStates.Failed;
                    e.PostInstallMessage = "Download failed before portable installation.";
                }
                if (failure.Contains("SHA-256", StringComparison.Ordinal)) CleanPartial(e); // a corrupt file is not kept for resume
            }
            else if (e.State == GameDownloadStates.Completed) { /* set by TransferAsync */ }
            else if (externalPause is not null)
            {
                // Paused inside the user's own client: stays paused until the user resumes it here.
                e.UserPause = true;
                e.State = GameDownloadStates.Paused;
                e.Reason = externalPause;
            }
            else
            {
                // Interrupted: by the user, or because a game started. Either way the partial file is kept.
                e.State = GameDownloadStates.Paused;
                e.Reason = e.UserPause ? "Paused by you." : "Paused while a game is running.";
            }
            SaveHistory();
            Pump();
        }
    }

    /// <summary>
    /// A single owner-confirmed retry for a completed, independently verified ZIP.
    /// No source URL, magnet trackers or executable command is accepted here.
    /// Interrupted jobs recovered from disk history are deliberately ineligible:
    /// the original source and exact published path are not persisted.
    /// </summary>
    public async Task<GameDownloadDto> RetryPortableInstallAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GameDownloadJob entry;
        lock (_gate)
        {
            entry = Find(id) ?? throw new InvalidOperationException("The download is not in this session.");
            if (_disposed || _gamingActive() || !entry.AutoInstallPortable ||
                entry.State != GameDownloadStates.Completed ||
                entry.PostInstallState is not (GamePostInstallStates.Interrupted or GamePostInstallStates.Failed) ||
                entry.InstallerRun is not null || entry.ExpectedSha256 is null ||
                string.IsNullOrEmpty(entry.PublishedPath))
                throw new InvalidOperationException("This portable install cannot be retried now.");
            // Re-authorize exactly one owner-requested retry. Removing history
            // after this check revokes authorization before the install starts.
            entry.PostInstallState = GamePostInstallStates.Pending;
            entry.PostInstallMessage = "Owner-approved retry awaiting verification.";
        }
        await RunAutoPortableInstallAsync(entry, ct).ConfigureAwait(false);
        lock (_gate) return ToDto(entry);
    }

    private async Task RunAutoPortableInstallAsync(GameDownloadJob e, CancellationToken ct)
    {
        if (_portableInstaller is null) return;
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (!_entries.Contains(e) || ct.IsCancellationRequested ||
                e.PostInstallState == GamePostInstallStates.Interrupted ||
                e.InstallerRun is not null ||
                e.PostInstallState is GamePostInstallStates.Installing or GamePostInstallStates.Installed)
                throw new OperationCanceledException("Portable installation is no longer authorized for this job.");
            if (e.State != GameDownloadStates.Completed ||
                !e.IntegrityChecked || e.ExpectedSha256 is null ||
                !string.Equals(e.ExpectedSha256, e.VerifiedSha256, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(e.PublishedPath))
            {
                e.PostInstallState = GamePostInstallStates.Failed;
                e.PostInstallMessage = "Portable deployment requires the independently verified completed package.";
                return;
            }
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            e.InstallerRun = linked;
            e.PostInstallState = GamePostInstallStates.Installing;
            e.PostInstallMessage = "Safely extracting and registering the portable game; nothing is running.";
        }
        try
        {
            ManualGameDto installed = await _portableInstaller.InstallAsync(
                e.PublishedPath!, e.ExpectedSha256!, e.PortableExecutableRelativePath!,
                e.PortableGameTitle!, linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                e.PostInstallState = GamePostInstallStates.Installed;
                e.PostInstallMessage = "Portable game registered in your library: " + installed.Name + ". Not executed.";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                e.PostInstallState = GamePostInstallStates.Interrupted;
                e.PostInstallMessage = "Portable install stopped; the verified ZIP is still available.";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            lock (_gate)
            {
                e.PostInstallState = GamePostInstallStates.Failed;
                e.PostInstallMessage = "Portable install refused: " + ex.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(e.InstallerRun, linked)) e.InstallerRun = null;
                SaveHistory();
            }
            linked.Dispose();
        }
    }

    private static async Task AbandonExternalAsync(IGameDownloadTransport transport, GameDownloadJob job)
    {
        try { await transport.AbandonAsync(job, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* best effort: the user's own client may be gone */ }
    }

    private static bool DiskIsFull(GameDownloadJob e)
    {
        try { return new DriveInfo(Path.GetPathRoot(e.Directory)!).AvailableFreeSpace < 1024 * 1024; }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ transfer

    private async Task TransferAsync(GameDownloadJob e, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_gamingActive())
            throw new OperationCanceledException("Gaming disabled new download disk and network work.");
        EnsureSafeDownloadPaths(e);
        IGameDownloadTransport transport = e.ChosenTransport ??
            (_transports.TryGetValue(e.Kind, out IGameDownloadTransport? t) ? t
                : throw new GameDownloadException("This kind of download is not available."));
        var context = new GameDownloadTransferContext
        {
            Job = e, GamingActive = _gamingActive, FreeBytes = _freeBytes, MaxFileBytes = _maxFileBytes,
            Throttle = _limiter.WaitAsync,
            NetworkBytesPerSecond = () => _limiter.CurrentLimit,
            Report = (done, total) => { lock (_gate) { e.BytesDone = done; if (total is not null) e.TotalBytes = total; } },
            UserCancelled = () => { lock (_gate) return e.State == GameDownloadStates.Cancelled || _disposed; },
        };
        // The transport only produces the finished bytes at PartPath. Hashing, the
        // expected-checksum comparison and publication below never trust it.
        await transport.FetchAsync(context, ct).ConfigureAwait(false);

        string sha = await HashFileAsync(e.PartPath, _gamingActive, ct).ConfigureAwait(false);
        if (e.ExpectedSha256 is not null && !string.Equals(sha, e.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The file's SHA-256 does not match the expected value; it was deleted.");

        // Publication is the cancellation boundary: serialize the final rename
        // with Pause/Cancel so a request accepted before the rename cannot later
        // be overwritten by a spurious Completed state. The move is same-directory
        // and non-overwriting. A completed move wins; a later Cancel removes only
        // its history entry, never deletes a user-facing completed file.
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || e.State == GameDownloadStates.Cancelled || e.UserPause || _gamingActive())
                throw new OperationCanceledException("Download publication was interrupted.");
            EnsureSafeDownloadPaths(e);
            string finalPath = UniquePath(e.FinalPath);
            ct.ThrowIfCancellationRequested();
            File.Move(e.PartPath, finalPath);
            ResumeValidator.Delete(e.PartPath);
            DeleteResumeIdentity(e);
            e.IntegrityChecked = e.ExpectedSha256 is not null;
            e.VerifiedSha256 = sha;
            e.PublishedPath = finalPath;
            e.State = GameDownloadStates.Completed;
            e.Reason = e.ExpectedSha256 is null ? "Completed. No expected checksum was given, so the file is not verified." : "Completed and verified.";
            e.FinishedUtc = DateTime.UtcNow;
        }
    }

    // ------------------------------------------------------------------ validation and helpers

    /// <summary>
    /// Recheck the previously selected destination immediately before opening or publishing files.
    /// The directory may have been exchanged for a junction while this job was queued, and the
    /// app-owned resume sidecars may have been exchanged for links by a different local process.
    /// This is path hygiene, not a race-free filesystem identity or malware trust guarantee.
    /// </summary>
    internal static void EnsureSafeDownloadPaths(GameDownloadJob e)
    {
        if (!string.Equals(ValidateDirectory(e.Directory), e.Directory, StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The download destination changed since it was selected.");

        foreach (string path in new[] { e.PartPath, ResumeIdentityPath(e), e.PartPath + ".validator" })
        {
            try
            {
                // LinkTarget also catches dangling symlinks, for which File.Exists may be false.
                if (new FileInfo(path).LinkTarget is not null ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new GameDownloadException("A download temporary file or sidecar is a linked file.");
            }
            catch (FileNotFoundException) { /* A new, regular file may be created. */ }
            catch (DirectoryNotFoundException)
            {
                throw new GameDownloadException("The download destination no longer exists.");
            }
        }
    }

    internal static Uri ValidateUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || text.Any(char.IsControl) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri))
            throw new GameDownloadException("Enter a valid web address.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new GameDownloadException("Addresses with embedded credentials are not accepted.");
        bool secure = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!secure && !loopbackHttp) throw new GameDownloadException("Only https addresses are accepted.");
        if (string.IsNullOrEmpty(uri.Host)) throw new GameDownloadException("Enter a valid web address.");
        return uri;
    }

    internal static string ValidateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || directory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new GameDownloadException("Choose an absolute local destination folder.");
        string full = Path.GetFullPath(directory);
        if (!ManualGameLibrary.IsSafeDirectory(full))
            throw new GameDownloadException("The destination folder does not exist or is a linked folder.");
        try
        {
            var volume = new DriveInfo(Path.GetPathRoot(full)!);
            if (volume.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                throw new GameDownloadException("Choose a local volume rather than a mapped network folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new GameDownloadException("The download destination volume is unavailable.");
        }
        return full;
    }

    internal static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string cleaned = new string(Path.GetFileName(name.Trim()).Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).ToArray()).Trim(' ', '.');
        if (cleaned.Length == 0 || cleaned.Length > 200) return null;
        string stem = cleaned.Contains('.') ? cleaned[..cleaned.IndexOf('.')] : cleaned;
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return null;
        if (cleaned.EndsWith(PartExtension, StringComparison.OrdinalIgnoreCase)) return null;
        return cleaned;
    }

    private static string? NormalizeSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? null
        : sha.Trim().Length == 64 && sha.Trim().All(Uri.IsHexDigit) ? sha.Trim().ToLowerInvariant() : null;

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new GameDownloadException("Too many files with that name.");
    }

    /// <summary>
    /// Streaming integrity verification with a Gaming hard-off check between bounded reads.
    /// A canceled checksum leaves the partial file intact for explicit resume.
    /// </summary>
    internal static async Task<string> HashFileAsync(string path, Func<bool> gamingActive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gamingActive);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (gamingActive()) throw new OperationCanceledException("Gaming interrupted integrity verification.");
            int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        if (gamingActive()) throw new OperationCanceledException("Gaming interrupted integrity verification.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Do not persist credential-bearing URLs: only a versioned SHA-256 of the
    // entire URL (including its query) and the independently expected checksum.
    // A changed signed link starts afresh instead of silently stitching bytes.
    internal static string ResumeIdentityPath(GameDownloadJob e) => e.PartPath + ".identity";

    private static string ExpectedResumeIdentity(GameDownloadJob e)
    {
        string material = e.Url.AbsoluteUri + "\n" + (e.ExpectedSha256 ?? "");
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    internal static bool ResumeIdentityMatches(GameDownloadJob e)
    {
        try
        {
            string path = ResumeIdentityPath(e);
            if (!File.Exists(path) || new FileInfo(path).Length is < 67 or > 70) return false;
            return string.Equals(File.ReadAllText(path).Trim(), ExpectedResumeIdentity(e), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static void WriteResumeIdentity(GameDownloadJob e)
    {
        string path = ResumeIdentityPath(e);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, ExpectedResumeIdentity(e));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void DeleteResumeIdentity(GameDownloadJob e)
    {
        try { File.Delete(ResumeIdentityPath(e)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void CleanPartial(GameDownloadJob e)
    {
        try { File.Delete(e.PartPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ResumeValidator.Delete(e.PartPath);
        DeleteResumeIdentity(e);
    }

    private static long DefaultFreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    private GameDownloadJob? Find(string id) => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static GameDownloadDto ToDto(GameDownloadJob e) => new(
        e.Id, e.FileName, e.Source, e.State, e.Reason, e.BytesDone, e.TotalBytes,
        e.Priority, e.IntegrityChecked, e.VerifiedSha256, e.CreatedUtc, e.FinishedUtc)
    {
        PostInstallState = e.PostInstallState,
        PostInstallMessage = e.PostInstallMessage
    };

    // ------------------------------------------------------------------ history

    /// <summary>
    /// Restore only owner-queued native torrents into a visible PAUSED state,
    /// never auto-connect to a peer or repeat post-download auto-install.
    /// User must choose Resume. Source paths/magnets stayed DPAPI encrypted.
    /// </summary>
    private void LoadNativeTorrentRecovery()
    {
        if (_nativeRecovery is null || _nativeTorrent is null) return;
        try
        {
            foreach (NativeTorrentRecoveryStore.Entry job in _nativeRecovery.Load())
            {
                if (_entries.Count >= 400) break;
                if (job.Id is not { Length: 32 } || !job.Id.All(Uri.IsHexDigit) ||
                    job.InfoHash is not { Length: 40 } || !job.InfoHash.All(Uri.IsHexDigit) ||
                    job.Kind is not (GameDownloadKinds.Magnet or GameDownloadKinds.TorrentFile) ||
                    job.FileName is null || !string.Equals(SafeFileName(job.FileName), job.FileName, StringComparison.Ordinal) ||
                    job.Directory is null || !Path.IsPathFullyQualified(job.Directory) ||
                    !Directory.Exists(job.Directory) || !ManualGameLibrary.IsSafeDirectory(job.Directory) ||
                    job.Target is null || job.Target.Length > 4096 ||
                    _entries.Any(x => x.Id == job.Id))
                    continue;
                if (job.ExpectedSha256 is not null &&
                    !string.Equals(NormalizeSha(job.ExpectedSha256), job.ExpectedSha256, StringComparison.Ordinal))
                    continue;
                if (job.Kind == GameDownloadKinds.Magnet)
                {
                    GameMagnetLink magnet;
                    try { magnet = GameDownloadSourceValidator.ValidateMagnet(job.Target); }
                    catch (GameDownloadException) { continue; }
                    if (!string.Equals(magnet.InfoHashHex, job.InfoHash, StringComparison.Ordinal))
                        continue;
                }
                else if (!Path.IsPathFullyQualified(job.Target) ||
                         !string.Equals(Path.GetExtension(job.Target), ".torrent", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Require an explicit human resume after a restart. Never re-run
                // auto-install from a recovered job without a new confirmation.
                _entries.Add(new GameDownloadJob
                {
                    Id = job.Id, Kind = job.Kind, Target = job.Target,
                    Source = job.Kind == GameDownloadKinds.Magnet ? "magnet/" + job.InfoHash
                        : "torrent-file/" + (SafeFileName(Path.GetFileName(job.Target)) ?? "file"),
                    FileName = job.FileName, Directory = job.Directory,
                    InfoHash = job.InfoHash, ChosenTransport = _nativeTorrent,
                    ExpectedSha256 = job.ExpectedSha256, Priority = Math.Clamp(job.Priority, -10, 10),
                    CreatedUtc = job.CreatedUtc, BytesDone = Math.Max(0, job.BytesDone),
                    TotalBytes = job.TotalBytes,
                    State = GameDownloadStates.Paused, UserPause = true,
                    Reason = "Recovered after PaRuX restart. Review source and click Resume; portable auto-install is off.",
                    AutoInstallPortable = false, PostInstallState = GamePostInstallStates.None
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // A corrupt or undecryptable sidecar cannot start network activity.
            // Never log or echo the original magnet's private tracker parameters.
        }
    }

    /// <summary>
    /// Called only with manager lock or during construction. State changes are
    /// persisted before handing work to peer engines. Failed DPAPI persistence
    /// cannot block ordinary transfer; it only limits crash recovery.
    /// </summary>
    private void SaveNativeTorrentRecovery()
    {
        if (_nativeRecovery is null) return;
        try
        {
            var items = _entries.Where(x => !GameDownloadStates.IsTerminal(x.State)
                && x.ChosenTransport == _nativeTorrent
                && x.Kind is GameDownloadKinds.Magnet or GameDownloadKinds.TorrentFile)
                .Take(200)
                .Select(x => new NativeTorrentRecoveryStore.Entry(
                    x.Id, x.Kind, x.Target, x.Source, x.FileName, x.Directory,
                    x.InfoHash ?? string.Empty, x.ExpectedSha256,
                    x.Priority, x.CreatedUtc, x.BytesDone, x.TotalBytes))
                .ToArray();
            _nativeRecovery.Save(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or
            System.Security.Cryptography.CryptographicException)
        { /* Explicit resume is still possible until shutdown; do not leak secrets or claim persistence. */ }
    }

    private sealed record HistoryItem(string Id, string FileName, string Source, string State, string? Reason, long BytesDone, long? TotalBytes,
        int Priority, bool IntegrityChecked, string? VerifiedSha256, DateTime CreatedUtc, DateTime? FinishedUtc,
        string PostInstallState = GamePostInstallStates.None, string? PostInstallMessage = null);

    private void SaveHistory()
    {
        if (_historyPath is null) return;
        try
        {
            var items = _entries.Where(x => GameDownloadStates.IsTerminal(x.State)).OrderByDescending(x => x.FinishedUtc).Take(MaxHistory)
                .Select(x => new HistoryItem(x.Id, x.FileName, x.Source, x.State, x.Reason, x.BytesDone, x.TotalBytes,
                    x.Priority, x.IntegrityChecked, x.VerifiedSha256, x.CreatedUtc, x.FinishedUtc,
                    x.PostInstallState == GamePostInstallStates.Installing ? GamePostInstallStates.Interrupted : x.PostInstallState,
                    x.PostInstallMessage)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            string temp = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, _historyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        SaveNativeTorrentRecovery();
    }

    private void LoadHistory()
    {
        // Only finished jobs are remembered: an interrupted job needs its address again (it may carry a secret
        // query string that is deliberately never stored). Its partial file resumes when the same job is re-added.
        try
        {
            if (_historyPath is null || !File.Exists(_historyPath) || new FileInfo(_historyPath).Length > 4 * 1024 * 1024) return;
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(_historyPath)) ?? new();
            foreach (HistoryItem h in items.Take(MaxHistory))
            {
                if (!GameDownloadStates.IsTerminal(h.State)) continue;
                _entries.Add(new GameDownloadJob
                {
                    Id = h.Id, Kind = GameDownloadKinds.Http, Target = string.Empty, Source = h.Source, FileName = h.FileName, Directory = string.Empty, Priority = h.Priority,
                    CreatedUtc = h.CreatedUtc, State = h.State, Reason = h.Reason, BytesDone = h.BytesDone, TotalBytes = h.TotalBytes,
                    IntegrityChecked = h.IntegrityChecked, VerifiedSha256 = h.VerifiedSha256, FinishedUtc = h.FinishedUtc,
                    PostInstallState = h.PostInstallState == GamePostInstallStates.Installing
                        ? GamePostInstallStates.Interrupted : h.PostInstallState,
                    PostInstallMessage = h.PostInstallMessage,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (GameDownloadJob e in _entries)
            {
                e.Run?.Cancel();
                e.InstallerRun?.Cancel();
            }
        }
        _http.Dispose();
        _torrent?.Dispose();
        _nativeTorrent?.Dispose();
    }

    // ------------------------------------------------------------------ bandwidth

    private sealed class BandwidthLimiter
    {
        private readonly object _gate = new();
        private long _bytesPerSecond;
        private DateTime _next = DateTime.UtcNow;

        public void SetLimit(long bytesPerSecond) { lock (_gate) { _bytesPerSecond = bytesPerSecond; _next = DateTime.UtcNow; } }
    public long CurrentLimit { get { lock (_gate) return _bytesPerSecond; } }

        public async Task WaitAsync(int bytes, CancellationToken ct)
        {
            TimeSpan delay;
            lock (_gate)
            {
                if (_bytesPerSecond <= 0) return;
                DateTime now = DateTime.UtcNow;
                if (_next < now) _next = now;
                delay = _next - now;
                _next += TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);
            }
            if (delay > TimeSpan.FromMilliseconds(1)) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
