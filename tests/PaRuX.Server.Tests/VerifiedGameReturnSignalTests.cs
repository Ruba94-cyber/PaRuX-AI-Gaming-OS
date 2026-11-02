using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure file and freshness policy: no real process, foreground window,
/// local service, gamepad, GPU or gaming marker is touched.</summary>
public sealed class VerifiedGameReturnSignalTests
{
    [Fact]
    public void A_recent_matching_Core_verified_timestamp_is_required_for_Home_return()
    {
        DateTime now = DateTime.UtcNow;
        var signal = new VerifiedGameReturnSignal(42, now.AddSeconds(-3));
        Assert.True(VerifiedGameReturnSignal.IsRecentAndMatching(signal, signal.ExitUtc, now));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(signal, null, now));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(signal, now, now));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(
            signal, signal.ExitUtc, now.AddMinutes(3)));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(
            signal, signal.ExitUtc, now.AddMinutes(-1)));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(
            new VerifiedGameReturnSignal(0, signal.ExitUtc), signal.ExitUtc, now));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(
            new VerifiedGameReturnSignal(42, DateTime.SpecifyKind(signal.ExitUtc, DateTimeKind.Unspecified)),
            signal.ExitUtc, now));
        Assert.False(VerifiedGameReturnSignal.IsRecentAndMatching(null, signal.ExitUtc, now));
    }

    [Fact]
    public void Console_foreground_activation_requires_opt_in_open_console_and_verified_Core_receipt()
    {
        DateTime now = DateTime.UtcNow;
        var eventRecord = new VerifiedGameReturnSignal(19, now.AddSeconds(-1));
        var ready = new GamingStatusDto
        {
            State = "AiReady",
            IsGamingActive = false,
            LastVerifiedOwnedGameExitUtc = eventRecord.ExitUtc,
            LastVerifiedOwnedGameExitRestoredAi = true
        };
        Assert.True(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            false, true, false, false, ready, eventRecord, now)); // default off
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, false, false, false, ready, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, true, false, ready, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, true, ready, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, null, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready, null, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready,
            eventRecord with { ExitUtc = now.AddSeconds(-2) }, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready with { IsGamingActive = true }, eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready with { LastVerifiedOwnedGameExitRestoredAi = false },
            eventRecord, now));
        Assert.False(ConsoleAutoReturnPolicy.CanActivate(
            true, true, false, false, ready with { State = "RestoringAi" }, eventRecord, now));
    }

    [Fact]
    public void Atomic_file_round_trip_contains_only_session_number_and_UTC_exit_time()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-return-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = VerifiedGameReturnSignal.FileIn(root);
            var original = new VerifiedGameReturnSignal(12, DateTime.UtcNow);
            VerifiedGameReturnSignal.Publish(file, original);
            Assert.True(VerifiedGameReturnSignal.TryRead(file, out var read));
            Assert.Equal(original, read);
            string json = File.ReadAllText(file);
            Assert.Contains("SessionVersion", json);
            Assert.Contains("ExitUtc", json);
            Assert.DoesNotContain("Executable", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Pid", json, StringComparison.OrdinalIgnoreCase);
            Assert.Single(Directory.GetFiles(root)); // atomic write leaves no .tmp
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Missing_corrupt_and_oversized_receipts_fail_closed()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = VerifiedGameReturnSignal.FileIn(root);
            Assert.False(VerifiedGameReturnSignal.TryRead(file, out _));
            File.WriteAllText(file, "{bad-json");
            Assert.False(VerifiedGameReturnSignal.TryRead(file, out _));
            File.WriteAllText(file, new string('x', 1025));
            Assert.False(VerifiedGameReturnSignal.TryRead(file, out _));
            File.WriteAllText(file, """{"SessionVersion":-1,"ExitUtc":"2026-11-01T22:00:00Z"}""");
            Assert.False(VerifiedGameReturnSignal.TryRead(file, out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
