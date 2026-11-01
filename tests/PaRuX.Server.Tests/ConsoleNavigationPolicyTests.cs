using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConsoleNavigationPolicyTests
{
    [Theory]
    [InlineData(203, ConsoleNavigationAction.Up)]
    [InlineData(211, ConsoleNavigationAction.Up)]
    [InlineData(204, ConsoleNavigationAction.Down)]
    [InlineData(212, ConsoleNavigationAction.Down)]
    [InlineData(205, ConsoleNavigationAction.Left)]
    [InlineData(214, ConsoleNavigationAction.Left)]
    [InlineData(206, ConsoleNavigationAction.Right)]
    [InlineData(213, ConsoleNavigationAction.Right)]
    public void Dpad_and_left_thumbstick_navigate_through_the_same_first_party_policy(
        int keyCode, ConsoleNavigationAction expected)
    {
        Assert.Equal(expected, ConsoleNavigationPolicy.Resolve(keyCode, interactionBusy: false));
    }

    [Theory]
    [InlineData(27)]
    [InlineData(196)]
    public void Exit_is_blocked_only_while_user_interaction_is_busy(int keyCode)
    {
        Assert.Equal(ConsoleNavigationAction.Exit, ConsoleNavigationPolicy.Resolve(keyCode, false));
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(keyCode, true));
    }

    [Theory]
    [InlineData(195)] // Gamepad A: native WinUI control owns activation
    [InlineData(13)]  // Enter
    [InlineData(197)] // Gamepad X
    [InlineData(0)]
    public void Unmapped_keys_cannot_trigger_synthetic_selection_or_exit(int keyCode)
    {
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(keyCode, false));
    }

    [Fact]
    public void Directional_input_does_not_dismiss_confirmation_dialog()
    {
        Assert.Equal(ConsoleNavigationAction.Right,
            ConsoleNavigationPolicy.Resolve(213, interactionBusy: true));
    }

    [Theory]
    [InlineData(200, ConsoleNavigationAction.PreviousPage)]
    [InlineData(199, ConsoleNavigationAction.NextPage)]
    [InlineData(215, ConsoleNavigationAction.ScrollUp)]
    [InlineData(216, ConsoleNavigationAction.ScrollDown)]
    [InlineData(207, ConsoleNavigationAction.FocusSearch)]
    public void Shoulders_right_stick_and_menu_work_only_when_idle(int keyCode, ConsoleNavigationAction expected)
    {
        Assert.Equal(expected, ConsoleNavigationPolicy.Resolve(keyCode, interactionBusy: false));
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(keyCode, interactionBusy: true));
    }
}
