using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingProviderInjectionTests
{
    private sealed class Trigger : IGamingTriggerProvider
    {
        public string Id => "test-trigger";
        public string Name => "Test trigger";
        public bool IsDetected => true;
        public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Suspend : IGameSuspendProvider
    {
        public string Id => "test-suspend";
        public string Name => "Test suspend";
        public bool IsAvailable => true;
        public Task<bool> SuspendProcessAsync(int processId, CancellationToken ct) => throw new InvalidOperationException("No process should be touched.");
        public Task<bool> ResumeProcessAsync(int processId, CancellationToken ct) => throw new InvalidOperationException("No process should be touched.");
    }

    [Fact]
    public void Injected_trigger_and_suspend_providers_are_reported_instead_of_builtins()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-provider-seam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var process = new LocalAiProcessManager();
            var runtime = new LocalRuntimeService(new TestHostEnvironment(), process, new ExternalTextProviderService());
            var orchestrator = new WorkloadOrchestrator(runtime, process, new MediaJobService(null!), new SpeechService(runtime),
                new GamingProfileStore(Path.Combine(root, "profiles.json")), triggerProviders: new[] { new Trigger() }, suspendProviders: new[] { new Suspend() });
            var status = orchestrator.GetProviderStatuses();
            Assert.Equal("test-trigger", Assert.Single(status, p => p.Category == "trigger").Id);
            Assert.Equal("test-suspend", Assert.Single(status, p => p.Category == "suspend").Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Heuristic_game_process_cannot_be_suspended_manually_or_on_exit()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-owned-game-safety-" + Guid.NewGuid().ToString("N"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(root, "state"));
        Directory.CreateDirectory(root);
        using IDisposable finder = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => Environment.ProcessId);
        try
        {
            var store = new GamingProfileStore(Path.Combine(root, "profiles.json"));
            store.Save(new GamingProfileDto
            {
                Name = "Unverified process",
                SessionType = "local",
                LauncherProvider = "none",
                InputProvider = "native",
                SuspendProvider = "test-suspend",
                QuickResumePolicy = "always"
            });
            var process = new LocalAiProcessManager();
            var runtime = new LocalRuntimeService(new TestHostEnvironment(), process, new ExternalTextProviderService());
            var orchestrator = new WorkloadOrchestrator(
                runtime, process, new MediaJobService(null!), new SpeechService(runtime), store,
                suspendProviders: new[] { new Suspend() },
                runtimeHealthCheck: _ => Task.FromResult(true),
                restoreRuntimeToReady: _ => Task.CompletedTask);

            Assert.True(await orchestrator.EnterGamingSessionAsync("Unverified process"));
            Assert.Equal("GamingLocal", orchestrator.CurrentState);
            // The finder deliberately reports this test process. Suspending it must
            // never reach the provider, even for an explicit request or Quick Resume.
            Assert.False(await orchestrator.SuspendActiveGameAsync());
            Assert.True(await orchestrator.ExitGamingSessionAsync());
            Assert.Equal("AiReady", orchestrator.CurrentState);
            Assert.Null(orchestrator.GetStatus().SuspendedProcessId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

}
