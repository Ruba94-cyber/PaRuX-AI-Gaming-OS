using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PERF-003a and PERF-003c through the real StartTextAsync/RunActionAsync code, with the launch seam standing in for
/// llama-server (the same pattern as the launch-ladder tests). A "restart" is observable as one more launch.
/// </summary>
public sealed class WarmReuseAndGamingRestoreTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-perf003-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;
    private readonly List<IReadOnlyList<string>> _launches = new();
    private bool _healthy;

    public WarmReuseAndGamingRestoreTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.SmallModel);
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LocalAiProcessManager NewManager() => new(0)
    {
        InventoryProbe = () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()),
        TextHealthProbeForTests = _ => Task.FromResult(_healthy),
        LaunchInterceptForTests = (_, arguments) =>
        {
            _launches.Add(arguments);
            // A real launch records the model it serves; the Ensure path reads that record to recognise a resident.
            LocalAiProcessManager.RecordActiveTextRuntime(arguments);
            _healthy = true;
            return new LocalAiProcessManager.RuntimeResult(true, "Started: intercepted");
        }
    };

    private string Model(string name) => WriteFile(Path.Combine(_work, "models", name));

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        int index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    // ---- PERF-003a: warm reuse ----

    [Fact]
    public async Task An_identical_profile_request_on_a_healthy_runtime_is_a_no_op()
    {
        string model = Model("a.gguf");
        var manager = NewManager();

        Assert.True((await manager.RunActionAsync("Standard", CancellationToken.None, model)).Success);
        LocalAiProcessManager.RuntimeResult again = await manager.RunActionAsync("Standard", CancellationToken.None, model);

        Assert.True(again.Success);
        Assert.StartsWith("Already running", again.Message);
        Assert.Single(_launches);
    }

    [Fact]
    public async Task Requests_that_differ_only_in_irrelevant_fields_do_not_restart()
    {
        string model = Model("a.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, model);

        // The same file spelled differently (case, redundant segment) is the same model.
        string respelled = Path.Combine(Path.GetDirectoryName(model)!, ".", Path.GetFileName(model).ToUpperInvariant());
        await manager.RunActionAsync("Standard", CancellationToken.None, respelled);
        // A different Local AI mode is not a different launch.
        manager.SetMode("ON");
        await manager.RunActionAsync("Standard", CancellationToken.None, model);

        Assert.Single(_launches);
    }

    [Fact]
    public async Task Ensure_auto_and_on_keep_a_healthy_default_resident()
    {
        var manager = NewManager();
        await manager.RunActionAsync("Ensure", CancellationToken.None);

        await manager.RunActionAsync("Auto", CancellationToken.None);
        await manager.RunActionAsync("On", CancellationToken.None);
        await manager.RunActionAsync("Ensure", CancellationToken.None);

        Assert.Single(_launches);
    }

    [Fact]
    public async Task A_genuinely_different_model_restarts_and_the_forced_restart_always_does()
    {
        string a = Model("a.gguf");
        string b = Model("b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, a);

        await manager.RunActionAsync("Standard", CancellationToken.None, b);
        Assert.Equal(2, _launches.Count);
        Assert.Equal(b, ValueOf(_launches[1], "--model"));

        await manager.RunActionAsync("Standard", CancellationToken.None, b, forceRestart: true);
        Assert.Equal(3, _launches.Count);
    }

    [Fact]
    public void A_different_gpu_layer_count_or_context_is_a_different_launch()
    {
        const string exe = @"C:\LocalAI\runtime\cuda\llama-server.exe";
        const string model = @"C:\LocalAI\models\m.gguf";
        string[] resident = ["--model", model, "--ctx-size", "32768", "--gpu-layers", "all"];
        var healthy = new WarmTextReusePolicy.Resident(model, exe, resident);

        Assert.True(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, healthy, model,
            [new WarmTextReusePolicy.Candidate(exe, resident)]));
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, healthy, model,
            [new WarmTextReusePolicy.Candidate(exe, ["--model", model, "--ctx-size", "32768", "--gpu-layers", "20"])]));
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, healthy, model,
            [new WarmTextReusePolicy.Candidate(exe, ["--model", model, "--ctx-size", "8192", "--gpu-layers", "all"])]));
    }

    // ---- PERF-003c: GameStop restore ----

    [Fact]
    public async Task What_was_loaded_is_remembered_across_the_gaming_hard_off_and_reported_honestly()
    {
        string coder = Model("coder-14b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, coder);

        LocalAiProcessManager.RuntimeResult start = await manager.RunActionAsync("GameStart", CancellationToken.None);
        Assert.True(start.Success);

        GamingUnloadedText? remembered = manager.GetRememberedGamingUnload();
        Assert.NotNull(remembered);
        Assert.Equal(coder, remembered!.ModelPath);
        string whileGaming = await manager.GetStatusTextAsync(CancellationToken.None);
        Assert.Contains("unloaded for Gaming (coder-14b.gguf)", whileGaming);
        Assert.Contains("will reload on demand after Gaming ends", whileGaming);
    }

    [Fact]
    public async Task Nothing_reloads_while_gaming_is_active_and_a_second_hard_off_attempt_keeps_the_memory()
    {
        string coder = Model("coder-14b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, coder);
        await manager.RunActionAsync("GameStart", CancellationToken.None);
        int launchesBefore = _launches.Count;

        LocalAiProcessManager.RuntimeResult ensure = await manager.RunActionAsync("Ensure", CancellationToken.None);
        await manager.RunActionAsync("GameStart", CancellationToken.None); // the guard retries while Gaming stays on

        Assert.False(ensure.Success);
        Assert.Contains("game is currently active", ensure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(launchesBefore, _launches.Count);
        Assert.Equal(coder, manager.GetRememberedGamingUnload()?.ModelPath);
    }

    [Fact]
    public async Task Ending_gaming_loads_nothing_and_the_first_request_afterwards_reloads_the_remembered_model_without_error()
    {
        string coder = Model("coder-14b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, coder);
        await manager.RunActionAsync("GameStart", CancellationToken.None);
        _healthy = false;
        int launchesBefore = _launches.Count;

        Assert.True((await manager.RunActionAsync("GameStop", CancellationToken.None)).Success);

        Assert.Equal(launchesBefore, _launches.Count); // lazy: leaving Gaming started nothing
        string afterGaming = await manager.GetStatusTextAsync(CancellationToken.None);
        Assert.Contains("will reload on demand", afterGaming);

        LocalAiProcessManager.RuntimeResult first = await manager.RunActionAsync("Ensure", CancellationToken.None);

        Assert.True(first.Success, first.Message);
        Assert.Equal(launchesBefore + 1, _launches.Count);
        Assert.Equal(coder, ValueOf(_launches[^1], "--model"));
        Assert.Null(manager.GetRememberedGamingUnload()); // serving again: nothing is waiting any more
    }

    [Fact]
    public async Task The_default_profile_model_reloads_through_the_ordinary_start_without_an_override()
    {
        var manager = NewManager();
        await manager.RunActionAsync("Ensure", CancellationToken.None);
        Assert.Equal(LocalAiPaths.SmallModel, ValueOf(_launches[0], "--model"));
        await manager.RunActionAsync("GameStart", CancellationToken.None);
        await manager.RunActionAsync("GameStop", CancellationToken.None);
        _healthy = false;

        LocalAiProcessManager.RuntimeResult first = await manager.RunActionAsync("Ensure", CancellationToken.None);

        Assert.True(first.Success, first.Message);
        Assert.Equal(2, _launches.Count);
        Assert.Equal(LocalAiPaths.SmallModel, ValueOf(_launches[1], "--model"));
    }

    [Fact]
    public async Task A_remembered_model_that_no_longer_exists_is_dropped_and_the_default_starts_instead()
    {
        string coder = Model("coder-14b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, coder);
        await manager.RunActionAsync("GameStart", CancellationToken.None);
        await manager.RunActionAsync("GameStop", CancellationToken.None);
        File.Delete(coder);
        _healthy = false;

        LocalAiProcessManager.RuntimeResult first = await manager.RunActionAsync("Ensure", CancellationToken.None);

        Assert.True(first.Success, first.Message);
        Assert.Equal(LocalAiPaths.SmallModel, ValueOf(_launches[^1], "--model"));
        Assert.Null(manager.GetRememberedGamingUnload());
    }

    [Fact]
    public async Task With_Local_AI_off_the_status_says_it_will_not_reload()
    {
        string coder = Model("coder-14b.gguf");
        var manager = NewManager();
        await manager.RunActionAsync("Standard", CancellationToken.None, coder);
        await manager.RunActionAsync("GameStart", CancellationToken.None);
        await manager.RunActionAsync("GameStop", CancellationToken.None);
        manager.SetMode("OFF");

        Assert.Contains("will not reload", await manager.GetStatusTextAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("ON", false, true, true)]
    [InlineData("on", false, true, true)]
    [InlineData("AUTO", false, true, false)]
    [InlineData("OFF", false, true, false)]
    [InlineData("ON", true, true, false)]
    [InlineData("ON", false, false, false)]
    public void Only_the_always_on_setting_asks_for_an_automatic_reload_and_never_during_gaming(
        string mode, bool gamingActive, bool hasRemembered, bool expected)
    {
        Assert.Equal(expected, GamingRestorePolicy.ShouldEagerRestore(mode, gamingActive, hasRemembered));
    }
}
