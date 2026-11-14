using System.Net;
using System.Net.Sockets;
using PaRuX.Server.Gateway;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GatewayPortProbeTests
{
    [Fact]
    public void A_loopback_port_held_by_another_socket_is_not_free()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        Assert.False(GatewaySettings.IsPortFree(port));
    }

    [Fact]
    public void A_released_loopback_port_is_free_again()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        holder.Stop();
        Assert.True(GatewaySettings.IsPortFree(port));
    }
}
