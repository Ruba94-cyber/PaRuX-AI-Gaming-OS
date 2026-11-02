using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>A local HttpListener stands in for the file host: no real network, no real game files.</summary>
public sealed class GameDownloadManagerTests : IDisposable
{
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public byte[] Data = Array.Empty<byte>();
        public string ETag = "\"v1\"";
        public bool SupportRange = true;
        public bool UnsolicitedPartial; // malicious/nonconforming host answers 206 to a plain GET
        public bool DropAfterFirstHalf;       // closes the connection mid-body once
        public int Requests, RangeRequests;
        public string? RedirectTo;
        public int FailFirst;                 // answer 503 this many times
        public int Port { get; }

        public TestServer()
        {
            // A failed Start() leaves that HttpListener instance aborted/disposed, so every
            // attempt gets a brand-new listener instead of re-using (and re-touching) the old one.
            while (true)
            {
                int port = Random.Shared.Next(20000, 60000);
                var candidate = new HttpListener();
                try
                {
                    candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                    candidate.Start();
                    _listener = candidate;
                    Port = port;
                    break;
                }
                catch (HttpListenerException) { try { candidate.Close(); } catch { } }
            }
            _ = Task.Run(Loop);
        }

        public string Url(string path = "file.bin") => $"http://127.0.0.1:{Port}/{path}";

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is ObjectDisposedException or HttpListenerException or InvalidOperationException) { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                Interlocked.Increment(ref Requests);
                if (RedirectTo is not null && ctx.Request.Url!.AbsolutePath == "/redirect")
                { ctx.Response.StatusCode = 302; ctx.Response.Headers["Location"] = RedirectTo; ctx.Response.Close(); return; }
                if (Interlocked.Decrement(ref FailFirst) >= 0) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }

                long start = 0;
                string? range = ctx.Request.Headers["Range"];
                string? ifRange = ctx.Request.Headers["If-Range"];
                bool partial = SupportRange && range is not null && range.StartsWith("bytes=") && (ifRange is null || ifRange == ETag);
                if (partial) { Interlocked.Increment(ref RangeRequests); start = long.Parse(range!["bytes=".Length..].TrimEnd('-')); }
                if (start >= Data.Length) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }

                ctx.Response.Headers["ETag"] = ETag;
                long length = Data.Length - start;
                if (partial || UnsolicitedPartial)
                {
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{Data.Length - 1}/{Data.Length}";
                }
                ctx.Response.ContentLength64 = length;
                if (DropAfterFirstHalf)
                {
                    DropAfterFirstHalf = false;
                    ctx.Response.OutputStream.Write(Data, (int)start, (int)(length / 2));
                    ctx.Response.OutputStream.Flush();
                    ctx.Response.Abort();
                    return;
                }
                ctx.Response.OutputStream.Write(Data, (int)start, (int)length);
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            try { _listener.Close(); } catch (ObjectDisposedException) { }
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-dl-" + Guid.NewGuid().ToString("N"));
    private readonly TestServer _server = new();
    private bool _gaming;

    public GameDownloadManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Bytes(int n)
    {
        var data = new byte[n];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    private GameDownloadManager Make(long free = long.MaxValue, string? history = null) =>
        new(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan },
            () => _gaming, _ => free, history, new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) });

    private async Task<GameDownloadDto> WaitFor(GameDownloadManager m, string id, string state, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var dto = m.List().First(d => d.Id == id);
            if (dto.State == state) return dto;
            await Task.Delay(15);
        }
        throw new TimeoutException($"download never reached '{state}': {m.List().First(d => d.Id == id).State} {m.List().First(d => d.Id == id).Reason}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".identity")]
    [InlineData(".validator")]
    public async Task Linked_partial_or_resume_sidecar_is_refused_before_any_target_is_modified(string suffix)
    {
        // The user-selected directory may contain pre-existing data controlled by another process.
        // Nothing outside the expected managed filename may be changed by the download manager.
        _server.Data = Bytes(16_384);
        string outside = Path.Combine(_dir, "sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "keep-original");
        string linked = Path.Combine(_dir, "file.bin.parux-part" + suffix);
        try
        {
            File.CreateSymbolicLink(linked, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some locked-down Windows configurations do not permit creating test symlinks.
            return;
        }

        using var manager = Make();
        var job = manager.Enqueue(new(_server.Url(), _dir));
        var failed = await WaitFor(manager, job.Id, GameDownloadStates.Failed);
        Assert.Contains("linked", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep-original", await File.ReadAllTextAsync(outside));
        Assert.False(File.Exists(Path.Combine(_dir, "file.bin")));
    }

    [Fact]
    public async Task Dangling_download_partial_link_cannot_create_a_file_outside_the_destination()
    {
        _server.Data = Bytes(4096);
        string outside = Path.Combine(_dir, "should-not-be-created.bin");
        string linked = Path.Combine(_dir, "file.bin.parux-part");
        try
        {
            File.CreateSymbolicLink(linked, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Test fixture cannot create links on this Windows configuration.
            return;
        }

        using var manager = Make();
        var failed = await WaitFor(manager,
            manager.Enqueue(new(_server.Url(), _dir)).Id, GameDownloadStates.Failed);
        Assert.Contains("linked", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(_dir, "file.bin")));
    }

    [Fact]
    public async Task Integrity_verification_is_interrupted_as_soon_as_gaming_is_detected()
    {
        byte[] source = Bytes(3 * 1024 * 1024);
        string part = Path.Combine(_dir, "hash-interruption.parux-part");
        await File.WriteAllBytesAsync(part, source);
        int checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GameDownloadManager.HashFileAsync(
                part,
                () => Interlocked.Increment(ref checks) >= 2,
                CancellationToken.None));
        Assert.True(checks >= 2);
        Assert.True(File.Exists(part)); // pausing never deletes partial data
        Assert.Equal(source.Length, new FileInfo(part).Length);

        // When Gaming ends, the same bytes are checked without needing another download.
        string digest = await GameDownloadManager.HashFileAsync(part, () => false, CancellationToken.None);
        Assert.Equal(Sha(source), digest);
    }

    [Fact]
    public async Task Integrity_verification_honors_a_pre_cancelled_token()
    {
        string part = Path.Combine(_dir, "cancel-hash.parux-part");
        await File.WriteAllBytesAsync(part, Bytes(65_536));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GameDownloadManager.HashFileAsync(part, () => false, cancelled.Token));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public async Task Downloads_a_file_and_verifies_the_expected_checksum()
    {
        _server.Data = Bytes(300_000);
        using var m = Make();
        var job = m.Enqueue(new(_server.Url(), _dir, ExpectedSha256: Sha(_server.Data)));
        var done = await WaitFor(m, job.Id, GameDownloadStates.Completed);
        Assert.True(done.IntegrityChecked);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file.bin")));
        Assert.Empty(Directory.GetFiles(_dir, "*.parux-part*"));
    }

    [Fact]
    public async Task Completed_download_can_be_inspected_by_id_only_and_is_rechecked_against_its_original_bytes()
    {
        _server.Data = Bytes(64_000);
        using var manager = Make();
        var job = manager.Enqueue(new(_server.Url("installer.zip"), _dir,
            ExpectedSha256: Sha(_server.Data)));
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        var inspector = new PaRuX.Server.Orchestration.Gaming.GameInstallerInspectionService(() => false);
        var inspected = await manager.InspectCompletedAsync(job.Id, inspector, CancellationToken.None);
        Assert.Equal("installer.zip", inspected.FileName);
        Assert.Equal(Sha(_server.Data), inspected.Sha256);
        Assert.Contains("not a publisher authenticity check", inspected.Advisory);

        // A previously completed download can be changed by the user or another program;
        // a fresh hash must not silently attest to the original transfer.
        await File.AppendAllTextAsync(Path.Combine(_dir, "installer.zip"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InspectCompletedAsync(job.Id, inspector, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InspectCompletedAsync("not-a-job", inspector, CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_download_with_an_independently_supplied_digest_may_stage_a_checked_copy()
    {
        _server.Data = Bytes(42_000);
        string stagedFolder = Path.Combine(_dir, "managed-staging");
        var stage = new PaRuX.Server.Orchestration.Gaming.GamePackageStageService(
            () => _gaming, () => CancellationToken.None, stagedFolder);
        using var manager = Make();
        var withoutExpected = manager.Enqueue(new(_server.Url("unchecked.zip"), _dir));
        await WaitFor(manager, withoutExpected.Id, GameDownloadStates.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StageCompletedAsync(withoutExpected.Id, stage, CancellationToken.None));
        Assert.False(Directory.Exists(stagedFolder));

        var verified = manager.Enqueue(new(_server.Url("checked.zip"), _dir,
            ExpectedSha256: Sha(_server.Data)));
        await WaitFor(manager, verified.Id, GameDownloadStates.Completed);
        var staged = await manager.StageCompletedAsync(verified.Id, stage, CancellationToken.None);
        Assert.Equal("checked.zip", staged.FileName);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(staged.StagedPath));
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "checked.zip")));
        Assert.Single(Directory.GetFiles(stagedFolder, "game-*.zip"));
        _gaming = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StageCompletedAsync(verified.Id, stage, CancellationToken.None));
        Assert.Single(Directory.GetFiles(stagedFolder, "game-*.zip"));
    }

    [Fact]
    public async Task Mutated_finished_download_is_not_published_as_a_staged_package()
    {
        _server.Data = Bytes(4096);
        using var manager = Make();
        var job = manager.Enqueue(new(_server.Url("mutated.zip"), _dir,
            ExpectedSha256: Sha(_server.Data)));
        await WaitFor(manager, job.Id, GameDownloadStates.Completed);
        await File.AppendAllTextAsync(Path.Combine(_dir, "mutated.zip"), "changed");
        string folder = Path.Combine(_dir, "verified-copies");
        var stage = new PaRuX.Server.Orchestration.Gaming.GamePackageStageService(
            () => false, () => CancellationToken.None, folder);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.StageCompletedAsync(job.Id, stage, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(folder, "game-*.zip"));
    }

    [Fact]
    public async Task Completed_download_inspection_does_not_persist_an_absolute_file_path()
    {
        _server.Data = Bytes(16_000);
        string history = Path.Combine(_dir, "history.json");
        string id;
        using (var manager = Make(history: history))
        {
            id = manager.Enqueue(new(_server.Url("package.zip"), _dir)).Id;
            await WaitFor(manager, id, GameDownloadStates.Completed);
        }
        Assert.DoesNotContain(_dir, await File.ReadAllTextAsync(history));
        using var reopened = Make(history: history);
        var inspector = new PaRuX.Server.Orchestration.Gaming.GameInstallerInspectionService(() => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.InspectCompletedAsync(id, inspector, CancellationToken.None));
    }

    [Fact]
    public async Task Without_an_expected_checksum_the_file_is_hashed_but_reported_as_unverified()
    {
        _server.Data = Bytes(50_000);
        using var m = Make();
        var done = await WaitFor(m, m.Enqueue(new(_server.Url(), _dir)).Id, GameDownloadStates.Completed);
        Assert.False(done.IntegrityChecked);
        Assert.Equal(Sha(_server.Data), done.VerifiedSha256);
        Assert.Contains("not verified", done.Reason);
    }

    [Fact]
    public async Task A_wrong_checksum_deletes_the_file_and_fails_the_job()
    {
        _server.Data = Bytes(40_000);
        using var m = Make();
        var failed = await WaitFor(m, m.Enqueue(new(_server.Url(), _dir, ExpectedSha256: new string('0', 64))).Id, GameDownloadStates.Failed);
        Assert.Contains("SHA-256", failed.Reason);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Unrelated_partial_download_is_discarded_before_a_new_request()
    {
        _server.Data = Bytes(180_000);
        string partial = Path.Combine(_dir, "file.bin.parux-part");
        await File.WriteAllBytesAsync(partial, Bytes(42_000));
        await File.WriteAllTextAsync(partial + ".identity", "v1:" + new string('0', 64));
        await File.WriteAllTextAsync(partial + ".validator", "\"wrong-etag\"");
        using var manager = Make();
        var done = await WaitFor(manager,
            manager.Enqueue(new(_server.Url(), _dir, ExpectedSha256: Sha(_server.Data))).Id,
            GameDownloadStates.Completed);
        Assert.True(done.IntegrityChecked);
        Assert.Equal(0, _server.RangeRequests);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file.bin")));
        Assert.False(File.Exists(partial + ".identity"));
    }

    [Fact]
    public async Task A_paused_download_preserves_a_source_fingerprint_without_persisting_its_url()
    {
        _server.Data = Bytes(1_000_000);
        string part = Path.Combine(_dir, "file.bin.parux-part");
        string secretQuery = "?download_token=private-fixture";
        using (var manager = Make())
        {
            manager.SetBandwidthLimit(180_000);
            var job = manager.Enqueue(new(_server.Url() + secretQuery, _dir));
            await WaitFor(manager, job.Id, GameDownloadStates.Running);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!File.Exists(part) || new FileInfo(part).Length == 0) && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(manager.Pause(job.Id));
            await WaitFor(manager, job.Id, GameDownloadStates.Paused);
            Assert.True(File.Exists(part + ".identity"));
            string identity = await File.ReadAllTextAsync(part + ".identity");
            Assert.DoesNotContain("private-fixture", identity);
            Assert.DoesNotContain("download_token", identity);
            Assert.Equal(67, identity.Length);
        }

        using var resumed = Make();
        var done = await WaitFor(resumed,
            resumed.Enqueue(new(_server.Url() + secretQuery, _dir, ExpectedSha256: null)).Id,
            GameDownloadStates.Completed);
        Assert.False(done.IntegrityChecked);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file.bin")));
        Assert.True(_server.RangeRequests >= 1);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_with_a_range_request_and_ends_byte_exact()
    {
        _server.Data = Bytes(400_000);
        _server.DropAfterFirstHalf = true;
        using var m = Make();
        var done = await WaitFor(m, m.Enqueue(new(_server.Url(), _dir, ExpectedSha256: Sha(_server.Data))).Id, GameDownloadStates.Completed);
        Assert.True(done.IntegrityChecked);
        Assert.True(_server.RangeRequests >= 1, "the retry must continue from the bytes already on disk");
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file.bin")));
    }

    [Fact]
    public async Task A_file_that_changed_on_the_server_restarts_instead_of_mixing_versions()
    {
        _server.Data = Bytes(200_000);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "file.bin.parux-part"), _server.Data.AsSpan(0, 50_000).ToArray());
        await File.WriteAllTextAsync(Path.Combine(_dir, "file.bin.parux-part.validator"), "\"old-version\"");
        using var m = Make();
        var done = await WaitFor(m, m.Enqueue(new(_server.Url(), _dir, ExpectedSha256: Sha(_server.Data))).Id, GameDownloadStates.Completed);
        Assert.True(done.IntegrityChecked);
    }

    [Fact]
    public async Task An_unsolicited_206_is_not_published_as_a_complete_file()
    {
        _server.Data = Bytes(80_000);
        _server.UnsolicitedPartial = true;
        using var manager = Make();
        var failed = await WaitFor(manager,
            manager.Enqueue(new(_server.Url(), _dir)).Id, GameDownloadStates.Failed);
        Assert.Contains("partial response", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_dir, "file.bin")));
        Assert.Empty(Directory.GetFiles(_dir, "*.parux-part*"));
    }

    [Fact]
    public async Task Transient_server_errors_are_retried()
    {
        _server.Data = Bytes(30_000);
        _server.FailFirst = 2;
        using var m = Make();
        await WaitFor(m, m.Enqueue(new(_server.Url(), _dir)).Id, GameDownloadStates.Completed);
    }

    [Fact]
    public async Task Cancel_removes_the_partial_file_and_pause_keeps_it()
    {
        _server.Data = Bytes(2_000_000);
        using var m = Make();
        m.SetBandwidthLimit(150_000); // slow enough to still be running
        var job = m.Enqueue(new(_server.Url(), _dir));
        await WaitFor(m, job.Id, GameDownloadStates.Running);
        await Task.Delay(300);
        Assert.True(m.Pause(job.Id));
        var paused = await WaitFor(m, job.Id, GameDownloadStates.Paused);
        Assert.True(paused.BytesDone > 0);
        Assert.True(File.Exists(Path.Combine(_dir, "file.bin.parux-part")));

        m.SetBandwidthLimit(0);
        Assert.True(m.Resume(job.Id));
        await WaitFor(m, job.Id, GameDownloadStates.Completed);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file.bin")));

        var second = m.Enqueue(new(_server.Url("other.bin"), _dir));
        m.SetBandwidthLimit(50_000);
        Assert.True(m.CancelOrRemove(second.Id));
        await Task.Delay(300);
        Assert.Empty(Directory.GetFiles(_dir, "other.bin*"));
        Assert.Equal(GameDownloadStates.Cancelled, m.List().First(d => d.Id == second.Id).State);
    }

    [Fact]
    public async Task Bandwidth_limit_slows_the_transfer()
    {
        _server.Data = Bytes(300_000);
        using var m = Make();
        m.SetBandwidthLimit(200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitFor(m, m.Enqueue(new(_server.Url(), _dir)).Id, GameDownloadStates.Completed);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1.0), $"300 KB at 200 KB/s took only {sw.Elapsed}");
    }

    [Fact]
    public async Task Priority_and_concurrency_decide_the_start_order()
    {
        _server.Data = Bytes(400_000);
        using var m = Make();
        m.MaxConcurrent = 1;
        m.SetBandwidthLimit(300_000);
        var first = m.Enqueue(new(_server.Url("a.bin"), _dir));
        var low = m.Enqueue(new(_server.Url("b.bin"), _dir, Priority: 0));
        var high = m.Enqueue(new(_server.Url("c.bin"), _dir, Priority: 5));
        Assert.Equal(1, m.List().Count(d => d.State == GameDownloadStates.Running));
        m.SetBandwidthLimit(0);
        await WaitFor(m, first.Id, GameDownloadStates.Completed);
        await WaitFor(m, high.Id, GameDownloadStates.Completed);
        await WaitFor(m, low.Id, GameDownloadStates.Completed);
        var finished = m.List().OrderBy(d => d.FinishedUtc).Select(d => d.Id).ToArray();
        Assert.True(Array.IndexOf(finished, high.Id) < Array.IndexOf(finished, low.Id), "higher priority must finish first");
    }

    [Fact]
    public void Enqueue_refuses_a_Gaming_transition_between_admission_and_queue_lock()
    {
        int reads = 0;
        using var manager = new GameDownloadManager(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
            () => Interlocked.Increment(ref reads) >= 2,
            _ => long.MaxValue, null, Array.Empty<TimeSpan>());

        Assert.Throws<GameDownloadException>(() =>
            manager.Enqueue(new(_server.Url("never-started.zip"), _dir)));
        Assert.Empty(manager.List());
        Assert.Equal(2, reads);
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public async Task Pre_cancelled_Gaming_resume_leaves_queue_untouched()
    {
        using var manager = Make();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ResumeGamingPausedAsync(stop.Token));
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Immediate_cancel_of_many_queued_download_workers_remains_terminal()
    {
        _server.Data = Bytes(8192);
        using var manager = Make();
        manager.MaxConcurrent = 1;
        var ids = new List<string>();
        for (int i = 0; i < 25; i++)
        {
            var item = manager.Enqueue(new(_server.Url("cancel-" + i + ".zip"), _dir));
            ids.Add(item.Id);
            Assert.True(manager.CancelOrRemove(item.Id));
        }
        await Task.Delay(150);
        Assert.All(manager.List().Where(d => ids.Contains(d.Id)),
            d => Assert.Equal(GameDownloadStates.Cancelled, d.State));
    }

    [Fact]
    public async Task A_running_game_blocks_new_work_pauses_running_work_and_resumption_is_explicit()
    {
        _server.Data = Bytes(2_000_000);
        using var m = Make();
        m.SetBandwidthLimit(200_000);
        var job = m.Enqueue(new(_server.Url(), _dir));
        await WaitFor(m, job.Id, GameDownloadStates.Running);
        await Task.Delay(200);
        _gaming = true;
        var paused = await WaitFor(m, job.Id, GameDownloadStates.Paused);
        Assert.Contains("game", paused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url("x.bin"), _dir)));
        Assert.False(m.Resume(job.Id));                         // refused while the game runs
        Assert.Equal(0, await m.ResumeGamingPausedAsync(CancellationToken.None));

        _gaming = false;
        m.SetBandwidthLimit(0);
        Assert.Equal(1, await m.ResumeGamingPausedAsync(CancellationToken.None));
        await WaitFor(m, job.Id, GameDownloadStates.Completed);
    }

    [Fact]
    public async Task A_user_pause_is_not_undone_by_the_end_of_a_game()
    {
        _server.Data = Bytes(1_000_000);
        using var m = Make();
        m.SetBandwidthLimit(100_000);
        var job = m.Enqueue(new(_server.Url(), _dir));
        await WaitFor(m, job.Id, GameDownloadStates.Running);
        m.Pause(job.Id);
        await WaitFor(m, job.Id, GameDownloadStates.Paused);
        Assert.Equal(0, await m.ResumeGamingPausedAsync(CancellationToken.None));
        Assert.Equal(GameDownloadStates.Paused, m.List().First(d => d.Id == job.Id).State);
    }

    [Fact]
    public async Task Redirects_are_followed_but_must_obey_the_same_rules()
    {
        _server.Data = Bytes(20_000);
        _server.RedirectTo = _server.Url("file.bin");
        using var m = Make();
        await WaitFor(m, m.Enqueue(new(_server.Url("redirect"), _dir, FileName: "r.bin")).Id, GameDownloadStates.Completed);

        _server.RedirectTo = "http://example.com/plain"; // downgrade to a non-loopback http address
        var failed = await WaitFor(m, m.Enqueue(new(_server.Url("redirect"), _dir, FileName: "r2.bin")).Id, GameDownloadStates.Failed);
        Assert.Contains("https", failed.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/a.bin")]
    [InlineData("http://example.com/a.bin")]
    [InlineData("https://user:pass@example.com/a.bin")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("not a url")]
    public void Unsafe_addresses_are_refused(string url) =>
        Assert.Throws<GameDownloadException>(() => GameDownloadManager.ValidateUrl(url));

    [Fact]
    public void Destination_and_names_are_validated()
    {
        using var m = Make();
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url(), "relative")));
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url(), @"\\server\share")));
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url(), Path.Combine(_dir, "missing"))));
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url(), _dir, ExpectedSha256: "abc")));
        Assert.Null(GameDownloadManager.SafeFileName("con.txt"));
        Assert.Null(GameDownloadManager.SafeFileName("x.parux-part"));
        Assert.Null(GameDownloadManager.SafeFileName("   "));
        Assert.Equal("evil.exe", GameDownloadManager.SafeFileName(@"..\..\evil.exe"));
        Assert.Equal("a_b.bin", GameDownloadManager.SafeFileName("a_b.bin"));
    }

    [Fact]
    public async Task Existing_file_is_never_overwritten_and_the_same_target_cannot_run_twice()
    {
        _server.Data = Bytes(10_000);
        File.WriteAllText(Path.Combine(_dir, "file.bin"), "keep me");
        using var m = Make();
        m.SetBandwidthLimit(5_000);
        var job = m.Enqueue(new(_server.Url(), _dir));
        Assert.Throws<GameDownloadException>(() => m.Enqueue(new(_server.Url(), _dir)));
        m.SetBandwidthLimit(0);
        await WaitFor(m, job.Id, GameDownloadStates.Completed);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_dir, "file.bin")));
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(Path.Combine(_dir, "file (1).bin")));
    }

    [Fact]
    public async Task Disk_space_and_size_limits_fail_the_job_before_the_data_is_written()
    {
        _server.Data = Bytes(5_000);
        using (var small = Make(free: 100))
        {
            var failed = await WaitFor(small, small.Enqueue(new(_server.Url(), _dir, FileName: "s.bin")).Id, GameDownloadStates.Failed);
            Assert.Contains("disk space", failed.Reason);
        }
        using var capped = Make();
        capped.MaxFileBytes = 1_000;
        var tooBig = await WaitFor(capped, capped.Enqueue(new(_server.Url(), _dir, FileName: "t.bin")).Id, GameDownloadStates.Failed);
        Assert.Contains("larger", tooBig.Reason);
        Assert.Empty(Directory.GetFiles(_dir, "*.bin"));
    }

    [Fact]
    public async Task History_keeps_finished_jobs_without_query_strings_and_can_be_cleared()
    {
        _server.Data = Bytes(5_000);
        string history = Path.Combine(_dir, "history.json");
        string id;
        using (var m = Make(history: history))
        {
            id = m.Enqueue(new(_server.Url("file.bin?token=SECRET"), _dir)).Id;
            await WaitFor(m, id, GameDownloadStates.Completed);
        }
        string saved = File.ReadAllText(history);
        Assert.DoesNotContain("SECRET", saved);
        Assert.DoesNotContain("token", saved);

        using var reopened = Make(history: history);
        var dto = Assert.Single(reopened.List());
        Assert.Equal(GameDownloadStates.Completed, dto.State);
        Assert.DoesNotContain("SECRET", dto.Source);
        Assert.True(reopened.CancelOrRemove(id));
        Assert.Empty(reopened.List());
    }
}
