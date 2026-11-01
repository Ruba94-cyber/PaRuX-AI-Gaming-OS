using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Deterministic handoffs: no real launcher, game, device, or input process is touched.</summary>
public sealed class GamingTransitionConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-gos-transitions-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _stateScope;
    private readonly IDisposable _rootScope;

    public GamingTransitionConcurrencyTests()
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

    private sealed class Input : IInputProvider
    {
        public string Id => "test-input";
        public string Name => "Test input";
        public string? ExecutablePath => null;
        public bool IsInstalled => true;
        public int Starts;
        public int Stops;
        public bool StopSucceeds = true;

        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            return Task.FromResult(true);
        }

        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
        {
            Interlocked.Increment(ref Stops);
            return Task.FromResult(StopSucceeds);
        }
    }

    private sealed class Launcher : IGameLauncherProvider
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "test-launcher";
        public string Name => "Test launcher";
        public string? ExecutablePath => null;
        public bool IsInstalled => true;
        public int Launches;
        public int Stops;

        public Launcher(bool block = false)
        {
            if (!block) _release.TrySetResult(true);
        }

        public async Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
        {
            Interlocked.Increment(ref Launches);
            Started.TrySetResult(true);
            return await _release.Task.WaitAsync(ct);
        }

        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
        {
            Interlocked.Increment(ref Stops);
            return Task.FromResult(true);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class DeferredTrigger : IGamingTriggerProvider
    {
        private readonly string _id;
        private int _deferFirstCheck = 1;
        public DeferredTrigger(string id = "test-trigger") => _id = id;
        public readonly TaskCompletionSource<bool> Checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> FirstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => _id;
        public string Name => "Test trigger";
        public bool IsDetected => true;
        public bool Current;

        public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _deferFirstCheck, 0) == 1)
            {
                Checked.TrySetResult(true);
                return FirstResult.Task.WaitAsync(ct);
            }
            return Task.FromResult(Current);
        }
    }

    private (WorkloadOrchestrator Orchestrator, Launcher Launcher, Input Input) MakeOrchestrator(
        bool blockLauncher = false, DeferredTrigger? trigger = null, string sessionType = "local",
        int gracePeriodSeconds = 30, string inputProviderId = "test-input")
    {
        var store = new GamingProfileStore(Path.Combine(_root, "profiles.json"));
        foreach (GamingProfileDto preset in store.List())
        {
            preset.Enabled = false;
            store.Save(preset);
        }
        store.Save(new GamingProfileDto
        {
            Name = "Race test",
            Enabled = true,
            TriggerProvider = trigger?.Id ?? "test-trigger",
            SessionType = sessionType,
            GracePeriodSeconds = gracePeriodSeconds,
            LauncherProvider = "test-launcher",
            InputProvider = inputProviderId,
            SuspendProvider = "none",
            QuickResumePolicy = "off"
        });
        var launcher = new Launcher(blockLauncher);
        var input = new Input();
        var process = new LocalAiProcessManager();
        var runtime = new LocalRuntimeService(new TestHostEnvironment(), process, new ExternalTextProviderService());
        var orchestrator = new WorkloadOrchestrator(
            runtime, process, new MediaJobService(null!), new SpeechService(runtime), store,
            inputProviders: new IInputProvider[] { input, new NativeInputProvider(), new XOutputInputProvider(() => Array.Empty<System.Diagnostics.Process>()) },
            launcherProviders: new IGameLauncherProvider[] { launcher, new NoneLauncherProvider() },
            triggerProviders: trigger == null ? new IGamingTriggerProvider[] { new ManualTriggerProvider() } : new[] { trigger },
            runtimeHealthCheck: _ => Task.FromResult(true),
            restoreRuntimeToReady: _ => Task.CompletedTask);
        return (orchestrator, launcher, input);
    }

    [Fact]
    public async Task Unsafe_remote_xoutput_profile_rejects_entry_before_any_provider_start()
    {
        var (orchestrator, launcher, input) =
            MakeOrchestrator(sessionType: "remote", inputProviderId: "xoutput");
        Assert.False(await orchestrator.EnterGamingSessionAsync("Race test"));
        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.Equal(0, launcher.Launches);
        Assert.Equal(0, input.Starts);
    }

    [Fact]
    public async Task Failed_input_helper_stop_does_not_claim_successful_return_to_AI()
    {
        var (orchestrator, launcher, input) = MakeOrchestrator();
        Assert.True(await orchestrator.EnterGamingSessionAsync("Race test"));
        input.StopSucceeds = false;

        Assert.False(await orchestrator.ExitGamingSessionAsync());
        Assert.Equal("Error", orchestrator.CurrentState);
        Assert.Equal(1, input.Stops);
        Assert.Equal(0, launcher.Stops);
    }

    [Fact]
    public async Task A_different_active_gaming_session_does_not_grant_manual_game_launch()
    {
        var (orchestrator, launcher, _) = MakeOrchestrator();
        Assert.True(await orchestrator.EnterGamingSessionAsync("Race test"));
        Assert.False(await orchestrator.EnterManualGameSessionAsync());
        Assert.Null(orchestrator.ActiveManualGameSessionVersion);
        Assert.Equal(1, launcher.Launches);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
    }

    [Fact]
    public async Task Previous_manual_game_exit_cannot_end_a_new_manual_session()
    {
        var (orchestrator, _, _) = MakeOrchestrator();
        Assert.True(await orchestrator.EnterManualGameSessionAsync());
        long oldVersion = Assert.IsType<long>(orchestrator.ActiveManualGameSessionVersion);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
        Assert.True(await orchestrator.EnterManualGameSessionAsync());

        Assert.False(await orchestrator.ExitManualGameSessionAfterProcessExitAsync(
            oldVersion, CancellationToken.None));
        Assert.Equal("GamingLocal", orchestrator.CurrentState);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
    }

    [Fact]
    public async Task Exit_while_launcher_is_starting_waits_and_does_not_overlap_cleanup()
    {
        var (orchestrator, launcher, input) = MakeOrchestrator(blockLauncher: true);
        Task<bool> enter = orchestrator.EnterGamingSessionAsync("Race test");
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Task<bool> exit = orchestrator.ExitGamingSessionAsync();
        Assert.False(exit.IsCompleted);
        Assert.Equal(0, launcher.Stops);
        Assert.Equal(0, input.Stops);

        launcher.Release();
        Assert.True(await enter.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(await exit.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(1, launcher.Stops);
        Assert.Equal(1, input.Starts);
        Assert.Equal(1, input.Stops);
    }

    [Fact]
    public async Task Concurrent_entries_are_idempotent_and_start_one_launcher()
    {
        var (orchestrator, launcher, input) = MakeOrchestrator(blockLauncher: true);
        Task<bool> first = orchestrator.EnterGamingSessionAsync("Race test");
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<bool> second = orchestrator.EnterGamingSessionAsync("Race test");
        Assert.False(second.IsCompleted);

        launcher.Release();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(1, input.Starts);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
    }

    [Fact]
    public async Task Late_remote_disconnect_from_old_session_cannot_exit_a_new_session()
    {
        var sunshine = new DeferredTrigger("sunshine");
        var (orchestrator, launcher, _) = MakeOrchestrator(trigger: sunshine, sessionType: "remote", gracePeriodSeconds: 0);
        Assert.True(await orchestrator.EnterGamingSessionAsync("Race test"));
        Assert.Equal("GamingRemote", orchestrator.CurrentState);

        Task delayedPoll = orchestrator.PollTriggersAndSessionAsync(CancellationToken.None);
        await sunshine.Checked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(await orchestrator.ExitGamingSessionAsync());
        Assert.True(await orchestrator.EnterGamingSessionAsync("Race test"));
        sunshine.FirstResult.TrySetResult(false);
        await delayedPoll.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("GamingRemote", orchestrator.CurrentState);
        Assert.Equal(2, launcher.Launches);
        Assert.Equal(1, launcher.Stops);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
    }

    [Fact]
    public async Task Delayed_trigger_cannot_reenter_after_explicit_exit_and_requires_release_edge()
    {
        var trigger = new DeferredTrigger();
        var (orchestrator, launcher, _) = MakeOrchestrator(trigger: trigger);
        Task pendingPoll = orchestrator.PollTriggersAndSessionAsync(CancellationToken.None);
        await trigger.Checked.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(await orchestrator.ExitGamingSessionAsync());
        trigger.FirstResult.TrySetResult(true);
        await pendingPoll.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.Equal(0, launcher.Launches);

        trigger.Current = true;
        await orchestrator.PollTriggersAndSessionAsync(CancellationToken.None);
        Assert.Equal(0, launcher.Launches); // held source cannot undo Return to AI

        trigger.Current = false;
        await orchestrator.PollTriggersAndSessionAsync(CancellationToken.None);
        trigger.Current = true;
        await orchestrator.PollTriggersAndSessionAsync(CancellationToken.None);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal("GamingLocal", orchestrator.CurrentState);
        Assert.True(await orchestrator.ExitGamingSessionAsync());
    }
}
