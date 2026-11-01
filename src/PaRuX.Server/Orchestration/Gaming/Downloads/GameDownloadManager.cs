using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

public sealed class GameDownloadException : Exception
{
    public GameDownloadException(string message) : base(message) { }
}

/// <summary>
/// Native, modular download manager for files the user is entitled to fetch (installers, patches, mods, media):
/// HTTP(S) with resume, optional SHA-256 check, bandwidth limit, queue with priority and concurrency, free-space and
/// size checks, history, and automatic pause while Gaming. There is no built-in catalogue and no torrent engine.
/// It never starts a downloaded file; opening or installing it is a separate, explicit step.
/// </summary>
public sealed class GameDownloadManager : IGamingResumable, IDisposable
{
    public const int MaxHistory = 200;
    public const long DefaultMaxFileBytes = 200L * 1024 * 1024 * 1024;
    private const long FreeSpaceMarginBytes = 256L * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const string PartExtension = ".parux-part";

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required Uri Url { get; init; }
        public required string FileName { get; init; }
        public required string Directory { get; init; }
        public string? ExpectedSha256 { get; init; }
        public int Priority { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public string State = GameDownloadStates.Queued;
        public string? Reason;
        public long BytesDone;
        public long? TotalBytes;
        public bool IntegrityChecked;
        public string? VerifiedSha256;
        public DateTime? FinishedUtc;
        public CancellationTokenSource? Run;
        public bool UserPause;
        public string PartPath => Path.Combine(Directory, FileName + PartExtension);
        public string FinalPath => Path.Combine(Directory, FileName);
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly HttpClient _http;
    private readonly Func<bool> _gamingActive;
    private readonly Func<string, long> _freeBytes;
    private readonly string? _historyPath;
    private readonly TimeSpan[] _retryDelays;
    private readonly BandwidthLimiter _limiter = new();
    private int _maxConcurrent = 2;
    private long _maxFileBytes = DefaultMaxFileBytes;
    private bool _disposed;

    public GameDownloadManager(LocalRuntimeService runtime)
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
               { Timeout = Timeout.InfiniteTimeSpan },
            () => runtime.GamingMode, DefaultFreeBytes,
            Path.Combine(PaRuXDataRoot.Path, "server", "game-downloads.json"),
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30) }) { }

    internal GameDownloadManager(HttpClient http, Func<bool> gamingActive, Func<string, long> freeBytes,
        string? historyPath, TimeSpan[] retryDelays)
    {
        _http = http;
        _gamingActive = gamingActive;
        _freeBytes = freeBytes;
        _historyPath = historyPath;
        _retryDelays = retryDelays;
        LoadHistory();
    }

    internal int MaxConcurrent { get => _maxConcurrent; set => _maxConcurrent = Math.Clamp(value, 1, 8); }
    internal long MaxFileBytes { get => _maxFileBytes; set => _maxFileBytes = Math.Max(1, value); }

    public void SetBandwidthLimit(long bytesPerSecond) => _limiter.SetLimit(Math.Max(0, bytesPerSecond));

    public IReadOnlyList<GameDownloadDto> List()
    {
        lock (_gate) return _entries.Select(ToDto).OrderByDescending(d => d.CreatedUtc).ToArray();
    }

    // ------------------------------------------------------------------ commands

    public GameDownloadDto Enqueue(GameDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri url = ValidateUrl(request.Url);
        string directory = ValidateDirectory(request.DestinationDirectory);
        string name = SafeFileName(request.FileName) ?? SafeFileName(Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath))) ?? "download";
        string? sha = NormalizeSha(request.ExpectedSha256);
        if (request.ExpectedSha256 is { Length: > 0 } && sha is null) throw new GameDownloadException("The SHA-256 must be 64 hexadecimal characters.");
        if (_gamingActive()) throw new GameDownloadException("Downloads are paused while a game is running.");

        lock (_gate)
        {
            if (_entries.Count(e => !GameDownloadStates.IsTerminal(e.State)) >= 200) throw new GameDownloadException("Too many active downloads.");
            // Two active jobs for the same target file would corrupt each other's part file.
            if (_entries.Any(e => !GameDownloadStates.IsTerminal(e.State) &&
                                  string.Equals(e.PartPath, Path.Combine(directory, name + PartExtension), StringComparison.OrdinalIgnoreCase)))
                throw new GameDownloadException("A download to that file is already in progress.");
            var entry = new Entry
            {
                Id = Guid.NewGuid().ToString("N"), Url = url, FileName = name, Directory = directory,
                ExpectedSha256 = sha, Priority = Math.Clamp(request.Priority, -10, 10),
            };
            _entries.Add(entry);
            Pump();
            return ToDto(entry);
        }
    }

    public bool Pause(string id)
    {
        lock (_gate)
        {
            Entry? e = Find(id);
            if (e is null || GameDownloadStates.IsTerminal(e.State)) return false;
            e.UserPause = true;
            e.Reason = "Paused by you.";
            if (e.State == GameDownloadStates.Running) e.Run?.Cancel(); else e.State = GameDownloadStates.Paused;
            return true;
        }
    }

    public bool Resume(string id)
    {
        lock (_gate)
        {
            Entry? e = Find(id);
            if (e is null || e.State != GameDownloadStates.Paused) return false;
            if (_gamingActive()) { e.Reason = "A game is running; the download will continue afterwards."; return false; }
            e.UserPause = false;
            e.Reason = null;
            e.State = GameDownloadStates.Queued;
            Pump();
            return true;
        }
    }

    /// <summary>Cancels a job and deletes its partial file, or removes a finished job from the history.</summary>
    public bool CancelOrRemove(string id)
    {
        lock (_gate)
        {
            Entry? e = Find(id);
            if (e is null) return false;
            if (GameDownloadStates.IsTerminal(e.State))
            {
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
            SaveHistory();
            Pump();
            return true;
        }
    }

    /// <summary>Called when Gaming ends: jobs paused only because of the game are queued again.</summary>
    public Task<int> ResumeGamingPausedAsync(CancellationToken ct)
    {
        if (_gamingActive()) return Task.FromResult(0);
        lock (_gate)
        {
            var paused = _entries.Where(e => e.State == GameDownloadStates.Paused && !e.UserPause).ToList();
            foreach (Entry e in paused) { e.State = GameDownloadStates.Queued; e.Reason = null; }
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
        foreach (Entry next in _entries.Where(e => e.State == GameDownloadStates.Queued)
                     .OrderByDescending(e => e.Priority).ThenBy(e => e.CreatedUtc).ToList())
        {
            if (running >= _maxConcurrent) break;
            next.State = GameDownloadStates.Running;
            next.Reason = null;
            next.Run = new CancellationTokenSource();
            running++;
            _ = Task.Run(() => RunAsync(next, next.Run.Token));
        }
    }

    private async Task RunAsync(Entry e, CancellationToken ct)
    {
        string? failure = null;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await TransferAsync(e, ct).ConfigureAwait(false); break; }
                catch (HttpRequestException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
                { await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false); }
                catch (IOException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested && !DiskIsFull(e))
                { await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { /* pause, cancel or Gaming: handled below */ }
        catch (GameDownloadException ex) { failure = ex.Message; }
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
                if (failure.Contains("SHA-256", StringComparison.Ordinal)) CleanPartial(e); // a corrupt file is not kept for resume
            }
            else if (e.State == GameDownloadStates.Completed) { /* set by TransferAsync */ }
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

    private static bool DiskIsFull(Entry e)
    {
        try { return new DriveInfo(Path.GetPathRoot(e.Directory)!).AvailableFreeSpace < 1024 * 1024; }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ transfer

    private async Task TransferAsync(Entry e, CancellationToken ct)
    {
        // Never follow a user-placed symbolic link as a partial download target.
        // The destination directory was validated at enqueue, but the part file
        // may have been replaced since then.
        if (File.Exists(e.PartPath) &&
            (File.GetAttributes(e.PartPath) & FileAttributes.ReparsePoint) != 0)
            throw new GameDownloadException("The partial download path is a linked file.");
        long existing = File.Exists(e.PartPath) ? new FileInfo(e.PartPath).Length : 0;
        // A filename is not a source identity. Never append an old partial from a
        // different URL or checksum, even when its server happens to accept Range.
        if (existing > 0 && !ResumeIdentityMatches(e))
        {
            CleanPartial(e);
            existing = 0;
        }
        if (existing == 0) WriteResumeIdentity(e);
        EntityTagHeaderValue? validator = ResumeValidator.Read(e.PartPath);

        using HttpResponseMessage response = await SendFollowingRedirectsAsync(e.Url, existing, validator, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // Our partial file no longer matches the server: start again from zero.
            CleanPartial(e);
            throw new HttpRequestException("range not satisfiable");
        }
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            // Timeouts, rate limits and server errors are temporary: retry. Anything else (404, 403...) will not change.
            if (code is 408 or 429 or >= 500)
                throw new HttpRequestException($"The server answered HTTP {code}.", null, response.StatusCode);
            throw new GameDownloadException($"The server answered HTTP {code}.");
        }

        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        // HTTP 206 is valid only in response to OUR own nonzero Range request.
        // A server supplying unsolicited partial bytes must never cause us to
        // append or publish a truncated file as if it were a complete download.
        if (resumed && (existing == 0 ||
            !ResumeValidator.ContentRangeMatches(response.Content.Headers.ContentRange, existing)))
        {
            CleanPartial(e);
            throw new GameDownloadException("The server supplied an unexpected or misaligned partial response.");
        }
        if (!resumed && existing > 0) { CleanPartial(e); existing = 0; } // server ignored the range or the file changed
        if (!resumed) ResumeValidator.Write(e.PartPath, response.Headers.ETag);

        long? total = resumed
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (total is > 0 && total > _maxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
        long needed = (total ?? 0) - existing;
        if (needed > 0 && _freeBytes(e.Directory) < needed + FreeSpaceMarginBytes) throw new GameDownloadException("Not enough free disk space.");
        lock (_gate) { e.TotalBytes = total; e.BytesDone = existing; }

        await using (var target = new FileStream(e.PartPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[1 << 16];
            long written = existing;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (_gamingActive()) throw new OperationCanceledException(); // no transfer competes with a game
                written += read;
                if (written > _maxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
                await _limiter.WaitAsync(read, ct).ConfigureAwait(false);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                lock (_gate) e.BytesDone = written;
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            if (total is not null && written != total)
                throw new HttpRequestException("connection closed before the end of the file");
        }

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
            string finalPath = UniquePath(e.FinalPath);
            ct.ThrowIfCancellationRequested();
            File.Move(e.PartPath, finalPath);
            ResumeValidator.Delete(e.PartPath);
            DeleteResumeIdentity(e);
            e.IntegrityChecked = e.ExpectedSha256 is not null;
            e.VerifiedSha256 = sha;
            e.State = GameDownloadStates.Completed;
            e.Reason = e.ExpectedSha256 is null ? "Completed. No expected checksum was given, so the file is not verified." : "Completed and verified.";
            e.FinishedUtc = DateTime.UtcNow;
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri url, long rangeFrom, EntityTagHeaderValue? validator, CancellationToken ct)
    {
        Uri current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(rangeFrom, null);
                if (validator is not null) request.Headers.IfRange = new RangeConditionHeaderValue(validator);
            }
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateUrl(next.ToString()); // every hop obeys the same rules: no downgrade, no credentials
                current = next;
                continue;
            }
            return response;
        }
        throw new GameDownloadException("Too many redirects.");
    }

    // ------------------------------------------------------------------ validation and helpers

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
        if (!ManualGameLibrary.IsSafeDirectory(full)) throw new GameDownloadException("The destination folder does not exist or is a linked folder.");
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
    private static string ResumeIdentityPath(Entry e) => e.PartPath + ".identity";

    private static string ExpectedResumeIdentity(Entry e)
    {
        string material = e.Url.AbsoluteUri + "\n" + (e.ExpectedSha256 ?? "");
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static bool ResumeIdentityMatches(Entry e)
    {
        try
        {
            string path = ResumeIdentityPath(e);
            if (!File.Exists(path) || new FileInfo(path).Length is < 67 or > 70) return false;
            return string.Equals(File.ReadAllText(path).Trim(), ExpectedResumeIdentity(e), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void WriteResumeIdentity(Entry e)
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

    private static void DeleteResumeIdentity(Entry e)
    {
        try { File.Delete(ResumeIdentityPath(e)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CleanPartial(Entry e)
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

    private Entry? Find(string id) => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static GameDownloadDto ToDto(Entry e) => new(
        e.Id, e.FileName, e.Url.Host + e.Url.AbsolutePath, e.State, e.Reason, e.BytesDone, e.TotalBytes,
        e.Priority, e.IntegrityChecked, e.VerifiedSha256, e.CreatedUtc, e.FinishedUtc);

    // ------------------------------------------------------------------ history

    private sealed record HistoryItem(string Id, string FileName, string Source, string State, string? Reason, long BytesDone, long? TotalBytes,
        int Priority, bool IntegrityChecked, string? VerifiedSha256, DateTime CreatedUtc, DateTime? FinishedUtc);

    private void SaveHistory()
    {
        if (_historyPath is null) return;
        try
        {
            var items = _entries.Where(x => GameDownloadStates.IsTerminal(x.State)).OrderByDescending(x => x.FinishedUtc).Take(MaxHistory)
                .Select(x => new HistoryItem(x.Id, x.FileName, x.Url.Host + x.Url.AbsolutePath, x.State, x.Reason, x.BytesDone, x.TotalBytes,
                    x.Priority, x.IntegrityChecked, x.VerifiedSha256, x.CreatedUtc, x.FinishedUtc)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            string temp = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, _historyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
                _entries.Add(new Entry
                {
                    Id = h.Id, Url = new Uri("https://" + h.Source), FileName = h.FileName, Directory = string.Empty, Priority = h.Priority,
                    CreatedUtc = h.CreatedUtc, State = h.State, Reason = h.Reason, BytesDone = h.BytesDone, TotalBytes = h.TotalBytes,
                    IntegrityChecked = h.IntegrityChecked, VerifiedSha256 = h.VerifiedSha256, FinishedUtc = h.FinishedUtc,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UriFormatException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Entry e in _entries) e.Run?.Cancel();
        }
        _http.Dispose();
    }

    // ------------------------------------------------------------------ bandwidth

    private sealed class BandwidthLimiter
    {
        private readonly object _gate = new();
        private long _bytesPerSecond;
        private DateTime _next = DateTime.UtcNow;

        public void SetLimit(long bytesPerSecond) { lock (_gate) { _bytesPerSecond = bytesPerSecond; _next = DateTime.UtcNow; } }

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
