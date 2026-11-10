using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>PERF-003a: an identical Standard/Uncensored request keeps the healthy llama-server; anything else restarts.</summary>
public sealed class WarmTextReuseTests : IDisposable
{
    private const string Model = @"C:\LocalAI\models\Qwen-27B.gguf";
    private const string Exe = @"C:\LocalAI\runtime\cuda\llama-server.exe";
    private static readonly string[] Args = ["--model", Model, "--port", "8080", "--ctx-size", "32768", "--flash-attn", "on"];

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-warm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static WarmTextReusePolicy.Resident Resident(IReadOnlyList<string>? args = null, string model = Model, string exe = Exe) =>
        new(model, exe, args ?? Args);

    private static IEnumerable<WarmTextReusePolicy.Candidate> Planned(IReadOnlyList<string>? args = null) =>
        [new WarmTextReusePolicy.Candidate(Exe, args ?? Args)];

    [Fact]
    public void Identical_request_on_a_healthy_resident_is_reused()
    {
        Assert.True(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, gamingActive: false, processHealthy: true,
            Resident(), Model, Planned()));
    }

    [Fact]
    public void Resident_on_a_fallback_rung_is_reused_when_the_request_would_produce_that_launch_again()
    {
        string[] fallback = [.. Args, "--no-mmap"];
        var candidates = new[]
        {
            new WarmTextReusePolicy.Candidate(Exe, Args),
            new WarmTextReusePolicy.Candidate(Exe, fallback)
        };
        Assert.True(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, Resident(fallback), Model, candidates));
    }

    [Fact]
    public void A_changed_launch_setting_restarts()
    {
        string[] changed = ["--model", Model, "--port", "8080", "--ctx-size", "65536", "--flash-attn", "on"];
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, Resident(), Model, Planned(changed)));
    }

    [Fact]
    public void A_different_model_or_backend_restarts()
    {
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true,
            Resident(), @"C:\LocalAI\models\Other.gguf", Planned()));
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true,
            Resident(exe: @"C:\LocalAI\runtime\vulkan\llama-server.exe"), Model, Planned()));
    }

    [Fact]
    public void A_dead_or_unhealthy_or_unknown_resident_restarts()
    {
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, processHealthy: false, Resident(), Model, Planned()));
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, false, true, resident: null, Model, Planned()));
    }

    [Fact]
    public void An_explicit_restart_and_ensure_semantics_never_use_the_strict_reuse()
    {
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ForcedRestart, false, true, Resident(), Model, Planned()));
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.Ensure, false, true, Resident(), Model, Planned()));
    }

    [Fact]
    public void Gaming_never_reuses()
    {
        Assert.False(WarmTextReusePolicy.CanReuse(TextStartIntent.ProfileRequest, gamingActive: true, true, Resident(), Model, Planned()));
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Uncensored")]
    public async Task Profile_actions_are_still_refused_while_gaming_owns_the_gpu(string action)
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!);
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");

        var manager = new LocalAiProcessManager(0);
        LocalAiProcessManager.RuntimeResult result = await manager.RunActionAsync(action, CancellationToken.None);
        LocalAiProcessManager.RuntimeResult forced = await manager.RunActionAsync(action, CancellationToken.None, forceRestart: true);

        Assert.False(result.Success);
        Assert.Contains("game is currently active", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(forced.Success);
        Assert.Contains("game is currently active", forced.Message, StringComparison.OrdinalIgnoreCase);
    }
}
