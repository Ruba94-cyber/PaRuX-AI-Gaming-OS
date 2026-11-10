using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseKeyComboTests
{
    private sealed class Driver : IComputerUseInputDriver
    {
        public List<(ushort Key, uint Flags)> Events = new();
        public Func<ushort, uint, bool> Accepted = (_, _) => true;
        public Func<ushort, uint, bool> Throws = (_, _) => false;
        public Action? AfterSend;
        public bool MoveCursor(int x, int y) => true;
        public uint SendMouseEvent(uint flags, uint data) => 1;
        public uint SendKeyboardEvent(ushort key, ushort scan, uint flags)
        {
            Events.Add((key, flags));
            if (Throws(key, flags)) throw new InvalidOperationException("simulated driver failure");
            bool accepted = Accepted(key, flags);
            AfterSend?.Invoke();
            return accepted ? 1u : 0u;
        }
    }

    [Fact]
    public async Task Focus_or_authorization_loss_after_first_key_stops_and_releases_it()
    {
        var driver = new Driver();
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 },
            () => driver.Events.Count == 0, CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Revoked, result.Outcome);
        Assert.Equal(1, result.PressedCount);
        Assert.True(result.AllReleasesAccepted);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Cancellation_between_keydowns_still_releases_pressed_keys()
    {
        using var cts = new CancellationTokenSource();
        var driver = new Driver { AfterSend = () => cts.Cancel() };
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, cts.Token);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.PressedCount);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Failed_keydown_reports_partial_delivery_and_releases_previous_keys()
    {
        var driver = new Driver { Accepted = (key, flags) => key != 83 || flags != 0 };
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Blocked, result.Outcome);
        Assert.Equal(1, result.PressedCount);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)83, 0u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Failed_release_does_not_prevent_other_releases()
    {
        var driver = new Driver { Accepted = (key, flags) => key != 83 || flags != 2 };
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Delivered, result.Outcome);
        Assert.Equal(2, result.PressedCount);
        Assert.False(result.AllReleasesAccepted);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)83, 0u), ((ushort)83, 2u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Callback_exception_reports_error_and_releases_previous_key()
    {
        var driver = new Driver();
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 },
            () => driver.Events.Count == 0 ? true : throw new InvalidOperationException("revoked"), CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Error, result.Outcome);
        Assert.Equal(1, result.PressedCount);
        Assert.False(result.DeliveryUncertain);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Driver_exception_reports_uncertain_delivery_and_attempts_every_release()
    {
        var driver = new Driver { Throws = (key, flags) => key == 83 && flags == 0 };
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Error, result.Outcome);
        Assert.Equal(1, result.PressedCount);
        Assert.True(result.DeliveryUncertain);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)83, 0u), ((ushort)83, 2u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Release_exception_is_reported_and_remaining_releases_are_attempted()
    {
        var driver = new Driver { Throws = (key, flags) => key == 83 && flags == 2 };
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, CancellationToken.None);
        Assert.False(result.AllReleasesAccepted);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)83, 0u), ((ushort)83, 2u), ((ushort)17, 2u) }, driver.Events);
    }

    [Fact]
    public async Task Complete_combo_releases_in_reverse_order()
    {
        var driver = new Driver();
        var result = await ComputerUseKeyboard.PressComboAsync(driver, new ushort[] { 17, 83 }, () => true, CancellationToken.None);
        Assert.Equal(ComputerUseKeyboard.ComboOutcome.Delivered, result.Outcome);
        Assert.True(result.AllReleasesAccepted);
        Assert.Equal(new[] { ((ushort)17, 0u), ((ushort)83, 0u), ((ushort)83, 2u), ((ushort)17, 2u) }, driver.Events);
    }
}
