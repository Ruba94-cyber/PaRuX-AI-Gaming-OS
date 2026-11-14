using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaRuX.Server.Gateway;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Probe-to-bind gap of the Local AI Gateway: the port is free when probed, another program takes it before Kestrel binds. A real
/// Kestrel host and a real listener race the port; the host must keep serving its main port and report the gateway as off.
/// </summary>
public sealed class GatewayBindGuardTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static WebApplication Host(int corePort, int gatewayPort, Action<string> onUnavailable)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, corePort);
            options.Listen(IPAddress.Loopback, gatewayPort);
        });
        GatewayBindGuard.Install(builder.Services, gatewayPort, onUnavailable);
        WebApplication app = builder.Build();
        app.MapGet("/ping", (HttpContext context) => context.Connection.LocalPort.ToString());
        return app;
    }

    [Fact]
    public async Task A_gateway_port_taken_after_the_probe_does_not_stop_the_host_and_is_reported_off()
    {
        int core = FreePort(), gateway = FreePort();
        Assert.True(GatewaySettings.IsPortFree(gateway)); // the probe passes ...
        using var racer = new TcpListener(IPAddress.Loopback, gateway) { ExclusiveAddressUse = true };
        racer.Start();                                    // ... and another program takes the port before the host binds it
        string? reason = null;
        await using WebApplication app = Host(core, gateway, r => reason = r);

        await app.StartAsync(); // without the guard this throws "address already in use" and the Core would exit

        using var http = new HttpClient();
        Assert.Equal(core.ToString(), await http.GetStringAsync($"http://127.0.0.1:{core}/ping"));
        Assert.Equal(GatewayBindGuard.BindFailedReason, reason);
        await app.StopAsync();
        Assert.Equal(gateway, ((IPEndPoint)racer.LocalEndpoint).Port); // the other program still owns it
    }

    [Fact]
    public async Task A_free_gateway_port_still_serves_and_reports_nothing()
    {
        int core = FreePort(), gateway = FreePort();
        string? reason = null;
        await using WebApplication app = Host(core, gateway, r => reason = r);
        await app.StartAsync();
        using var http = new HttpClient();
        Assert.Equal(gateway.ToString(), await http.GetStringAsync($"http://127.0.0.1:{gateway}/ping"));
        Assert.Equal(core.ToString(), await http.GetStringAsync($"http://127.0.0.1:{core}/ping"));
        Assert.Null(reason);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_taken_main_port_still_fails_loudly_only_the_gateway_is_tolerated()
    {
        int core = FreePort(), gateway = FreePort();
        using var racer = new TcpListener(IPAddress.Loopback, core) { ExclusiveAddressUse = true };
        racer.Start();
        await using WebApplication app = Host(core, gateway, _ => { });
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
    }

    [Fact]
    public async Task Stopping_a_host_with_an_idle_gateway_listener_completes_promptly()
    {
        int core = FreePort(), gateway = FreePort();
        using var racer = new TcpListener(IPAddress.Loopback, gateway) { ExclusiveAddressUse = true };
        racer.Start();
        await using WebApplication app = Host(core, gateway, _ => { });
        await app.StartAsync();
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
