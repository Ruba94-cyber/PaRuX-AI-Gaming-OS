using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F01 unit evidence: key shape, scoping, descriptor stability and redaction. No process is started.</summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ManagedRuntimeApiKeyUnitTests : IDisposable
{
    public ManagedRuntimeApiKeyUnitTests() => ManagedRuntimeCredentials.ResetForTests();
    public void Dispose() => ManagedRuntimeCredentials.ResetForTests();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void A_key_is_256_random_bits_in_comma_free_base64url_and_never_repeats()
    {
        string a = ManagedRuntimeCredentials.NewKey();
        string b = ManagedRuntimeCredentials.NewKey();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain(',', a);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        string padded = a.Replace('-', '+').Replace('_', '/') + "=";
        Assert.Equal(32, Convert.FromBase64String(padded).Length);
    }

    [Fact]
    public void The_key_goes_into_the_child_environment_and_never_into_the_argument_list()
    {
        var start = new ProcessStartInfo("llama-server.exe");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add("1234");
        start.Environment["LLAMA_ARG_API_KEY_FILE"] = "C:/inherited.txt";
        start.Environment["LLAMA_ARG_API_KEY"] = "inherited";

        string key = ManagedRuntimeCredentials.ApplyTo(start);

        Assert.Equal(key, start.Environment["LLAMA_API_KEY"]);
        Assert.DoesNotContain(key, start.ArgumentList);
        Assert.DoesNotContain("--api-key", start.ArgumentList);
        Assert.False(start.Environment.ContainsKey("LLAMA_ARG_API_KEY_FILE"));
        Assert.False(start.Environment.ContainsKey("LLAMA_ARG_API_KEY"));
    }

    [Fact]
    public void The_launch_descriptor_is_identical_with_any_key_and_never_contains_it()
    {
        string[] args = { "--model", "C:/m.gguf", "--alias", "a", "--host", "127.0.0.1", "--port", "1234", "--ctx-size", "4096" };
        var plain = new Dictionary<string, string> { ["GGML_X"] = "1" };
        string keyA = ManagedRuntimeCredentials.NewKey();
        string keyB = ManagedRuntimeCredentials.NewKey();
        TextLaunchDescriptor none = TextLaunchDescriptor.Create("text-vulkan", args, plain).Descriptor!;
        TextLaunchDescriptor withA = TextLaunchDescriptor.Create("text-vulkan", args,
            new Dictionary<string, string>(plain) { ["LLAMA_API_KEY"] = keyA }).Descriptor!;
        TextLaunchDescriptor withB = TextLaunchDescriptor.Create("text-vulkan", args,
            new Dictionary<string, string>(plain) { ["LLAMA_API_KEY"] = keyB }).Descriptor!;

        Assert.Equal(none.Fingerprint, withA.Fingerprint);
        Assert.Equal(none.Fingerprint, withB.Fingerprint);
        string json = JsonSerializer.Serialize(withA);
        Assert.DoesNotContain(keyA, json);
        Assert.DoesNotContain("LLAMA_API_KEY", json);

        // The actual-launch view (from a ProcessStartInfo environment) behaves the same.
        var start = new ProcessStartInfo("llama-server.exe");
        ManagedRuntimeCredentials.ApplyTo(start);
        Assert.DoesNotContain(TextLaunchDescriptor.RelevantEnvironment(start.Environment).Keys,
            name => name.Equals("LLAMA_API_KEY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Internal_clients_attach_the_key_only_to_the_exact_managed_loopback_port()
    {
        var start = new ProcessStartInfo("llama-server.exe");
        ManagedRuntimeCredentials.BeginTextLaunch(start, 45678);
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(45678, out string key));
        var inner = new CapturingHandler();
        using var client = new HttpClient(new ManagedRuntimeKeyHandler(inner));

        await client.GetAsync("http://127.0.0.1:45678/props");
        Assert.Equal(new AuthenticationHeaderValue("Bearer", key), inner.Seen);

        // A forwarded caller token (for example a Core token) is replaced, never passed on to the runtime.
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:45678/props");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "parux-sk-core-token");
        await client.SendAsync(foreign);
        Assert.Equal(key, inner.Seen!.Parameter);

        inner.Seen = new AuthenticationHeaderValue("x", "y");
        await client.GetAsync("http://127.0.0.1:45679/props"); // another loopback port: no key
        Assert.Null(inner.Seen);

        inner.Seen = null;
        using var elsewhere = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid:45678/props");
        await client.SendAsync(elsewhere);
        Assert.Null(inner.Seen);

        ManagedRuntimeCredentials.RetireText();
        Assert.False(ManagedRuntimeCredentials.TryGetForPort(45678, out _));
        await client.GetAsync("http://127.0.0.1:45678/props");
        Assert.Null(inner.Seen);
    }

    [Fact]
    public void Starting_the_next_text_launch_rotates_the_key_and_retires_the_old_one()
    {
        var first = new ProcessStartInfo("llama-server.exe");
        var second = new ProcessStartInfo("llama-server.exe");
        ManagedRuntimeCredentials.BeginTextLaunch(first, 45700);
        ManagedRuntimeCredentials.TryGetForPort(45700, out string one);
        ManagedRuntimeCredentials.BeginTextLaunch(second, 45701);

        Assert.False(ManagedRuntimeCredentials.TryGetForPort(45700, out _));
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(45701, out string two));
        Assert.NotEqual(one, two);
        Assert.Equal(two, second.Environment["LLAMA_API_KEY"]);
    }

    [Fact]
    public void Redaction_removes_every_issued_key_even_after_it_was_retired()
    {
        var start = new ProcessStartInfo("llama-server.exe");
        ManagedRuntimeCredentials.BeginTextLaunch(start, 45710);
        ManagedRuntimeCredentials.TryGetForPort(45710, out string key);
        ManagedRuntimeCredentials.RetireText();

        string? redacted = ManagedRuntimeCredentials.Redact("auth failed for " + key + " twice " + key);
        Assert.DoesNotContain(key, redacted);
        Assert.Contains(ManagedRuntimeCredentials.RedactionMarker, redacted);
        Assert.Null(ManagedRuntimeCredentials.Redact(null));
    }

    [Fact]
    public void The_launch_port_comes_from_the_argument_list_and_defaults_to_the_text_port()
    {
        Assert.Equal(4321, LocalAiProcessManager.LaunchPort(new[] { "--model", "m", "--port", "4321" }));
        Assert.Equal(LocalAiPaths.TextPort, LocalAiProcessManager.LaunchPort(new[] { "--model", "m" }));
        Assert.Equal(LocalAiPaths.TextPort, LocalAiProcessManager.LaunchPort(new[] { "--port", "-1" }));
    }
}

/// <summary>
/// SEC-F01 against a real child process: PaRuX.RuntimeStub installed as llama-server enforces the bearer key the way
/// <c>llama-server --api-key</c> / <c>LLAMA_API_KEY</c> does (public: /health and /v1/models; everything else 401). The launch goes
/// through the production fresh-exact loader, so launch, health, the post-health output check and the internal clients all
/// run the shipped code.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ManagedRuntimeApiKeyIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-apikey-" + Guid.NewGuid().ToString("N"));
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-apikey-state-" + Guid.NewGuid().ToString("N"));
    private readonly int _port;
    private readonly IDisposable _rootScope;
    private readonly IDisposable _stateScope;
    private readonly IDisposable _portScope;
    private readonly string? _previousServe = Environment.GetEnvironmentVariable("PARUX_STUB_SERVE_HTTP");
    private readonly string? _previousLeak = Environment.GetEnvironmentVariable("PARUX_STUB_LEAK_KEY");

    public ManagedRuntimeApiKeyIntegrationTests()
    {
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        _rootScope = LocalAiPaths.UseRootForTests(_root);
        _stateScope = LocalAiPaths.UseStateDirForTests(_state);
        _portScope = LocalAiPaths.UseTextPortForTests(_port);
        Environment.SetEnvironmentVariable("PARUX_STUB_SERVE_HTTP", "1");
        // The stub deliberately writes the key it received to its stderr, to prove that the log path redacts it.
        Environment.SetEnvironmentVariable("PARUX_STUB_LEAK_KEY", "1");
    }

    public Task InitializeAsync()
    {
        ManagedRuntimeCredentials.ResetForTests();
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Assert.True(File.Exists(Path.Combine(staged, LocalAiPaths.LlamaServerExecutable)),
            "PaRuX.RuntimeStub was not staged; this test refuses to pass without a real child process.");
        Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*"))
            File.Copy(source, Path.Combine(LocalAiPaths.RuntimeVulkanPath, Path.GetFileName(source)), overwrite: true);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { new LocalAiProcessManager(0).StopText(); } catch { }
        LocalAiProcessManager.ClearTextLaunchIdentity();
        Environment.SetEnvironmentVariable("PARUX_STUB_SERVE_HTTP", _previousServe);
        Environment.SetEnvironmentVariable("PARUX_STUB_LEAK_KEY", _previousLeak);
        _portScope.Dispose();
        _stateScope.Dispose();
        _rootScope.Dispose();
        ManagedRuntimeCredentials.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_state, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(LocalAiProcessManager Manager, string Model)> LaunchAsync(string name, CancellationToken ct)
    {
        string model = Path.Combine(_root, "models", name + ".gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, new byte[] { 1, 2, 3 }, ct);
        var manager = new LocalAiProcessManager(0);
        LocalAiProcessManager.RuntimeResult launched = await manager.StartFreshExactTextModelAsync(model, "Standard", ct);
        Assert.True(launched.Success, launched.Message);
        return (manager, model);
    }

    private string Url(string path) => "http://127.0.0.1:" + _port + path;

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string url, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Unauthenticated_requests_to_the_runtime_port_are_refused_and_internal_clients_succeed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        (LocalAiProcessManager manager, _) = await LaunchAsync("refused", timeout.Token);
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(_port, out string key));

        // Any other local process: a bare client with no key, or a guessed one.
        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Get, Url("/props")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Post, Url("/v1/chat/completions")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Get, Url("/props"), "not-the-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Get, Url("/_stub/process")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(stranger, HttpMethod.Get, Url("/health"))); // public, like llama-server
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(stranger, HttpMethod.Get, Url("/props"), key));

        // PaRuX's own clients: no per-call key handling in their code, the shared handler does it.
        using var internalClient = new HttpClient(ManagedRuntimePeers.CreateHandler());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(internalClient, HttpMethod.Get, Url("/props")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(internalClient, HttpMethod.Post, Url("/v1/chat/completions")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(internalClient, HttpMethod.Get, Url("/props"), "parux-sk-forwarded-core-token"));
        Assert.True(await manager.IsTextHealthyAsync(CancellationToken.None));
        ActiveContextWindow.Invalidate();
        Assert.Equal(4096, await ActiveContextWindow.GetAsync(internalClient, "http://127.0.0.1:" + _port, CancellationToken.None));
        // /slots needs the key too: the quiescence probe reaches it through the attested key-carrying client.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Get, Url("/slots")));
        TextInferenceQuiescence.SlotProbe slots = await TextInferenceQuiescence.ProbeAsync(CancellationToken.None);
        Assert.Equal(TextInferenceQuiescence.SlotState.Idle, slots.State);
        // The post-health output check ran inside the launch above, through the same handler: Success already proved it.
    }

    [Fact]
    public async Task Core_reads_the_keyed_runtime_context_window_that_a_keyless_caller_cannot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await LaunchAsync("ctxwindow", timeout.Token);

        RuntimeContextWindow viaCore = await RuntimeContextWindowProbe.ReadAsync(refresh: true, timeout.Token);
        Assert.True(viaCore.Observed, viaCore.Reason);
        Assert.Equal(4096, viaCore.ContextTokens);

        // What the desktop App did before it was routed through Core: a bare client gets 401 and no usable window.
        using var stranger = new HttpClient();
        RuntimeContextWindow direct = await RuntimeContextWindowProbe.ReadAsync(
            stranger, "http://127.0.0.1:" + _port, refresh: true, timeout.Token);
        Assert.False(direct.Observed);
        Assert.Equal(0, direct.ContextTokens);
    }

    [Fact]
    public async Task The_key_never_appears_in_argv_the_descriptor_or_the_logs_and_the_descriptor_stays_stable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string model = Path.Combine(_root, "models", "leak.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, new byte[] { 4, 5, 6 }, timeout.Token);
        var manager = new LocalAiProcessManager(0);
        TextLaunchDescriptor planned = (await manager.PlanFreshExactTextLaunchAsync(model, "Standard", timeout.Token)).Descriptor!;

        Assert.True((await manager.StartFreshExactTextModelAsync(model, "Standard", timeout.Token)).Success);
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(_port, out string key));

        // What the child process itself received: key in the environment, nowhere in its command line.
        using var internalClient = new HttpClient(ManagedRuntimePeers.CreateHandler());
        using JsonDocument seen = JsonDocument.Parse(await internalClient.GetStringAsync(Url("/_stub/process")));
        Assert.True(seen.RootElement.GetProperty("environmentHasKey").GetBoolean());
        Assert.DoesNotContain(key, seen.RootElement.GetProperty("commandLine").GetString()!);
        Assert.All(seen.RootElement.GetProperty("arguments").EnumerateArray(), a => Assert.DoesNotContain(key, a.GetString()!));
        Assert.DoesNotContain("--api-key", seen.RootElement.GetProperty("commandLine").GetString()!);

        // The actual-launch fence is the planned one: the key is not part of the identity.
        TextLaunchDescriptor actual = LocalAiProcessManager.GetVerifiedTextLaunchDescriptor()!;
        Assert.Equal(planned.Fingerprint, actual.Fingerprint);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(actual));
        Assert.DoesNotContain(key, string.Join('\n', actual.Arguments) + string.Join('\n', actual.Environment));

        // Logs: the stub echoed the key to its stderr on purpose; the drained file must carry the marker, not the key.
        Assert.True(manager.StopText());
        string errLog = Path.Combine(LocalAiPaths.LogsPath, "llama-server.stderr.log");
        string text = string.Empty;
        for (int attempt = 0; attempt < 50 && !text.Contains(ManagedRuntimeCredentials.RedactionMarker); attempt++)
        {
            await Task.Delay(100);
            try { text = File.ReadAllText(errLog); } catch (IOException) { }
        }
        Assert.Contains(ManagedRuntimeCredentials.RedactionMarker, text);
        foreach (string log in Directory.EnumerateFiles(LocalAiPaths.LogsPath))
            Assert.DoesNotContain(key, File.ReadAllText(log));
        Assert.False(ManagedRuntimeCredentials.TryGetForPort(_port, out _));
    }

    [Fact]
    public async Task A_restart_rotates_the_key_and_the_old_key_is_refused_by_the_new_runtime()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        (LocalAiProcessManager manager, string model) = await LaunchAsync("rotate", timeout.Token);
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(_port, out string first));
        TextLaunchDescriptor firstDescriptor = LocalAiProcessManager.GetVerifiedTextLaunchDescriptor()!;

        Assert.True(manager.StopText());
        Assert.False(ManagedRuntimeCredentials.TryGetForPort(_port, out _));

        Assert.True((await manager.StartFreshExactTextModelAsync(model, "Standard", timeout.Token)).Success);
        Assert.True(ManagedRuntimeCredentials.TryGetForPort(_port, out string second));
        Assert.NotEqual(first, second);
        Assert.Equal(firstDescriptor.Fingerprint, LocalAiProcessManager.GetVerifiedTextLaunchDescriptor()!.Fingerprint);

        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stranger, HttpMethod.Get, Url("/props"), first));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(stranger, HttpMethod.Get, Url("/props"), second));
        using var internalClient = new HttpClient(ManagedRuntimePeers.CreateHandler());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(internalClient, HttpMethod.Get, Url("/props")));
    }
}

/// <summary>SEC-F01 regression: the router's per-model children are not verified to carry the key, so the pool stays off.</summary>
public sealed class ManagedRuntimeHotPoolKeyTests
{
    [Fact]
    public void The_hot_model_pool_is_not_started_until_router_key_propagation_is_verified()
    {
        Assert.False(LocalAiProcessManager.HotModelPoolKeyPropagationVerified);
        Assert.Contains("per-launch key", LocalAiProcessManager.HotPoolUnverifiedKeyReason, StringComparison.Ordinal);
        var ok = new LocalAiProcessManager.RuntimeResult(true, "Started: M.");
        var failed = new LocalAiProcessManager.RuntimeResult(false, "boom");
        Assert.Equal("Started: M. why", LocalAiProcessManager.AnnotateHotPoolRefusal(ok, "why").Message);
        Assert.Equal(ok, LocalAiProcessManager.AnnotateHotPoolRefusal(ok, null));
        Assert.Equal(failed, LocalAiProcessManager.AnnotateHotPoolRefusal(failed, "why"));
    }
}
