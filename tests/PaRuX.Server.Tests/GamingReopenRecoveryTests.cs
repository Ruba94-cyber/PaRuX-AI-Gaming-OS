using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// H1: AI admission must reopen after Gaming ends even when the single reopen at the transition could not complete
/// (marker flap, a disposed owned Process, an owned child that had not exited yet).
/// </summary>
public sealed class GamingReopenRecoveryTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-reopen-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public GamingReopenRecoveryTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Disposed_owned_process_entry_does_not_block_reopen()
    {
        var admission = new SpeechProcessAdmission(() => false);
        Process child = NewChild();
        admission.StartOwned(child, admission.Capture());
        admission.BlockForGaming();      // kills and confirms exit
        child.Dispose();                 // the caller's "using var process" disposes it; the entry is still registered

        Assert.True(admission.ReopenAfterGaming());
        Assert.Equal(0, admission.TrackedCount);
        admission.Capture();
    }

    [Fact]
    public void Disposed_owned_process_entry_is_not_reported_as_unconfirmed_exit()
    {
        var admission = new SpeechProcessAdmission(() => false);
        Process child = NewChild();
        admission.StartOwned(child, admission.Capture());
        child.Kill(entireProcessTree: true);
        child.WaitForExit(5000);
        child.Dispose();

        Assert.True(admission.BlockForGaming());
        Assert.Equal(0, admission.TrackedCount);
    }

    [Fact]
    public void Reopen_is_idempotent_and_does_not_invalidate_healthy_tickets()
    {
        var admission = new SpeechProcessAdmission(() => false);
        long ticket = admission.Capture();
        Assert.True(admission.ReopenAfterGaming());
        Assert.True(admission.ReopenAfterGaming());
        admission.Check(ticket); // would throw if each reopen bumped the generation
    }

    [Fact]
    public async Task Marker_flap_between_the_tick_and_the_reopen_recovers_on_a_later_tick()
    {
        bool gaming = true;
        GamingGuardService guard = NewGuard(() => gaming, out LocalRuntimeService runtime);
        bool scopeSeesGaming = false;
        var scope = new GamingChildScope("test", () => scopeSeesGaming, runtime.HardOff);

        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // Gaming on: admission closes
        Assert.True(scope.IsClosed);

        gaming = false;
        scopeSeesGaming = true;                                           // flap: marker is back when the scope looks
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // transition tick; reopen refused
        Assert.True(scope.IsClosed);

        scopeSeesGaming = false;                                          // marker settles, no new transition
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.False(scope.IsClosed);
        scope.Capture();
    }

    [Fact]
    public async Task A_disposed_child_left_in_a_scope_ledger_does_not_keep_ai_denied_after_gaming()
    {
        bool gaming = false;
        GamingGuardService guard = NewGuard(() => gaming, out LocalRuntimeService runtime);
        var scope = new GamingChildScope("test", () => gaming, runtime.HardOff);
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);

        Process child = NewChild();
        GamingChildLease lease = GamingChildGate.StartOwned(child, CancellationToken.None, scope);
        gaming = true;
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // kills the child
        lease.Dispose();
        child.Dispose();                                                  // caller's using var

        gaming = false;
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.False(scope.IsClosed);
        Assert.Equal(0, scope.TrackedCount);
        scope.Capture();
    }

    [Fact]
    public void A_genuine_failure_is_relabelled_as_Gaming_only_when_the_scope_issued_the_stop()
    {
        bool gaming = false;
        using var scope = new GamingChildScope("test", () => gaming, null);
        long ticket = scope.Capture();
        var failure = new InvalidOperationException("exit code 3");

        gaming = true;   // the marker is raised, but this scope has not stopped anything yet
        Assert.False(scope.IsGamingInterruption(failure, CancellationToken.None, ticket));

        scope.StopForGaming();
        Assert.True(scope.IsGamingInterruption(failure, CancellationToken.None, ticket));
        Assert.False(scope.IsGamingInterruption(failure, new CancellationToken(true), ticket)); // a caller cancel is never rewritten
    }

    private static Process NewChild()
    {
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/c ping -n 30 127.0.0.1 >nul";
        }
        else
        {
            info.FileName = "/bin/sleep";
            info.ArgumentList.Add("30");
        }
        return new Process { StartInfo = info };
    }

    private static GamingGuardService NewGuard(Func<bool> gamingMode, out LocalRuntimeService runtime)
    {
        var process = new LocalAiProcessManager(0);
        runtime = new LocalRuntimeService(new FakeHost(), process, new ExternalTextProviderService());
        return new GamingGuardService(runtime, new MediaJobService(null!), new SpeechService(),
            gamingMode, _ => Task.FromResult(true));
    }

    private sealed class FakeHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
