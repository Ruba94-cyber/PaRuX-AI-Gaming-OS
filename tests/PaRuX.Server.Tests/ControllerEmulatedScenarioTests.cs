using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// EMULATED controller scenarios (fake slot provider and raw key codes). No hardware, driver, ViGEm,
/// Steam or Bluetooth is touched. Physical-button behaviour is verified separately (BLOCKED-HW).
/// </summary>
public sealed class ControllerEmulatedScenarioTests
{
    private static ControllerProfileMath.RawPad Idle() => new(0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Two_controllers_keep_independent_slot_identity_and_state()
    {
        var states = NativeXInputProbe.ReadStates(null, slot => slot switch
        {
            0 => Idle() with { Buttons = 0x1000 },          // A
            2 => Idle() with { Buttons = 0x2000, LeftTrigger = 255 }, // B + LT
            _ => null,
        });
        Assert.Equal(new[] { 0, 2 }, states.Select(s => s.Slot));
        Assert.Equal((ushort)0x1000, states[0].Buttons);
        Assert.Equal((ushort)0x2000, states[1].Buttons);
        Assert.Equal(0f, states[0].LeftTrigger);
        Assert.Equal(1f, states[1].LeftTrigger);
    }

    [Fact]
    public void Hot_unplug_between_reads_drops_only_that_slot_and_replug_restores_it()
    {
        bool slot1Present = true;
        ControllerProfileMath.RawPad? Read(int s) => s is 0 || (s == 1 && slot1Present) ? Idle() : null;
        Assert.Equal(2, NativeXInputProbe.ReadStates(null, Read).Count);
        slot1Present = false;
        Assert.Equal(new[] { 0 }, NativeXInputProbe.ReadStates(null, Read).Select(s => s.Slot));
        slot1Present = true;
        Assert.Equal(new[] { 0, 1 }, NativeXInputProbe.ReadStates(null, Read).Select(s => s.Slot));
    }

    [Fact]
    public void Idle_pad_with_typical_stick_noise_reports_no_phantom_input()
    {
        var noisy = Idle() with { LeftX = 3000, LeftY = -2500, RightX = -4000, RightY = 3500, LeftTrigger = 12, RightTrigger = 20 };
        var s = Assert.Single(NativeXInputProbe.ReadStates(null, slot => slot == 0 ? noisy : null));
        Assert.Equal(0, s.Buttons);
        Assert.Equal((0f, 0f, 0f, 0f), (s.LeftX, s.LeftY, s.RightX, s.RightY));
        Assert.Equal((0f, 0f), (s.LeftTrigger, s.RightTrigger));
    }

    [Fact]
    public async Task Worn_stick_drift_above_default_deadzone_is_visible_and_calibration_removes_it()
    {
        var drift = Idle() with { LeftX = 9000, LeftY = 0 };
        var before = Assert.Single(NativeXInputProbe.ReadStates(null, s => s == 0 ? drift : null));
        Assert.NotEqual(0f, before.LeftX);
        var cal = await NativeXInputProbe.CalibrateAsync(0, CancellationToken.None, s => s == 0 ? drift : null, 12, TimeSpan.Zero);
        Assert.NotNull(cal);
        var profile = new GamingControllerProfile { LeftDeadzone = cal!.Value.Left, RightDeadzone = cal.Value.Right };
        Assert.Null(profile.Validate());
        var after = Assert.Single(NativeXInputProbe.ReadStates(profile, s => s == 0 ? drift : null));
        Assert.Equal(0f, after.LeftX);
    }

    [Fact]
    public void Extreme_raw_values_never_overflow_or_produce_NaN()
    {
        var max = new ControllerProfileMath.RawPad(0xFFFF, 255, 255, short.MinValue, short.MinValue, short.MaxValue, short.MaxValue);
        var s = Assert.Single(NativeXInputProbe.ReadStates(null, slot => slot == 3 ? max : null));
        foreach (float v in new[] { s.LeftX, s.LeftY, s.RightX, s.RightY, s.LeftTrigger, s.RightTrigger })
        {
            Assert.False(float.IsNaN(v) || float.IsInfinity(v));
            Assert.InRange(v, -1f, 1f);
        }
    }

    [Fact]
    public void Slot_read_failure_for_an_unplugged_pad_does_not_hide_other_pads()
    {
        var states = NativeXInputProbe.ReadStates(null, slot => slot == 1 ? null : Idle());
        Assert.Equal(new[] { 0, 2, 3 }, states.Select(s => s.Slot));
    }

    // Windows.System.VirtualKey gamepad codes (SDK-stable): A195 B196 X197 Y198 RS199 LS200 LT201 RT202
    // Up203 Down204 Left205 Right206 Menu207 View208 LSB209 RSB210 LStick U211 D212 R213 L214 RStick U215 D216.
    [Theory]
    [InlineData(195)] [InlineData(197)] [InlineData(198)] [InlineData(201)] [InlineData(202)]
    [InlineData(208)] [InlineData(209)] [InlineData(210)] [InlineData(217)] [InlineData(218)]
    public void Face_trigger_view_and_thumb_buttons_never_navigate_exit_or_page(int key)
    {
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(key, false));
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(key, true));
    }

    [Theory]
    [InlineData(211, ConsoleNavigationAction.Up)] [InlineData(212, ConsoleNavigationAction.Down)]
    [InlineData(213, ConsoleNavigationAction.Right)] [InlineData(214, ConsoleNavigationAction.Left)]
    [InlineData(203, ConsoleNavigationAction.Up)] [InlineData(204, ConsoleNavigationAction.Down)]
    [InlineData(205, ConsoleNavigationAction.Left)] [InlineData(206, ConsoleNavigationAction.Right)]
    public void Left_stick_and_dpad_directions_match_the_windows_virtual_key_layout(int key, ConsoleNavigationAction expected)
    {
        Assert.Equal(expected, ConsoleNavigationPolicy.Resolve(key, false));
        Assert.Equal(expected, ConsoleNavigationPolicy.Resolve(key, true)); // direction keeps working inside dialogs
    }

    [Fact]
    public void Held_B_cannot_exit_twice_while_a_command_is_busy()
    {
        Assert.Equal(ConsoleNavigationAction.Exit, ConsoleNavigationPolicy.Resolve(196, false));
        for (int i = 0; i < 50; i++)
            Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(196, true));
    }
}
