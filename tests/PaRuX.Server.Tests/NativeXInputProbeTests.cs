using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class NativeXInputProbeTests
{
    [Fact]
    public void Enumerates_only_supported_XInput_slots_and_preserves_slot_identity()
    {
        var queried = new List<int>();
        IReadOnlyList<int> connected = NativeXInputProbe.ConnectedSlots(slot =>
        {
            queried.Add(slot);
            return slot is 0 or 2 ? 0u : 1167u;
        });
        Assert.Equal(new[] { 0, 1, 2, 3 }, queried);
        Assert.Equal(new[] { 0, 2 }, connected);
    }

    [Theory]
    [InlineData(0, 0, 7849)]
    [InlineData(100, 100, 7849)]
    [InlineData(7000, 3000, 7849)]
    public void Stick_input_within_radial_deadzone_is_zero(short x, short y, int deadzone)
    {
        var result = NativeXInputProbe.NormalizeStick(x, y, deadzone);
        Assert.Equal(0f, result.X);
        Assert.Equal(0f, result.Y);
    }

    [Fact]
    public void Stick_at_full_deflection_is_normalized_without_overflow()
    {
        var right = NativeXInputProbe.NormalizeStick(short.MaxValue, 0, 7849);
        var left = NativeXInputProbe.NormalizeStick(short.MinValue, 0, 7849);
        Assert.InRange(right.X, 0.999f, 1f);
        Assert.InRange(left.X, -1f, -0.999f);
        Assert.Equal(0f, right.Y);
        Assert.Equal(0f, left.Y);
    }

    [Fact]
    public void Diagonal_stick_is_normalized_radially_not_by_individual_axes()
    {
        var normalized = NativeXInputProbe.NormalizeStick(20000, 20000, 7849);
        Assert.True(normalized.X > 0);
        Assert.True(normalized.Y > 0);
        Assert.InRange(Math.Sqrt(normalized.X * normalized.X + normalized.Y * normalized.Y), 0, 1.00001);
        Assert.Equal(normalized.X, normalized.Y);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32767)]
    [InlineData(99999)]
    public void Impossible_deadzone_settings_are_refused(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeXInputProbe.NormalizeStick(100, 100, value));

    [Fact]
    public void No_connected_slots_does_not_activate_or_install_virtual_input()
    {
        Assert.Empty(NativeXInputProbe.ConnectedSlots(_ => 1167u));
        Assert.Empty(NativeXInputProbe.ConnectedSlots(_ => 5u));
    }
}
