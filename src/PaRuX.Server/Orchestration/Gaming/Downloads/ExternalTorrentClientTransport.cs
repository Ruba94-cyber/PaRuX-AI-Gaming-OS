using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>
/// Optional adapter that drives a torrent client the USER installed (qBittorrent) through its local WebUI API.
/// The bundled MonoTorrent engine is separate and optional; this adapter is OFF until the user configures it and only talks
/// to 127.0.0.1, and keeps the port and credentials in memory (never persisted, never logged). The client only
/// produces bytes: the manager re-hashes the finished file itself and publishes it through the normal integrity path.
/// </summary>
public sealed class ExternalTorrentClientTransport : IGameDownloadTransport, IDisposable
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int PausedGracePolls = 3;
    private const int MaxUnknownPolls = 30;

    private sealed class Session
    {
        public required HttpClient Http { get; init; }
        public required int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public Uri Base => new($"http://127.0.0.1:{Port}/");
    }

    private readonly object _gate = new();
    private Session? _session;
    private readonly Func<bool> _gamingActive;

    public ExternalTorrentClientTransport(LocalRuntimeService runtime) : this(() => runtime.GamingMode) { }

    internal ExternalTorrentClientTransport(Func<bool> gamingActive) => _gamingActive = gamingActive;

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsEnabled { get { lock (_gate) return _session is not null; } }
    public int? Port { get { lock (_gate) return _session?.Port; } }

    /// <summary>Verifies the WebUI answers on loopback (and the credentials work), then keeps the settings in memory.</summary>
    public async Task ConfigureAsync(int port, string? username, string? password, CancellationToken ct)
    {
        if (_gamingActive()) throw new GameDownloadException("Leave Gaming before changing download settings.");
        if (port is < 1 or > 65535) throw new GameDownloadException("Enter a port between 1 and 65535.");
        username ??= string.Empty;
        password ??= string.Empty;
        if (username.Length > 128 || password.Length > 256 || username.Any(char.IsControl) || password.Any(char.IsControl))
            throw new GameDownloadException("The torrent client credentials are not valid.");
        var session = NewSession(port, username, password);
        try
        {
            await EnsureLoginAsync(session, ct).ConfigureAwait(false);
            (HttpStatusCode code, _) = await SendAsync(session, () => new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version"), ct)
                .ConfigureAwait(false);
            if (code != HttpStatusCode.OK) throw new GameDownloadException("That port did not answer like a qBittorrent WebUI.");
        }
        catch (HttpRequestException)
        {
            session.Http.Dispose();
            throw new GameDownloadException("The torrent client could not be reached on that local port.");
        }
        catch
        {
            session.Http.Dispose();
            throw;
        }
        lock (_gate) _session = session;
    }

    /// <summary>Turns the adapter off and forgets the credentials.</summary>
    public void Disable()
    {
        lock (_gate) _session = null;
    }

    public void Dispose() => Disable();

    private static Session NewSession(int port, string user, string pass)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        return new Session
        {
            Http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) },
            Port = port, Username = user, Password = pass,
        };
    }

    private Session RequireSession()
    {
        lock (_gate) return _session ?? throw new GameDownloadException("The external torrent client adapter is off.");
    }

    // ------------------------------------------------------------------ state mapping

    /// <summary>Maps a qBittorrent state string onto the existing download states.</summary>
    internal static string MapState(string clientState)
    {
        switch (clientState)
        {
            case "error":
            case "missingFiles":
                return GameDownloadStates.Failed;
            case "uploading": case "stalledUP": case "forcedUP": case "queuedUP": case "checkingUP":
            case "pausedUP": case "stoppedUP":
                return GameDownloadStates.Completed;
            case "pausedDL": case "stoppedDL":
                return GameDownloadStates.Paused;
            case "queuedDL":
                return GameDownloadStates.Queued;
            default:
                // downloading, forcedDL, metaDL, forcedMetaDL, stalledDL, checkingDL, allocating, checkingResumeData, moving, ...
                // Never infer completion from a progress number: "moving"/"checkingDL" at 100% are not finished.
                return GameDownloadStates.Running;
        }
    }

    // ------------------------------------------------------------------ transport

    async Task IGameDownloadTransport.FetchAsync(GameDownloadTransferContext context, CancellationToken ct)
    {
        GameDownloadJob job = context.Job;
        Session session = RequireSession();
        ct.ThrowIfCancellationRequested();
        if (context.GamingActive()) throw new OperationCanceledException("Gaming disabled new download disk and network work.");
        GameDownloadManager.EnsureSafeDownloadPaths(job);
        string staging = Path.Combine(job.Directory, ".parux-torrent-" + job.Id);
        string? hash = job.InfoHash ?? throw new GameDownloadException("The torrent source is missing.");
        try
        {
            EnsureStaging(staging);
            if (context.Job.ExternalId is null) await AddAsync(session, context, staging, hash, ct).ConfigureAwait(false);
            else await PostWithFallbackAsync(session, new[] { "api/v2/torrents/resume", "api/v2/torrents/start" }, hash, ct).ConfigureAwait(false);

            await WaitForCompletionAsync(session, context, hash, ct).ConfigureAwait(false);
            await CopyResultAsync(session, context, staging, hash, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pause, cancel or Gaming: stop the client's network and disk work too. Never let that call block shutdown.
            if (job.ExternalId is not null) await BestEffortAsync(() => PostWithFallbackAsync(session,
                new[] { "api/v2/torrents/pause", "api/v2/torrents/stop" }, hash, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
        catch (GameDownloadException ex) when (ex is not GameDownloadPausedException && job.ExternalId is not null)
        {
            // A terminal failure (client error, too large, ...): do not leave our own half-finished job behind.
            await RemoveFromClientAsync(session, hash, staging).ConfigureAwait(false);
            job.ExternalId = null;
            throw;
        }
    }

    Task IGameDownloadTransport.AbandonAsync(GameDownloadJob job, CancellationToken ct)
    {
        Session? session;
        lock (_gate) session = _session;
        if (session is null || job.ExternalId is null || job.InfoHash is null) return Task.CompletedTask;
        string staging = Path.Combine(job.Directory, ".parux-torrent-" + job.Id);
        return RemoveFromClientAsync(session, job.InfoHash, staging);
    }

    private const int MaxStageEntries = 10_000;

    private static bool OwnedStageName(string staging)
    {
        string name = Path.GetFileName(staging.TrimEnd(Path.DirectorySeparatorChar));
        const string prefix = ".parux-torrent-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length == prefix.Length + 32
            && name[prefix.Length..].All(Uri.IsHexDigit);
    }

    /// <summary>Fail closed before qBittorrent touches an existing PaRuX stage.</summary>
    internal static void EnsureStaging(string staging)
    {
        if (!OwnedStageName(staging))
            throw new GameDownloadException("The torrent staging path is not PaRuX-owned.");
        if (!ManualGameLibrary.IsSafeDirectory(Path.GetDirectoryName(staging)!))
            throw new GameDownloadException("The torrent staging parent is not a safe local folder.");
        if (File.Exists(staging))
            throw new GameDownloadException("A file occupies the torrent staging directory.");
        Directory.CreateDirectory(staging);
        ValidateStageTree(staging);
    }

    private static void ValidateStageTree(string root)
    {
        var remaining = new Stack<string>();
        remaining.Push(root);
        int visited = 0;
        while (remaining.Count > 0)
        {
            string folder = remaining.Pop();
            if (++visited > MaxStageEntries || !ManualGameLibrary.IsSafeDirectory(folder))
                throw new GameDownloadException("The torrent stage contains unsafe links or too many entries.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (++visited > MaxStageEntries) throw new GameDownloadException("The torrent stage is too large.");
                FileAttributes attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(entry).LinkTarget is not null)
                    throw new GameDownloadException("The torrent stage contains a linked file or directory.");
                if ((attr & FileAttributes.Directory) != 0) remaining.Push(entry);
            }
        }
    }

    /// <summary>
    /// qBittorrent never receives deleteFiles=true: only PaRuX cleans its own
    /// GUID stage after it has inspected every directory/file for links.
    /// A suspicious stage is preserved for manual inspection.
    /// </summary>
    internal static void CleanOwnedStaging(string staging)
    {
        try
        {
            if (!OwnedStageName(staging) || !Directory.Exists(staging)
                || !ManualGameLibrary.IsSafeDirectory(Path.GetDirectoryName(staging)!)) return;
            ValidateStageTree(staging);
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GameDownloadException)
        { /* Leave unsafe partials untouched rather than following links. */ }
    }

    private async Task AddAsync(Session session, GameDownloadTransferContext context, string staging, string hash, CancellationToken ct)
    {
        GameDownloadJob job = context.Job;
        (HttpStatusCode existsCode, string existsBody) = await SendAsync(session,
            () => new HttpRequestMessage(HttpMethod.Get, "api/v2/torrents/info?hashes=" + hash), ct).ConfigureAwait(false);
        ThrowForStatus(existsCode);
        if (ParseArray(existsBody).Count > 0)
            throw new GameDownloadException("That torrent is already in your torrent client. Remove it there first.");

        byte[]? torrentBytes = job.Kind == GameDownloadKinds.TorrentFile
            ? GameDownloadSourceValidator.ReadValidatedTorrentBytes(job.Target, hash) : null;
        (HttpStatusCode code, string body) = await SendAsync(session, () =>
        {
            var form = new MultipartFormDataContent();
            if (torrentBytes is null) form.Add(new StringContent(job.Target, Encoding.UTF8), "urls");
            else
            {
                var file = new ByteArrayContent(torrentBytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                form.Add(file, "torrents", "job.torrent");
            }
            form.Add(new StringContent(staging, Encoding.UTF8), "savepath");
            form.Add(new StringContent("false"), "autoTMM");
            return new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/add") { Content = form };
        }, ct).ConfigureAwait(false);
        ThrowForStatus(code);
        if (!body.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The torrent client refused the torrent.");
        job.ExternalId = hash;
    }

    private async Task<long> WaitForCompletionAsync(
        Session session, GameDownloadTransferContext context, string hash, CancellationToken ct)
    {
        int unknown = 0, pausedSeen = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (context.GamingActive()) throw new OperationCanceledException("Gaming stopped the torrent transfer.");
            (HttpStatusCode code, string body) = await SendAsync(session,
                () => new HttpRequestMessage(HttpMethod.Get, "api/v2/torrents/info?hashes=" + hash), ct).ConfigureAwait(false);
            ThrowForStatus(code);
            List<JsonElement> list = ParseArray(body);
            if (list.Count == 0) throw new GameDownloadException("The torrent was removed from the torrent client.");
            JsonElement t = list[0];
            string state = GetString(t, "state") ?? "unknown";
            double progress = GetDouble(t, "progress");
            long size = GetLong(t, "size");
            if (size <= 0) size = GetLong(t, "total_size");
            if (size > context.MaxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
            context.Report((long)(Math.Clamp(progress, 0, 1) * Math.Max(size, 0)), size > 0 ? size : null);

            switch (MapState(state))
            {
                case GameDownloadStates.Failed:
                    throw new GameDownloadException(state == "missingFiles"
                        ? "The torrent client reports missing files."
                        : "The torrent client reported an error for this torrent.");
                case GameDownloadStates.Paused:
                    if (++pausedSeen > PausedGracePolls)
                        throw new GameDownloadPausedException("Paused in your torrent client.");
                    break;
                case GameDownloadStates.Queued:
                    pausedSeen = 0;
                    break;
                case GameDownloadStates.Completed:
                    return size;
                default:
                    pausedSeen = 0;
                    break;
            }
            if (state == "unknown" && ++unknown > MaxUnknownPolls)
                throw new GameDownloadException("The torrent client reported an unknown state.");
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task CopyResultAsync(Session session, GameDownloadTransferContext context, string staging, string hash,
        CancellationToken ct)
    {
        GameDownloadJob job = context.Job;
        (HttpStatusCode code, string body) = await SendAsync(session,
            () => new HttpRequestMessage(HttpMethod.Get, "api/v2/torrents/files?hash=" + hash), ct).ConfigureAwait(false);
        ThrowForStatus(code);
        List<JsonElement> files = ParseArray(body);
        if (files.Count != 1) throw new GameDownloadException("Only single-file torrents are supported.");
        string relative = GetString(files[0], "name") ?? string.Empty;
        string source = Path.GetFullPath(Path.Combine(staging, relative));
        string root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // The client is not trusted to name a path outside the staging folder.
        if (relative.Length == 0 || relative.Contains('/') || relative.Contains('\\')
            || relative is "." or ".." || Path.IsPathRooted(relative)
            || !string.Equals(relative, GameDownloadManager.SafeFileName(relative), StringComparison.Ordinal)
            || !source.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The torrent client reported an unexpected file path.");
        var info = new FileInfo(source);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new GameDownloadException("The finished torrent file was not found.");
        if (info.Length > context.MaxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
        if (context.FreeBytes(job.Directory) < info.Length + GameDownloadManager.FreeSpaceMarginBytes)
            throw new GameDownloadException("Not enough free disk space.");

        GameDownloadManager.CleanPartial(job);
        GameDownloadManager.EnsureSafeDownloadPaths(job);
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
        await using (var to = new FileStream(job.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (context.GamingActive()) throw new OperationCanceledException();
                await context.Throttle(read, ct).ConfigureAwait(false);
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                context.Report(written, info.Length);
            }
            await to.FlushAsync(ct).ConfigureAwait(false);
        }
        // Our copy exists; the client's staging copy and torrent entry are ours to remove.
        await RemoveFromClientAsync(session, hash, staging).ConfigureAwait(false);
        job.ExternalId = null;
    }

    // ------------------------------------------------------------------ WebUI plumbing

    private async Task RemoveFromClientAsync(Session session, string hash, string staging)
    {
        await BestEffortAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(session, () => new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/delete")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", hash), new KeyValuePair<string, string>("deleteFiles", "false"),
                }),
            }, cts.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        CleanOwnedStaging(staging);
    }

    private static async Task BestEffortAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or GameDownloadException or OperationCanceledException or IOException) { }
    }

    private async Task PostWithFallbackAsync(Session session, string[] paths, string hash, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (string path in paths)
        {
            (HttpStatusCode code, _) = await SendAsync(session, () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("hashes", hash) }),
            }, cts.Token).ConfigureAwait(false);
            if (code != HttpStatusCode.NotFound) { ThrowForStatus(code); return; }
        }
    }

    private static void ThrowForStatus(HttpStatusCode code)
    {
        int n = (int)code;
        if (n is >= 200 and < 300) return;
        // Timeouts, rate limits and server errors are temporary (the manager retries a bounded number of times).
        if (n is 408 or 429 or >= 500) throw new HttpRequestException($"The torrent client answered HTTP {n}.", null, code);
        throw new GameDownloadException($"The torrent client refused the request (HTTP {n}).");
    }

    private async Task EnsureLoginAsync(Session session, CancellationToken ct)
    {
        if (session.Username.Length == 0 && session.Password.Length == 0) return; // WebUI auth bypass for localhost
        (HttpStatusCode code, string body) = await RawAsync(session, () => new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", session.Username), new KeyValuePair<string, string>("password", session.Password),
            }),
        }, ct).ConfigureAwait(false);
        if (code == HttpStatusCode.Forbidden) throw new GameDownloadException("The torrent client temporarily blocked this address after failed logins.");
        ThrowForStatus(code);
        if (!body.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException("The torrent client rejected the username or password.");
    }

    /// <summary>Sends a request; on 403 logs in once and repeats it.</summary>
    private async Task<(HttpStatusCode, string)> SendAsync(Session session, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        (HttpStatusCode code, string body) = await RawAsync(session, make, ct).ConfigureAwait(false);
        if (code != HttpStatusCode.Forbidden) return (code, body);
        await EnsureLoginAsync(session, ct).ConfigureAwait(false);
        (code, body) = await RawAsync(session, make, ct).ConfigureAwait(false);
        if (code == HttpStatusCode.Forbidden) throw new GameDownloadException("The torrent client did not accept the saved credentials.");
        return (code, body);
    }

    private static async Task<(HttpStatusCode, string)> RawAsync(Session session, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using HttpRequestMessage request = make();
        // qBittorrent's CSRF protection wants a Referer/Origin on its own host.
        request.Headers.Referrer = session.Base;
        request.Headers.Add("Origin", session.Base.GetLeftPart(UriPartial.Authority));
        using HttpResponseMessage response = await session.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new GameDownloadException("The torrent client sent an unexpectedly large response.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[1 << 14];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxResponseBytes) throw new GameDownloadException("The torrent client sent an unexpectedly large response.");
            ms.Write(buffer, 0, read);
        }
        return (response.StatusCode, Encoding.UTF8.GetString(ms.ToArray()));
    }

    // ------------------------------------------------------------------ json helpers

    private static List<JsonElement> ParseArray(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new HttpRequestException("The torrent client sent an unexpected answer.");
            return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        catch (JsonException) { throw new HttpRequestException("The torrent client sent an unexpected answer."); }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long GetLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
}
