using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F11: the one-use loopback launch code that replaced the desktop token in the query string.</summary>
public sealed class BrowserLaunchCodeSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public BrowserLaunchCodeSecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private static HttpClient Desktop(WebApplicationFactory<Program> f)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-PaRuX-Token", f.Services.GetRequiredService<ServerState>().Token);
        return c;
    }

    private static async Task<string> IssueAsync(HttpClient desktop)
    {
        using var r = await desktop.PostAsync("/api/v1/web/launch-code", null);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>();
    }

    private static string RandomCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Second_redemption_fails_and_a_different_code_is_unaffected()
    {
        var codes = new BrowserLaunchCodes();
        string a = codes.Issue(), b = codes.Issue();
        Assert.NotEqual(a, b);
        Assert.True(codes.Consume(a));
        Assert.False(codes.Consume(a));
        Assert.True(codes.Consume(b));
    }

    [Fact]
    public void Code_expires_exactly_at_the_lifetime_and_not_before()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var codes = new BrowserLaunchCodes(() => now);
        string early = codes.Issue();
        now = now.AddMinutes(2).AddTicks(-1);
        Assert.True(codes.Consume(early));
        string late = codes.Issue();
        now = now.AddMinutes(2);
        Assert.False(codes.Consume(late));
        Assert.False(codes.Consume(late));
    }

    [Fact]
    public void Expired_codes_are_purged_so_they_do_not_exhaust_the_pending_limit()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var codes = new BrowserLaunchCodes(() => now);
        for (int i = 0; i < 32; i++) codes.Issue();
        Assert.Throws<InvalidOperationException>(() => codes.Issue());
        now = now.AddMinutes(3);
        Assert.NotNull(codes.Issue());
    }

    [Fact]
    public async Task Concurrent_redemption_of_one_code_has_exactly_one_winner()
    {
        for (int round = 0; round < 20; round++)
        {
            var codes = new BrowserLaunchCodes();
            string code = codes.Issue();
            using var start = new ManualResetEventSlim(false);
            Task<bool>[] racers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return codes.Consume(code);
            })).ToArray();
            start.Set();
            bool[] results = await Task.WhenAll(racers);
            Assert.Equal(1, results.Count(r => r));
        }
    }

    [Fact]
    public void Malformed_or_guessed_codes_never_redeem_and_never_burn_a_valid_code()
    {
        var codes = new BrowserLaunchCodes();
        string valid = codes.Issue();
        Assert.Equal(64, valid.Length);
        Assert.False(codes.Consume(""));
        Assert.False(codes.Consume(valid[..63]));
        Assert.False(codes.Consume(valid + "0"));
        Assert.False(codes.Consume(valid.ToLowerInvariant()));
        for (int i = 0; i < 5000; i++) Assert.False(codes.Consume(RandomCode()));
        Assert.True(codes.Consume(valid));
    }

    [Fact]
    public async Task Pending_launches_are_capped_over_http_with_429()
    {
        using var factory = _factory.WithWebHostBuilder(_ => { });
        using HttpClient desktop = Desktop(factory);
        for (int i = 0; i < 32; i++) await IssueAsync(desktop);
        using var over = await desktop.PostAsync("/api/v1/web/launch-code", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    [Fact]
    public async Task Concurrent_http_redemption_has_exactly_one_page_with_the_token()
    {
        using HttpClient desktop = Desktop(_factory);
        string code = await IssueAsync(desktop);
        using HttpClient anonymous = _factory.CreateClient();
        HttpStatusCode[] statuses = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
            (await anonymous.GetAsync("/app?code=" + code)).StatusCode));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task Non_loopback_source_cannot_redeem_or_issue()
    {
        // The derived host keeps the same service container configuration but reports a LAN source address.
        using WebApplicationFactory<Program> lan = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new FixedAddressFilter(IPAddress.Parse("192.168.1.10")))));
        // Seed the LAN host's own store directly so the only thing standing between the caller and the page is the source check.
        string code = lan.Services.GetRequiredService<BrowserLaunchCodes>().Issue();
        string token = lan.Services.GetRequiredService<ServerState>().Token;
        using HttpClient remote = lan.CreateClient();
        using var response = await remote.GetAsync("/app?code=" + code);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpClient lanDesktop = Desktop(lan);
        Assert.NotEqual(HttpStatusCode.OK, (await lanDesktop.PostAsync("/api/v1/web/launch-code", null)).StatusCode);

        // The refused LAN attempt did not burn the code: it is still redeemable.
        Assert.True(lan.Services.GetRequiredService<BrowserLaunchCodes>().Consume(code));
    }

    [Fact]
    public async Task Code_issued_before_a_restart_is_dead_on_the_new_process()
    {
        string code;
        using (HttpClient desktop = Desktop(_factory)) code = await IssueAsync(desktop);
        using WebApplicationFactory<Program> restarted = _factory.WithWebHostBuilder(_ => { });
        using HttpClient anonymous = restarted.CreateClient();
        Assert.NotSame(_factory.Services.GetRequiredService<BrowserLaunchCodes>(),
            restarted.Services.GetRequiredService<BrowserLaunchCodes>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app?code=" + code)).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_do_not_lock_out_or_consume_a_pending_valid_code()
    {
        using HttpClient desktop = Desktop(_factory);
        string code = await IssueAsync(desktop);
        using HttpClient anonymous = _factory.CreateClient();
        for (int i = 0; i < 50; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app?code=" + RandomCode())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/app?code=" + code)).StatusCode);
    }

    [Fact]
    public async Task Query_forms_are_strict_and_the_redeemed_page_is_uncacheable_referrer_free_and_code_free()
    {
        using HttpClient desktop = Desktop(_factory);
        string code = await IssueAsync(desktop);
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app?code=" + code + "&code=" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/app?token=" + _factory.Services.GetRequiredService<ServerState>().Token)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.PostAsync("/app?code=" + code, null)).StatusCode);
        using var page = await anonymous.GetAsync("/app?code=" + code);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain(code, await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_the_token_nor_the_code_is_written_to_logs_when_the_code_is_redeemed()
    {
        var sink = new CapturingLoggerProvider();
        using WebApplicationFactory<Program> logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(sink); }));
        string token = logged.Services.GetRequiredService<ServerState>().Token;
        using HttpClient desktop = Desktop(logged);
        string code = await IssueAsync(desktop);
        using HttpClient anonymous = logged.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/app?code=" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app?code=" + code)).StatusCode);

        string all = string.Join("\n", sink.Lines);
        Assert.DoesNotContain(token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(code, all, StringComparison.Ordinal);
    }

    private sealed class FixedAddressFilter : IStartupFilter
    {
        private readonly IPAddress _address;
        public FixedAddressFilter(IPAddress address) => _address = address;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = _address; await continuation(); });
            next(app);
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }

        private sealed class Capture : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public Capture(CapturingLoggerProvider owner) => _owner = owner;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _owner.Lines.Enqueue(formatter(state, exception) + (exception?.ToString() ?? ""));
        }
    }
}
