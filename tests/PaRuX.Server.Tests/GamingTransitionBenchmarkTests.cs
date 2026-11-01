using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Repeatable benchmark of the orchestration layer only (stub launcher/input, no GPU, no game, no model).
/// It times AI->Gaming and Gaming->AI cycles and writes percentiles to PARUX_BENCH_OUT when set. The
/// asserted bound is deliberately generous: it catches pathological regressions (hangs, accidental sleeps),
/// not small differences. It says nothing about FPS, VRAM or real runtime start-up cost.
/// </summary>
public sealed class GamingTransitionBenchmarkTests : IDisposable
{
    private const int Cycles = 20;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-gos-bench-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _stateScope;
    private readonly IDisposable _rootScope;

    public GamingTransitionBenchmarkTests()
    {
        Directory.CreateDirectory(_root);
        _stateScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "state"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_root, "runtime"));
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        _stateScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StubInput : IInputProvider
    {
        public string Id => "bench-input"; public string Name => "Bench input";
        public string? ExecutablePath => null; public bool IsInstalled => true;
        public Task<bool> StartAsync(GamingProfileDto p, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto p, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class StubLauncher : IGameLauncherProvider
    {
        public string Id => "bench-launcher"; public string Name => "Bench launcher";
        public string? ExecutablePath => null; public bool IsInstalled => true;
        public Task<bool> LaunchAsync(GamingProfileDto p, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto p, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class StubTrigger : IGamingTriggerProvider
    {
        public string Id => "bench-trigger"; public string Name => "Bench trigger"; public bool IsDetected => true;
        public Task<bool> IsTriggeredAsync(GamingProfileDto p, CancellationToken ct) => Task.FromResult(false);
    }

    private static double Percentile(List<double> sortedMs, double p) =>
        sortedMs[Math.Clamp((int)Math.Ceiling(p * sortedMs.Count) - 1, 0, sortedMs.Count - 1)];

    [Fact]
    public async Task Enter_and_exit_cycles_stay_fast_and_leave_no_residue()
    {
        var store = new GamingProfileStore(Path.Combine(_root, "profiles.json"));
        foreach (GamingProfileDto preset in store.List()) { preset.Enabled = false; store.Save(preset); }
        store.Save(new GamingProfileDto
        {
            Name = "Bench", Enabled = true, TriggerProvider = "bench-trigger", SessionType = "local",
            LauncherProvider = "bench-launcher", InputProvider = "bench-input", SuspendProvider = "none",
            QuickResumePolicy = "off"
        });
        var process = new LocalAiProcessManager();
        var runtime = new LocalRuntimeService(new TestHostEnvironment(), process, new ExternalTextProviderService());
        var orchestrator = new WorkloadOrchestrator(
            runtime, process, new MediaJobService(null!), new SpeechService(runtime), store,
            inputProviders: new IInputProvider[] { new StubInput() },
            launcherProviders: new IGameLauncherProvider[] { new StubLauncher() },
            triggerProviders: new IGamingTriggerProvider[] { new StubTrigger() },
            runtimeHealthCheck: _ => Task.FromResult(true),
            restoreRuntimeToReady: _ => Task.CompletedTask);

        var enter = new List<double>(Cycles);
        var exit = new List<double>(Cycles);
        for (int i = 0; i < Cycles; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.True(await orchestrator.EnterGamingSessionAsync("Bench", CancellationToken.None), $"enter failed on cycle {i}");
            enter.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal("GamingLocal", orchestrator.CurrentState);

            sw.Restart();
            Assert.True(await orchestrator.ExitGamingSessionAsync(CancellationToken.None), $"exit failed on cycle {i}");
            exit.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal("AiReady", orchestrator.CurrentState);
            Assert.False(runtime.GamingMode);
        }

        enter.Sort(); exit.Sort();
        var report = new
        {
            kind = "orchestration-only (stub providers)", cycles = Cycles,
            enterMs = new { p50 = Percentile(enter, 0.5), p95 = Percentile(enter, 0.95), max = enter[^1] },
            exitMs = new { p50 = Percentile(exit, 0.5), p95 = Percentile(exit, 0.95), max = exit[^1] },
            machine = Environment.ProcessorCount + " logical CPUs",
        };
        string? output = Environment.GetEnvironmentVariable("PARUX_BENCH_OUT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(Percentile(enter, 0.95) < 5000, "p95 enter latency regressed past 5 s with stub providers");
        Assert.True(Percentile(exit, 0.95) < 5000, "p95 exit latency regressed past 5 s with stub providers");
        Assert.False(File.Exists(LocalAiPaths.GameMarkerFile));
    }
}
