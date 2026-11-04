using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickResumeTests
{
    [Theory]
    [InlineData("csrss")]
    [InlineData("explorer")]
    [InlineData("lsass")]
    [InlineData("PaRuX")]
    [InlineData("PaRuX.Server")]
    [InlineData("llama-server")]
    [InlineData("")]
    public void Windows_security_shell_and_parux_processes_are_never_pausable(string name) =>
        Assert.NotNull(QuickResumePolicy.RefusalReason(name, "A window", sameSession: true, ownProcess: false));

    [Fact]
    public void An_ordinary_application_window_may_be_paused_but_not_without_a_window_or_from_another_session()
    {
        Assert.Null(QuickResumePolicy.RefusalReason("eden", "Eden", sameSession: true, ownProcess: false));
        Assert.NotNull(QuickResumePolicy.RefusalReason("eden", "", sameSession: true, ownProcess: false));
        Assert.NotNull(QuickResumePolicy.RefusalReason("eden", "Eden", sameSession: false, ownProcess: false));
        Assert.NotNull(QuickResumePolicy.RefusalReason("eden", "Eden", sameSession: true, ownProcess: true));
    }

    [Fact]
    public void A_process_is_the_same_only_when_the_start_time_matches()
    {
        DateTime start = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
        Assert.True(QuickResumePolicy.SameProcess(start, start.AddSeconds(1)));
        Assert.False(QuickResumePolicy.SameProcess(start, start.AddMinutes(5))); // pid reused by a different process
    }

    [Fact]
    public async Task Pausing_a_process_without_a_window_or_with_a_stale_identity_is_refused_and_nothing_is_journaled()
    {
        string journal = Path.Combine(Path.GetTempPath(), "parux-qr-" + Guid.NewGuid().ToString("N"), "paused.json");
        var service = new QuickResumeService(journal);
        using var child = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            DateTime started = child.StartTime.ToUniversalTime();
            QuickResumeActionResult noWindow = await service.SuspendAsync(new QuickResumeActionRequest(child.Id, started), CancellationToken.None);
            Assert.False(noWindow.Succeeded);
            QuickResumeActionResult stale = await service.SuspendAsync(new QuickResumeActionRequest(child.Id, started.AddHours(-1)), CancellationToken.None);
            Assert.False(stale.Succeeded);
            Assert.False(File.Exists(journal));
            Assert.False(child.HasExited); // never touched
        }
        finally { try { child.Kill(); } catch { } }
    }

    [Fact]
    public async Task Only_what_parux_paused_can_be_resumed()
    {
        string journal = Path.Combine(Path.GetTempPath(), "parux-qr-" + Guid.NewGuid().ToString("N"), "paused.json");
        var service = new QuickResumeService(journal);
        QuickResumeActionResult result = await service.ResumeAsync(new QuickResumeActionRequest(Environment.ProcessId, DateTime.UtcNow), CancellationToken.None);
        Assert.False(result.Succeeded);
        QuickResumeActionResult all = await service.ResumeAllAsync(CancellationToken.None);
        Assert.True(all.Succeeded); // nothing paused: nothing to fail
    }
}
