using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Services;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// SEC-F01 follow-up: desktop clients never call the keyed managed runtime port. The coding agent's context budget asks
/// Core (desktop-owner endpoint), a failed/denied read stays "unknown" instead of being cached as a value, and the
/// hot-pool router, whose per-model children cannot be verified to carry the key, is not started.
/// </summary>
public sealed class ManagedRuntimeBoundaryRoutingTests
{
    private static string Source(params string[] relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(Path.Combine(relative));
    }

    [Fact]
    public async Task Context_budget_comes_from_the_Core_source_and_is_cached_within_the_ttl()
    {
        int calls = 0;
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new CodingContextBudgetCache((refresh, _) => { calls++; return Task.FromResult(32768); },
            TimeSpan.FromSeconds(30), () => now);

        Assert.Equal(32768, await cache.GetTokensAsync(CancellationToken.None));
        Assert.Equal(32768, await cache.GetTokensAsync(CancellationToken.None));
        Assert.Equal(1, calls);

        now = now.AddSeconds(31);
        Assert.Equal(32768, await cache.GetTokensAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_denied_or_failed_Core_read_is_unknown_and_not_cached()
    {
        int calls = 0;
        var cache = new CodingContextBudgetCache((_, _) =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("Model role request failed (401)");
            return Task.FromResult(16384);
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(0, await cache.GetTokensAsync(CancellationToken.None));
        Assert.False(CodingContextBudgetPolicy.ForContextTokens(0).Observed);
        Assert.Equal(16384, await cache.GetTokensAsync(CancellationToken.None)); // recovered on the very next ask
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidate_forces_a_refetch_and_asks_Core_to_drop_its_own_cache_exactly_once()
    {
        var refreshFlags = new List<bool>();
        var cache = new CodingContextBudgetCache((refresh, _) => { refreshFlags.Add(refresh); return Task.FromResult(8192); },
            TimeSpan.FromSeconds(30));

        await cache.GetTokensAsync(CancellationToken.None);
        cache.Invalidate();
        await cache.GetTokensAsync(CancellationToken.None);
        await cache.GetTokensAsync(CancellationToken.None); // cached again

        Assert.Equal(new[] { false, true }, refreshFlags);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_refresh_request_for_the_next_attempt()
    {
        var refreshFlags = new List<bool>();
        var cache = new CodingContextBudgetCache((refresh, _) =>
        {
            refreshFlags.Add(refresh);
            if (refreshFlags.Count == 1) throw new HttpRequestException("core down");
            return Task.FromResult(4096);
        }, TimeSpan.FromSeconds(30));

        cache.Invalidate();
        Assert.Equal(0, await cache.GetTokensAsync(CancellationToken.None));
        Assert.Equal(4096, await cache.GetTokensAsync(CancellationToken.None));
        Assert.Equal(new[] { true, true }, refreshFlags);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var cache = new CodingContextBudgetCache((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }, TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetTokensAsync(cts.Token));
    }

    [Fact]
    public void The_coding_budget_resolver_has_no_direct_runtime_access()
    {
        string source = Source("src", "PaRuX.App", "Services", "CodingContextBudgetResolver.cs");
        Assert.DoesNotContain("/props", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagedRuntimeEndpoint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.Contains("GetRuntimeContextWindowAsync", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/runtime/context-window", Source("src", "PaRuX.App", "Services", "ModelRolesClient.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_desktop_or_cli_code_reaches_the_managed_runtime_routes_directly()
    {
        // The only App code that reads the managed provider endpoint is the settings diagnostics label, which displays it
        // and never requests it. Everything else goes through Core (control-plane client or gateway).
        foreach (string file in new[]
        {
            "CodingContextBudgetResolver.cs", "ChatClient.cs", "StructuredChatClient.cs", "ChatSession.cs",
            "EmbeddingClient.cs", "CodingAgentService.cs"
        })
        {
            string source = Source("src", "PaRuX.App", "Services", file);
            Assert.DoesNotContain("GetManagedProviderEndpointAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CurrentManagedProviderEndpoint", source, StringComparison.Ordinal);
        }
        string settings = Source("src", "PaRuX.App", "Views", "SettingsView.xaml.cs");
        Assert.DoesNotContain("GetAsync(endpoint", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("TextPort", Source("src", "PaRuX.Cli", "Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_runtime_Core_reports_an_unknown_window_with_a_reason_and_never_a_guess()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        using var stranger = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        ActiveContextWindow.Invalidate();
        RuntimeContextWindow window = await RuntimeContextWindowProbe.ReadAsync(
            stranger, "http://127.0.0.1:" + port, refresh: true, CancellationToken.None);

        Assert.False(window.Observed);
        Assert.Equal(0, window.ContextTokens);
        Assert.False(string.IsNullOrWhiteSpace(window.Reason));
    }

    [Fact]
    public void The_hot_model_pool_is_not_started_until_router_key_propagation_is_verified()
    {
        Assert.False(LocalAiProcessManager.HotModelPoolKeyPropagationVerified);
        Assert.Contains("per-launch key", LocalAiProcessManager.HotPoolUnverifiedKeyReason, StringComparison.Ordinal);

        var ok = new LocalAiProcessManager.RuntimeResult(true, "Started: M.");
        Assert.Equal("Started: M. why", LocalAiProcessManager.AnnotateHotPoolRefusal(ok, "why").Message);
        Assert.Equal(ok, LocalAiProcessManager.AnnotateHotPoolRefusal(ok, null));
        var failed = new LocalAiProcessManager.RuntimeResult(false, "boom");
        Assert.Equal(failed, LocalAiProcessManager.AnnotateHotPoolRefusal(failed, "why"));
    }
}

/// <summary>The Core endpoint itself, through the real HTTP pipeline.</summary>
public sealed class RuntimeContextWindowEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public RuntimeContextWindowEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task The_endpoint_requires_the_desktop_token_and_answers_unknown_when_no_runtime_serves()
    {
        using HttpClient anonymous = _factory.CreateClient();
        HttpStatusCode denied = (await anonymous.GetAsync("/api/v1/runtime/context-window")).StatusCode;
        Assert.True(denied is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, denied.ToString());

        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(port);
        using HttpClient desktop = _factory.CreateClient();
        desktop.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        using HttpResponseMessage response = await desktop.GetAsync("/api/v1/runtime/context-window?refresh=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RuntimeContextWindow body = (await response.Content.ReadFromJsonAsync<RuntimeContextWindow>())!;
        Assert.False(body.Observed);
        Assert.Equal(0, body.ContextTokens);
        Assert.False(string.IsNullOrWhiteSpace(body.Reason));
    }
}
