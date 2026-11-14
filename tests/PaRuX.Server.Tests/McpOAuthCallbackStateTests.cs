using System.Net;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Security review follow-up: the loopback OAuth callback only accepts the response that carries PaRuX's own state.</summary>
public sealed class McpOAuthCallbackStateTests
{
    [Theory]
    [InlineData("https://auth.example/authorize?response_type=code&state=abc123&x=1", "abc123")]
    [InlineData("https://auth.example/authorize?state=a%20b%2Bc", "a b+c")]
    [InlineData("https://auth.example/authorize?response_type=code", null)]
    [InlineData("https://auth.example/authorize?mystate=zzz", null)]
    public void The_expected_state_is_read_from_the_authorization_url(string url, string? expected) =>
        Assert.Equal(expected, McpOAuthCoordinator.ExpectedState(new Uri(url)));

    private static async Task<HttpStatusCode> Get(Uri uri, string pathAndQuery)
    {
        using var http = new HttpClient();
        using HttpResponseMessage response = await http.GetAsync(new Uri(uri, pathAndQuery));
        return response.StatusCode;
    }

    [Fact]
    public async Task A_request_without_the_expected_state_cannot_inject_a_code_or_end_the_flow()
    {
        Uri redirect = McpOAuthCoordinator.AllocateLoopbackRedirect();
        Task<string?> wait = McpOAuthCoordinator.WaitForCodeAsync(redirect, "real-state", CancellationToken.None, TimeSpan.FromSeconds(20));
        await Task.Delay(300);

        // A stray local process or a web page probing loopback: wrong state, no state, wrong path.
        Assert.Equal(HttpStatusCode.BadRequest, await Get(redirect, "?code=attacker&state=guess"));
        Assert.Equal(HttpStatusCode.BadRequest, await Get(redirect, "?code=attacker"));
        Assert.False(wait.IsCompleted);

        // The genuine redirect still completes the flow afterwards.
        Assert.Equal(HttpStatusCode.OK, await Get(redirect, "?code=genuine&state=real-state"));
        Assert.Equal("genuine", await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_genuine_error_redirect_ends_the_flow_without_a_code()
    {
        Uri redirect = McpOAuthCoordinator.AllocateLoopbackRedirect();
        Task<string?> wait = McpOAuthCoordinator.WaitForCodeAsync(redirect, "s1", CancellationToken.None, TimeSpan.FromSeconds(20));
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.OK, await Get(redirect, "?error=access_denied&state=s1"));
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Without_any_genuine_redirect_the_wait_times_out_with_no_code()
    {
        Uri redirect = McpOAuthCoordinator.AllocateLoopbackRedirect();
        Task<string?> wait = McpOAuthCoordinator.WaitForCodeAsync(redirect, "s2", CancellationToken.None, TimeSpan.FromMilliseconds(600));
        await Task.Delay(200);
        Assert.Equal(HttpStatusCode.BadRequest, await Get(redirect, "?code=x&state=nope"));
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task An_authorization_url_without_state_still_accepts_the_redirect_for_servers_that_send_none()
    {
        Uri redirect = McpOAuthCoordinator.AllocateLoopbackRedirect();
        Task<string?> wait = McpOAuthCoordinator.WaitForCodeAsync(redirect, null, CancellationToken.None, TimeSpan.FromSeconds(20));
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.OK, await Get(redirect, "?code=c1"));
        Assert.Equal("c1", await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
