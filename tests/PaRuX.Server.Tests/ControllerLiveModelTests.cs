using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ControllerLiveModelTests
{
    [Fact]
    public void A_newly_pressed_button_is_named_and_a_held_button_is_not()
    {
        Assert.Equal("button 2", ControllerLiveModel.DescribeChange(new[] { false, false, false }, new double[2], new[] { false, false, true }, new double[2]));
        Assert.Null(ControllerLiveModel.DescribeChange(new[] { false, false, true }, new double[2], new[] { false, false, true }, new double[2]));
    }

    [Fact]
    public void Axis_noise_is_ignored_but_a_real_move_is_named()
    {
        Assert.Null(ControllerLiveModel.DescribeChange(new bool[1], new[] { 0.0, 0.0 }, new bool[1], new[] { 0.1, -0.1 }));
        Assert.Equal("axis 1", ControllerLiveModel.DescribeChange(new bool[1], new[] { 0.0, 0.0 }, new bool[1], new[] { 0.1, 0.9 }));
    }

    [Fact]
    public void The_first_reading_names_only_pressed_buttons_never_axes()
    {
        Assert.Null(ControllerLiveModel.DescribeChange(null, null, new[] { false }, new[] { 0.9 }));
        Assert.Equal("button 0", ControllerLiveModel.DescribeChange(null, null, new[] { true }, new[] { 0.9 }));
    }

    [Fact]
    public void Stick_points_are_clamped_and_missing_axes_sit_in_the_centre()
    {
        Assert.Equal((1.0, -1.0), ControllerLiveModel.StickPoint(new[] { 3.0, -5.0 }, 0));
        Assert.Equal((0.0, 0.0), ControllerLiveModel.StickPoint(new[] { 0.5 }, 0));
        Assert.Equal((0.0, 0.0), ControllerLiveModel.StickPoint(null, 2));
        Assert.Equal((0.0, 0.0), ControllerLiveModel.StickPoint(new[] { double.NaN, double.PositiveInfinity }, 0));
    }
}
