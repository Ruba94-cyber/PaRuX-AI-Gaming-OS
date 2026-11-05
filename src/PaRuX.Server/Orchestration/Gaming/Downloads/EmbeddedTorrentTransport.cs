using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using MonoTorrent;
using MonoTorrent.Client;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>
/// Integrated, in-process BitTorrent transport for explicitly user-provided single-file
/// v1 torrents and btih magnets. It is NOT a catalogue or a game installer. MonoTorrent
/// is shipped inside the PaRuX Core, not run as a separate process or qBittorrent service.
/// Each active transfer owns an engine. Network access starts only for a queued user job;
/// no permanent daemon, UPnP/NAT port mapping or ongoing seeding is permitted.
///
/// The existing GameDownloadManager remains responsible for SHA-256, publication,
/// pause/resume, Gaming hard-off, history and consent. A torrent's SHA-1 piece hashes
/// verify transfer integrity, not the publisher's authenticity or distribution licence.
/// </summary>
public sealed class EmbeddedTorrentTransport : IGameDownloadTransport, IDisposable
{
    private const int MaxMetadataBytes = (int)GameDownloadSourceValidator.MaxTorrentFileBytes;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly Func<bool> _gamingActive;
    private readonly string _cacheDirectory;

    public EmbeddedTorrentTransport(LocalRuntimeService runtime)
        : this(() => runtime.GamingMode, Path.Combine(PaRuXDataRoot.Path, "cache", "native-torrent")) { }

    internal EmbeddedTorrentTransport(Func<bool> gamingActive, string cacheDirectory)
    {
        _gamingActive = gamingActive ?? throw new ArgumentNullException(nameof(gamingActive));
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    internal static string StagingPath(GameDownloadJob job) =>
        Path.Combine(job.Directory, ".parux-native-torrent-" + job.Id);

    async Task IGameDownloadTransport.FetchAsync(GameDownloadTransferContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        GameDownloadJob job = context.Job;
        if (job.Kind is not (GameDownloadKinds.Magnet or GameDownloadKinds.TorrentFile))
            throw new GameDownloadException("The integrated engine accepts only magnets and local .torrent files.");
        if (PaRuX.Contracts.CorporateMode.IsActive || _gamingActive() || context.GamingActive())
            throw new OperationCanceledException("Gaming or Corporate Lite disabled torrent transfers.");
        GameDownloadManager.EnsureSafeDownloadPaths(job);
        string staging = StagingPath(job);
        EnsureOwnedStaging(job, staging);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_active.TryAdd(job.Id, stop))
            throw new GameDownloadException("This native torrent transfer is already active.");

        // No outbound network connections until this worker has the explicit queued
        // job and its validated, selected local destination.
        int requestedRate = EffectiveSpeedLimit(context);
        var settings = new EngineSettings
        {
            MaximumDownloadSpeed = requestedRate,
            CacheDirectory = _cacheDirectory,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            MaximumOpenFiles = 8,
            MaximumHalfOpenConnections = 4,
            MaximumUploadSpeed = 64 * 1024
        };
        ClientEngine? engine = null;
        TorrentManager? manager = null;
        bool finished = false;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            if (!ManualGameLibrary.IsSafeDirectory(_cacheDirectory))
                throw new GameDownloadException("The torrent metadata cache must be a regular local directory.");

            engine = new ClientEngine(settings);
            Torrent metadata;
            if (job.Kind == GameDownloadKinds.TorrentFile)
            {
                byte[] bytes = GameDownloadSourceValidator.ReadValidatedTorrentBytes(job.Target, job.InfoHash!);
                metadata = DecodeAndAdmit(job, bytes, context.MaxFileBytes);
            }
            else
            {
                GameMagnetLink magnet = GameDownloadSourceValidator.ValidateMagnet(job.Target);
                if (!string.Equals(magnet.InfoHashHex, job.InfoHash, StringComparison.Ordinal))
                    throw new GameDownloadException("The magnet identity changed during the transfer.");
                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                metadataDeadline.CancelAfter(TimeSpan.FromMinutes(2));
                // DownloadMetadataAsync performs peer discovery, not a payload download.
                // Validate the complete metadata BEFORE starting any file transfer.
                byte[] bytes;
                try
                {
                    bytes = await engine.DownloadMetadataAsync(MagnetLink.Parse(magnet.Normalized),
                        metadataDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    throw new GameDownloadException("The magnet metadata could not be obtained in two minutes.");
                }
                metadata = DecodeAndAdmit(job, bytes, context.MaxFileBytes);
            }

            stop.Token.ThrowIfCancellationRequested();
            if (_gamingActive() || context.GamingActive()) throw new OperationCanceledException(stop.Token);
            if (context.FreeBytes(job.Directory) < metadata.Size + GameDownloadManager.FreeSpaceMarginBytes)
                throw new GameDownloadException("Not enough free space for this torrent.");

            // From here on, MonoTorrent may create its ONE validated file in the
            // PaRuX-owned stage. It cannot write directly to the user's library.
            manager = await engine.AddAsync(metadata, staging).ConfigureAwait(false);
            job.ExternalId = job.InfoHash;
            if (manager.Files.Count != 1)
                throw new GameDownloadException("Only single-file torrents can be safely published yet.");
            await manager.StartAsync().ConfigureAwait(false);

            while (!manager.Complete)
            {
                stop.Token.ThrowIfCancellationRequested();
                int newRate = EffectiveSpeedLimit(context);
                if (newRate != requestedRate)
                {
                    // Apply the same user-selected limit to the actual peer traffic,
                    // not only to the final copy into PaRuX's managed part file.
                    await engine.UpdateSettingsAsync(engine.Settings with
                    {
                        MaximumDownloadSpeed = newRate
                    }).ConfigureAwait(false);
                    requestedRate = newRate;
                }
                if (_gamingActive() || context.GamingActive())
                    throw new OperationCanceledException(stop.Token);
                if (manager.State == TorrentState.Error)
                    throw new GameDownloadException("The embedded BitTorrent engine stopped because of a transfer error.");
                long bytes = (long)(Math.Clamp(manager.Progress, 0, 100) * metadata.Size / 100.0);
                context.Report(bytes, metadata.Size);
                await Task.Delay(PollInterval, stop.Token).ConfigureAwait(false);
            }

            // Stop seeding before opening the verified single file.
            await manager.StopAsync(StopDeadline).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (_gamingActive() || context.GamingActive())
                throw new OperationCanceledException(stop.Token);
            await CopyVerifiedFileAsync(context, staging, metadata, stop.Token).ConfigureAwait(false);
            finished = true;
        }
        catch (Exception ex) when (ex is not GameDownloadException and not OperationCanceledException)
        {
            // Third-party engine failures are not allowed to escape into an
            // unobserved fire-and-forget Task.Run in the existing download queue.
            throw new GameDownloadException("The embedded torrent engine could not complete this transfer.");
        }
        finally
        {
            // Even cancellation/exception must stop peer traffic and release handles.
            if (manager is not null)
            {
                try { await manager.StopAsync(StopDeadline).ConfigureAwait(false); }
                catch (Exception) { /* stopping a failed engine is best effort */ }
                if (engine is not null)
                    try { await engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false); }
                    catch (Exception) { /* directory is checked and managed separately */ }
            }
            try { engine?.Dispose(); } catch (Exception) { }
            _active.TryRemove(job.Id, out _);
            if (finished || context.UserCancelled())
                CleanOwnedStaging(job);
            job.ExternalId = null;
        }
    }

    private static int EffectiveSpeedLimit(GameDownloadTransferContext context)
    {
        long requested = Math.Max(0, context.NetworkBytesPerSecond?.Invoke() ?? 0);
        return requested == 0 ? 0 : (int)Math.Min(requested, int.MaxValue);
    }

    /// <summary>A cancellation removes only PaRuX's own stage; never an installed game or user .torrent.</summary>
    Task IGameDownloadTransport.AbandonAsync(GameDownloadJob job, CancellationToken ct)
    {
        if (_active.TryGetValue(job.Id, out var stop))
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        else CleanOwnedStaging(job);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fail closed on untrusted metadata. The existing bencode parser checks the
    /// precise v1 info-hash; MonoTorrent independently checks individual pieces.
    /// Explicitly reject all multi-file payloads until atomic folder publication exists.
    /// </summary>
    internal static Torrent DecodeAndAdmit(GameDownloadJob job, byte[] metadataBytes, long maximumBytes)
    {
        if (metadataBytes.Length == 0 || metadataBytes.Length > MaxMetadataBytes)
            throw new GameDownloadException("Torrent metadata is missing or too large.");
        var parsed = GameDownloadSourceValidator.ParseTorrent(metadataBytes);
        if (!string.Equals(parsed.InfoHash, job.InfoHash, StringComparison.Ordinal))
            throw new GameDownloadException("Torrent metadata does not match the selected content hash.");
        Torrent torrent;
        try { torrent = Torrent.Load(metadataBytes); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        { throw new GameDownloadException("The embedded engine could not parse this torrent."); }

        if (torrent.Files.Count != 1 || torrent.Size <= 0 || torrent.Size > maximumBytes)
            throw new GameDownloadException("Only single-file torrents within the configured size limit are currently supported.");

        string relative = torrent.Files[0].Path;
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 200
            || relative is "." or ".." || relative.Contains('/') || relative.Contains('\\')
            || Path.IsPathRooted(relative) || relative.Any(char.IsControl)
            || !string.Equals(relative, GameDownloadManager.SafeFileName(relative), StringComparison.Ordinal))
            throw new GameDownloadException("The torrent contains an unsafe file name.");
        return torrent;
    }

    private static async Task CopyVerifiedFileAsync(
        GameDownloadTransferContext context, string staging, Torrent metadata, CancellationToken ct)
    {
        GameDownloadJob job = context.Job;
        EnsureOwnedStaging(job, staging);
        string source = Path.Combine(staging, metadata.Files[0].Path);
        var file = new FileInfo(source);
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || file.Length != metadata.Size)
            throw new GameDownloadException("The torrent payload is missing, linked or has an unexpected length.");
        if (context.FreeBytes(job.Directory) < file.Length + GameDownloadManager.FreeSpaceMarginBytes)
            throw new GameDownloadException("Not enough free disk space to publish the completed file.");

        GameDownloadManager.CleanPartial(job);
        GameDownloadManager.EnsureSafeDownloadPaths(job);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1 << 16, useAsync: true))
        await using (var output = new FileStream(job.PartPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 1 << 16, useAsync: true))
        {
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (context.GamingActive()) throw new OperationCanceledException(ct);
                await context.Throttle(read, ct).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                context.Report(done, metadata.Size);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static void EnsureOwnedStaging(GameDownloadJob job, string staging)
    {
        GameDownloadManager.EnsureSafeDownloadPaths(job);
        string expected = Path.GetFullPath(StagingPath(job));
        if (!string.Equals(Path.GetFullPath(staging), expected, StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The torrent staging path is not owned by PaRuX.");
        Directory.CreateDirectory(staging);
        if (!ManualGameLibrary.IsSafeDirectory(staging))
            throw new GameDownloadException("Torrent staging must not be a linked directory.");
    }

    private static void CleanOwnedStaging(GameDownloadJob job)
    {
        string path = StagingPath(job);
        try
        {
            if (!Directory.Exists(path) || !ManualGameLibrary.IsSafeDirectory(path)) return;
            // Refuse to delete an unexpected nested directory or any linked file.
            if (Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Any(p =>
                    new FileInfo(p).LinkTarget is not null ||
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                return;
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* leave partials for manual recovery rather than following an untrusted path */ }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource token in _active.Values)
            try { token.Cancel(); } catch (ObjectDisposedException) { }
    }
}
