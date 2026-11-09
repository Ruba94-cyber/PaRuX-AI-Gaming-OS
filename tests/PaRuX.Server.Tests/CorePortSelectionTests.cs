using System.Net;
using System.Net.Sockets;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CorePortSelectionTests
{
    [Fact]
    public void Core_falls_back_to_a_bindable_loopback_port_when_the_preferred_port_is_unavailable()
    {
        TcpListener? holder = null;
        try
        {
            try { holder = new TcpListener(IPAddress.Loopback, ServerState.PreferredPort); holder.Start(); }
            catch (SocketException) { holder = null; /* already reserved/in use on this host: same scenario */ }

            int port = ServerState.ChooseCorePort();

            Assert.NotEqual(ServerState.PreferredPort, port);
            Assert.Equal(port, ServerState.CorePort);
            Assert.Equal("http://127.0.0.1:" + port, new ServerState().Endpoint);
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
        }
        finally
        {
            holder?.Stop();
            ServerState.ChooseCorePort();   // restore the normal selection for other tests
        }
    }

    [Fact]
    public void Core_uses_the_preferred_port_when_it_can_be_bound()
    {
        bool bindable;
        try { var p = new TcpListener(IPAddress.Loopback, ServerState.PreferredPort); p.Start(); p.Stop(); bindable = true; }
        catch (SocketException) { bindable = false; }
        if (!bindable) return;   // reserved on this host; covered by the fallback test

        Assert.Equal(ServerState.PreferredPort, ServerState.ChooseCorePort());
    }
}
