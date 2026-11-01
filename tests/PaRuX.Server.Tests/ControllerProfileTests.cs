using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ControllerProfileTests
{
    private static ControllerProfileMath.RawPad Pad(ushort buttons = 0, byte lt = 0, byte rt = 0,
        short lx = 0, short ly = 0, short rx = 0, short ry = 0) => new(buttons, lt, rt, lx, ly, rx, ry);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "parux-ctl-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Default_profile_is_valid_and_keeps_reference_deadzones()
    {
        Assert.Null(GamingControllerProfile.Default.Validate());
        var state = ControllerProfileMath.Apply(0, Pad(lx: 7000, ly: 0, rx: 8000), GamingControllerProfile.Default);
        Assert.Equal(0f, state.LeftX);
        Assert.Equal(0f, state.RightX);
    }

    [Fact]
    public void Custom_deadzone_and_invert_are_applied()
    {
        var profile = new GamingControllerProfile { LeftDeadzone = 2000, InvertLeftY = true };
        var state = ControllerProfileMath.Apply(2, Pad(ly: 32767), profile);
        Assert.Equal(2, state.Slot);
        Assert.True(state.LeftY < -0.99f);
    }

    [Fact]
    public void Trigger_threshold_rescales_so_release_is_zero_and_full_is_one()
    {
        var profile = new GamingControllerProfile { TriggerThreshold = 50 };
        Assert.Equal(0f, ControllerProfileMath.Apply(0, Pad(lt: 50), profile).LeftTrigger);
        Assert.Equal(1f, ControllerProfileMath.Apply(0, Pad(rt: 255), profile).RightTrigger);
    }

    [Fact]
    public void Button_remap_swaps_a_and_b_without_touching_other_buttons()
    {
        var profile = new GamingControllerProfile
        {
            ButtonRemap = new() { [0x1000] = 0x2000, [0x2000] = 0x1000 }
        };
        Assert.Null(profile.Validate());
        Assert.Equal(0x2000 | 0x0100, ControllerProfileMath.Apply(0, Pad(buttons: 0x1000 | 0x0100), profile).Buttons);
        Assert.Equal(0x1000, ControllerProfileMath.Apply(0, Pad(buttons: 0x2000), profile).Buttons);
    }

    [Theory]
    [InlineData(-1, 8689, 30)]
    [InlineData(7849, 20001, 30)]
    [InlineData(7849, 8689, 255)]
    public void Out_of_range_values_are_rejected(int left, int right, int trigger) =>
        Assert.NotNull(new GamingControllerProfile { LeftDeadzone = left, RightDeadzone = right, TriggerThreshold = trigger }.Validate());

    [Fact]
    public void Multi_bit_unknown_or_colliding_remaps_are_rejected()
    {
        Assert.NotNull(new GamingControllerProfile { ButtonRemap = new() { [0x3000] = 0x1000 } }.Validate());
        Assert.NotNull(new GamingControllerProfile { ButtonRemap = new() { [0x1000] = 0x0400 } }.Validate());
        Assert.NotNull(new GamingControllerProfile { ButtonRemap = new() { [0x1000] = 0x2000, [0x4000] = 0x2000 } }.Validate());
    }

    [Fact]
    public void Store_round_trips_and_rejects_invalid_without_persisting()
    {
        string path = TempFile();
        try
        {
            var store = new ControllerProfileStore(path);
            Assert.Null(store.Save(new GamingControllerProfile { LeftDeadzone = 4000, ButtonRemap = new() { [0x1000] = 0x2000 } }));
            var reloadedStore = new ControllerProfileStore(path); Assert.True(reloadedStore.LoadError is null, reloadedStore.LoadError + " :: " + File.ReadAllText(path)); var reloaded = reloadedStore.Get();
            Assert.Equal(4000, reloaded.LeftDeadzone);
            Assert.Equal((ushort)0x2000, reloaded.ButtonRemap[0x1000]);

            Assert.NotNull(store.Save(new GamingControllerProfile { LeftDeadzone = 99999 }));
            Assert.Equal(4000, new ControllerProfileStore(path).Get().LeftDeadzone);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_store_file_falls_back_to_defaults_and_is_not_overwritten()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "{ definitely not json");
            var store = new ControllerProfileStore(path);
            Assert.Equal(GamingControllerProfile.DefaultLeftDeadzone, store.Get().LeftDeadzone);
            Assert.NotNull(store.LoadError);
            Assert.Equal("{ definitely not json", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reset_restores_defaults_and_removes_the_file()
    {
        string path = TempFile();
        var store = new ControllerProfileStore(path);
        store.Save(new GamingControllerProfile { LeftDeadzone = 4000 });
        store.Reset();
        Assert.False(File.Exists(path));
        Assert.Equal(GamingControllerProfile.DefaultLeftDeadzone, store.Get().LeftDeadzone);
    }

    [Fact]
    public void Read_states_skips_absent_slots_and_uses_the_profile()
    {
        var states = NativeXInputProbe.ReadStates(
            new GamingControllerProfile { LeftDeadzone = 0 },
            slot => slot == 1 ? Pad(lx: 32767) : null);
        var only = Assert.Single(states);
        Assert.Equal(1, only.Slot);
        Assert.True(only.LeftX > 0.99f);
    }

    [Fact]
    public void Idle_samples_recommend_a_deadzone_above_the_drift()
    {
        var drift = Enumerable.Range(0, 20).Select(i => ((short)(i % 5 * 300), (short)0)).ToArray();
        int? value = ControllerProfileMath.RecommendDeadzone(drift);
        Assert.NotNull(value);
        Assert.True(value > 1200 && value <= GamingControllerProfile.MaxDeadzone);
        Assert.Null(ControllerProfileMath.RecommendDeadzone(drift.Take(3).ToArray()));
    }

    [Fact]
    public async Task Calibration_reads_a_bounded_number_of_samples_and_fails_when_the_pad_disappears()
    {
        int reads = 0;
        var ok = await NativeXInputProbe.CalibrateAsync(0, CancellationToken.None,
            _ => { reads++; return Pad(lx: 1000, ry: 8000); }, samples: 12, interval: TimeSpan.Zero);
        Assert.Equal(12, reads);
        Assert.NotNull(ok);
        Assert.True(ok!.Value.Right > ok.Value.Left);

        var gone = await NativeXInputProbe.CalibrateAsync(0, CancellationToken.None,
            _ => null, samples: 12, interval: TimeSpan.Zero);
        Assert.Null(gone);
        Assert.Null(await NativeXInputProbe.CalibrateAsync(9, CancellationToken.None, _ => Pad()));
    }

    [Fact]
    public async Task Calibration_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeXInputProbe.CalibrateAsync(0, cts.Token, _ => Pad(), samples: 5, interval: TimeSpan.Zero));
    }
}
