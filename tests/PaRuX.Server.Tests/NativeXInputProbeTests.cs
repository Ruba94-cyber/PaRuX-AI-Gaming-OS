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

    [Fact]
    public void No_connected_slots_does_not_activate_or_install_virtual_input()
    {
        Assert.Empty(NativeXInputProbe.ConnectedSlots(_ => 1167u));
        Assert.Empty(NativeXInputProbe.ConnectedSlots(_ => 5u));
    }
}
