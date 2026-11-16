using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure memory-pressure fixtures. No forced GC, no other process is touched and
/// no Windows RAM claim is fabricated by a test.</summary>
public sealed class CorporateCpuMemoryPlannerTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static double Estimate(int context) => context switch
    {
        4096 => 3100,
        3072 => 2750,
        2048 => 1600,
        _ => throw new ArgumentOutOfRangeException(nameof(context))
    };

    [Fact]
    public void Healthy_cpu_memory_preserves_full_context_and_the_selected_model()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 8 * GiB);
        var chosen = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
        Assert.True(chosen.Fits);
        Assert.Equal(4096, chosen.ContextTokens);
        Assert.False(chosen.Reduced);
        Assert.Equal(3100, chosen.NeededMib);
    }

    [Fact]
    public void Increasing_pressure_reduces_context_only_as_much_as_needed()
    {
        var moderate = new SystemRamSnapshot(16 * GiB, 5 * GiB);
        var constrained = new SystemRamSnapshot(16 * GiB, 4 * GiB);

        var middle = CorporateCpuMemoryPlanner.Select(moderate, 86, 0, Estimate);
        Assert.True(middle.Fits);
        Assert.Equal(3072, middle.ContextTokens);
        Assert.True(middle.Reduced);

        var smallest = CorporateCpuMemoryPlanner.Select(constrained, 86, 0, Estimate);
        Assert.True(smallest.Fits);
        Assert.Equal(2048, smallest.ContextTokens);
        Assert.True(smallest.Reduced);
    }

    [Fact]
    public void Too_little_system_ram_is_a_clear_refusal_not_a_silent_budget_override()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 2 * GiB);
        var plan = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
        Assert.False(plan.Fits);
        Assert.Equal(2048, plan.ContextTokens);
        Assert.Equal(0, plan.AvailableMib);
        string message = CorporateCpuMemoryPlanner.Shortfall(plan, ram, 86);
        Assert.Contains("2048-token context", message);
        Assert.Contains("No", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not expand the profile limit", message);
        Assert.Contains("did not expand", message);
    }

    [Fact]
    public void Only_explicitly_reclaimable_owned_runtime_memory_is_credited()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 2 * GiB);
        var without = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
        var with = CorporateCpuMemoryPlanner.Select(ram, 86, 3L * (long)GiB, Estimate);

        Assert.False(without.Fits);
        Assert.True(with.Fits);
        Assert.Equal(3072, with.ContextTokens);
        Assert.True(with.AvailableMib > 2750);
        Assert.True(with.AvailableMib < 3100);
        Assert.Equal(0, without.AvailableMib);
    }

    [Fact]
    public void Unknown_or_invalid_measurements_fail_closed_without_estimating_models()
    {
        int calls = 0;
        double Count(int _) { calls++; return 100; }
        Assert.False(CorporateCpuMemoryPlanner.Select(null, 86, 0, Count).Fits);
        Assert.False(CorporateCpuMemoryPlanner.Select(new SystemRamSnapshot(0, 0), 86, 0, Count).Fits);
        Assert.False(CorporateCpuMemoryPlanner.Select(new SystemRamSnapshot(16 * GiB, 8 * GiB), double.NaN, 0, Count).Fits);
        Assert.False(CorporateCpuMemoryPlanner.Select(new SystemRamSnapshot(16 * GiB, 8 * GiB), 0, 0, Count).Fits);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Unknown_gguf_geometry_keeps_native_runtime_reserve_in_the_memory_estimate()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "parux-corporate-unknown-kv-" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            // Sparse fake model: deliberately no valid GGUF metadata, never loaded.
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                output.SetLength(64 * 1024);
            double estimatedMib = CpuFallbackAdmission.EstimateNeededMib(path);
            Assert.True(estimatedMib >= 768, "Unknown KV shape cannot erase the native runtime RAM reserve.");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Bad_or_unmeasurable_model_footprint_cannot_be_promoted_to_compatible()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 8 * GiB);
        var unknown = CorporateCpuMemoryPlanner.Select(ram, 86, 0, _ => double.NaN);
        Assert.False(unknown.Fits);
        Assert.Equal(0, unknown.NeededMib);
        Assert.Contains("could not be measured safely", CorporateCpuMemoryPlanner.Shortfall(unknown, ram, 86));

        var zero = CorporateCpuMemoryPlanner.Select(ram, 86, 0, _ => 0);
        Assert.False(zero.Fits);
        var infinite = CorporateCpuMemoryPlanner.Select(ram, 86, 0, _ => double.PositiveInfinity);
        Assert.False(infinite.Fits);
    }

    [Fact]
    public void One_decision_never_tries_an_unusable_tiny_context()
    {
        var seen = new List<int>();
        var ram = new SystemRamSnapshot(16 * GiB, 2 * GiB);
        _ = CorporateCpuMemoryPlanner.Select(ram, 86, 0, ctx =>
        {
            seen.Add(ctx);
            return 6000;
        });
        Assert.Equal(new[] { 4096, 3072, 2048 }, seen);
        Assert.Equal(2048, CorporateCpuMemoryPlanner.MinimumUsefulContextTokens);
    }

    [Fact]
    public void Under_pressure_only_the_purchased_idle_runtime_memory_is_released_earlier()
    {
        var roomy = new CorporateCpuMemoryPlanner.Plan(4096, 3000, 5000, true);
        var moderate = new CorporateCpuMemoryPlanner.Plan(4096, 3000, 4000, true);
        var tight = new CorporateCpuMemoryPlanner.Plan(3072, 2750, 3050, true);
        var refused = new CorporateCpuMemoryPlanner.Plan(2048, 3000, 0, false);

        Assert.Equal(LocalAiProcessManager.CorporateIdleUnloadSeconds,
            CorporateCpuMemoryPlanner.IdleUnloadSeconds(roomy));
        Assert.Equal(180, CorporateCpuMemoryPlanner.IdleUnloadSeconds(moderate));
        Assert.Equal(120, CorporateCpuMemoryPlanner.IdleUnloadSeconds(tight));
        Assert.Equal(LocalAiProcessManager.CorporateIdleUnloadSeconds,
            CorporateCpuMemoryPlanner.IdleUnloadSeconds(refused));
        var argv = LocalAiProcessManager.BuildCorporateCpuArguments("m", 1234, 8, 3072, 120);
        Assert.Equal("120", argv[argv.IndexOf("--sleep-idle-seconds") + 1]);
    }

    [Fact]
    public void Cpu_launch_argument_builder_uses_same_context_without_touching_threads_or_cpu_only_settings()
    {
        var args = LocalAiProcessManager.BuildCorporateCpuArguments("model.gguf", 8123, 16, 3072);
        Assert.Equal("3072", args[args.IndexOf("--ctx-size") + 1]);
        Assert.Equal("8", args[args.IndexOf("--threads") + 1]);
        Assert.Contains("--no-repack", args);
        Assert.DoesNotContain("--device", args);
        Assert.DoesNotContain("-ngl", args);
        Assert.Equal("4096", LocalAiProcessManager.BuildCorporateCpuArguments("m", 8123, 16)[
            LocalAiProcessManager.BuildCorporateCpuArguments("m", 8123, 16).IndexOf("--ctx-size") + 1]);
    }

    [Fact]
    public void Smart_router_final_admission_and_native_cpu_launch_share_dynamic_context_policy()
    {
        string admission = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string launch = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string policy = Read("src", "PaRuX.Server", "CorporateCpuMemoryPlanner.cs");
        Assert.Contains("CorporateCpuMemoryPlanner.ForModel(", admission);
        Assert.Contains("CorporateCpuMemoryPlanner.ForModel(", launch);
        Assert.Contains("estimate(CorporateCpuMemoryPlanner.MinimumUsefulContextTokens)", admission);
        Assert.Contains("estimate(CorporateCpuMemoryPlanner.NormalContextTokens)", admission);
        Assert.Contains("CorporateCpuMemoryPlanner.IdleUnloadSeconds(memory)", launch);
        Assert.Contains("memory.ContextTokens", launch);
        Assert.Contains("memory.Reduced", launch);
        Assert.Contains("GetReclaimableTextWorkingSetBytes()", admission);
        Assert.Contains("CreateContextEstimator(modelPath)", policy);
    }

    private static string Read(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray()));
    }
}
