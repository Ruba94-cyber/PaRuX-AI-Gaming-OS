using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// SEC-F05: a browser session is default-deny. Fixtures/web-session-route-decisions.txt records a reviewed decision
/// (ALLOW or DENY) for every registered route; a route added without a decision fails this test, as does a stale entry
/// or a decision that disagrees with <see cref="WebSessionEndpointPolicy"/>. Set PARUX_WRITE_ROUTE_BASELINE=1 to regenerate
/// the file, then review the diff: only the routes WebClient/index.html calls may be ALLOW.
/// </summary>
public sealed class WebSessionRouteDecisionTests : IDisposable
{
    private const string GoodPassphrase = "correct horse battery staple";
    private readonly ApiSecurityTests.PaRuXFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static string Concrete(string pattern)
    {
        string p = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{\*\*?[^}]*\}", "x");
        return System.Text.RegularExpressions.Regex.Replace(p, @"\{[^}]*\}", "x");
    }

    private SortedSet<string> Actual()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (RouteEndpoint e in _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            string pattern = e.RoutePattern.RawText ?? string.Empty;
            IReadOnlyList<string> methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "ANY" };
            foreach (string m in methods)
            {
                bool allowed = WebSessionEndpointPolicy.IsAllowed(m, new PathString(Concrete(pattern)));
                set.Add((allowed ? "ALLOW " : "DENY ") + m + " " + pattern);
            }
        }
        return set;
    }

    [Fact]
    public void Every_registered_route_has_a_reviewed_browser_session_decision()
    {
        SortedSet<string> actual = Actual();
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-session-route-decisions.txt");
        if (Environment.GetEnvironmentVariable("PARUX_WRITE_ROUTE_BASELINE") == "1")
        {
            string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "web-session-route-decisions.txt"));
            File.WriteAllLines(source, actual);
            return;
        }

        var baseline = new SortedSet<string>(File.ReadAllLines(path).Where(l => l.Length > 0), StringComparer.Ordinal);
        string[] unreviewed = actual.Except(baseline).ToArray();
        string[] stale = baseline.Except(actual).ToArray();
        Assert.True(unreviewed.Length == 0,
            "Routes without a reviewed browser-session decision (add them to Fixtures/web-session-route-decisions.txt, ALLOW only if WebClient needs them):\n" + string.Join("\n", unreviewed));
        Assert.True(stale.Length == 0, "Baseline entries for routes that no longer exist or changed decision:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void Only_the_routes_the_served_client_uses_are_allowed()
    {
        string[] allowed = Actual().Where(l => l.StartsWith("ALLOW ", StringComparison.Ordinal)).ToArray();
        Assert.True(allowed.Length is > 0 and <= 20, "Browser-session allowlist grew to " + allowed.Length + "; review it.");
        Assert.DoesNotContain(allowed, l => l.Contains("/api/v1/remote", StringComparison.Ordinal)
            || l.Contains("/mcp", StringComparison.Ordinal) || l.Contains("/skills", StringComparison.Ordinal)
            || l.Contains("/rag/", StringComparison.Ordinal) || l.Contains("autopilot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST", "/api/v1/internet-not-the-route")]
    [InlineData("DELETE", "/api/v1/skills/abc")]
    [InlineData("DELETE", "/api/v1/rag/sources/abc")]
    [InlineData("POST", "/api/v1/autopilot/runs/abc/approve")]
    [InlineData("GET", "/api/v1/conversations/a/b")]
    public void Policy_denies_everything_not_listed(string method, string path) =>
        Assert.False(WebSessionEndpointPolicy.IsAllowed(method, new PathString(path)));

    [Theory]
    [InlineData("DELETE", "/api/v1/skills/abc")]
    [InlineData("DELETE", "/api/v1/rag/sources/abc")]
    [InlineData("POST", "/api/v1/autopilot/runs/abc/approve")]
    [InlineData("POST", "/api/v1/skills")]
    public async Task A_signed_in_browser_session_gets_403_on_a_route_outside_the_allowlist(string method, string path)
    {
        WebAccessService web = _factory.Services.GetRequiredService<WebAccessService>();
        web.Reset();
        web.SetPassphrase(GoodPassphrase);
        web.SetEnabled(true);
        try
        {
            using HttpClient browser = _factory.CreateClient();
            HttpResponseMessage login = await browser.PostAsJsonAsync("/api/v1/web/session", new WebSessionRequest { Passphrase = GoodPassphrase });
            login.EnsureSuccessStatusCode();
            string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
            string csrf = (await login.Content.ReadFromJsonAsync<WebSessionResponse>())!.CsrfToken!;

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add(WebAccessService.CsrfHeaderName, csrf);
            if (method == "POST") request.Content = JsonContent.Create(new { });
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(request)).StatusCode);

            using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/conversations");
            allowed.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(allowed)).StatusCode);
        }
        finally { web.Reset(); }
    }
}
