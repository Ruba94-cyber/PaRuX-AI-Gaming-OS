using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutomaticWarmupPolicyTests
{
    [Theory]
    [InlineData("AUTO", false, true)]
    [InlineData("ON", false, true)]
    [InlineData("OFF", false, false)]
    [InlineData("AUTO", true, false)]
    public void Off_and_gaming_use_normal_ensure_refusal(string mode, bool gamingActive, bool preflight) =>
        Assert.Equal(preflight, LocalRuntimeService.ShouldPreflightAutomaticWarmup(mode, gamingActive));

    [Fact]
    public void Unconfigured_install_with_no_starter_skips_automatic_warmup()
    {
        Assert.True(LocalAiProcessManager.ShouldSkipAutomaticWarmup(null, null, true));
        Assert.True(LocalAiProcessManager.ShouldSkipAutomaticWarmup(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf"), false));
    }

    [Fact]
    public void Missing_assignment_used_by_launch_is_not_silently_skipped()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf");
        Assert.False(LocalAiProcessManager.ShouldSkipAutomaticWarmup(missing, missing, true));
    }

    [Fact]
    public async Task Warmup_skip_returns_before_runtime_actions_or_state_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-warmup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (LocalAiPaths.UseRootForTests(root))
            {
                int preflightCalls = 0;
                var manager = new LocalAiProcessManager
                {
                    AutomaticWarmupSkipForTests = _ => { preflightCalls++; return Task.FromResult(true); }
                };
                var runtime = new LocalRuntimeService(null!, manager, null!);
                LocalRuntimeService.RuntimeResult result = await runtime.RunActionAsync("Warmup", CancellationToken.None);
                Assert.True(result.Success);
                Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, preflightCalls);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cpu_fallback_uses_starter_even_when_role_is_assigned()
    {
        string assigned = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf");
        string existing = Path.GetTempFileName();
        try
        {
            Assert.True(LocalAiProcessManager.ShouldSkipAutomaticWarmup(assigned, null, false));
            Assert.False(LocalAiProcessManager.ShouldSkipAutomaticWarmup(assigned, existing, false));
        }
        finally { File.Delete(existing); }
    }

    [Fact]
    public void Existing_starter_remains_eligible_for_warmup()
    {
        string existing = Path.GetTempFileName();
        try { Assert.False(LocalAiProcessManager.ShouldSkipAutomaticWarmup(null, existing, true)); }
        finally { File.Delete(existing); }
    }
}
