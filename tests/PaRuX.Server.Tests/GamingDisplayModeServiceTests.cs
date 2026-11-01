using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingDisplayModeServiceTests : IDisposable
{
    private const string Monitor = @"\\.\DISPLAY1";
    private readonly string _record = Path.Combine(Path.GetTempPath(), "parux-display-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() { try { File.Delete(_record); } catch { } }

    private sealed class FakeApi : IDisplayModeApi
    {
        public DisplayModeEntry Now = new(1920, 1080, 60, 32, 0, 0);
        public bool Accept = true, FailTemporary, FailPermanent, FailRevert;
        public readonly List<string> Log = new();
        public readonly List<DisplayModeEntry> Modes = new()
        {
            new(1920, 1080, 60, 32, 0, 0), new(3840, 2160, 60, 32, 0, 0), new(3840, 2160, 120, 32, 0, 0),
        };
        public DisplayModeEntry? Current(string id) => Now;
        public IReadOnlyList<DisplayModeEntry> Supported(string id) => Modes;
        public bool Test(string id, DisplayModeEntry m) { Log.Add("test"); return Accept; }
        public bool ApplyTemporary(string id, DisplayModeEntry m)
        {
            if (FailTemporary || (FailRevert && m.Width == 1920)) return false;
            Log.Add($"temp {m.Width}x{m.Height}@{m.RefreshRateHz}");
            Now = m;
            return true;
        }
        public bool ApplyPermanent(string id, DisplayModeEntry m)
        {
            if (FailPermanent) return false;
            Log.Add($"perm {m.Width}x{m.Height}@{m.RefreshRateHz}");
            return true;
        }
    }

    private GamingDisplayModeService Make(FakeApi api, bool gaming = false, TimeSpan? timeout = null) =>
        new(api, new GamingDisplayTopologyService(() => new[]
            {
                new GamingDisplayModeDto(Monitor, true, 0, 0, 1920, 1080, 60)
            }), () => gaming, _record, timeout ?? TimeSpan.FromMinutes(5));

    [Fact]
    public void Request_tests_then_applies_temporarily_and_records_a_restore_point()
    {
        var api = new FakeApi();
        using var service = Make(api);
        var status = service.Request(new(Monitor, 3840, 2160, 60));
        Assert.True(status.Pending);
        Assert.Equal(new[] { "test", "temp 3840x2160@60" }, api.Log);
        Assert.True(File.Exists(_record));
    }

    [Fact]
    public void Confirm_saves_the_mode_and_clears_the_restore_point()
    {
        var api = new FakeApi();
        using var service = Make(api);
        service.Request(new(Monitor, 3840, 2160, 120));
        var status = service.Confirm();
        Assert.False(status.Pending);
        Assert.Contains("perm 3840x2160@120", api.Log);
        Assert.False(File.Exists(_record));
    }

    [Fact]
    public void Revert_restores_the_previous_mode()
    {
        var api = new FakeApi();
        using var service = Make(api);
        service.Request(new(Monitor, 3840, 2160, 60));
        Assert.False(service.Revert().Pending);
        Assert.Equal(1920, api.Now.Width);
        Assert.False(File.Exists(_record));
    }

    [Fact]
    public async Task Unconfirmed_change_reverts_by_itself_after_the_timeout()
    {
        var api = new FakeApi();
        using var service = Make(api, timeout: TimeSpan.FromMilliseconds(150));
        service.Request(new(Monitor, 3840, 2160, 60));
        for (int i = 0; i < 50 && api.Now.Width != 1920; i++) await Task.Delay(50);
        Assert.Equal(1920, api.Now.Width);
        Assert.False(service.Status().Pending);
        Assert.False(File.Exists(_record));
    }

    [Fact]
    public void Failed_revert_keeps_the_restore_record_for_a_retry()
    {
        var api = new FakeApi();
        using var service = Make(api);
        service.Request(new(Monitor, 3840, 2160, 60));
        api.FailRevert = true;
        Assert.True(service.Revert().Pending);
        Assert.True(File.Exists(_record));
        api.FailRevert = false;
        Assert.False(service.Revert().Pending);
        Assert.Equal(1920, api.Now.Width);
    }

    [Fact]
    public void Restart_recovery_reverts_a_change_that_was_never_confirmed()
    {
        var api = new FakeApi();
        using (var first = Make(api)) first.Request(new(Monitor, 3840, 2160, 60));
        Assert.Equal(3840, api.Now.Width);
        using var restarted = Make(api);
        Assert.True(restarted.RecoverPendingAfterRestart());
        Assert.Equal(1920, api.Now.Width);
        Assert.False(File.Exists(_record));
    }

    [Fact]
    public void Gaming_unknown_monitor_unsupported_mode_and_driver_rejection_change_nothing()
    {
        var api = new FakeApi();
        Assert.Throws<InvalidOperationException>(() => Make(api, gaming: true).Request(new(Monitor, 3840, 2160, 60)));
        using var service = Make(api);
        Assert.Throws<ArgumentException>(() => service.Request(new(@"\\.\DISPLAY9", 3840, 2160, 60)));
        Assert.Throws<ArgumentException>(() => service.Request(new(Monitor, 1234, 777, 60)));
        api.Accept = false;
        Assert.Throws<InvalidOperationException>(() => service.Request(new(Monitor, 3840, 2160, 60)));
        Assert.DoesNotContain(api.Log, l => l.StartsWith("temp"));
        Assert.False(File.Exists(_record));
    }

    [Fact]
    public void Apply_failure_deletes_the_record_and_second_request_is_refused_while_pending()
    {
        var api = new FakeApi { FailTemporary = true };
        using var service = Make(api);
        Assert.Throws<InvalidOperationException>(() => service.Request(new(Monitor, 3840, 2160, 60)));
        Assert.False(File.Exists(_record));
        api.FailTemporary = false;
        service.Request(new(Monitor, 3840, 2160, 60));
        Assert.Throws<InvalidOperationException>(() => service.Request(new(Monitor, 3840, 2160, 120)));
    }

    [Fact]
    public void Requesting_the_current_mode_is_a_no_op()
    {
        var api = new FakeApi();
        using var service = Make(api);
        Assert.False(service.Request(new(Monitor, 1920, 1080, 60)).Pending);
        Assert.Empty(api.Log);
    }
}

public sealed class GamingDisplayModeListTests
{
    [Fact]
    public void Modes_are_distinct_sorted_largest_first_and_only_for_known_monitors()
    {
        var topology = new GamingDisplayTopologyService(() => new[] { new GamingDisplayModeDto(@"\.\DISPLAY1", true, 0, 0, 1920, 1080, 60) });
        var api = new ListApi();
        using var service = new GamingDisplayModeService(api, topology, () => false,
            Path.Combine(Path.GetTempPath(), "parux-modes-" + Guid.NewGuid().ToString("N") + ".json"), TimeSpan.FromMinutes(1));
        var modes = service.Modes(@"\.\DISPLAY1");
        Assert.Equal(new[] { (3840, 2160, 120), (3840, 2160, 60), (1920, 1080, 60) },
            modes.Select(m => (m.Width, m.Height, m.RefreshRateHz)).ToArray());
        Assert.Throws<ArgumentException>(() => service.Modes(@"\.\DISPLAY7"));
    }

    private sealed class ListApi : IDisplayModeApi
    {
        public DisplayModeEntry? Current(string id) => null;
        public IReadOnlyList<DisplayModeEntry> Supported(string id) => new DisplayModeEntry[]
        {
            new(1920, 1080, 60, 32, 0, 0), new(1920, 1080, 60, 16, 0, 0), new(3840, 2160, 60, 32, 0, 0),
            new(3840, 2160, 120, 32, 0, 0), new(640, 480, 1, 32, 0, 0),
        };
        public bool Test(string id, DisplayModeEntry m) => true;
        public bool ApplyTemporary(string id, DisplayModeEntry m) => true;
        public bool ApplyPermanent(string id, DisplayModeEntry m) => true;
    }
}
