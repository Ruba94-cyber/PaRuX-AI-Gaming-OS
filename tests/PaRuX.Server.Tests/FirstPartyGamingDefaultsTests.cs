using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class FirstPartyGamingDefaultsTests
{
    [Fact]
    public void New_presets_prefer_existing_Windows_suspend_instead_of_external_Nyrna()
    {
        GamingProfileDto local = GamingProfileStore.BuildDefaultLocalPreset();
        GamingProfileDto remote = GamingProfileStore.BuildDefaultRemotePreset();
        Assert.Equal("native", local.InputProvider);
        Assert.Equal("win32native", local.SuspendProvider);
        Assert.Equal("win32native", remote.SuspendProvider);
        Assert.Equal("none", local.LauncherProvider);
        Assert.False(remote.Enabled);
    }

    [Fact]
    public void Legacy_serialized_profile_without_suspend_field_preserves_old_default()
    {
        GamingProfileDto? saved = JsonSerializer.Deserialize<GamingProfileDto>(
            """{"Id":"old-profile","Name":"Legacy","SessionType":"local"}""");
        Assert.NotNull(saved);
        Assert.Equal("nyrna", saved.SuspendProvider);
    }

    [Fact]
    public async Task No_suspend_provider_never_claims_to_have_paused_a_real_process()
    {
        var provider = new NoneGameSuspendProvider();
        Assert.False(await provider.SuspendProcessAsync(Environment.ProcessId, CancellationToken.None));
        Assert.False(await provider.ResumeProcessAsync(Environment.ProcessId, CancellationToken.None));
    }

    [Fact]
    public async Task Native_provider_honours_cancellation_before_opening_a_process()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var provider = new WindowsNativeSuspendProvider();
        Assert.False(await provider.SuspendProcessAsync(Environment.ProcessId, cancelled.Token));
        Assert.False(await provider.ResumeProcessAsync(Environment.ProcessId, cancelled.Token));
    }
}
