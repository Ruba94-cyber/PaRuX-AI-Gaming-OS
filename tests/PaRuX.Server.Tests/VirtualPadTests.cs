using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VirtualPadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-vpad-" + Guid.NewGuid().ToString("N"));
    private readonly string _journal;
    private readonly string _exe;

    public VirtualPadTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = Path.Combine(_dir, "journal.json");
        _exe = Path.Combine(_dir, "PaRuX.exe");
        File.WriteAllBytes(_exe, new byte[20_000]);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- HidHide, with a fake command-line tool

    private sealed class FakeCli
    {
        public bool Cloak;
        public readonly List<string> Apps = new();
        public readonly List<string> Devices = new();
        public readonly List<string> Log = new();
        public string? FailOn;   // command that returns a failure exit code
        public readonly HashSet<string> AlsoFailOn = new(StringComparer.Ordinal);

        public Task<(int, string)> Run(HidHideManager.ProcessSpec spec, CancellationToken ct)
        {
            string cmd = spec.Arguments[0];
            string arg = spec.Arguments.Count > 1 ? spec.Arguments[1] : "";
            Log.Add(spec.Arguments.Count > 1 ? cmd + " " + arg : cmd);
            if (cmd == FailOn || AlsoFailOn.Contains(cmd)) return Task.FromResult((1, "denied"));
            switch (cmd)
            {
                case "--cloak-state": return Task.FromResult((0, Cloak ? "--cloak-on" : "--cloak-off"));
                case "--app-list": return Task.FromResult((0, string.Join("\n", Apps.Select(a => $"--app-reg \"{a}\""))));
                case "--dev-list": return Task.FromResult((0, string.Join("\n", Devices.Select(d => $"--dev-hide \"{d}\""))));
                case "--cloak-on": Cloak = true; break;
                case "--cloak-off": Cloak = false; break;
                case "--app-reg": Apps.Add(arg); break;
                case "--app-unreg": Apps.RemoveAll(a => a == arg); break;
                case "--dev-hide": Devices.Add(arg); break;
                case "--dev-unhide": Devices.RemoveAll(d => d == arg); break;
            }
            return Task.FromResult((0, ""));
        }
    }

    private const string PadA = @"HID\VID_054C&PID_09CC\7&2a1b3c&0&0000";
    private const string PadB = @"HID\VID_057E&PID_2009\7&9f8e7d&0&0000";

    private HidHideManager Make(FakeCli cli, bool installed = true) =>
        new(() => installed ? @"C:\fake\HidHideCLI.exe" : null, cli.Run, _journal);

    [Fact]
    public async Task Hiding_changes_only_what_is_needed_and_restoring_reverses_exactly_that()
    {
        var cli = new FakeCli();
        var manager = Make(cli);
        Assert.False(manager.RecoveryRequired);
        var result = await manager.HideAsync(new[] { PadA }, _exe, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(manager.RecoveryRequired);
        Assert.Contains(PadA, cli.Devices);
        Assert.Contains(Path.GetFullPath(_exe), cli.Apps);
        Assert.True(cli.Cloak);
        Assert.True(File.Exists(_journal));

        Assert.True((await manager.RestoreAsync(CancellationToken.None)).Success);
        Assert.Empty(cli.Devices);
        Assert.Empty(cli.Apps);
        Assert.False(cli.Cloak);
        Assert.False(File.Exists(_journal));
        Assert.False(manager.RecoveryRequired);
    }

    [Fact]
    public async Task A_users_own_hidehide_setup_is_never_touched()
    {
        var cli = new FakeCli { Cloak = true };
        cli.Devices.Add(PadB);                       // already hidden by the user
        cli.Apps.Add(@"C:\Tools\Mapper.exe");        // user's own allowed program
        cli.Apps.Add(Path.GetFullPath(_exe));        // PaRuX already allowed by the user
        var manager = Make(cli);

        Assert.True((await manager.HideAsync(new[] { PadA, PadB }, _exe, CancellationToken.None)).Success);
        Assert.DoesNotContain("--cloak-on", cli.Log);                       // it was already on: not touched
        Assert.DoesNotContain(cli.Log, l => l.StartsWith("--app-reg"));     // already registered: not registered again
        Assert.DoesNotContain(cli.Log, l => l == "--dev-hide " + PadB);     // already hidden: not hidden again

        Assert.True((await manager.RestoreAsync(CancellationToken.None)).Success);
        Assert.Equal(new[] { PadB }, cli.Devices);                          // only PadA (ours) was unhidden
        Assert.Contains(@"C:\Tools\Mapper.exe", cli.Apps);
        Assert.Contains(Path.GetFullPath(_exe), cli.Apps);                  // the user's registration stays
        Assert.True(cli.Cloak);                                             // the user's cloak stays on
    }

    [Fact]
    public async Task A_refused_command_rolls_back_everything_it_changed()
    {
        var cli = new FakeCli { FailOn = "--cloak-on" };
        var result = await Make(cli).HideAsync(new[] { PadA }, _exe, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(cli.Devices);
        Assert.Empty(cli.Apps);
        Assert.False(cli.Cloak);
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public async Task A_crash_is_recovered_from_the_journal_by_a_new_instance()
    {
        var cli = new FakeCli();
        await Make(cli).HideAsync(new[] { PadA }, _exe, CancellationToken.None);   // process "dies" here without restoring
        var restarted = Make(cli);
        Assert.True((await restarted.RecoverAsync(CancellationToken.None)).Success);
        Assert.Empty(cli.Devices);
        Assert.False(cli.Cloak);
    }

    [Fact]
    public async Task A_partial_restore_keeps_a_journal_for_a_later_retry()
    {
        var cli = new FakeCli();
        var manager = Make(cli);
        Assert.True((await manager.HideAsync(new[] { PadA }, _exe, CancellationToken.None)).Success);
        cli.FailOn = "--dev-unhide";
        Assert.False((await manager.RestoreAsync(CancellationToken.None)).Success);
        Assert.True(File.Exists(_journal));
        Assert.Contains(PadA, cli.Devices);
        cli.FailOn = null;
        Assert.True((await manager.RestoreAsync(CancellationToken.None)).Success);
        Assert.Empty(cli.Devices);
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public async Task Failed_apply_reports_incomplete_rollback_and_retains_recovery_journal()
    {
        var cli = new FakeCli { FailOn = "--cloak-on" };
        cli.AlsoFailOn.Add("--dev-unhide");
        var manager = Make(cli);
        var result = await manager.HideAsync(new[] { PadA }, _exe, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_journal));
        cli.FailOn = null;
        cli.AlsoFailOn.Clear();
        Assert.True((await manager.RecoverAsync(CancellationToken.None)).Success);
        Assert.Empty(cli.Devices);
    }

    [Fact]
    public async Task Cancellation_during_apply_attempts_uncancelled_recovery()
    {
        var cli = new FakeCli();
        using var cts = new CancellationTokenSource();
        var manager = new HidHideManager(
            () => @"C:\fake\HidHideCLI.exe",
            (spec, ct) =>
            {
                if (spec.Arguments[0] == "--dev-hide")
                {
                    cts.Cancel();
                    return Task.FromCanceled<(int ExitCode, string Output)>(cts.Token);
                }
                return cli.Run(spec, ct);
            }, _journal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.HideAsync(new[] { PadA }, _exe, cts.Token));
        Assert.Empty(cli.Apps);
        Assert.Empty(cli.Devices);
        Assert.False(cli.Cloak);
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public async Task A_second_hide_is_refused_until_the_first_is_restored()
    {
        var cli = new FakeCli();
        var manager = Make(cli);
        await manager.HideAsync(new[] { PadA }, _exe, CancellationToken.None);
        Assert.False((await manager.HideAsync(new[] { PadB }, _exe, CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData("--cloak-off")]
    [InlineData("HID\\x; calc")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task Unsafe_device_identifiers_never_reach_the_command_line(string device)
    {
        var cli = new FakeCli();
        Assert.False((await Make(cli).HideAsync(new[] { device }, _exe, CancellationToken.None)).Success);
        Assert.Empty(cli.Log);
    }

    [Fact]
    public async Task Without_hidehide_nothing_runs_and_the_message_says_it_is_optional()
    {
        var cli = new FakeCli();
        var manager = Make(cli, installed: false);
        Assert.False(manager.IsAvailable);
        var result = await manager.HideAsync(new[] { PadA }, _exe, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("optional", result.Message);
        Assert.Empty(cli.Log);
        Assert.Null(await manager.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_tampered_journal_is_refused_instead_of_being_executed()
    {
        File.WriteAllText(_journal, "{\"HiddenByUs\":[\"--cloak-off\"],\"AppRegisteredByUs\":null,\"CloakTurnedOnByUs\":false}");
        var cli = new FakeCli();
        var result = await Make(cli).RestoreAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("journal", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_journal));
        Assert.Empty(cli.Log);
    }

    // ---------------------------------------------------------------- ViGEmBus wire contract

    [Fact]
    public void Ioctl_codes_and_structure_sizes_match_the_published_driver_contract()
    {
        // Values documented by the driver header: plug-in 0x2AA004, unplug 0x2AA008, version 0x2AA00C, report 0x2AA808.
        Assert.Equal(0x2AA004u, ViGEmBusClient.PluginTarget);
        Assert.Equal(0x2AA008u, ViGEmBusClient.UnplugTarget);
        Assert.Equal(0x2AA00Cu, ViGEmBusClient.CheckVersion);
        Assert.Equal(0x2AA010u, ViGEmBusClient.WaitDeviceReady);
        Assert.Equal(0x2AA808u, ViGEmBusClient.XusbSubmitReport);

        Assert.Equal(16, ViGEmBusClient.PluginRequest(1).Length);
        Assert.Equal(8, ViGEmBusClient.UnplugRequest(1).Length);
        Assert.Equal(8, ViGEmBusClient.VersionRequest().Length);
        Assert.Equal(20, ViGEmBusClient.SubmitRequest(1, default).Length);
    }

    [Fact]
    public void Requests_carry_their_own_size_serial_and_target_type()
    {
        byte[] plug = ViGEmBusClient.PluginRequest(3);
        Assert.Equal(16u, BitConverter.ToUInt32(plug, 0));
        Assert.Equal(3u, BitConverter.ToUInt32(plug, 4));
        Assert.Equal(0, BitConverter.ToInt32(plug, 8));        // Xbox360Wired
        Assert.Equal(0, BitConverter.ToUInt16(plug, 12));      // vendor id unset
        Assert.Equal(0, BitConverter.ToUInt16(plug, 14));      // product id unset
        Assert.Equal(1u, BitConverter.ToUInt32(ViGEmBusClient.VersionRequest(), 4));

        var report = new Xbox360Report(Xbox360Report.A | Xbox360Report.Start, 200, 17, -32767, 32767, 1000, -1000);
        byte[] submit = ViGEmBusClient.SubmitRequest(9, report);
        Assert.Equal(20u, BitConverter.ToUInt32(submit, 0));
        Assert.Equal(9u, BitConverter.ToUInt32(submit, 4));
        Assert.Equal((ushort)(Xbox360Report.A | Xbox360Report.Start), BitConverter.ToUInt16(submit, 8));
        Assert.Equal(200, submit[10]);
        Assert.Equal(17, submit[11]);
        Assert.Equal(-32767, BitConverter.ToInt16(submit, 12));
        Assert.Equal(32767, BitConverter.ToInt16(submit, 14));
        Assert.Equal(1000, BitConverter.ToInt16(submit, 16));
        Assert.Equal(-1000, BitConverter.ToInt16(submit, 18));
    }

    [Fact]
    public void Creating_a_pad_without_the_driver_reports_a_reason_and_never_throws()
    {
        IVirtualXboxPad? pad = new ViGEmBusClient().TryCreatePad(out string? error);
        if (pad is not null) { pad.Dispose(); return; }  // a PC that really has the driver: nothing more to assert here
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---------------------------------------------------------------- generic pad to Xbox 360

    private static PadMapping TypicalLayout() => new()
    {
        Buttons = new() { [0] = Xbox360Report.A, [1] = Xbox360Report.B, [2] = Xbox360Report.X, [3] = Xbox360Report.Y, [9] = Xbox360Report.Start },
        LeftXAxis = 0, LeftYAxis = 1, RightXAxis = 2, RightYAxis = 3,
        LeftTriggerAxis = 4, RightTriggerAxis = 5,
    };

    private static RawGamepadState State(bool[]? buttons = null, double[]? axes = null, int hat = -1) =>
        new(buttons ?? new bool[16], axes ?? new double[8], hat);

    [Fact]
    public void Buttons_hat_and_start_map_to_the_xbox_layout()
    {
        var buttons = new bool[16];
        buttons[0] = true; buttons[9] = true;
        var report = GenericPadMapper.Map(State(buttons, hat: 2), TypicalLayout());
        Assert.Equal((ushort)(Xbox360Report.A | Xbox360Report.Start | Xbox360Report.DpadRight), report.Buttons);
        Assert.Equal(Xbox360Report.DpadUp | Xbox360Report.DpadLeft, GenericPadMapper.HatToDpad(7));
        Assert.Equal(0, GenericPadMapper.HatToDpad(-1));
        Assert.Equal(0, GenericPadMapper.HatToDpad(99));
    }

    [Fact]
    public void Sticks_use_a_deadzone_scale_to_full_range_and_flip_the_vertical_axis()
    {
        var mapping = TypicalLayout();
        var centred = GenericPadMapper.Map(State(axes: new[] { 0.05, -0.05, 0, 0, 0, 0, 0, 0 }), mapping);
        Assert.Equal(0, centred.LeftX);
        Assert.Equal(0, centred.LeftY);

        var full = GenericPadMapper.Map(State(axes: new[] { 1.0, 1.0, -1.0, -1.0, 0, 0, 0, 0 }), mapping);
        Assert.Equal(32767, full.LeftX);
        Assert.Equal(-32767, full.LeftY);      // HID down (+1) becomes XInput down (negative)
        Assert.Equal(-32767, full.RightX);
        Assert.Equal(32767, full.RightY);
    }

    [Fact]
    public void Triggers_support_unsigned_signed_and_digital_sources()
    {
        var mapping = TypicalLayout();
        Assert.Equal(255, GenericPadMapper.Map(State(axes: new[] { 0, 0, 0, 0, 1.0, 0.5, 0, 0 }), mapping).LeftTrigger);
        Assert.Equal(128, GenericPadMapper.Map(State(axes: new[] { 0, 0, 0, 0, 1.0, 0.5, 0, 0 }), mapping).RightTrigger);

        mapping.TriggerAxisIsSigned = true;   // rest at -1
        Assert.Equal(0, GenericPadMapper.Map(State(axes: new[] { 0, 0, 0, 0, -1.0, -1.0, 0, 0 }), mapping).LeftTrigger);
        Assert.Equal(255, GenericPadMapper.Map(State(axes: new[] { 0, 0, 0, 0, 1.0, 1.0, 0, 0 }), mapping).RightTrigger);

        var digital = new PadMapping { LeftTriggerButton = 6 };
        var pressed = new bool[16]; pressed[6] = true;
        Assert.Equal(255, GenericPadMapper.Map(State(pressed), digital).LeftTrigger);
    }

    [Fact]
    public void Absent_or_invalid_signed_trigger_axis_is_released_not_half_pressed()
    {
        var mapping = new PadMapping { LeftTriggerAxis = 12, RightTriggerAxis = 1, TriggerAxisIsSigned = true };
        var absent = GenericPadMapper.Map(new RawGamepadState(Array.Empty<bool>(), Array.Empty<double>(), -1), mapping);
        Assert.Equal(0, absent.LeftTrigger);
        Assert.Equal(0, absent.RightTrigger);

        var malformed = GenericPadMapper.Map(new RawGamepadState(Array.Empty<bool>(), new[] { 0.0, double.NaN }, -1), mapping);
        Assert.Equal(0, malformed.LeftTrigger);
        Assert.Equal(0, malformed.RightTrigger);

        var released = GenericPadMapper.Map(new RawGamepadState(Array.Empty<bool>(), new[] { -1.0, -1.0 }, -1),
            new PadMapping { LeftTriggerAxis = 0, RightTriggerAxis = 1, TriggerAxisIsSigned = true });
        Assert.Equal(0, released.LeftTrigger);
        Assert.Equal(0, released.RightTrigger);
    }

    [Fact]
    public void Missing_or_nonsense_inputs_read_as_centred_and_unpressed()
    {
        var mapping = TypicalLayout();
        var report = GenericPadMapper.Map(new RawGamepadState(Array.Empty<bool>(), new[] { double.NaN, double.PositiveInfinity }, -1), mapping);
        Assert.Equal(default, report);
    }

    [Fact]
    public void Negative_mapping_indices_are_ignored_even_before_validation()
    {
        var mapping = new PadMapping
        {
            Buttons = new() { [-1] = Xbox360Report.A },
            LeftXAxis = -1,
            LeftTriggerAxis = -1,
            LeftTriggerButton = -1,
        };
        Assert.NotNull(mapping.Validate());
        Assert.Equal(default, GenericPadMapper.Map(State(), mapping));
    }

    [Fact]
    public void Nonfinite_deadzone_is_rejected_by_validation()
    {
        var mapping = TypicalLayout();
        mapping.StickDeadzone = double.NaN;
        Assert.NotNull(mapping.Validate());
        mapping.StickDeadzone = double.PositiveInfinity;
        Assert.NotNull(mapping.Validate());
    }

    [Fact]
    public void Mapping_validation_rejects_multi_bit_and_out_of_range_entries()
    {
        Assert.Null(TypicalLayout().Validate());
        var bad = TypicalLayout(); bad.Buttons[0] = (ushort)(Xbox360Report.A | Xbox360Report.B);
        Assert.NotNull(bad.Validate());
        bad = TypicalLayout(); bad.LeftXAxis = 500;
        Assert.NotNull(bad.Validate());
        bad = TypicalLayout(); bad.StickDeadzone = 0.9;
        Assert.NotNull(bad.Validate());
        bad = TypicalLayout(); bad.Buttons[-1] = Xbox360Report.A;
        Assert.NotNull(bad.Validate());
    }
}
