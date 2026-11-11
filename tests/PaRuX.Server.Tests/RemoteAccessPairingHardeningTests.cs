using System.Net;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// SEC-F09: paired-token expiry and re-pairing, bounded pairing lockout, single redemption of a code under concurrency, and an
/// atomic, non-lossy registry file. These tests use a fake clock and fake address/certificate seams, so they never touch the
/// machine's adapters, the LAN certificate or the real state directory.
/// </summary>
public sealed class RemoteAccessPairingHardeningTests : IDisposable
{
    private const string LanAddress = "192.168.77.5";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-sec-f09-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private DateTime _now = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    public RemoteAccessPairingHardeningTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "remote-access.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private void WriteRegistry(int idleDays = 0, int maxDays = 0) =>
        File.WriteAllText(_file, JsonSerializer.Serialize(new
        {
            enabled = true,
            address = LanAddress,
            port = 55100,
            certificateAddress = LanAddress,
            deviceTokenIdleDays = idleDays,
            deviceTokenMaxDays = maxDays,
            devices = Array.Empty<object>()
        }));

    private RemoteAccessService Open(int idleDays = 0, int maxDays = 0)
    {
        if (!File.Exists(_file)) WriteRegistry(idleDays, maxDays);
        return new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "FINGERPRINT");
    }

    private static PairingCompleteRequest Request(string code, string name = "Pixel 9") =>
        new() { Code = code, DeviceName = name };

    private static IPAddress Source(int n) => IPAddress.Parse($"192.168.77.{n}");

    private PairingCompleteResponse Pair(RemoteAccessService service, string name = "Pixel 9", int source = 20) =>
        service.CompletePairing(Request(service.StartPairing().Code, name), Source(source));

    private void Fail(RemoteAccessService service, int count, IPAddress source)
    {
        for (int i = 0; i < count; i++)
            Assert.ThrowsAny<UnauthorizedAccessException>(() => service.CompletePairing(Request("00000000"), source));
    }

    private static string Message(Action action) => Assert.ThrowsAny<UnauthorizedAccessException>(action).Message;

    // ---- (a) paired-token expiry -------------------------------------------------------------------------------------

    [Fact]
    public void Token_expires_after_the_idle_lifetime_and_is_reported_as_expired()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);
        Assert.Equal(90, RemoteAccessService.DefaultDeviceTokenIdleDays);

        _now = _now.AddDays(89);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _, out bool expired));
        Assert.False(expired);

        _now = _now.AddDays(90).AddMinutes(1);
        Assert.False(service.TryValidateDeviceToken(paired.DeviceToken, out string deviceId, out expired));
        Assert.True(expired);
        Assert.Equal(string.Empty, deviceId);
        Assert.False(service.IsDevicePaired(paired.DeviceId));
    }

    [Fact]
    public void Activity_slides_the_idle_window_but_never_past_the_absolute_lifetime()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);

        // Seen every 60 days: the idle limit (90) is never reached, so only the absolute 365-day cap can end it.
        for (int day = 60; day <= 360; day += 60)
        {
            _now = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(day);
            Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _), $"day {day}");
        }

        _now = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(366);
        Assert.False(service.TryValidateDeviceToken(paired.DeviceToken, out _, out bool expired));
        Assert.True(expired);
    }

    [Fact]
    public async Task Lifetimes_are_configurable_in_the_registry_and_idle_is_clamped_to_the_absolute_cap()
    {
        WriteRegistry(idleDays: 1, maxDays: 3);
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);

        _now = _now.AddHours(23);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
        _now = _now.AddHours(25);
        Assert.False(service.TryValidateDeviceToken(paired.DeviceToken, out _, out bool idleExpired));
        Assert.True(idleExpired);

        await service.WaitForLastSeenFlushAsync(); // the first service must be idle before the file is rewritten under it
        // An idle setting larger than the absolute one cannot outlive it.
        WriteRegistry(idleDays: 100, maxDays: 2);
        RemoteAccessService clamped = Open();
        PairingCompleteResponse second = Pair(clamped, "Second");
        _now = _now.AddDays(1);
        Assert.True(clamped.TryValidateDeviceToken(second.DeviceToken, out _));
        _now = _now.AddDays(1).AddMinutes(1);
        Assert.False(clamped.TryValidateDeviceToken(second.DeviceToken, out _));
    }

    [Fact]
    public void Expired_device_can_pair_again_with_the_normal_flow_and_stays_listed_as_expired()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse old = Pair(service, "Old phone");
        _now = _now.AddDays(100);
        Assert.False(service.TryValidateDeviceToken(old.DeviceToken, out _, out bool expired));
        Assert.True(expired);

        PairingCompleteResponse fresh = Pair(service, "Old phone");

        Assert.NotEqual(old.DeviceId, fresh.DeviceId);
        Assert.NotEqual(old.DeviceToken, fresh.DeviceToken);
        Assert.True(service.TryValidateDeviceToken(fresh.DeviceToken, out string freshId));
        Assert.Equal(fresh.DeviceId, freshId);
        Assert.False(service.TryValidateDeviceToken(old.DeviceToken, out _));
        string json = File.ReadAllText(_file);
        // The expired device is kept (never pruned on the pairing path) and flagged, so a clock jump cannot erase it.
        Assert.Contains(old.DeviceId, json, StringComparison.Ordinal);
        var devices = service.GetStatus().Devices;
        Assert.True(devices.Single(d => d.Id == old.DeviceId).Expired);
        Assert.False(devices.Single(d => d.Id == fresh.DeviceId).Expired);
        Assert.Contains(fresh.DeviceId, json, StringComparison.Ordinal);
        // The pairing response keeps the shape the Android client reads.
        Assert.Equal($"https://{LanAddress}:55100", fresh.Endpoint);
        Assert.Equal("FINGERPRINT", fresh.CertificateFingerprint);
        Assert.Equal(64, fresh.DeviceToken.Length);
    }

    [Fact]
    public void A_device_with_no_creation_time_fails_closed()
    {
        RemoteAccessService service = Open();
        service.ImportDevicesForBackup(new List<RemoteAccessService.PairedDevice>
        {
            new() { Id = "legacy", Name = "Legacy", TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("t"u8)) }
        });

        Assert.False(service.TryValidateDeviceToken("t", out _, out bool expired));
        Assert.True(expired);
    }

    [Fact]
    public async Task Last_seen_is_written_at_most_once_per_interval_and_survives_a_restart()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);
        string before = File.ReadAllText(_file);

        _now = _now.AddMinutes(5);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
        await service.WaitForLastSeenFlushAsync();
        Assert.Equal(before, File.ReadAllText(_file));

        _now = _now.AddMinutes(11);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
        await service.WaitForLastSeenFlushAsync();
        Assert.NotEqual(before, File.ReadAllText(_file));

        // Sliding state is on disk: a restarted server keeps the device alive from the flushed LastSeenUtc.
        _now = _now.AddDays(80);
        var restarted = new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "FINGERPRINT");
        Assert.True(restarted.TryValidateDeviceToken(paired.DeviceToken, out _));
    }

    [Fact]
    public async Task A_background_last_seen_write_cannot_bring_back_a_revoked_device()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);
        _now = _now.AddMinutes(20);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));

        Assert.True(service.Revoke(paired.DeviceId));
        await service.WaitForLastSeenFlushAsync();

        Assert.DoesNotContain(paired.DeviceId, File.ReadAllText(_file), StringComparison.Ordinal);
        Assert.False(new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F")
            .TryValidateDeviceToken(paired.DeviceToken, out _));
    }

    // ---- (b) bounded lockout / backoff -------------------------------------------------------------------------------

    [Fact]
    public void Source_lockout_backs_off_and_locked_attempts_do_not_extend_it()
    {
        RemoteAccessService service = Open();
        IPAddress attacker = Source(99);
        service.StartPairing();

        Fail(service, 10, attacker);
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));

        // Hammering while locked does not push the end out: the first lockout is one minute from the trip.
        _now = _now.AddSeconds(30);
        for (int i = 0; i < 20; i++) Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
        _now = _now.AddSeconds(30);
        Assert.Contains("invalid or expired", Message(() => service.CompletePairing(Request("00000000"), attacker)));

        // Tripping again soon after doubles the wait to two minutes.
        Fail(service, 9, attacker);
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
        _now = _now.AddSeconds(119);
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
        _now = _now.AddSeconds(1);
        Assert.Contains("invalid or expired", Message(() => service.CompletePairing(Request("00000000"), attacker)));
    }

    [Fact]
    public void Lockout_is_bounded_by_a_fifteen_minute_cap_and_forgotten_after_a_quiet_period()
    {
        RemoteAccessService service = Open();
        IPAddress attacker = Source(98);

        // 1, 2, 4, 8, 15, 15 minutes: the wait never exceeds the cap.
        int[] expectedMinutes = { 1, 2, 4, 8, 15, 15 };
        foreach (int minutes in expectedMinutes)
        {
            // The first attempt of a round is judged normally (the previous lockout is over), then the budget is filled.
            Assert.Contains("invalid or expired", Message(() => service.CompletePairing(Request("00000000"), attacker)));
            Fail(service, 9, attacker);
            Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
            _now = _now.AddMinutes(minutes).AddSeconds(-1);
            Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
            _now = _now.AddSeconds(1);
        }

        // After 30 quiet minutes the history is dropped and the next trip is a one-minute lockout again.
        _now = _now.AddMinutes(31);
        Fail(service, 10, attacker);
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), attacker)));
        _now = _now.AddMinutes(1);
        Assert.Contains("invalid or expired", Message(() => service.CompletePairing(Request("00000000"), attacker)));
    }

    [Fact]
    public void Global_lockout_blocks_distributed_guessing_without_consuming_the_code_and_the_owner_can_clear_it()
    {
        RemoteAccessService service = Open();
        string code = service.StartPairing().Code;
        for (int i = 1; i <= 200; i++)
            Assert.ThrowsAny<UnauthorizedAccessException>(() => service.CompletePairing(Request("00000000"), IPAddress.Parse($"10.20.{i / 250}.{i % 250}")));

        // The 201st attempt trips the global lockout, and even the correct code from a fresh address is turned away.
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), Source(1))));
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request(code), Source(2))));

        // The locked attempt did not spend the code: once the one-minute lockout ends it still redeems.
        _now = _now.AddMinutes(1);
        PairingCompleteResponse paired = service.CompletePairing(Request(code), Source(2));
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));

        // A second distributed burst inside the memory window locks for two minutes; the owner starting a new pairing clears it.
        for (int i = 1; i <= 200; i++)
            Assert.ThrowsAny<UnauthorizedAccessException>(() => service.CompletePairing(Request("00000000"), IPAddress.Parse($"10.30.{i / 250}.{i % 250}")));
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), Source(3))));
        _now = _now.AddSeconds(61);
        Assert.Contains("Too many", Message(() => service.CompletePairing(Request("00000000"), Source(3))));

        string next = service.StartPairing().Code;
        PairingCompleteResponse second = service.CompletePairing(Request(next, "Second"), Source(4));
        Assert.True(service.TryValidateDeviceToken(second.DeviceToken, out _));
    }

    // ---- concurrency -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_code_redeemed_concurrently_succeeds_exactly_once()
    {
        RemoteAccessService service = Open();
        string code = service.StartPairing().Code;
        const int Racers = 32;
        using var gate = new Barrier(Racers);
        var successes = new List<PairingCompleteResponse>();
        var rejections = new List<Exception>();

        // Dedicated threads (not the pool) so all racers really are in flight together.
        Thread[] threads = Enumerable.Range(0, Racers).Select(i => new Thread(() =>
        {
            gate.SignalAndWait();
            try
            {
                PairingCompleteResponse response = service.CompletePairing(Request(code, $"Phone {i}"), Source(100 + i));
                lock (successes) successes.Add(response);
            }
            catch (UnauthorizedAccessException exception)
            {
                lock (rejections) rejections.Add(exception);
            }
        })).ToArray();
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Single(successes);
        Assert.Equal(Racers - 1, rejections.Count);
        Assert.Single(service.GetStatus().Devices);
        Assert.True(service.TryValidateDeviceToken(successes[0].DeviceToken, out _));
        Assert.Single(new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F").GetStatus().Devices);
    }

    [Fact]
    public void Concurrent_pairings_of_different_codes_all_persist_without_corrupting_the_registry()
    {
        RemoteAccessService service = Open();
        const int Phones = 12;
        string[] codes = Enumerable.Range(0, Phones).Select(_ => service.StartPairing().Code).ToArray();
        var tokens = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, Phones, i => tokens.Add(service.CompletePairing(Request(codes[i], $"Phone {i}"), Source(30 + i)).DeviceToken));

        var reloaded = new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F");
        Assert.Equal(Phones, reloaded.GetStatus().Devices.Count);
        foreach (string token in tokens) Assert.True(reloaded.TryValidateDeviceToken(token, out _));
    }

    // ---- (c) persistence atomicity -----------------------------------------------------------------------------------

    [Fact]
    public void Saving_leaves_a_complete_registry_with_the_previous_one_as_backup_and_no_temporary_file()
    {
        RemoteAccessService service = Open();
        Pair(service, "First");
        Pair(service, "Second");

        Assert.False(File.Exists(_file + ".tmp"));
        Assert.True(File.Exists(_file + ".bak"));
        using JsonDocument current = JsonDocument.Parse(File.ReadAllText(_file));
        using JsonDocument previous = JsonDocument.Parse(File.ReadAllText(_file + ".bak"));
        Assert.Equal(2, current.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(1, previous.RootElement.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public void A_crash_that_left_a_truncated_temporary_file_does_not_affect_the_registry_or_the_next_save()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service, "Survivor");
        // What a crash mid-write leaves behind: half a JSON document next to the intact registry.
        File.WriteAllText(_file + ".tmp", "{\"enabled\":true,\"devices\":[{\"id\":\"half");

        var restarted = new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F");
        Assert.True(restarted.TryValidateDeviceToken(paired.DeviceToken, out _));

        PairingCompleteResponse second = Pair(restarted, "After crash");
        Assert.False(File.Exists(_file + ".tmp"));
        var again = new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F");
        Assert.True(again.TryValidateDeviceToken(paired.DeviceToken, out _));
        Assert.True(again.TryValidateDeviceToken(second.DeviceToken, out _));
    }

    [Fact]
    public void A_failed_write_records_no_device_keeps_the_registry_and_does_not_spend_the_code()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse kept = Pair(service, "Kept");
        string code = service.StartPairing().Code;
        string before = File.ReadAllText(_file);

        // Holding the temporary file open makes the write fail the way a full disk or a locked file would.
        using (new FileStream(_file + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => service.CompletePairing(Request(code, "Lost"), Source(40)));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
        }

        Assert.Equal(before, File.ReadAllText(_file));
        Assert.Single(service.GetStatus().Devices);
        Assert.True(service.TryValidateDeviceToken(kept.DeviceToken, out _));

        // The code was not spent by the failed attempt: the same code pairs once the file is writable again.
        PairingCompleteResponse retry = service.CompletePairing(Request(code, "Lost"), Source(40));
        Assert.True(service.TryValidateDeviceToken(retry.DeviceToken, out _));
        Assert.Equal(2, service.GetStatus().Devices.Count);
    }

    // ---- review follow-ups -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_last_seen_write_is_attempted_once_per_interval_not_once_per_request()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse paired = Pair(service);
        _now = _now.AddMinutes(20);

        using (new FileStream(_file + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
                await service.WaitForLastSeenFlushAsync();
            }
        }

        Assert.Equal(1, service.LastSeenFlushAttempts);
        // One interval later it tries again.
        _now = _now.AddMinutes(16);
        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
        await service.WaitForLastSeenFlushAsync();
        Assert.Equal(2, service.LastSeenFlushAttempts);
    }

    [Fact]
    public void A_forward_clock_jump_does_not_erase_devices_when_pairing_and_they_return_with_the_clock()
    {
        RemoteAccessService service = Open();
        PairingCompleteResponse early = Pair(service, "Early");
        DateTime real = _now;

        _now = real.AddYears(10);
        Assert.False(service.TryValidateDeviceToken(early.DeviceToken, out _));
        Pair(service, "During jump");
        _now = real.AddMinutes(1);

        var reloaded = new RemoteAccessService(_file, () => _now, a => a == LanAddress, () => "F");
        Assert.Contains(reloaded.GetStatus().Devices, d => d.Id == early.DeviceId);
        Assert.True(reloaded.TryValidateDeviceToken(early.DeviceToken, out _));
    }

    [Fact]
    public void Starting_a_pairing_clears_the_global_failure_counter_so_the_owner_is_not_refused_after_a_burst()
    {
        RemoteAccessService service = Open();
        service.StartPairing();
        for (int i = 1; i <= 200; i++)
            Assert.ThrowsAny<UnauthorizedAccessException>(() => service.CompletePairing(Request("00000000"), IPAddress.Parse($"10.40.{i / 250}.{i % 250}")));

        string code = service.StartPairing().Code;
        PairingCompleteResponse paired = service.CompletePairing(Request(code), Source(7));

        Assert.True(service.TryValidateDeviceToken(paired.DeviceToken, out _));
    }

    [Fact]
    public void Lockouts_raise_a_distinct_exception_with_retry_after_while_a_wrong_code_stays_a_plain_unauthorized()
    {
        RemoteAccessService service = Open();
        IPAddress attacker = Source(90);
        var wrong = Assert.ThrowsAny<UnauthorizedAccessException>(() => service.CompletePairing(Request("00000000"), attacker));
        Assert.IsNotType<RemoteAccessService.PairingLockedException>(wrong);

        Fail(service, 9, attacker);
        var locked = Assert.Throws<RemoteAccessService.PairingLockedException>(() => service.CompletePairing(Request("00000000"), attacker));
        Assert.Equal(TimeSpan.FromMinutes(1), locked.RetryAfter);
        Assert.Contains("Too many failed pairing attempts", locked.Message);
    }
}
