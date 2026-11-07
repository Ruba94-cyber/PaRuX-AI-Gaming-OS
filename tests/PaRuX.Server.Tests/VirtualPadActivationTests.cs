using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Everything here uses fakes: no real pad, ViGEmBus or HidHide is touched (those need physical hardware).</summary>
public sealed class VirtualPadActivationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-vact-" + Guid.NewGuid().ToString("N"));
    private readonly RawPadMappingStore _deviceStore;
    private readonly GamePadProfileStore _gameStore;
    private readonly string _exe;
    private const string Pad = "{wgi/nrid/test-pad-1}";
    private const string HidPath = @"HID\VID_054C&PID_09CC\7&2a1b3c&0&0000";

    public VirtualPadActivationTests()
    {
        Directory.CreateDirectory(_dir);
        _deviceStore = new RawPadMappingStore(Path.Combine(_dir, "device"));
        _gameStore = new GamePadProfileStore(Path.Combine(_dir, "game"), _deviceStore);
        _exe = Path.Combine(_dir, "PaRuX.exe");
        File.WriteAllBytes(_exe, new byte[20_000]);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ------------------------------------------------------------ fakes

    private sealed class Source : IRawGamepadSource
    {
        public string DeviceId => Pad;
        public volatile bool Connected = true;
        public bool TryRead(out RawGamepadState? state)
        {
            state = Connected ? new RawGamepadState(new[] { true }, new[] { 0.0 }, -1) : null;
            return Connected;
        }
    }

    private sealed class FakePad : IVirtualXboxPad
    {
        public readonly List<Xbox360Report> Reports = new();
        public int Disposals;
        public bool Submit(Xbox360Report report) { lock (Reports) Reports.Add(report); return true; }
        public void Dispose() => Interlocked.Increment(ref Disposals);
        public Xbox360Report? Last { get { lock (Reports) return Reports.Count == 0 ? null : Reports[^1]; } }
    }

    private sealed class Backend : IVirtualControllerBackend
    {
        public string Id => "fake";
        public int Attempts;
        public int SlotFailures;       // first N creations fail with a transient message
        public string? PermanentError; // every creation fails with this
        public readonly FakePad Pad = new();
        public IVirtualXboxPad? TryCreatePad(out string? error)
        {
            Attempts++;
            if (PermanentError is not null) { error = PermanentError; return null; }
            if (SlotFailures > 0) { SlotFailures--; error = "The virtual controller driver has no free slot."; return null; }
            error = null;
            return Pad;
        }
    }

    private sealed class Isolation : IPadIsolation
    {
        public bool IsolateSucceeds = true;
        public volatile bool Still = true;
        public bool Throws;
        public int IsolateCalls;
        public Task<PadIsolationOutcome> IsolateAsync(PadDeviceIdentity device, CancellationToken ct)
        {
            IsolateCalls++;
            return Task.FromResult(IsolateSucceeds
                ? new PadIsolationOutcome(true, "hidden", HidPath)
                : new PadIsolationOutcome(false, "The binding could not be proven. Nothing was hidden.", null));
        }
        public Task<bool> IsStillIsolatedAsync(string path, CancellationToken ct)
        {
            if (Throws) throw new InvalidOperationException("synthetic");
            return Task.FromResult(Still);
        }
    }

    private static PadDeviceIdentity Identity() => new(Pad, "Test pad", 0x054C, 0x09CC, 1);
    private static PadMapping MapTo(ushort bit) => new() { Buttons = new() { [0] = bit }, LeftXAxis = 0 };

    private sealed class Rig : IAsyncDisposable
    {
        public Backend Backend = new();
        public Isolation Isolation = new();
        public Source Source = new();
        public bool DriverPresent = true;
        public VirtualPadForwardingService Service = null!;
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private Rig MakeRig(bool saveDeviceMapping = true)
    {
        var rig = new Rig();
        if (saveDeviceMapping) Assert.True(_deviceStore.Save(Pad, MapTo(Xbox360Report.A), out _));
        rig.Service = new VirtualPadForwardingService(rig.Backend, id => id == Pad ? rig.Source : null,
            () => rig.DriverPresent ? (true, null) : (false, null), rig.Isolation, _gameStore,
            new VirtualPadServiceOptions
            {
                Interval = TimeSpan.FromMilliseconds(10), RetryDelay = TimeSpan.FromMilliseconds(5),
                IsolationRefresh = TimeSpan.FromMilliseconds(20), IsolationMaxAge = TimeSpan.FromMilliseconds(250),
                MaxStartAttempts = 3
            });
        return rig;
    }

    private static async Task WaitUntil(Func<bool> condition, string what = "condition")
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Never reached: " + what);
    }

    // ------------------------------------------------------------ binding verification (fail closed)

    private static HidGamingDevice Hid(string path, ushort v = 0x054C, ushort p = 0x09CC) => new(path, v, p);

    [Fact]
    public void Raw_controller_isolation_identity_checks_all_devices_not_just_the_ui_first_sixteen()
    {
        var observed = Enumerable.Range(0, 20)
            .Select(n => new RawPadIdentitySnapshot(
                "other-" + n, (ushort)0x045e, (ushort)n)).ToList();
        observed[0] = new RawPadIdentitySnapshot("selected", 0x054c, 0x09cc);
        Assert.Equal(1, PadBindingVerifier.CountMatchingRawControllers(
            "selected", 0x054c, 0x09cc, observed));

        // Identical second controller lies beyond the 16 entries shown in UI.
        observed[19] = new RawPadIdentitySnapshot("duplicate-pad", 0x054c, 0x09cc);
        Assert.Equal(2, PadBindingVerifier.CountMatchingRawControllers(
            "selected", 0x054c, 0x09cc, observed));
        Assert.False(PadBindingVerifier.Verify(0x054c, 0x09cc,
            PadBindingVerifier.CountMatchingRawControllers("selected", 0x054c, 0x09cc, observed),
            new[] { Hid(HidPath) }).Proven);
    }

    [Fact]
    public void Stale_selected_raw_controller_fails_closed_even_when_an_identical_pad_remains()
    {
        var observed = new[]
        {
            new RawPadIdentitySnapshot("different-instance", 0x054c, 0x09cc)
        };
        Assert.Equal(0, PadBindingVerifier.CountMatchingRawControllers(
            "disconnected-instance", 0x054c, 0x09cc, observed));
        Assert.Equal(0, PadBindingVerifier.CountMatchingRawControllers(
            null, 0x054c, 0x09cc, observed));
        Assert.Equal(0, PadBindingVerifier.CountMatchingRawControllers(
            "different-instance", 0, 0, observed));
        Assert.Equal(0, PadBindingVerifier.CountMatchingRawControllers(
            "different-instance", 0x054c, 0x09cc, null));
        Assert.Equal(1, PadBindingVerifier.CountMatchingRawControllers(
            "different-instance", 0x054c, 0x09cc, observed));
    }

    [Fact]
    public void Binding_is_proven_only_for_exactly_one_matching_hid_device()
    {
        var ok = PadBindingVerifier.Verify(0x054C, 0x09CC, 1, new[] { Hid(HidPath), Hid(@"HID\VID_057E&PID_2009\1", 0x057E, 0x2009) });
        Assert.True(ok.Proven);
        Assert.Equal(HidPath, ok.InstancePath);
    }

    [Fact]
    public void Binding_fails_closed_when_ambiguous_missing_or_identity_unknown()
    {
        Assert.False(PadBindingVerifier.Verify(0x054C, 0x09CC, 2, new[] { Hid(HidPath) }).Proven);           // two identical pads
        Assert.False(PadBindingVerifier.Verify(0x054C, 0x09CC, 1, new[] { Hid(HidPath), Hid(@"HID\VID_054C&PID_09CC\2") }).Proven); // two HID matches
        Assert.False(PadBindingVerifier.Verify(0x054C, 0x09CC, 1, new[] { Hid(@"HID\VID_057E&PID_2009\1", 0x057E, 0x2009) }).Proven);               // none match
        Assert.False(PadBindingVerifier.Verify(0x054C, 0x09CC, 1, Array.Empty<HidGamingDevice>()).Proven);
        Assert.False(PadBindingVerifier.Verify(0, 0, 1, new[] { Hid(HidPath, 0, 0) }).Proven);               // unknown identity
        Assert.False(PadBindingVerifier.Verify(0x054C, 0x09CC, 0, new[] { Hid(HidPath) }).Proven);           // pad no longer listed
    }

    [Fact]
    public void Gaming_device_list_parser_reads_present_devices_and_rejects_garbage()
    {
        const string json = """
        [{"friendlyName":"Pad","devices":[{"present":true,"gamingDevice":true,"deviceInstancePath":"HID\\VID_054C&PID_09CC\\7&2a1b3c&0&0000"},
        {"present":false,"deviceInstancePath":"HID\\VID_057E&PID_2009\\gone"}]}]
        """;
        var list = HidGamingDeviceListParser.Parse(json);
        Assert.NotNull(list);
        var only = Assert.Single(list!);
        Assert.Equal((ushort)0x054C, only.VendorId);
        Assert.Null(HidGamingDeviceListParser.Parse("not json"));
        Assert.Null(HidGamingDeviceListParser.Parse(""));
    }

    private sealed class HidCli
    {
        public string GamingOutput = "";
        public int GamingExit;
        public bool Cloak;
        public readonly List<string> Apps = new(), Devices = new(), Log = new();
        public Task<(int, string)> Run(HidHideManager.ProcessSpec spec, CancellationToken ct)
        {
            string cmd = spec.Arguments[0];
            string arg = spec.Arguments.Count > 1 ? spec.Arguments[1] : "";
            Log.Add(cmd);
            switch (cmd)
            {
                case "--dev-gaming": return Task.FromResult((GamingExit, GamingOutput));
                case "--cloak-state": return Task.FromResult((0, Cloak ? "--cloak-on" : "--cloak-off"));
                case "--app-list": return Task.FromResult((0, string.Join("\n", Apps.Select(a => $"--app-reg \"{a}\""))));
                case "--dev-list": return Task.FromResult((0, string.Join("\n", Devices.Select(d => $"--dev-hide \"{d}\""))));
                case "--cloak-on": Cloak = true; break;
                case "--cloak-off": Cloak = false; break;
                case "--app-unreg": Apps.RemoveAll(a => a == arg); break;
                case "--dev-unhide": Devices.RemoveAll(d => d == arg); break;
                case "--app-reg": Apps.Add(arg); break;
                case "--dev-hide": Devices.Add(arg); break;
            }
            return Task.FromResult((0, ""));
        }
    }

    private const string OneDeviceJson =
        """[{"devices":[{"present":true,"deviceInstancePath":"HID\\VID_054C&PID_09CC\\7&2a1b3c&0&0000"}]}]""";

    private HidHideManager HidManager(HidCli cli) =>
        new(() => @"C:\fake\HidHideCLI.exe", cli.Run, Path.Combine(_dir, "journal.json"));

    [Fact]
    public async Task Hide_requires_consent_and_changes_nothing_without_it()
    {
        var cli = new HidCli { GamingOutput = OneDeviceJson };
        var result = await HidManager(cli).HideVerifiedAsync(0x054C, 0x09CC, 1, false, _exe, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(cli.Log);
    }

    [Fact]
    public async Task Hide_is_refused_without_changes_when_the_binding_cannot_be_proven()
    {
        var cli = new HidCli { GamingOutput = OneDeviceJson };
        var ambiguous = await HidManager(cli).HideVerifiedAsync(0x054C, 0x09CC, 2, true, _exe, CancellationToken.None);
        Assert.False(ambiguous.Success);
        Assert.False(ambiguous.BindingProven);
        Assert.Contains("Nothing was hidden", ambiguous.Message);

        cli.GamingOutput = "<<garbage>>";
        Assert.False((await HidManager(cli).HideVerifiedAsync(0x054C, 0x09CC, 1, true, _exe, CancellationToken.None)).Success);
        cli.GamingOutput = OneDeviceJson; cli.GamingExit = 5;
        Assert.False((await HidManager(cli).HideVerifiedAsync(0x054C, 0x09CC, 1, true, _exe, CancellationToken.None)).Success);

        Assert.Empty(cli.Devices);
        Assert.False(cli.Cloak);
        Assert.DoesNotContain("--dev-hide", cli.Log);
        Assert.False(File.Exists(Path.Combine(_dir, "journal.json")));
    }

    [Fact]
    public async Task Hide_with_consent_and_a_proven_binding_hides_exactly_that_instance_and_keeps_the_journal()
    {
        var cli = new HidCli { GamingOutput = OneDeviceJson };
        var manager = HidManager(cli);
        var result = await manager.HideVerifiedAsync(0x054C, 0x09CC, 1, true, _exe, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.BindingProven);
        Assert.Equal(HidPath, result.InstancePath);
        Assert.Equal(new[] { HidPath }, cli.Devices);
        Assert.True(manager.RecoveryRequired);
        Assert.True(await manager.IsIsolatedAsync(HidPath, CancellationToken.None));
        Assert.False(await manager.IsIsolatedAsync(@"HID\VID_057E&PID_2009\1", CancellationToken.None));
        Assert.True((await manager.RestoreAsync(CancellationToken.None)).Success);
        Assert.False(await manager.IsIsolatedAsync(HidPath, CancellationToken.None));
    }

    // ------------------------------------------------------------ mapping model

    [Fact]
    public void Full_mapping_supports_all_buttons_hat_routing_and_axis_inversion()
    {
        var mapping = new PadMapping
        {
            Buttons = new() { [0] = Xbox360Report.A, [1] = Xbox360Report.B, [2] = Xbox360Report.X, [3] = Xbox360Report.Y,
                [4] = Xbox360Report.LeftShoulder, [5] = Xbox360Report.RightShoulder, [6] = Xbox360Report.Back,
                [7] = Xbox360Report.Start, [8] = Xbox360Report.LeftThumb, [9] = Xbox360Report.RightThumb, [10] = Xbox360Report.Guide },
            LeftXAxis = 0, InvertLeftX = true, HatTarget = PadHatTarget.LeftStick
        };
        Assert.Null(mapping.Validate());
        var state = new RawGamepadState(new bool[12].Select((_, i) => i is 4 or 7 or 10).ToArray(), new[] { 0.5 }, 2);
        var report = GenericPadMapper.Map(state, mapping);
        Assert.Equal((ushort)(Xbox360Report.LeftShoulder | Xbox360Report.Start | Xbox360Report.Guide), report.Buttons);
        Assert.Equal(32767, report.LeftX);   // hat right overrides the inverted stick
        Assert.Equal(0, report.LeftY);

        mapping.HatTarget = PadHatTarget.Dpad;
        var dpad = GenericPadMapper.Map(state, mapping);
        Assert.True((dpad.Buttons & Xbox360Report.DpadRight) != 0);
        Assert.True(dpad.LeftX < 0);         // InvertLeftX flips +0.5
        var clone = mapping.Clone();
        clone.Buttons[0] = Xbox360Report.B;
        Assert.Equal(Xbox360Report.A, mapping.Buttons[0]);
        Assert.Equal(PadHatTarget.Dpad, clone.HatTarget);
        mapping.HatTarget = (PadHatTarget)99;
        Assert.NotNull(mapping.Validate());
    }

    // ------------------------------------------------------------ per-game profiles

    [Fact]
    public void Game_profile_wins_and_the_device_mapping_is_the_fallback()
    {
        Assert.True(_deviceStore.Save(Pad, MapTo(Xbox360Report.A), out _));
        Assert.Equal(PadMappingSource.DeviceMapping, _gameStore.Resolve(Pad, "game-1").Source);
        Assert.Equal(PadMappingSource.DeviceMapping, _gameStore.Resolve(Pad, null).Source);
        Assert.True(_gameStore.Save("game-1", Pad, MapTo(Xbox360Report.B), out _));
        var game = _gameStore.Resolve(Pad, "game-1");
        Assert.Equal(PadMappingSource.GameProfile, game.Source);
        Assert.Equal(Xbox360Report.B, game.Mapping!.Buttons[0]);
        Assert.Equal(PadMappingSource.DeviceMapping, _gameStore.Resolve(Pad, "game-2").Source);
        Assert.Equal(PadMappingSource.None, _gameStore.Resolve("{other-pad}", "game-1").Source);
        Assert.True(_gameStore.Delete("game-1", Pad));
        Assert.Equal(PadMappingSource.DeviceMapping, _gameStore.Resolve(Pad, "game-1").Source);
    }

    [Fact]
    public void A_damaged_game_profile_is_preserved_and_the_device_mapping_still_applies()
    {
        Assert.True(_deviceStore.Save(Pad, MapTo(Xbox360Report.A), out _));
        Assert.True(_gameStore.Save("g", Pad, MapTo(Xbox360Report.B), out _));
        string file = Directory.GetFiles(Path.Combine(_dir, "game"), "*.json").Single();
        File.WriteAllText(file, "{ broken");
        var resolved = _gameStore.Resolve(Pad, "g");
        Assert.Equal(PadMappingSource.DeviceMapping, resolved.Source);
        Assert.NotNull(resolved.Error);
        Assert.Equal("{ broken", File.ReadAllText(file));
        Assert.False(_gameStore.Save("g", Pad, MapTo(Xbox360Report.X), out _)); // never overwrites a damaged file
        Assert.Equal("{ broken", File.ReadAllText(file));
    }

    [Fact]
    public void Game_profiles_reject_invalid_ids_and_mappings_and_store_no_plaintext_ids()
    {
        Assert.False(_gameStore.Save("", Pad, MapTo(Xbox360Report.A), out _));
        Assert.False(_gameStore.Save("g\n", Pad, MapTo(Xbox360Report.A), out _));
        var bad = MapTo(Xbox360Report.A); bad.StickDeadzone = double.NaN;
        Assert.False(_gameStore.Save("g", Pad, bad, out _));
        Assert.True(_gameStore.Save("secret-game-id", Pad, MapTo(Xbox360Report.A), out _));
        string file = Directory.GetFiles(Path.Combine(_dir, "game"), "*.json").Single();
        Assert.DoesNotContain("secret-game-id", file + File.ReadAllText(file));
        Assert.DoesNotContain("test-pad-1", file + File.ReadAllText(file));
    }

    // ------------------------------------------------------------ service: refusals

    [Fact]
    public async Task Driver_absent_refuses_before_any_isolation_or_pad_creation()
    {
        await using var rig = MakeRig();
        rig.DriverPresent = false;
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Contains("never installs", status.Message);
        Assert.Equal(0, rig.Backend.Attempts);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
    }

    [Fact]
    public async Task Start_without_consent_to_hide_is_refused_and_hides_nothing()
    {
        await using var rig = MakeRig();
        rig.Isolation.Still = false;
        var status = await rig.Service.StartAsync(Identity(), false, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
        Assert.Equal(0, rig.Backend.Attempts);
    }

    [Fact]
    public async Task Unproven_binding_means_no_hiding_and_no_forwarding()
    {
        await using var rig = MakeRig();
        rig.Isolation.IsolateSucceeds = false;
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Contains("Nothing was hidden", status.Message);
        Assert.Equal(0, rig.Backend.Attempts);
    }

    [Fact]
    public async Task Missing_mapping_is_refused()
    {
        await using var rig = MakeRig(saveDeviceMapping: false);
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
    }

    [Fact]
    public async Task Missing_physical_pad_is_refused()
    {
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity() with { DeviceId = "{unknown}" }, true, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(0, rig.Backend.Attempts);
    }

    // ------------------------------------------------------------ service: run / stop / isolation

    [Fact]
    public async Task Start_forwards_and_explicit_stop_leaves_a_neutral_unplugged_pad()
    {
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Running, status.Phase);
        Assert.Equal(PadMappingSource.DeviceMapping, status.MappingSource);
        await WaitUntil(() => rig.Backend.Pad.Reports.Any(r => r.Buttons == Xbox360Report.A), "A forwarded");
        var stopped = await rig.Service.StopAsync();
        Assert.Equal(VirtualPadPhase.Idle, stopped.Phase);
        Assert.True(rig.Backend.Pad.Last == default(Xbox360Report), "last report is neutral");
        Assert.Equal(1, rig.Backend.Pad.Disposals);
        Assert.Equal(1, rig.Isolation.IsolateCalls);
    }

    [Fact]
    public async Task A_second_start_while_running_is_refused()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        var again = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Running, again.Phase);
        Assert.Equal(1, rig.Backend.Attempts);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Loss_of_isolation_stops_forwarding_and_neutralises()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        await WaitUntil(() => rig.Backend.Pad.Reports.Any(r => r.Buttons == Xbox360Report.A));
        rig.Isolation.Still = false;
        await WaitUntil(() => rig.Backend.Pad.Disposals == 1, "pad unplugged");
        Assert.True(rig.Backend.Pad.Last == default(Xbox360Report), "last report is neutral");
        var status = rig.Service.Status;
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Contains("isolation", status.Message, StringComparison.OrdinalIgnoreCase);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_failing_isolation_check_is_treated_as_lost_isolation()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        rig.Isolation.Throws = true;
        await WaitUntil(() => rig.Backend.Pad.Disposals == 1, "pad unplugged");
        Assert.Equal(VirtualPadPhase.Failed, rig.Service.Status.Phase);
    }

    // ------------------------------------------------------------ service: bounded retries

    [Fact]
    public async Task Transient_driver_busy_is_retried_a_bounded_number_of_times()
    {
        await using var rig = MakeRig();
        rig.Backend.SlotFailures = 2;
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Running, status.Phase);
        Assert.Equal(3, rig.Backend.Attempts);
        await rig.Service.StopAsync();

        await using var stuck = MakeRig();
        stuck.Backend.SlotFailures = 100;
        var failed = await stuck.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Failed, failed.Phase);
        Assert.Equal(3, stuck.Backend.Attempts);
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried()
    {
        await using var rig = MakeRig();
        rig.Backend.PermanentError = "The installed virtual controller driver is a different version than PaRuX expects.";
        var status = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(1, rig.Backend.Attempts);
    }

    [Fact]
    public async Task Cancelled_start_leaves_nothing_running()
    {
        await using var rig = MakeRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var status = await rig.Service.StartAsync(Identity(), true, null, cts.Token);
        Assert.Equal(VirtualPadPhase.Idle, status.Phase);
        Assert.Equal(0, rig.Backend.Attempts);
    }

    // ------------------------------------------------------------ service: hot-plug

    [Fact]
    public async Task Removal_neutralises_and_pauses_and_return_only_offers_an_explicit_rearm()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        await WaitUntil(() => rig.Backend.Pad.Reports.Any(r => r.Buttons == Xbox360Report.A));

        await rig.Service.OnDeviceRemovedAsync(Pad);
        Assert.Equal(VirtualPadPhase.PausedRemoved, rig.Service.Status.Phase);
        Assert.True(rig.Backend.Pad.Last == default(Xbox360Report), "last report is neutral");
        Assert.Equal(1, rig.Backend.Pad.Disposals);

        rig.Service.OnDeviceAdded(Pad);
        var offered = rig.Service.Status;
        Assert.Equal(VirtualPadPhase.PausedRemoved, offered.Phase); // never resumes by itself
        Assert.True(offered.CanRearm);
        await Task.Delay(60);
        Assert.Equal(1, rig.Backend.Attempts);

        var rearmed = await rig.Service.RearmAsync();
        Assert.Equal(VirtualPadPhase.Running, rearmed.Phase);
        Assert.Equal(2, rig.Backend.Attempts);
        Assert.Equal(1, rig.Isolation.IsolateCalls); // re-arm never hides anything again
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Removal_of_another_pad_is_ignored_and_rearm_without_return_is_refused()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        await rig.Service.OnDeviceRemovedAsync("{someone-else}");
        Assert.Equal(VirtualPadPhase.Running, rig.Service.Status.Phase);
        await rig.Service.OnDeviceRemovedAsync(Pad);
        var refused = await rig.Service.RearmAsync();            // pad has not come back
        Assert.Equal(VirtualPadPhase.PausedRemoved, refused.Phase);
        Assert.Equal(1, rig.Backend.Attempts);
    }

    [Fact]
    public async Task A_pad_that_vanishes_without_an_event_pauses_and_can_be_rearmed_after_it_returns()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        rig.Source.Connected = false;
        await WaitUntil(() => rig.Service.Status.Phase == VirtualPadPhase.PausedRemoved, "paused");
        Assert.Equal(1, rig.Backend.Pad.Disposals);
        rig.Source.Connected = true;
        rig.Service.OnDeviceAdded(Pad);
        Assert.True(rig.Service.Status.CanRearm);
        Assert.Equal(VirtualPadPhase.Running, (await rig.Service.RearmAsync()).Phase);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Rearm_is_refused_when_the_pad_is_no_longer_hidden()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        await rig.Service.OnDeviceRemovedAsync(Pad);
        rig.Service.OnDeviceAdded(Pad);
        rig.Isolation.Still = false;
        var status = await rig.Service.RearmAsync();
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(1, rig.Backend.Attempts);
        Assert.Equal(1, rig.Isolation.IsolateCalls);
    }

    // ------------------------------------------------------------ service: per-game profile selection

    [Fact]
    public async Task Game_start_switches_to_its_profile_and_game_stop_falls_back_to_the_device_mapping()
    {
        Assert.True(_gameStore.Save("game-1", Pad, MapTo(Xbox360Report.B), out _));
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), true, null);
        await WaitUntil(() => rig.Backend.Pad.Last?.Buttons == Xbox360Report.A, "device mapping");

        var inGame = await rig.Service.OnGameStartedAsync("game-1");
        Assert.Equal(PadMappingSource.GameProfile, inGame.MappingSource);
        await WaitUntil(() => rig.Backend.Pad.Last?.Buttons == Xbox360Report.B, "game profile applied");

        var other = await rig.Service.OnGameStartedAsync("game-without-profile");
        Assert.Equal(PadMappingSource.DeviceMapping, other.MappingSource);
        await WaitUntil(() => rig.Backend.Pad.Last?.Buttons == Xbox360Report.A, "fallback");

        await rig.Service.OnGameStartedAsync("game-1");
        var ended = await rig.Service.OnGameStoppedAsync();
        Assert.Equal(PadMappingSource.DeviceMapping, ended.MappingSource);
        Assert.Equal(1, rig.Isolation.IsolateCalls); // switching never re-hides or re-asks
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_game_start_never_activates_forwarding_by_itself()
    {
        Assert.True(_gameStore.Save("game-1", Pad, MapTo(Xbox360Report.B), out _));
        await using var rig = MakeRig();
        var status = await rig.Service.OnGameStartedAsync("game-1");
        Assert.Equal(VirtualPadPhase.Idle, status.Phase);
        Assert.Equal(0, rig.Backend.Attempts);
    }

    [Fact]
    public async Task Start_after_game_start_uses_that_games_profile()
    {
        Assert.True(_gameStore.Save("game-1", Pad, MapTo(Xbox360Report.B), out _));
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity(), true, "game-1");
        Assert.Equal(PadMappingSource.GameProfile, status.MappingSource);
        await WaitUntil(() => rig.Backend.Pad.Last?.Buttons == Xbox360Report.B);
        await rig.Service.StopAsync();
    }
}
