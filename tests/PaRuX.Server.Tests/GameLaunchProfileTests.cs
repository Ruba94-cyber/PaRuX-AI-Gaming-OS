using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameLaunchProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-launch-profile-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "profiles.json");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static GameLaunchProfile Sample() => new()
    {
        DisplayId = "DISPLAY1", Width = 1920, Height = 1080, RefreshHz = 120, Fullscreen = true,
        ControllerPreset = "xbox", PerformanceProfile = "performance",
        LaunchArgs = new() { "-windowed", "-w", "{width}", "-h", "{height}", "-fps={refresh}" },
        Streaming = new() { Enabled = true, BitrateKbps = 20000, Fps = 60, Width = 1920, Height = 1080, Codec = "hevc" }
    };

    private static ManualGameDto Game(string args = "") =>
        new("abc123", "Game", Path.Combine(Path.GetTempPath(), "game.exe"), args, "", "game", "", "", "");

    private sealed class FakeStarter : IGameProcessStarter
    {
        public ProcessStartInfo? Seen; public int Calls;
        public Task<bool> StartAsync(ProcessStartInfo info, CancellationToken ct) { Calls++; Seen = info; return Task.FromResult(true); }
    }

    [Fact]
    public void Fullscreen_is_honestly_reported_as_preference_only_and_survives_a_reload()
    {
        var store = new GameLaunchProfileStore(StorePath);
        Assert.Null(store.Save("abc123", Sample()));
        var json = System.Text.Json.JsonSerializer.Serialize(store.Get("abc123"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"fullscreenSupport\":\"preference_only\"", json);
        Assert.True(new GameLaunchProfileStore(StorePath).Get("abc123")!.Fullscreen);
        Assert.Equal("preference_only", new GameLaunchProfile().FullscreenSupport);
        // A client echoing the read-only field back must not break the save.
        var echoed = System.Text.Json.JsonSerializer.Deserialize<GameLaunchProfile>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Null(store.Save("abc123", echoed));
    }

    [Fact]
    public void Roundtrip_persists_every_field_in_a_versioned_file()
    {
        var store = new GameLaunchProfileStore(StorePath);
        Assert.Null(store.Save("abc123", Sample()));
        var loaded = new GameLaunchProfileStore(StorePath).Get("abc123")!;
        Assert.Equal("DISPLAY1", loaded.DisplayId);
        Assert.Equal(1920, loaded.Width);
        Assert.Equal(120, loaded.RefreshHz);
        Assert.True(loaded.Fullscreen);
        Assert.Equal("xbox", loaded.ControllerPreset);
        Assert.Equal("performance", loaded.PerformanceProfile);
        Assert.Equal(6, loaded.LaunchArgs.Count);
        Assert.Equal("hevc", loaded.Streaming.Codec);
        Assert.Equal("hard_off", loaded.AiConcurrency);
        Assert.Contains("\"Version\": 1", File.ReadAllText(StorePath));
        Assert.True(new GameLaunchProfileStore(StorePath).Delete("abc123"));
        Assert.Null(new GameLaunchProfileStore(StorePath).Get("abc123"));
    }

    [Fact]
    public void Old_entry_without_new_fields_loads_with_defaults()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StorePath, "{\"Version\":1,\"Profiles\":{\"abc123\":{\"DisplayId\":\"D2\"}}}");
        var store = new GameLaunchProfileStore(StorePath);
        Assert.Null(store.LoadError);
        var p = store.Get("abc123")!;
        Assert.Equal("D2", p.DisplayId);
        Assert.Equal("auto", p.ControllerPreset);
        Assert.Equal("balanced", p.PerformanceProfile);
        Assert.Empty(p.LaunchArgs);
        Assert.Equal("hard_off", p.AiConcurrency);
        Assert.Equal(GameLaunchProfile.CurrentSchemaVersion, p.SchemaVersion);
    }

    [Fact]
    public void Unreadable_or_future_file_is_preserved_and_writes_are_refused()
    {
        Directory.CreateDirectory(_root);
        const string future = "{\"Version\":99,\"Profiles\":{}}";
        File.WriteAllText(StorePath, future);
        var store = new GameLaunchProfileStore(StorePath);
        Assert.NotNull(store.LoadError);
        Assert.NotNull(store.Save("abc123", Sample()));
        Assert.Equal(future, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("-x & calc")]
    [InlineData("-x | del *")]
    [InlineData("a;b")]
    [InlineData("a>out.txt")]
    [InlineData("%PATH%")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("a\"b")]
    [InlineData("-x\nnext")]
    [InlineData("--config=..\\..\\secret.ini")]
    [InlineData("../../evil")]
    [InlineData("{unknown}")]
    public void Injection_and_traversal_arguments_are_rejected_and_nothing_is_stored(string arg)
    {
        var store = new GameLaunchProfileStore(StorePath);
        var p = new GameLaunchProfile { LaunchArgs = new() { arg } };
        Assert.NotNull(store.Save("abc123", p));
        Assert.Null(store.Get("abc123"));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Out_of_range_values_and_non_hard_off_ai_mode_are_rejected()
    {
        var store = new GameLaunchProfileStore(StorePath);
        Assert.NotNull(store.Save("k", new GameLaunchProfile { Width = 100000, Height = 1080 }));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { Width = 1920 }));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { RefreshHz = 5 }));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { PerformanceProfile = "turbo" }));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { AiConcurrency = "keep_running" }));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { LaunchArgs = new() { "{width}" } }));
        Assert.NotNull(store.Save("bad key!", new GameLaunchProfile()));
        Assert.NotNull(store.Save("k", new GameLaunchProfile { Streaming = new() { Fps = 1000 } }));
    }

    [Fact]
    public async Task Applied_at_launch_uses_argument_list_with_resolution_placeholders()
    {
        var starter = new FakeStarter();
        var result = await GameLaunchProfileApplier.ApplyAsync(Game("-nosplash \"my save\""), Sample(), starter, CancellationToken.None);
        Assert.True(result.Started);
        Assert.Equal("DISPLAY1", result.DisplayId);
        ProcessStartInfo info = starter.Seen!;
        Assert.False(info.UseShellExecute);
        Assert.Equal(string.Empty, info.Arguments);
        Assert.Equal(new[] { "-nosplash", "my save", "-windowed", "-w", "1920", "-h", "1080", "-fps=120" }, info.ArgumentList.ToArray());
    }

    [Fact]
    public async Task Without_profile_the_legacy_arguments_string_is_unchanged()
    {
        var starter = new FakeStarter();
        await GameLaunchProfileApplier.ApplyAsync(Game("-a -b"), null, starter, CancellationToken.None);
        Assert.Equal("-a -b", starter.Seen!.Arguments);
        Assert.Empty(starter.Seen.ArgumentList);
    }

    [Fact]
    public async Task Corrupted_profile_is_refused_at_launch_without_starting()
    {
        var starter = new FakeStarter();
        var bad = new GameLaunchProfile { LaunchArgs = new() { "a & b" } };
        var result = await GameLaunchProfileApplier.ApplyAsync(Game(), bad, starter, CancellationToken.None);
        Assert.False(result.Started);
        Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public async Task Cancellation_before_start_launches_nothing()
    {
        var starter = new FakeStarter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GameLaunchProfileApplier.ApplyAsync(Game(), Sample(), starter, cts.Token));
        Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public void Command_line_for_elevation_quotes_arguments_with_spaces()
    {
        ProcessStartInfo info = GameLaunchProfileApplier.BuildStartInfo(Game("\"my save\""), new GameLaunchProfile { LaunchArgs = new() { "-x" } });
        Assert.Equal("\"my save\" -x", GameLaunchProfileApplier.ToCommandLine(info));
    }
}
