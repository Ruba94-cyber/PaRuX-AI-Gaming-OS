using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A fake qBittorrent WebUI on 127.0.0.1 stands in for the user's client: no real torrent client, no internet.
/// </summary>
public sealed class GameDownloadTorrentTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string Magnet = "magnet:?xt=urn:btih:" + Hash;

    private sealed class FakeQbittorrent : IDisposable
    {
        private HttpListener _listener = new();
        private readonly object _gate = new();
        public int Port { get; }
        public string User = "admin", Pass = "secret";
        public string ValidSid = "none";
        public void Expire() { lock (_gate) ValidSid = "expired"; }
        public byte[] Content = Array.Empty<byte>();
        public string ContentName = "game.bin";
        public string[] Files = { "game.bin" };
        public List<string> States = new() { "downloading", "uploading" };
        public double Progress = 0.2;
        public bool Exists, Deleted, PauseCalled, ResumeCalled, StartRoute, ReportsTorrentPresent;
        public bool V5; // v5 uses stop/start instead of pause/resume
        public int FailInfo;     // answer 503 this many times on /info
        public int InfoCalls, Logins;
        public string? SavePath, AddedBody, DeletedBody;
        public List<string> Paths = new();
        private int _poll;

        public FakeQbittorrent()
        {
            Port = Random.Shared.Next(20000, 60000);
            while (true)
            {
                try { _listener = new HttpListener(); _listener.Prefixes.Add($"http://127.0.0.1:{Port}/"); _listener.Start(); break; }
                catch (HttpListenerException) { Port = Random.Shared.Next(20000, 60000); }
            }
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private static void Write(HttpListenerContext c, int status, string body, string type = "text/plain")
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            c.Response.ContentType = type;
            c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b);
            c.Response.Close();
        }

        private void Handle(HttpListenerContext c)
        {
            try
            {
                string path = c.Request.Url!.AbsolutePath;
                string body;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.Latin1)) body = r.ReadToEnd();
                lock (_gate) Paths.Add(path);
                // The adapter must send a Referer/Origin on its own host (qBittorrent CSRF check).
                if (c.Request.Headers["Referer"] is not { } referer || !referer.StartsWith($"http://127.0.0.1:{Port}"))
                { Write(c, 401, "bad referer"); return; }
                if (path == "/api/v2/auth/login")
                {
                    lock (_gate) Logins++;
                    if (body.Contains("username=" + User) && body.Contains("password=" + Pass))
                    {
                        string sid;
                        lock (_gate) { sid = "sid" + Logins; ValidSid = sid; }
                        c.Response.Headers.Add("Set-Cookie", $"SID={sid}; Path=/");
                        Write(c, 200, "Ok.");
                    }
                    else Write(c, 200, "Fails.");
                    return;
                }
                string valid; lock (_gate) valid = ValidSid;
                if (c.Request.Cookies["SID"]?.Value != valid) { Write(c, 403, "Forbidden"); return; }
                switch (path)
                {
                    case "/api/v2/app/version": Write(c, 200, "v4.6.0"); break;
                    case "/api/v2/torrents/add":
                        lock (_gate)
                        {
                            Exists = true; Deleted = false; AddedBody = body;
                            Match m = Regex.Match(body, "name=\"?savepath\"?\r\n(?:[^\r\n]+\r\n)*\r\n([^\r]*)\r\n");
                            SavePath = m.Success ? m.Groups[1].Value : null;
                        }
                        Write(c, 200, "Ok.");
                        break;
                    case "/api/v2/torrents/info":
                        lock (_gate)
                        {
                            InfoCalls++;
                            if (FailInfo > 0) { FailInfo--; Write(c, 503, "busy"); return; }
                            if (!Exists || Deleted) { Write(c, 200, "[]", "application/json"); return; }
                            string state = States[Math.Min(_poll++, States.Count - 1)];
                            if (state is "uploading" or "stalledUP" && SavePath is not null)
                            {
                                Directory.CreateDirectory(SavePath);
                                string fp = Path.Combine(SavePath, ContentName);
                                if (!File.Exists(fp)) File.WriteAllBytes(fp, Content);
                            }
                            double p = state is "uploading" or "stalledUP" ? 1.0 : Progress;
                            Write(c, 200, $"[{{\"hash\":\"{Hash}\",\"state\":\"{state}\",\"progress\":{p.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"size\":{Content.Length},\"amount_left\":0}}]", "application/json");
                        }
                        break;
                    case "/api/v2/torrents/files":
                        Write(c, 200, "[" + string.Join(",", Files.Select(f => $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(f)}}}")) + "]", "application/json");
                        break;
                    case "/api/v2/torrents/pause": lock (_gate) PauseCalled = true; if (V5) Write(c, 404, ""); else Write(c, 200, ""); break;
                    case "/api/v2/torrents/stop": lock (_gate) PauseCalled = true; Write(c, 200, ""); break;
                    case "/api/v2/torrents/resume": lock (_gate) ResumeCalled = true; if (V5) Write(c, 404, ""); else Write(c, 200, ""); break;
                    case "/api/v2/torrents/start": lock (_gate) { ResumeCalled = true; StartRoute = true; } Write(c, 200, ""); break;
                    case "/api/v2/torrents/delete": lock (_gate) { Deleted = true; DeletedBody = body; } Write(c, 200, ""); break;
                    default: Write(c, 404, ""); break;
                }
            }
            catch { try { c.Response.Abort(); } catch { } }
        }

        public bool Saw(string path) { lock (_gate) return Paths.Contains(path); }
        public void Dispose() { try { _listener.Close(); } catch { } }
    }

    private readonly FakeQbittorrent _client = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("parux-torrent-").FullName;
    private volatile bool _gaming;
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        _client.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Bytes(int n) { var d = new byte[n]; new Random(7).NextBytes(d); return d; }
    private static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    private ExternalTorrentClientTransport NewTorrent() =>
        new(() => _gaming) { PollInterval = TimeSpan.FromMilliseconds(10) };

    private async Task<(GameDownloadManager Manager, ExternalTorrentClientTransport Torrent)> MakeEnabled(string? history = null)
    {
        ExternalTorrentClientTransport torrent = NewTorrent();
        await torrent.ConfigureAsync(_client.Port, _client.User, _client.Pass, CancellationToken.None);
        var manager = new GameDownloadManager(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan },
            () => _gaming, _ => long.MaxValue, history,
            new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) }, torrent);
        _disposables.Add(manager);
        return (manager, torrent);
    }

    private static async Task<GameDownloadDto> WaitFor(GameDownloadManager m, string id, string state, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var dto = m.List().First(d => d.Id == id);
            if (dto.State == state) return dto;
            await Task.Delay(15);
        }
        var last = m.List().First(d => d.Id == id);
        throw new TimeoutException($"never reached '{state}': {last.State} {last.Reason}");
    }

    private static byte[] TorrentBytes(string name = "game.bin", long length = 1000, int pieceBytes = 20)
    {
        string head = $"d8:announce20:http://t.example/ann4:infod6:lengthi{length}e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces{pieceBytes}:";
        return Encoding.Latin1.GetBytes(head).Concat(new byte[pieceBytes]).Concat(Encoding.Latin1.GetBytes("ee")).ToArray();
    }

    private string WriteTorrent(byte[] data, string file = "x.torrent")
    {
        string p = Path.Combine(_dir, file);
        File.WriteAllBytes(p, data);
        return p;
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("MAGNET:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Some+Game&tr=udp%3A%2F%2Ftracker.example%3A80%2Fannounce")]
    [InlineData("magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVSDEGM2KNLFLSXY&tr=https%3A%2F%2Ft.example%2Fa")]
    public void Valid_magnets_are_accepted(string link)
    {
        GameMagnetLink m = GameDownloadSourceValidator.ValidateMagnet(link);
        Assert.Equal(40, m.InfoHashHex.Length);
        Assert.Equal(m.InfoHashHex, m.InfoHashHex.ToLowerInvariant());
    }

    [Fact]
    public void Base32_info_hash_converts_to_hex()
    {
        // 20 zero bytes in base32 is 32 times 'A'.
        Assert.Equal(new string('0', 40),
            GameDownloadSourceValidator.ValidateMagnet("magnet:?xt=urn:btih:" + new string('A', 32)).InfoHashHex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com/a.zip")]
    [InlineData("magnet:xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]            // no ?
    [InlineData("magnet:?dn=x&xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]       // hash not first
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef0123456")]             // 39 chars
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef0123456g")]            // not hex
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("magnet:?xt=urn:sha1:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=http%3A%2F%2Fuser%3Apw%40t.example%2Fa")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=http://user:pw@t.example/a")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=file%3A%2F%2F%2Fc%3A%2Fsecret")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=a b")]     // whitespace
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567#frag")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&bad")]
    public void Malformed_or_unsafe_magnets_are_refused(string link) =>
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateMagnet(link));

    [Fact]
    public void Overlong_magnet_is_refused() =>
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateMagnet(Magnet + "&dn=" + new string('a', 3000)));

    [Fact]
    public void Valid_torrent_file_is_accepted_and_hashed()
    {
        byte[] data = TorrentBytes("Name.iso", 5000);
        GameTorrentFile t = GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(data));
        Assert.Equal("Name.iso", t.Name);
        Assert.Equal(40, t.InfoHashHex.Length);
        Assert.Equal(5000, t.ContentLength);
    }

    [Fact]
    public void Torrent_files_with_the_wrong_extension_size_or_structure_are_refused()
    {
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(TorrentBytes(), "x.txt")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(Path.Combine(_dir, "missing.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile("relative.torrent"));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(@"\\server\share\x.torrent"));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(Array.Empty<byte>(), "empty.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(
            WriteTorrent(new byte[(int)GameDownloadSourceValidator.MaxTorrentFileBytes + 1], "big.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(Encoding.ASCII.GetBytes("not bencode at all"), "junk.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(Encoding.ASCII.GetBytes("d3:fooi1ee"), "noinfo.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(
            WriteTorrent(TorrentBytes().Concat(Encoding.ASCII.GetBytes("trailing")).ToArray(), "trail.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(
            WriteTorrent(TorrentBytes(pieceBytes: 19), "pieces.torrent"))); // pieces not a multiple of 20
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(
            WriteTorrent(Encoding.ASCII.GetBytes("d4:infod4:name1:x6:lengthi99999999999999999999ee"), "huge.torrent")));
        Assert.Throws<GameDownloadException>(() => GameDownloadSourceValidator.ValidateTorrentFile(
            WriteTorrent(Encoding.ASCII.GetBytes("d1:a" + new string('l', 100) + new string('e', 100) + "e"), "deep.torrent")));
    }

    [Fact]
    public void Torrent_transfer_rechecks_the_bytes_not_only_the_selected_filename()
    {
        string path = WriteTorrent(TorrentBytes("safe.bin", 5000), "changing.torrent");
        GameTorrentFile initial = GameDownloadSourceValidator.ValidateTorrentFile(path);
        Assert.Equal(File.ReadAllBytes(path),
            GameDownloadSourceValidator.ReadValidatedTorrentBytes(path, initial.InfoHashHex));
        File.WriteAllBytes(path, TorrentBytes("other.bin", 5000));
        Assert.Throws<GameDownloadException>(() =>
            GameDownloadSourceValidator.ReadValidatedTorrentBytes(path, initial.InfoHashHex));
    }

    [Fact]
    public void Ambiguous_duplicate_info_dictionary_is_rejected()
    {
        byte[] original = TorrentBytes("safe.bin", 5000);
        // The top-level bencode dictionary contains two "info" keys; never
        // hash one dictionary and allow the torrent engine to choose another.
        byte[] duplicate = Encoding.Latin1.GetBytes("d4:info")
            .Concat(original.Skip("d8:announce20:http://t.example/ann4:info".Length).SkipLast(1))
            .Concat(Encoding.Latin1.GetBytes("4:info"))
            .Concat(original.Skip("d8:announce20:http://t.example/ann4:info".Length).SkipLast(1))
            .Concat(Encoding.Latin1.GetBytes("e")).ToArray();
        Assert.Throws<GameDownloadException>(() =>
            GameDownloadSourceValidator.ParseTorrent(duplicate));
    }

    [Fact]
    public void Kind_defaults_to_http_and_unknown_kinds_are_refused()
    {
        Assert.Equal(GameDownloadKinds.Http, new GameDownloadRequest("https://x.example/a", _dir).Kind);
        Assert.Equal(GameDownloadKinds.Http, GameDownloadKinds.Normalize(null));
        Assert.Equal(GameDownloadKinds.Magnet, GameDownloadKinds.Normalize(" MAGNET "));
        Assert.Null(GameDownloadKinds.Normalize("ftp"));
        using var manager = new GameDownloadManager(new HttpClient(), () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>());
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(new("https://x.example/a", _dir, Kind: "ftp")));
    }

    [Fact]
    public void Adapter_is_off_by_default_and_torrent_kinds_are_refused_until_configured()
    {
        using ExternalTorrentClientTransport torrent = NewTorrent();
        Assert.False(torrent.IsEnabled);
        Assert.Null(torrent.Port);
        using var manager = new GameDownloadManager(new HttpClient(), () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>(), torrent);
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(new(Magnet, _dir, Kind: GameDownloadKinds.Magnet)));
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(new(WriteTorrent(TorrentBytes()), _dir, Kind: GameDownloadKinds.TorrentFile)));
        Assert.Empty(manager.List());
        // Without the adapter object at all (older host) the same refusal applies.
        using var bare = new GameDownloadManager(new HttpClient(), () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>());
        Assert.Throws<GameDownloadException>(() => bare.Enqueue(new(Magnet, _dir, Kind: GameDownloadKinds.Magnet)));
    }

    // ------------------------------------------------------------------ configuration

    [Fact]
    public async Task Configure_verifies_the_login_and_rejects_bad_credentials_ports_and_unreachable_clients()
    {
        using ExternalTorrentClientTransport torrent = NewTorrent();
        await Assert.ThrowsAsync<GameDownloadException>(() => torrent.ConfigureAsync(_client.Port, "admin", "wrong", CancellationToken.None));
        Assert.False(torrent.IsEnabled);
        await Assert.ThrowsAsync<GameDownloadException>(() => torrent.ConfigureAsync(0, "admin", "secret", CancellationToken.None));
        await Assert.ThrowsAsync<GameDownloadException>(() => torrent.ConfigureAsync(70000, "admin", "secret", CancellationToken.None));
        _client.Dispose();
        await Assert.ThrowsAsync<GameDownloadException>(() => torrent.ConfigureAsync(_client.Port, "admin", "secret", CancellationToken.None));
        Assert.False(torrent.IsEnabled);
    }

    [Fact]
    public async Task Configure_enables_and_Disable_forgets_the_settings()
    {
        using ExternalTorrentClientTransport torrent = NewTorrent();
        await torrent.ConfigureAsync(_client.Port, "admin", "secret", CancellationToken.None);
        Assert.True(torrent.IsEnabled);
        Assert.Equal(_client.Port, torrent.Port);
        torrent.Disable();
        Assert.False(torrent.IsEnabled);
        Assert.Null(torrent.Port);
    }

    [Fact]
    public async Task Configure_is_refused_while_Gaming()
    {
        using ExternalTorrentClientTransport torrent = NewTorrent();
        _gaming = true;
        await Assert.ThrowsAsync<GameDownloadException>(() => torrent.ConfigureAsync(_client.Port, "admin", "secret", CancellationToken.None));
        Assert.Equal(0, _client.Logins);
    }

    // ------------------------------------------------------------------ state mapping

    [Theory]
    [InlineData("error", GameDownloadStates.Failed)]
    [InlineData("missingFiles", GameDownloadStates.Failed)]
    [InlineData("downloading", GameDownloadStates.Running)]
    [InlineData("forcedDL", GameDownloadStates.Running)]
    [InlineData("metaDL", GameDownloadStates.Running)]
    [InlineData("stalledDL", GameDownloadStates.Running)]
    [InlineData("checkingDL", GameDownloadStates.Running)]
    [InlineData("allocating", GameDownloadStates.Running)]
    [InlineData("queuedDL", GameDownloadStates.Queued)]
    [InlineData("pausedDL", GameDownloadStates.Paused)]
    [InlineData("stoppedDL", GameDownloadStates.Paused)]
    [InlineData("uploading", GameDownloadStates.Completed)]
    [InlineData("stalledUP", GameDownloadStates.Completed)]
    [InlineData("pausedUP", GameDownloadStates.Completed)]
    [InlineData("stoppedUP", GameDownloadStates.Completed)]
    [InlineData("moving", GameDownloadStates.Running)]
    [InlineData("unknown", GameDownloadStates.Running)]
    public void Client_states_map_onto_download_states(string state, string expected) =>
        Assert.Equal(expected, ExternalTorrentClientTransport.MapState(state));

    // ------------------------------------------------------------------ end to end

    [Fact]
    public async Task Magnet_download_completes_is_rehashed_and_cleaned_up()
    {
        _client.Content = Bytes(50_000);
        _client.States = new() { "metaDL", "downloading", "queuedDL", "downloading", "uploading" };
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet + "&dn=Secret+Name&tr=udp%3A%2F%2Ftracker.example%3A1%2Fannounce", _dir,
            FileName: "game.bin", ExpectedSha256: Sha(_client.Content), Kind: GameDownloadKinds.Magnet));
        Assert.Equal("magnet/" + Hash, job.Source);
        var done = await WaitFor(manager, job.Id, GameDownloadStates.Completed);

        Assert.True(done.IntegrityChecked);
        Assert.Equal(Sha(_client.Content), done.VerifiedSha256);
        Assert.Equal(_client.Content, await File.ReadAllBytesAsync(Path.Combine(_dir, "game.bin")));
        Assert.DoesNotContain(".parux-part", string.Join('|', Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName)));
        Assert.True(_client.Deleted, "the finished torrent entry we added is removed from the client");
        Assert.Contains("deleteFiles=false", _client.DeletedBody); // Client never recursively removes stage data itself.
        Assert.False(Directory.Exists(Path.Combine(_dir, ".parux-torrent-" + job.Id)));
        Assert.Contains("urls", _client.AddedBody);
        Assert.Contains("tracker.example", _client.AddedBody); // trackers go to the client, only there
    }

    [Fact]
    public async Task History_and_listing_never_contain_trackers_names_or_local_paths()
    {
        string history = Path.Combine(_dir, "history.json");
        _client.Content = Bytes(2_000);
        var (manager, _) = await MakeEnabled(history);
        var a = manager.Enqueue(new(Magnet + "&dn=Secret+Name&tr=udp%3A%2F%2Ftracker.example%3A1%2Fannounce", _dir, FileName: "a.bin", Kind: GameDownloadKinds.Magnet));
        await WaitFor(manager, a.Id, GameDownloadStates.Completed);
        string torrentPath = WriteTorrent(TorrentBytes("b.bin", 2_000));
        _client.Exists = false; _client.Deleted = false;
        var b = manager.Enqueue(new(torrentPath, _dir, FileName: "b.bin", Kind: GameDownloadKinds.TorrentFile));
        // The fake client reports the magnet's hash; use the torrent's own hash for this second job.
        Assert.StartsWith("torrent-file/", b.Source);
        Assert.DoesNotContain(_dir, b.Source);
        await Task.Delay(50);
        string text = await File.ReadAllTextAsync(history);
        Assert.DoesNotContain("tracker.example", text);
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("btih", text);
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), text);
        foreach (var d in manager.List()) Assert.DoesNotContain("tracker", d.Source + d.Reason);
    }

    [Fact]
    public async Task Torrent_file_is_uploaded_and_completes()
    {
        byte[] torrent = TorrentBytes("game.bin", 3000);
        string hash = GameDownloadSourceValidator.ValidateTorrentFile(WriteTorrent(torrent)).InfoHashHex;
        _client.Content = Bytes(3000);
        var (manager, _) = await MakeEnabled();
        // The fake reports one fixed hash; a torrent with a different info-hash is still driven by its own hash in the URL.
        var job = manager.Enqueue(new(Path.Combine(_dir, "x.torrent"), _dir, FileName: "game2.bin", Kind: GameDownloadKinds.TorrentFile));
        var done = await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        Assert.Equal(Sha(_client.Content), done.VerifiedSha256);
        Assert.Contains("torrents", _client.AddedBody);
        Assert.Equal(40, hash.Length);
    }

    [Fact]
    public async Task Wrong_expected_hash_after_client_completion_fails_deletes_the_file_and_trusts_nothing()
    {
        _client.Content = Bytes(10_000);
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", ExpectedSha256: new string('a', 64), Kind: GameDownloadKinds.Magnet));
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("SHA-256", failed.Reason);
        Assert.False(File.Exists(Path.Combine(_dir, "game.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "game.bin.parux-part")));
        Assert.False(failed.IntegrityChecked);
    }

    [Fact]
    public async Task Without_an_expected_hash_the_result_is_published_but_marked_unverified()
    {
        _client.Content = Bytes(1_000);
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        var done = await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        Assert.False(done.IntegrityChecked);
        Assert.Contains("not verified", done.Reason);
    }

    [Fact]
    public async Task Client_error_state_fails_the_job_and_removes_it_from_the_client()
    {
        _client.Content = Bytes(1_000);
        _client.States = new() { "downloading", "error" };
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("error", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(_client.Deleted);
    }

    [Fact]
    public async Task Multi_file_torrents_and_path_escapes_are_refused()
    {
        _client.Content = Bytes(1_000);
        _client.Files = new[] { "a.bin", "b.bin" };
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "m.bin", Kind: GameDownloadKinds.Magnet));
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("single-file", failed.Reason);

        _client.Files = new[] { @"..\..\escape.bin" };
        _client.Exists = false; _client.Deleted = false;
        var job2 = manager.Enqueue(new(Magnet, _dir, FileName: "n.bin", Kind: GameDownloadKinds.Magnet));
        var failed2 = await WaitFor(manager, job2.Id, GameDownloadStates.Failed);
        Assert.Contains("unexpected file path", failed2.Reason);
        Assert.False(File.Exists(Path.Combine(_dir, "n.bin")));
    }

    [Fact]
    public void External_client_never_resumes_from_a_linked_or_unowned_stage()
    {
        string stage = Path.Combine(_dir, ".parux-torrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string outside = Path.Combine(_dir, "outside.txt");
        File.WriteAllText(outside, "preserve");
        string link = Path.Combine(stage, "unsafe.txt");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { return; }
        Assert.Throws<GameDownloadException>(() => ExternalTorrentClientTransport.EnsureStaging(stage));
        ExternalTorrentClientTransport.CleanOwnedStaging(stage);
        Assert.True(Directory.Exists(stage));
        Assert.Equal("preserve", File.ReadAllText(outside));
        File.Delete(link);
        ExternalTorrentClientTransport.CleanOwnedStaging(stage);
        Assert.False(Directory.Exists(stage));
        Assert.Equal("preserve", File.ReadAllText(outside));
    }

    [Fact]
    public void External_client_staging_cleanup_is_strictly_owned_and_bounded()
    {
        string wrong = Path.Combine(_dir, "ordinary-folder");
        Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "keep.txt"), "keep");
        ExternalTorrentClientTransport.CleanOwnedStaging(wrong);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(wrong, "keep.txt")));
        string safe = Path.Combine(_dir, ".parux-torrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(safe, "nested", "content"));
        File.WriteAllText(Path.Combine(safe, "nested", "content", "piece.bin"), "fixture");
        ExternalTorrentClientTransport.EnsureStaging(safe);
        ExternalTorrentClientTransport.CleanOwnedStaging(safe);
        Assert.False(Directory.Exists(safe));
    }

    [Fact]
    public async Task Persistent_client_failure_is_retried_a_bounded_number_of_times_then_fails_honestly()
    {
        _client.FailInfo = 1000;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("torrent client", failed.Reason);
        Assert.Equal(3, _client.InfoCalls); // one attempt plus two bounded retries
    }

    [Fact]
    public async Task Session_expiry_logs_in_again_transparently()
    {
        _client.Content = Bytes(500);
        var (manager, _) = await MakeEnabled();
        int loginsAtStart = _client.Logins;
        Assert.True(loginsAtStart >= 1);
        _client.Expire();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        Assert.True(_client.Logins > loginsAtStart);
    }

    [Fact]
    public async Task Cancel_removes_the_torrent_from_the_client_and_leaves_no_file()
    {
        _client.Content = Bytes(1_000);
        _client.States = new() { "downloading" };
        _client.Progress = 0.1;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitUntil(() => _client.InfoCalls >= 2);
        Assert.True(manager.CancelOrRemove(job.Id));
        await WaitFor(manager, job.Id, GameDownloadStates.Cancelled);
        await WaitUntil(() => _client.Deleted);
        Assert.True(_client.Deleted);
        await manager.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(Path.Combine(_dir, "game.bin")));
    }

    [Fact]
    public async Task User_pause_pauses_the_client_and_resume_continues_the_same_torrent()
    {
        _client.Content = Bytes(1_000);
        _client.States = new() { "downloading" };
        _client.Progress = 0.1;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitUntil(() => _client.InfoCalls >= 2);
        Assert.True(manager.Pause(job.Id));
        await WaitFor(manager, job.Id, GameDownloadStates.Paused);
        await WaitUntil(() => _client.PauseCalled);

        _client.States = new() { "downloading", "uploading" };
        Assert.True(manager.Resume(job.Id));
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        Assert.True(_client.ResumeCalled);
        Assert.Equal(1, _client.Paths.Count(p => p == "/api/v2/torrents/add"));
    }

    [Fact]
    public async Task Newer_clients_that_use_stop_and_start_are_driven_through_the_fallback_routes()
    {
        _client.V5 = true;
        _client.Content = Bytes(1_000);
        _client.States = new() { "downloading" };
        _client.Progress = 0.1;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitUntil(() => _client.InfoCalls >= 2);
        manager.Pause(job.Id);
        await WaitFor(manager, job.Id, GameDownloadStates.Paused);
        await WaitUntil(() => _client.Saw("/api/v2/torrents/stop"));
        _client.States = new() { "uploading" };
        manager.Resume(job.Id);
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        Assert.True(_client.StartRoute);
    }

    [Fact]
    public async Task Gaming_hard_off_stops_the_client_and_pauses_the_job_and_refuses_new_ones()
    {
        _client.Content = Bytes(1_000);
        _client.States = new() { "downloading" };
        _client.Progress = 0.1;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitUntil(() => _client.InfoCalls >= 2);

        _gaming = true;
        var paused = await WaitFor(manager, job.Id, GameDownloadStates.Paused);
        Assert.Contains("game", paused.Reason, StringComparison.OrdinalIgnoreCase);
        await WaitUntil(() => _client.PauseCalled);
        int callsWhileGaming = _client.InfoCalls;
        await Task.Delay(100);
        Assert.Equal(callsWhileGaming, _client.InfoCalls); // no further polling during Gaming
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(new(Magnet, _dir, FileName: "other.bin", Kind: GameDownloadKinds.Magnet)));

        _gaming = false;
        _client.States = new() { "uploading" };
        await manager.ResumeGamingPausedAsync(CancellationToken.None);
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
    }

    [Fact]
    public async Task Pausing_inside_the_client_pauses_the_job_without_deleting_anything()
    {
        _client.Content = Bytes(1_000);
        _client.States = new() { "pausedDL" };
        _client.Progress = 0.3;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        var paused = await WaitFor(manager, job.Id, GameDownloadStates.Paused);
        Assert.Contains("torrent client", paused.Reason);
        Assert.False(_client.Deleted);
    }

    [Fact]
    public async Task A_torrent_that_vanishes_from_the_client_fails_the_job()
    {
        _client.States = new() { "downloading" };
        _client.Progress = 0.1;
        var (manager, _) = await MakeEnabled();
        var job = manager.Enqueue(new(Magnet, _dir, FileName: "game.bin", Kind: GameDownloadKinds.Magnet));
        await WaitUntil(() => _client.InfoCalls >= 2);
        _client.Deleted = true;
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("removed", failed.Reason);
    }

    [Fact]
    public async Task Existing_http_requests_without_a_kind_still_work_as_http()
    {
        // Back-compat at the JSON boundary: an old client sends no Kind.
        var req = System.Text.Json.JsonSerializer.Deserialize<GameDownloadRequest>(
            "{\"Url\":\"https://x.example/a.zip\",\"DestinationDirectory\":\"C:\\\\x\"}")!;
        Assert.Equal(GameDownloadKinds.Http, req.Kind);
        await Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 15)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("condition never became true");
    }
}
