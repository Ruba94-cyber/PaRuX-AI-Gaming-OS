using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gaming hard-off for the shared child boundary used by the avatar engine, the CLIP judge and every managed installer,
/// converter and one-shot AI child. The children are tiny PowerShell sleepers, the Gaming signal is an injected flag driven
/// exactly like the guard drives it (stop through the registry, then reopen).
/// </summary>
public sealed class GamingChildScopeHardOffTests : IDisposable
{
    private readonly GamingHardOffRegistry _registry = new();
    private readonly List<Process> _started = new();
    private bool _gaming;
    private int _spawnCount;

    public GamingChildScopeHardOffTests()
    {
        Assert.True(OperatingSystem.IsWindows(), "The fake children use Windows PowerShell.");
    }

    public void Dispose()
    {
        _gaming = true;
        _registry.StopAll();
        foreach (Process process in _started)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private GamingChildScope NewScope(string label = "test") => new(label, () => _gaming, _registry);

    private Process NewSleeper()
    {
        Interlocked.Increment(ref _spawnCount);
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("Start-Sleep -Seconds 120");
        var process = new Process { StartInfo = info };
        _started.Add(process);
        return process;
    }

    private bool StartGaming()
    {
        _gaming = true;
        return _registry.StopAll();
    }

    private void StopGaming()
    {
        _gaming = false;
        _registry.ReopenAll();
    }

    [Fact]
    public void NoSpawnWhileGamingIsActive_AndTheRefusalIsTyped()
    {
        GamingChildScope scope = NewScope();
        _gaming = true;
        Process child = NewSleeper();
        var refused = Assert.Throws<UnavailableDuringGamingException>(
            () => GamingChildGate.StartOwned(child, CancellationToken.None, scope));
        Assert.Contains("Gaming", refused.Message);
        Assert.Throws<InvalidOperationException>(() => child.Id); // never started
        Assert.Equal(0, scope.TrackedCount);
    }

    [Fact]
    public async Task GamingStart_KillsTheRegisteredChild_AndTheLeaseReportsTheTypedError()
    {
        GamingChildScope scope = NewScope();
        Process child = NewSleeper();
        using GamingChildLease lease = GamingChildGate.StartOwned(child, CancellationToken.None, scope);
        Assert.False(child.HasExited);

        Assert.True(StartGaming(), "The child must confirm exit.");

        await child.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.True(lease.Token.IsCancellationRequested);
        Assert.Throws<UnavailableDuringGamingException>(() => lease.ThrowIfGamingInterrupted());
    }

    [Fact]
    public async Task CallerCancel_IsNotRewrittenAsGaming()
    {
        GamingChildScope scope = NewScope();
        Process child = NewSleeper();
        using var cts = new CancellationTokenSource();
        using GamingChildLease lease = GamingChildGate.StartOwned(child, cts.Token, scope);
        cts.Cancel();
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        lease.ThrowIfGamingInterrupted(); // no exception: the caller cancelled, Gaming did not
    }

    [Fact]
    public async Task AfterGaming_NewChildrenAreAdmittedAgain_AndNothingStartsByItself()
    {
        GamingChildScope scope = NewScope();
        Process first = NewSleeper();
        using (GamingChildLease lease = GamingChildGate.StartOwned(first, CancellationToken.None, scope))
        {
            Assert.True(StartGaming());
            await first.WaitForExitAsync();
        }
        StopGaming();
        Assert.Equal(1, _spawnCount);

        Process second = NewSleeper();
        using GamingChildLease again = GamingChildGate.StartOwned(second, CancellationToken.None, scope);
        Assert.False(second.HasExited);
        Assert.Equal(2, _spawnCount);
    }

    [Fact]
    public async Task RunAsync_InFlightWork_GetsTheTypedError_NotAGenericCancellation()
    {
        GamingChildScope scope = NewScope();
        Task<int> work = scope.RunAsync<int>(CancellationToken.None, async (token, _) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        });
        await Task.Delay(100);
        Assert.True(StartGaming());
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));

        // A caller cancel stays a plain cancellation.
        StopGaming();
        using var cts = new CancellationTokenSource();
        Task<int> cancelled = scope.RunAsync<int>(cts.Token, async (token, _) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void DisposedScope_LeavesTheRegistry()
    {
        GamingChildScope scope = NewScope();
        Assert.Equal(1, _registry.Count);
        scope.Dispose();
        Assert.Equal(0, _registry.Count);
    }

    // ----- avatar engine --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Avatar_DuringGaming_ReportsPausedWithoutSpawning_AndGenerationIsTyped()
    {
        GamingChildScope scope = NewScope("avatar");
        var engine = new AvatarEngineService(new SpeechService(), scope);
        _gaming = true;
        scope.StopForGaming();

        AvatarEngineStatus status = await engine.GetStatusAsync(CancellationToken.None);
        Assert.False(status.Installed);
        Assert.Contains("Gaming", status.LastError);
        Assert.True(engine.GamingPaused);
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => engine.GenerateAsync("hello", CancellationToken.None));
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => engine.InstallEngineAsync(CancellationToken.None));
        Assert.Equal(0, scope.TrackedCount);

        StopGaming();
        Assert.False(engine.GamingPaused);
    }

    // ----- CLIP judge -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClipJudge_DuringGaming_RefusesScoringAndInstallWithoutSpawning()
    {
        GamingChildScope scope = NewScope("semantic judge");
        await using var judge = new ManagedClipImageJudge(scope);
        _gaming = true;
        var asset = new MediaAsset { FileName = "a.png", MediaType = "image/png", Base64Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };

        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => judge.ScoreAsync("a cat", asset, CancellationToken.None));
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => judge.InstallAsync(null, CancellationToken.None));
        Assert.Equal(0, scope.TrackedCount);
    }

    // ----- LoRA conversion: no half-applied output ---------------------------------------------------------------------

    [Fact]
    public async Task LoraConvert_DuringGaming_IsRefusedBeforeAnyChild_AndLeavesThePreviousGgufUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-lora-gaming-" + Guid.NewGuid().ToString("N"));
        try
        {
            string scripts = Path.Combine(root, "scripts");
            string runtime = Path.Combine(root, "runtime");
            string adapter = Path.Combine(root, "adapter");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(Path.Combine(runtime, ".venv", "Scripts"));
            Directory.CreateDirectory(adapter);
            File.WriteAllText(Path.Combine(scripts, "convert_lora_to_gguf.py"), "# synthetic");
            File.WriteAllText(Path.Combine(runtime, ".venv", "Scripts", "python.exe"), "not a real interpreter");
            string output = Path.Combine(adapter, "adapter.gguf");
            File.WriteAllText(output, "previous-good-gguf");

            GamingChildScope scope = NewScope("install/conversion");
            _gaming = true;
            var service = new LoraGgufConversionService(runtime, scripts);
            using (GamingChildGate.UseForCurrentFlow(scope))
            {
                await Assert.ThrowsAsync<UnavailableDuringGamingException>(() =>
                    service.ConvertAsync(adapter, "synthetic/base", output, "f16", CancellationToken.None));
            }

            Assert.Equal("previous-good-gguf", File.ReadAllText(output));
            Assert.Empty(Directory.GetFiles(adapter, "*.partial.gguf"));
            Assert.Equal(0, scope.TrackedCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
