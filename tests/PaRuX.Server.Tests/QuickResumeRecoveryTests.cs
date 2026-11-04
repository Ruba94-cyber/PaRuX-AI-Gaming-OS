using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickResumeRecoveryTests
{
    private sealed class FakeSuspend : IGameSuspendProvider
    {
        public List<int> Suspended { get; } = new();
        public List<int> Resumed { get; } = new();
        public HashSet<int> FailResume { get; } = new();
        public string Id => "fake";
        public string Name => "fake";
        public bool IsAvailable => true;
        public Task<bool> SuspendProcessAsync(int processId, CancellationToken ct) { Suspended.Add(processId); return Task.FromResult(true); }
        public Task<bool> ResumeProcessAsync(int processId, CancellationToken ct)
        {
            Resumed.Add(processId);
            return Task.FromResult(!FailResume.Contains(processId));
        }
    }

    private static string NewJournal() => Path.Combine(Path.GetTempPath(), "parux-qrr-" + Guid.NewGuid().ToString("N"), "paused.json");

    private static Process StartSleeper(string arguments = "-n 60 127.0.0.1") =>
        Process.Start(new ProcessStartInfo("ping", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    private static void WriteJournal(string path, object entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    private static void Kill(params Process[] processes)
    {
        foreach (Process p in processes) { try { p.Kill(entireProcessTree: true); } catch { } p.Dispose(); }
    }

    private static bool IsFrozen(int pid)
    {
        using Process p = Process.GetProcessById(pid);
        return p.Threads.Cast<ProcessThread>().All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int seconds = 10)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    // ---------- recovery with fakes ----------

    [Fact]
    public async Task Recovery_resumes_a_journalled_process_whose_identity_still_verifies_and_reports_it()
    {
        string journal = NewJournal();
        using Process child = StartSleeper();
        try
        {
            WriteJournal(journal, new[] { new { ProcessId = child.Id, StartedUtc = child.StartTime.ToUniversalTime(), ProcessName = "ping" } });
            var fake = new FakeSuspend();
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => Array.Empty<QuickResumeChild>());

            IReadOnlyList<QuickResumeRecoveryDto> outcomes = await service.RecoverAsync("startup", CancellationToken.None);

            Assert.Equal(new[] { child.Id }, fake.Resumed);
            QuickResumeRecoveryDto only = Assert.Single(outcomes);
            Assert.Equal("resumed", only.Outcome);
            Assert.Equal("startup", only.Trigger);
            Assert.DoesNotContain("\"ProcessId\":" + child.Id, File.ReadAllText(journal));
            QuickResumeStatusDto status = service.List();
            Assert.Contains(status.Recovery!, r => r.ProcessId == child.Id && r.Outcome == "resumed"); // surfaced to the UI list
        }
        finally { Kill(child); }
    }

    [Fact]
    public async Task Recovery_never_touches_a_process_that_reused_the_pid_or_is_gone()
    {
        string journal = NewJournal();
        using Process other = StartSleeper();
        try
        {
            DateTime wrongStart = other.StartTime.ToUniversalTime().AddHours(-3); // same pid, different start: not the process PaRuX paused
            WriteJournal(journal, new[]
            {
                new { ProcessId = other.Id, StartedUtc = wrongStart, ProcessName = "ping" },
                new { ProcessId = 2147000000, StartedUtc = DateTime.UtcNow.AddHours(-1), ProcessName = "gone" }
            });
            var fake = new FakeSuspend();
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => Array.Empty<QuickResumeChild>());

            IReadOnlyList<QuickResumeRecoveryDto> outcomes = await service.RecoverAsync("startup", CancellationToken.None);

            Assert.Empty(fake.Resumed);
            Assert.All(outcomes, o => Assert.Equal("already-closed", o.Outcome));
            Assert.False(other.HasExited);
        }
        finally { Kill(other); }
    }

    [Fact]
    public async Task A_failed_recovery_keeps_the_entry_and_is_reported_as_failed_instead_of_pretending()
    {
        string journal = NewJournal();
        using Process child = StartSleeper();
        try
        {
            WriteJournal(journal, new[] { new { ProcessId = child.Id, StartedUtc = child.StartTime.ToUniversalTime(), ProcessName = "ping" } });
            var fake = new FakeSuspend();
            fake.FailResume.Add(child.Id);
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => Array.Empty<QuickResumeChild>());

            QuickResumeRecoveryDto o = Assert.Single(await service.RecoverAsync("startup", CancellationToken.None));

            Assert.Equal("failed", o.Outcome);
            Assert.Contains("\"ProcessId\":" + child.Id, File.ReadAllText(journal)); // still journalled for a manual retry
        }
        finally { Kill(child); }
    }

    [Fact]
    public async Task Recovery_with_an_empty_or_missing_journal_does_nothing()
    {
        var fake = new FakeSuspend();
        var service = new QuickResumeService(NewJournal(), provider: fake);
        Assert.Empty(await service.RecoverAsync("shutdown", CancellationToken.None));
        Assert.Empty(fake.Resumed);
    }

    // ---------- process group with fakes ----------

    [Fact]
    public async Task Suspending_pauses_the_verified_children_and_resuming_brings_them_all_back()
    {
        string journal = NewJournal();
        using Process parent = StartSleeper();
        using Process kid = StartSleeper();
        try
        {
            var fake = new FakeSuspend();
            var children = new[] { new QuickResumeChild(kid.Id, kid.StartTime.ToUniversalTime(), "ping") };
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => children);
            DateTime started = parent.StartTime.ToUniversalTime();

            QuickResumeActionResult paused = await service.SuspendAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None, requireWindow: false);
            Assert.True(paused.Succeeded, paused.Message);
            Assert.Equal(new[] { parent.Id, kid.Id }, fake.Suspended); // parent first so it cannot spawn more
            Assert.Contains("1 related process", paused.Message);

            QuickResumeActionResult resumed = await service.ResumeAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None);
            Assert.True(resumed.Succeeded, resumed.Message);
            Assert.Equal(new[] { kid.Id, parent.Id }, fake.Resumed); // children first, then the main process
            Assert.False(File.ReadAllText(journal).Contains("ProcessId"));
        }
        finally { Kill(parent, kid); }
    }

    [Fact]
    public async Task A_child_that_cannot_be_resumed_stays_journalled_and_the_result_says_so()
    {
        string journal = NewJournal();
        using Process parent = StartSleeper();
        using Process kid = StartSleeper();
        try
        {
            var fake = new FakeSuspend();
            fake.FailResume.Add(kid.Id);
            var children = new[] { new QuickResumeChild(kid.Id, kid.StartTime.ToUniversalTime(), "ping") };
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => children);
            DateTime started = parent.StartTime.ToUniversalTime();
            await service.SuspendAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None, requireWindow: false);

            QuickResumeActionResult resumed = await service.ResumeAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None);

            Assert.False(resumed.Succeeded);
            Assert.Contains("could not be resumed", resumed.Message);
            Assert.Contains("\"ProcessId\":" + kid.Id, File.ReadAllText(journal));
        }
        finally { Kill(parent, kid); }
    }

    [Fact]
    public async Task A_child_whose_pid_was_reused_is_not_resumed()
    {
        string journal = NewJournal();
        using Process parent = StartSleeper();
        using Process stranger = StartSleeper();
        try
        {
            var fake = new FakeSuspend();
            // Journalled "child" has the stranger's pid but a different start time: identity check must refuse to touch it.
            var children = new[] { new QuickResumeChild(stranger.Id, stranger.StartTime.ToUniversalTime().AddHours(-2), "ping") };
            var service = new QuickResumeService(journal, provider: fake, childFinder: (_, _) => children);
            DateTime started = parent.StartTime.ToUniversalTime();
            QuickResumeActionResult paused = await service.SuspendAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None, requireWindow: false);

            Assert.DoesNotContain(stranger.Id, fake.Suspended);
            Assert.Contains("not paused", paused.Message);
            await service.ResumeAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None);
            Assert.DoesNotContain(stranger.Id, fake.Resumed);
        }
        finally { Kill(parent, stranger); }
    }

    [Fact]
    public async Task A_system_process_is_still_refused_even_when_no_window_is_required()
    {
        var fake = new FakeSuspend();
        var service = new QuickResumeService(NewJournal(), provider: fake);
        using Process self = Process.GetCurrentProcess();
        QuickResumeActionResult own = await service.SuspendAsync(new QuickResumeActionRequest(self.Id, self.StartTime.ToUniversalTime()), CancellationToken.None, requireWindow: false);
        Assert.False(own.Succeeded);
        Assert.Empty(fake.Suspended);
        Process? winlogon = Process.GetProcessesByName("lsass").FirstOrDefault();
        if (winlogon is not null)
        {
            using (winlogon)
            {
                DateTime start;
                try { start = winlogon.StartTime.ToUniversalTime(); } catch (System.ComponentModel.Win32Exception) { start = DateTime.UtcNow; } // not inspectable without elevation
                QuickResumeActionResult r = await service.SuspendAsync(new QuickResumeActionRequest(winlogon.Id, start), CancellationToken.None, requireWindow: false);
                Assert.False(r.Succeeded);
                Assert.Empty(fake.Suspended);
            }
        }
    }

    // ---------- pure tree selection ----------

    [Fact]
    public void Tree_selection_follows_grandchildren_skips_protected_self_reused_parents_and_ignores_unrelated()
    {
        DateTime t0 = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
        var table = new List<(int, int)>
        {
            (300, 200), // grandchild listed before its parent
            (200, 100), // child
            (210, 100), // protected child (explorer)
            (220, 100), // started before the root: parent id reused, not a child
            (230, 999), // unrelated
            (240, 100), // is PaRuX itself
            (250, 100)  // exited between snapshot and identification
        };
        var info = new Dictionary<int, (string, DateTime)>
        {
            [200] = ("launcher", t0.AddSeconds(5)),
            [300] = ("game", t0.AddSeconds(8)),
            [210] = ("explorer", t0.AddSeconds(6)),
            [220] = ("old", t0.AddMinutes(-5)),
            [230] = ("other", t0.AddSeconds(7)),
            [240] = ("PaRuX.Server", t0.AddSeconds(7))
        };

        IReadOnlyList<QuickResumeChild> result = QuickResumeProcessTree.SelectDescendants(table, 100, t0, selfPid: 240,
            pid => info.TryGetValue(pid, out var v) ? v : null);

        Assert.Equal(new[] { 200, 300 }, result.Select(c => c.ProcessId));
    }

    // ---------- real throw-away processes ----------

    [Fact]
    public async Task Real_process_suspended_journalled_and_recovered_by_a_new_service_instance()
    {
        if (!OperatingSystem.IsWindows()) return;
        string journal = NewJournal();
        using Process victim = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command Start-Sleep 60") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            DateTime started = victim.StartTime.ToUniversalTime();
            var first = new QuickResumeService(journal, childFinder: (_, _) => Array.Empty<QuickResumeChild>());
            QuickResumeActionResult paused = await first.SuspendAsync(new QuickResumeActionRequest(victim.Id, started), CancellationToken.None, requireWindow: false);
            Assert.True(paused.Succeeded, paused.Message);
            Assert.True(await WaitUntil(() => IsFrozen(victim.Id)), "the process should be frozen");
            Assert.Contains("\"ProcessId\":" + victim.Id, File.ReadAllText(journal));

            // "Crash": the first service instance is dropped; a fresh one (Core restart) reads the same journal.
            var restarted = new QuickResumeService(journal);
            IReadOnlyList<QuickResumeRecoveryDto> outcomes = await restarted.RecoverAsync("startup", CancellationToken.None);

            Assert.Equal("resumed", Assert.Single(outcomes).Outcome);
            Assert.True(await WaitUntil(() => !IsFrozen(victim.Id)), "the process should run again");
            Assert.False(victim.HasExited);
            Assert.DoesNotContain("\"ProcessId\"", File.ReadAllText(journal));
        }
        finally { Kill(victim); }
    }

    [Fact]
    public async Task Real_parent_and_child_tree_is_paused_together_and_resumed_together()
    {
        if (!OperatingSystem.IsWindows()) return;
        string journal = NewJournal();
        using Process parent = Process.Start(new ProcessStartInfo("cmd", "/c ping -n 60 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            DateTime started = parent.StartTime.ToUniversalTime();
            Assert.True(await WaitUntil(() => QuickResumeProcessTree.FindDescendants(parent.Id, started).Count > 0), "cmd should have spawned ping");
            QuickResumeChild child = QuickResumeProcessTree.FindDescendants(parent.Id, started).First();
            Assert.Equal("PING", child.ProcessName, ignoreCase: true);

            var service = new QuickResumeService(journal);
            QuickResumeActionResult paused = await service.SuspendAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None, requireWindow: false);
            Assert.True(paused.Succeeded, paused.Message);
            Assert.True(await WaitUntil(() => IsFrozen(parent.Id) && IsFrozen(child.ProcessId)), "parent and child should both be frozen");
            Assert.Contains("\"ProcessId\":" + child.ProcessId, File.ReadAllText(journal)); // the child is journalled for recovery too

            QuickResumeActionResult resumed = await service.ResumeAsync(new QuickResumeActionRequest(parent.Id, started), CancellationToken.None);
            Assert.True(resumed.Succeeded, resumed.Message);
            Assert.True(await WaitUntil(() => !IsFrozen(parent.Id) && !IsFrozen(child.ProcessId)), "both should run again");
        }
        finally { Kill(parent); }
    }
}
