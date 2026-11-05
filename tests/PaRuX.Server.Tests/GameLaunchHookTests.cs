using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameLaunchHookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-hook-" + Guid.NewGuid().ToString("N"));
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private bool _gaming;

    public GameLaunchHookTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private GameLaunchHookStore Store() => new(Path.Combine(_root, "hooks.json"));
    private GameLaunchHookService Service(GameLaunchHookStore store) => new(store, () => _gaming);
    private GameHookSpec Writer(string marker, int timeout = 15) => new(Cmd, "/c echo ran> \"" + marker + "\"", timeout);

    [Fact]
    public async Task Off_by_default_even_when_a_hook_is_configured()
    {
        var store = Store();
        string marker = Path.Combine(_root, "m.txt");
        Assert.Null(store.Set("mygame", new GameHooksDto(Writer(marker), null)));

        var result = await Service(store).RunAsync(GameHookPhase.Pre, "mygame", default);

        Assert.False(store.Enabled);
        Assert.False(result.Ran);
        Assert.Equal("disabled", result.Reason);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Enabled_hook_runs_and_reports_the_exit_code()
    {
        var store = Store();
        string marker = Path.Combine(_root, "m.txt");
        store.Set("mygame", new GameHooksDto(Writer(marker), new GameHookSpec(Cmd, "/c exit 7", 15)));
        store.SetEnabled(true);
        var service = Service(store);

        var pre = await service.RunAsync(GameHookPhase.Pre, "mygame", default);
        var post = await service.RunAsync(GameHookPhase.Post, "mygame", default);

        Assert.True(pre.Ran);
        Assert.Equal(0, pre.ExitCode);
        Assert.True(File.Exists(marker));
        Assert.Equal(7, post.ExitCode);
        Assert.Equal("no-hook", (await service.RunAsync(GameHookPhase.Pre, "othergame", default)).Reason);
    }

    [Fact]
    public async Task Never_runs_while_gaming_is_active()
    {
        var store = Store();
        string marker = Path.Combine(_root, "m.txt");
        store.Set("mygame", new GameHooksDto(Writer(marker), null));
        store.SetEnabled(true);
        _gaming = true;

        var result = await Service(store).RunAsync(GameHookPhase.Pre, "mygame", default);

        Assert.False(result.Ran);
        Assert.Equal("gaming-active", result.Reason);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Timeout_kills_the_hook_process()
    {
        var store = Store();
        store.Set("mygame", new GameHooksDto(new GameHookSpec(Cmd, "/c ping -n 30 127.0.0.1 >nul", 1), null));
        store.SetEnabled(true);
        var started = DateTime.UtcNow;

        var result = await Service(store).RunAsync(GameHookPhase.Pre, "mygame", default);

        Assert.Equal("timeout", result.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Gaming_starting_during_a_hook_cancels_and_kills_it()
    {
        var store = Store();
        store.Set("mygame", new GameHooksDto(new GameHookSpec(Cmd, "/c ping -n 30 127.0.0.1 >nul", 30), null));
        store.SetEnabled(true);
        int polls = 0;
        var service = new GameLaunchHookService(store, () => ++polls > 3); // Gaming "starts" a moment after the hook began
        var started = DateTime.UtcNow;

        var result = await service.RunAsync(GameHookPhase.Pre, "mygame", default);

        Assert.Equal("gaming-started", result.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData(@"relative\x.exe")]
    [InlineData(@"C:\does\not\exist.exe")]
    public void Unsafe_paths_are_refused(string path)
    {
        var store = Store();
        Assert.NotNull(store.Set("mygame", new GameHooksDto(new GameHookSpec(path), null)));
        Assert.Null(store.Get("mygame"));
    }

    [Fact]
    public void Scripts_and_non_exe_files_bad_arguments_and_timeouts_are_refused()
    {
        string script = Path.Combine(_root, "a.cmd");
        File.WriteAllText(script, "@echo off");
        Assert.NotNull(GameLaunchHookStore.Validate(new GameHookSpec(script)));
        Assert.NotNull(GameLaunchHookStore.Validate(new GameHookSpec(Cmd, "bad\nargs")));
        Assert.NotNull(GameLaunchHookStore.Validate(new GameHookSpec(Cmd, new string('a', 1025))));
        Assert.NotNull(GameLaunchHookStore.Validate(new GameHookSpec(Cmd, "", 0)));
        Assert.NotNull(GameLaunchHookStore.Validate(new GameHookSpec(Cmd, "", 61)));
        Assert.Null(GameLaunchHookStore.Validate(new GameHookSpec(Cmd, "/c exit", 60)));
        Assert.NotNull(Store().Set("bad key!", null));
    }

    [Fact]
    public async Task A_hook_replaced_by_a_missing_file_after_configuration_is_not_run()
    {
        string copy = Path.Combine(_root, "helper.exe");
        File.Copy(Cmd, copy);
        var store = Store();
        Assert.Null(store.Set("mygame", new GameHooksDto(new GameHookSpec(copy, "/c exit"), null)));
        store.SetEnabled(true);
        File.Delete(copy);

        var result = await Service(store).RunAsync(GameHookPhase.Pre, "mygame", default);

        Assert.False(result.Ran);
        Assert.Equal("unsafe-path", result.Reason);
    }

    [Fact]
    public void Settings_persist_additively_and_a_corrupt_file_never_enables_hooks()
    {
        var store = Store();
        store.Set("mygame", new GameHooksDto(new GameHookSpec(Cmd, "/c exit", 5), null));
        store.SetEnabled(true);

        var reloaded = Store();
        Assert.True(reloaded.Enabled);
        Assert.Equal(5, reloaded.Get("mygame")!.Pre!.TimeoutSeconds);

        reloaded.Set("mygame", null);
        Assert.Null(Store().Get("mygame"));

        File.WriteAllText(Path.Combine(_root, "hooks.json"), "{ not json");
        Assert.False(Store().Enabled);
    }
}
