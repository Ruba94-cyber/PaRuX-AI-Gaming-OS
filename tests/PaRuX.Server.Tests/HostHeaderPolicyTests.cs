using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F10: Host-header allow-list against DNS rebinding.</summary>
public sealed class HostHeaderPolicyTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.50.7");

    private static HostHeaderPolicy Policy(IEnumerable<string>? configured = null, DateTime? now = null,
        Func<IEnumerable<string>>? source = null)
    {
        DateTime clock = now ?? new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        return new HostHeaderPolicy(source ?? (() => configured ?? Array.Empty<string>()),
            () => new[] { Lan }, () => clock);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:51123")]
    [InlineData("localhost.:51123")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:51123")]
    [InlineData("127.5.5.5:80")]
    [InlineData("[::1]")]
    [InlineData("[::1]:51123")]
    [InlineData("parux.local:51124")]
    [InlineData("192.168.50.7:51124")]
    public void Loopback_names_and_addresses_this_machine_has_bound_are_allowed(string host) =>
        Assert.True(Policy().IsAllowed(host, IPAddress.Loopback));

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:51123")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.evil.example")]
    [InlineData("10.9.9.9")]            // a private address this PC does not own
    [InlineData("8.8.8.8")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:")]
    [InlineData("local host")]
    [InlineData("user@localhost")]
    [InlineData("localhost/x")]
    [InlineData("::1")]                  // an unbracketed IPv6 literal is not a valid Host
    [InlineData("[::1")]
    [InlineData("[evil]:80")]
    public void Anything_else_is_refused(string host) =>
        Assert.False(Policy().IsAllowed(host, IPAddress.Loopback));

    [Fact]
    public void A_missing_host_header_is_refused() => Assert.False(Policy().IsAllowed(null, IPAddress.Loopback));

    [Fact]
    public void The_address_the_connection_arrived_on_is_allowed_even_when_the_adapter_list_is_stale()
    {
        var policy = new HostHeaderPolicy(() => Array.Empty<string>(), () => Array.Empty<IPAddress>(), () => DateTime.UtcNow);
        Assert.True(policy.IsAllowed("192.168.50.7:51124", Lan));
        Assert.False(policy.IsAllowed("192.168.50.8:51124", Lan));
    }

    [Fact]
    public void Configured_names_are_allowed_and_a_configured_ip_literal_grants_nothing()
    {
        HostHeaderPolicy policy = Policy(new[] { "parux.example.org", "https://bad", "10.9.9.9", "  Alias.Home:8443 " });
        Assert.True(policy.IsAllowed("parux.example.org", IPAddress.Loopback));
        Assert.True(policy.IsAllowed("PARUX.EXAMPLE.ORG:443", IPAddress.Loopback));
        Assert.True(policy.IsAllowed("alias.home", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("10.9.9.9", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("bad", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("other.example.org", IPAddress.Loopback));
    }

    [Fact]
    public void A_failing_configuration_source_keeps_the_previous_list_and_never_opens_the_gate()
    {
        DateTime now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        bool fail = false;
        var policy = new HostHeaderPolicy(() => fail ? throw new IOException("locked") : new[] { "good.example" },
            () => new[] { Lan }, () => now);
        Assert.True(policy.IsAllowed("good.example", IPAddress.Loopback));
        fail = true;
        now = now.AddMinutes(5);
        Assert.True(policy.IsAllowed("good.example", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("evil.example", IPAddress.Loopback));
    }

    [Fact]
    public void The_configuration_is_reread_after_the_refresh_interval()
    {
        DateTime now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        var names = new List<string>();
        var policy = new HostHeaderPolicy(() => names.ToArray(), () => new[] { Lan }, () => now);
        Assert.False(policy.IsAllowed("late.example", IPAddress.Loopback));
        names.Add("late.example");
        Assert.False(policy.IsAllowed("late.example", IPAddress.Loopback)); // inside the interval
        now = now.AddMinutes(2);
        Assert.True(policy.IsAllowed("late.example", IPAddress.Loopback));
    }

    [Fact]
    public async Task A_rebound_name_is_refused_before_any_route_including_health_and_sign_in()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        foreach ((HttpMethod method, string path) in new[]
                 { (HttpMethod.Get, "/api/v1/health"), (HttpMethod.Get, "/app/login"), (HttpMethod.Post, "/api/v1/web/session"),
                   (HttpMethod.Get, "/app"), (HttpMethod.Get, "/api/v1/system/status") })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Host = "attacker.example:" + 51123;
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Regression_a_rebound_page_cannot_burn_the_shared_passphrase_lockout()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        WebAccessService web = factory.Services.GetRequiredService<WebAccessService>();
        web.Reset();
        web.SetPassphrase("correct horse battery staple");
        web.SetEnabled(true);
        try
        {
            using HttpClient client = factory.CreateClient();
            for (int i = 0; i < 12; i++)
            {
                using var bad = new HttpRequestMessage(HttpMethod.Post, "/api/v1/web/session")
                {
                    Content = JsonContent.Create(new PaRuX.Contracts.WebSessionRequest { Passphrase = "wrong " + i })
                };
                bad.Headers.Host = "attacker.example";
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(bad)).StatusCode);
            }

            HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/web/session",
                new PaRuX.Contracts.WebSessionRequest { Passphrase = "correct horse battery staple" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        finally { web.Reset(); }
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:51123")]
    [InlineData("[::1]:51123")]
    public async Task Loopback_hosts_still_reach_the_api(string host)
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Host = host;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_lan_request_with_an_unlisted_host_is_refused_and_a_listed_host_still_reaches_the_lan_gate()
    {
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<Program> lan = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HostHeaderPolicy>();
                services.AddSingleton(new HostHeaderPolicy(() => new[] { "parux.example.org" }, () => new[] { Lan }, () => DateTime.UtcNow));
                services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(IPAddress.Parse("192.168.50.20")));
            }));
        using HttpClient client = lan.CreateClient();

        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/status");
        rebound.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebound)).StatusCode);

        // A configured name passes the Host gate; the LAN gate (HTTPS, paired-device token) still refuses this plain-HTTP, tokenless call.
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/status");
        allowed.Headers.Host = "parux.example.org";
        HttpResponseMessage response = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("LAN access", await response.Content.ReadAsStringAsync());
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        private readonly IPAddress _address;
        public RemoteAddressStartupFilter(IPAddress address) => _address = address;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = _address;
                await continuation();
            });
            next(app);
        };
    }
}
