using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Local, bounded owned-child tests. No real speech model, internet, game,
/// driver, user application or unrelated process is touched.
/// </summary>
public sealed class SpeechProcessAdmissionTests
{
    [Fact]
    public void Gaming_blocks_late_requests_and_invalidates_tickets_after_exit()
    {
        bool gaming = false;
        var admission = new SpeechProcessAdmission(() => gaming);
        long first = admission.Capture();

        gaming = true;
        Assert.Throws<InvalidOperationException>(() => admission.Capture());
        admission.BlockForGaming();
        gaming = false;
        Assert.Throws<InvalidOperationException>(() => admission.Capture());

        admission.ReopenAfterGaming();
        Assert.Throws<OperationCanceledException>(() => admission.Check(first));
        long second = admission.Capture();
        admission.Check(second);
        Assert.NotEqual(first, second);

        gaming = true;
        admission.ReopenAfterGaming(); // Caller cannot reopen while Gaming is still on.
        Assert.Throws<InvalidOperationException>(() => admission.Capture());
    }

    [Fact]
    public async Task Gaming_stop_cannot_miss_child_started_before_registration()
    {
        var admission = new SpeechProcessAdmission(() => false);
        long generation = admission.Capture();
        using var process = NewDisposableChild();
        using var startEntered = new ManualResetEventSlim();
        using var registerNow = new ManualResetEventSlim();

        try
        {
            // Deliberately hold the admission lock after OS process creation and before
            // StartOwned records ownership; Gaming Stop must wait for that registration.
            Task start = Task.Run(() => admission.StartOwned(process, generation, child =>
            {
                bool launched = child.Start();
                startEntered.Set();
                if (!registerNow.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Start registration gate never opened.");
                return launched;
            }));
            Assert.True(startEntered.Wait(TimeSpan.FromSeconds(10)));
            Task stop = Task.Run(admission.BlockForGaming);
            Assert.False(stop.IsCompleted);
            registerNow.Set();
            await start.WaitAsync(TimeSpan.FromSeconds(15));
            await stop.WaitAsync(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
            Assert.Throws<InvalidOperationException>(() => admission.Capture());
        }
        finally
        {
            registerNow.Set();
            admission.BlockForGaming();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            admission.Unregister(process);
        }
    }

    [Fact]
    public void Stale_pre_gaming_ticket_must_not_start_a_new_child_after_reopen()
    {
        var admission = new SpeechProcessAdmission(() => false);
        long old = admission.Capture();
        admission.BlockForGaming();
        admission.ReopenAfterGaming();
        using var child = NewDisposableChild();
        Assert.Throws<OperationCanceledException>(() => admission.StartOwned(child, old));
        Assert.Equal(0, admission.TrackedCount);
    }

    private static Process NewDisposableChild()
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
}
