using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// GOS-09: Gaming hard-off for the STT/TTS warm workers. The children are tiny PowerShell scripts that speak the
/// warm-worker JSONL protocol, so no speech model, Python environment, game or user process is touched. The Gaming
/// signal is an injected flag plus a cancellation source, the same pair the production runtime exposes.
/// </summary>
public sealed class SpeechGamingHardOffTests : IDisposable
{
    private const string OkChild =
        "[Console]::Out.WriteLine('{\"ready\":true}'); " +
        "while (($l = [Console]::In.ReadLine()) -ne $null) { [Console]::Out.WriteLine('{\"ok\":true,\"text\":\"hi\"}') }";

    // Ready, then never answers a request.
    private const string HangingChild =
        "[Console]::Out.WriteLine('{\"ready\":true}'); " +
        "while (($l = [Console]::In.ReadLine()) -ne $null) { Start-Sleep -Seconds 120 }";

    // Takes a long time to report readiness (a slow model load).
    private const string SlowReadyChild =
        "Start-Sleep -Seconds 120; [Console]::Out.WriteLine('{\"ready\":true}')";

    private readonly List<int> _spawned = new();
    private readonly object _spawnGate = new();
    private int _spawnCount;
    private bool _gaming;
    private CancellationTokenSource _gamingCts = new();
    private readonly SpeechProcessAdmission _admission;
    private readonly Dictionary<string, SpeechWarmWorker> _workers = new();
    private string _childScript = OkChild;
    private bool _workersUseGamingToken;

    public SpeechGamingHardOffTests()
    {
        Assert.True(OperatingSystem.IsWindows(), "Fake speech children use Windows PowerShell.");
        _admission = new SpeechProcessAdmission(() => _gaming);
    }

    public void Dispose()
    {
        _gaming = true;
        _admission.BlockForGaming();
        lock (_spawnGate)
        {
            foreach (int pid in _spawned)
            {
                try { using Process p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }
        foreach (SpeechWarmWorker worker in _workers.Values) worker.Dispose();
        _gamingCts.Dispose();
    }

    private ProcessStartInfo FakeChildInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(_childScript);
        Interlocked.Increment(ref _spawnCount);
        return info;
    }

    private SpeechWarmWorker NewWorker(string mode = "transcribe") =>
        new(mode, "unused", "unused", _admission,
            _workersUseGamingToken ? () => _gamingCts.Token : null, FakeChildInfo);

    private SpeechService NewService(bool workersUseGamingToken = false)
    {
        _workersUseGamingToken = workersUseGamingToken;
        return new SpeechService(_admission, () => _gaming, () => _gamingCts.Token, mode =>
        {
            var worker = NewWorker(mode);
            lock (_spawnGate) _workers[mode] = worker;
            return worker;
        });
    }

    private static TranscribeRequest SampleRequest() => new()
    {
        AudioBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        FileName = "a.wav"
    };

    /// <summary>The Gaming start as the guard performs it: marker and token first, then the speech stop.</summary>
    private bool StartGaming(SpeechService service)
    {
        _gaming = true;
        _gamingCts.Cancel();
        return service.StopWorkersForGaming();
    }

    private void StopGaming(SpeechService service)
    {
        _gaming = false;
        _gamingCts.Dispose();
        _gamingCts = new CancellationTokenSource();
        service.AllowWorkersAfterGaming();
    }

    private async Task<int> WaitForChildAsync(string mode)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            SpeechWarmWorker? worker;
            lock (_spawnGate) _workers.TryGetValue(mode, out worker);
            if (worker?.ChildProcessId is { } pid)
            {
                lock (_spawnGate) _spawned.Add(pid);
                return pid;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The fake speech child never appeared.");
    }

    private async Task<int> WaitForChildAsync(SpeechWarmWorker worker)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (worker.ChildProcessId is { } pid)
            {
                lock (_spawnGate) _spawned.Add(pid);
                return pid;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The fake speech child never appeared.");
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task Gaming_start_stops_and_reaps_the_resident_child_and_confirms_exit()
    {
        SpeechService service = NewService();
        await service.PrewarmAsync(stt: true, tts: false, CancellationToken.None);
        int pid = await WaitForChildAsync("transcribe");
        Assert.Equal(1, _admission.TrackedCount);

        bool confirmed = StartGaming(service);

        Assert.True(confirmed);
        Assert.True(IsGone(pid), "The speech child must have exited after the Gaming hard-off.");
        Assert.Null(_workers["transcribe"].ChildProcessId);
        Assert.Equal(0, _admission.TrackedCount);
    }

    [Fact]
    public async Task In_flight_request_is_cancelled_with_the_typed_error_even_when_only_the_pipe_closes()
    {
        // Regression: a killed child used to surface as "closed its response stream unexpectedly" (a generic 503)
        // or a bare OperationCanceledException instead of the Gaming-unavailable result.
        _childScript = HangingChild;
        SpeechService service = NewService(workersUseGamingToken: false);
        Task<TranscribeResponse> inFlight = service.TranscribeAsync(SampleRequest(), CancellationToken.None);
        int pid = await WaitForChildAsync("transcribe");
        await Task.Delay(500); // let the request reach the child

        StartGaming(service);

        var error = await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("Gaming", error.Message);
        Assert.True(IsGone(pid));
        Assert.Equal(0, _admission.TrackedCount);
    }

    [Fact]
    public async Task Worker_cancels_an_in_flight_request_from_the_gaming_token_without_waiting_for_the_guard()
    {
        _childScript = HangingChild;
        _workersUseGamingToken = true;
        using SpeechWarmWorker tokenWorker = NewWorker();
        Task<System.Text.Json.JsonElement> inFlight = tokenWorker.ExecuteAsync(
            new { audioPath = "x" }, CancellationToken.None, TimeSpan.FromMinutes(3));
        int pid = await WaitForChildAsync(tokenWorker);
        await Task.Delay(500);

        // Only the runtime Gaming state flips; nothing calls Stop on the worker.
        _gaming = true;
        _gamingCts.Cancel();

        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(IsGone(pid));
        Assert.Null(tokenWorker.ChildProcessId);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_rewritten_as_a_gaming_error()
    {
        _childScript = HangingChild;
        SpeechService service = NewService();
        using var caller = new CancellationTokenSource();
        Task<TranscribeResponse> inFlight = service.TranscribeAsync(SampleRequest(), caller.Token);
        int pid = await WaitForChildAsync("transcribe");
        await Task.Delay(500);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(IsGone(pid));
    }

    [Fact]
    public async Task While_gaming_is_active_new_requests_fail_fast_and_nothing_respawns()
    {
        SpeechService service = NewService();
        StartGaming(service);
        int spawnsBefore = _spawnCount;

        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(
            () => service.TranscribeAsync(SampleRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(
            () => service.SynthesizeAsync(new SynthesizeRequest { Text = "hello" }, CancellationToken.None));
        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(
            () => service.PrewarmAsync(stt: true, tts: true, CancellationToken.None));

        Assert.Equal(spawnsBefore, _spawnCount);
        Assert.Equal(0, _admission.TrackedCount);
        Assert.True(typeof(InvalidOperationException).IsAssignableFrom(typeof(SpeechUnavailableDuringGamingException)));
    }

    [Fact]
    public async Task Hard_off_arriving_during_child_spawn_leaves_no_resident_child()
    {
        _childScript = SlowReadyChild;
        SpeechService service = NewService();
        Task warm = service.PrewarmAsync(stt: true, tts: false, CancellationToken.None);
        int pid = await WaitForChildAsync("transcribe"); // spawned, not ready yet

        StartGaming(service);

        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(() => warm.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(IsGone(pid));
        Assert.Null(_workers["transcribe"].ChildProcessId);
        Assert.Equal(0, _admission.TrackedCount);
    }

    [Fact]
    public async Task Spawn_that_would_complete_after_hard_off_is_refused_before_it_starts()
    {
        using SpeechWarmWorker worker = NewWorker();
        long staleTicket = _admission.Capture();

        _gaming = true;
        _admission.BlockForGaming();
        int spawnsBefore = _spawnCount;

        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(
            () => worker.PrewarmAsync(CancellationToken.None, TimeSpan.FromSeconds(30), staleTicket));
        Assert.Equal(spawnsBefore, _spawnCount);
        Assert.Null(worker.ChildProcessId);
        Assert.Equal(0, _admission.TrackedCount);
    }

    [Fact]
    public async Task Stale_ticket_from_before_gaming_cannot_spawn_after_gaming_has_ended()
    {
        // Regression: after Gaming stopped, a pre-Gaming request got a bare OperationCanceledException (an aborted
        // request) instead of the typed result, and must still never start a child.
        using SpeechWarmWorker worker = NewWorker();
        long staleTicket = _admission.Capture();
        _gaming = true;
        _admission.BlockForGaming();
        _gaming = false;
        _admission.ReopenAfterGaming();
        int spawnsBefore = _spawnCount;

        await Assert.ThrowsAsync<SpeechUnavailableDuringGamingException>(
            () => worker.PrewarmAsync(CancellationToken.None, TimeSpan.FromSeconds(30), staleTicket));
        Assert.Equal(spawnsBefore, _spawnCount);
        Assert.Null(worker.ChildProcessId);
    }

    [Fact]
    public async Task Speech_recovers_lazily_after_gaming_stops_without_an_automatic_warm_up()
    {
        SpeechService service = NewService();
        await service.PrewarmAsync(stt: true, tts: false, CancellationToken.None);
        int firstPid = await WaitForChildAsync("transcribe");
        StartGaming(service);
        Assert.True(IsGone(firstPid));
        int spawnsDuringGaming = _spawnCount;

        StopGaming(service);

        Assert.Equal(spawnsDuringGaming, _spawnCount); // nothing warmed itself up
        Assert.Null(_workers["transcribe"].ChildProcessId);
        TranscribeResponse response = await service.TranscribeAsync(SampleRequest(), CancellationToken.None);
        Assert.Equal("hi", response.Text);
        int secondPid = await WaitForChildAsync("transcribe");
        Assert.NotEqual(firstPid, secondPid);
        Assert.Equal(spawnsDuringGaming + 1, _spawnCount);
    }
}
