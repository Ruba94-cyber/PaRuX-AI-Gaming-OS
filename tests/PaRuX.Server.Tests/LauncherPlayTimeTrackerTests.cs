using System.Diagnostics;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;
using Snap = PaRuX.Server.Orchestration.Gaming.LauncherPlayTimeTracker.ProcSnap;

namespace PaRuX.Server.Tests;

public sealed class LauncherPlayTimeTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-playtime-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Virtual clock; every delay advances it, so scripted scenarios are deterministic.</summary>
    private sealed class Script : LauncherPlayTimeTracker.IProbe
    {
        public DateTime Now = T0;
        public Func<DateTime, IReadOnlyList<Snap>> Live = _ => Array.Empty<Snap>();
        public int Snapshots;
        public IReadOnlyList<Snap> Snapshot(CancellationToken ct) { Snapshots++; return Live(Now); }
        public bool IsAlive(int pid, DateTime startUtc) => Live(Now).Any(s => s.Pid == pid && s.StartUtc == startUtc);
        public Task Delay(TimeSpan d, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Now += d; return Task.CompletedTask; }
    }

    private static readonly LauncherPlayTimeTracker.Options Fast = new(
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    private Task<LauncherPlayTimeTracker.Outcome> Run(Script s, CancellationToken ct = default) =>
        LauncherPlayTimeTracker.TrackAsync(Path.Combine(_root, "Game"), T0, s, () => s.Now, s.Delay, Fast, ct);

    private string Exe(string rel) => Path.Combine(_root, "Game", rel);

    [Fact]
    public async Task Credits_game_found_by_install_path_even_though_launcher_exited_first()
    {
        var s = new Script();
        // The launcher (outside the game folder) is gone from the very first snapshot; the game runs 100 s.
        s.Live = now => now >= T0.AddSeconds(3) && now < T0.AddSeconds(103)
            ? new[] { new Snap(500, 1, T0.AddSeconds(3), Exe("game.exe")) }
            : Array.Empty<Snap>();

        var outcome = await Run(s);

        Assert.True(outcome.Observed);
        Assert.Equal(T0.AddSeconds(3), outcome.StartedUtc);
        Assert.InRange(outcome.Played.TotalSeconds, 98, 101);
    }

    [Fact]
    public async Task Ignores_processes_outside_the_folder_sibling_prefixes_and_older_pids()
    {
        var s = new Script();
        s.Live = _ => new[]
        {
            new Snap(10, 1, T0.AddSeconds(1), Path.Combine(_root, "Game-Other", "game.exe")),   // sibling folder with same prefix
            new Snap(11, 1, T0.AddSeconds(1), Path.Combine(_root, "elsewhere", "game.exe")),
            new Snap(12, 1, T0.AddMinutes(-30), Exe("game.exe")),                               // started long before the request
            new Snap(13, 1, T0.AddSeconds(1), null),                                            // unreadable image: fail closed
        };

        var outcome = await Run(s);

        Assert.False(outcome.Observed);
        Assert.Equal(TimeSpan.Zero, outcome.Played);
    }

    [Fact]
    public async Task Follows_a_direct_child_that_outlives_the_game_executable()
    {
        var s = new Script();
        s.Live = now =>
        {
            var list = new List<Snap>();
            if (now < T0.AddSeconds(20)) list.Add(new Snap(500, 1, T0.AddSeconds(2), Exe("bootstrap.exe")));
            // The real game is a different exe (outside the folder) started by the bootstrapper during its lifetime.
            if (now >= T0.AddSeconds(10) && now < T0.AddSeconds(70))
                list.Add(new Snap(600, 500, T0.AddSeconds(10), @"C:\Elsewhere\engine.exe"));
            return list;
        };

        var outcome = await Run(s);

        Assert.True(outcome.Observed);
        Assert.InRange(outcome.Played.TotalSeconds, 66, 70); // 2 s -> 70 s, not just the bootstrapper's 18 s
    }

    [Fact]
    public async Task Child_with_reused_parent_pid_or_wrong_start_is_not_adopted()
    {
        var s = new Script();
        s.Live = now =>
        {
            var list = new List<Snap>();
            if (now < T0.AddSeconds(8)) list.Add(new Snap(500, 1, T0.AddSeconds(2), Exe("game.exe")));
            // Started long before the tracked parent: cannot be its child despite the matching parent PID.
            list.Add(new Snap(700, 500, T0.AddMinutes(-60), @"C:\Elsewhere\old.exe"));
            return list;
        };

        var outcome = await Run(s);

        Assert.True(outcome.Observed);
        Assert.InRange(outcome.Played.TotalSeconds, 5, 8); // only the game exe, 2 s -> ~8 s
    }

    [Fact]
    public async Task Relaunch_into_another_executable_within_the_grace_is_still_one_session()
    {
        var s = new Script();
        s.Live = now =>
        {
            if (now < T0.AddSeconds(30)) return new[] { new Snap(500, 1, T0.AddSeconds(2), Exe("launcher-stub.exe")) };
            if (now >= T0.AddSeconds(33) && now < T0.AddSeconds(90))
                return new[] { new Snap(501, 1, T0.AddSeconds(33), Exe("bin/game64.exe")) };
            return Array.Empty<Snap>();
        };

        var outcome = await Run(s);

        Assert.InRange(outcome.Played.TotalSeconds, 85, 89);
    }

    [Fact]
    public async Task No_scan_runs_while_the_game_is_alive()
    {
        var s = new Script();
        s.Live = now => now < T0.AddSeconds(200) ? new[] { new Snap(500, 1, T0.AddSeconds(1), Exe("game.exe")) } : Array.Empty<Snap>();

        await Run(s);

        // Discovery snapshot + one at the exit + a few in the end grace; never one per play-loop tick (200 ticks).
        Assert.InRange(s.Snapshots, 2, 12);
    }

    [Fact]
    public async Task Cancellation_credits_what_was_observed()
    {
        var s = new Script();
        s.Live = _ => new[] { new Snap(500, 1, T0.AddSeconds(1), Exe("game.exe")) };
        using var cts = new CancellationTokenSource();
        int ticks = 0;
        Task Delay(TimeSpan d, CancellationToken ct)
        {
            if (++ticks == 40) cts.Cancel();
            return s.Delay(d, ct);
        }

        var outcome = await LauncherPlayTimeTracker.TrackAsync(Path.Combine(_root, "Game"), T0, s, () => s.Now, Delay, Fast, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.True(outcome.Observed);
        Assert.InRange(outcome.Played.TotalSeconds, 30, 40);
    }

    [Fact]
    public async Task Credit_is_written_to_the_title_store_and_short_sessions_are_not()
    {
        string storePath = Path.Combine(_root, "titles.json");
        var store = new GameTitleStore(storePath);
        var s = new Script();
        s.Live = now => now < T0.AddSeconds(60) ? new[] { new Snap(500, 1, T0.AddSeconds(1), Exe("game.exe")) } : Array.Empty<Snap>();
        await LauncherPlayTimeTracker.TrackAndCreditAsync(store, "mygame", Path.Combine(_root, "Game"), T0, s, () => s.Now, s.Delay, Fast, default);
        long seconds = store.Snapshot()["mygame"].PlaySeconds;
        Assert.InRange(seconds, 55, 60);

        var shortRun = new Script();
        shortRun.Live = now => now < T0.AddSeconds(3) ? new[] { new Snap(500, 1, T0.AddSeconds(1), Exe("stub.exe")) } : Array.Empty<Snap>();
        await LauncherPlayTimeTracker.TrackAndCreditAsync(store, "stub", Path.Combine(_root, "Game"), T0, shortRun, () => shortRun.Now, shortRun.Delay, Fast, default);
        Assert.False(store.Snapshot().ContainsKey("stub"));
    }

    [Theory]
    [InlineData(@"C:\Games\A\game.exe", @"C:\Games\A", true)]
    [InlineData(@"c:\games\a\bin\GAME.EXE", @"C:\Games\A\", true)]
    [InlineData(@"C:\Games\AB\game.exe", @"C:\Games\A", false)]
    [InlineData(@"C:\Games\A\..\B\game.exe", @"C:\Games\A", false)]
    [InlineData(null, @"C:\Games\A", false)]
    public void Inside_test_is_separator_aware(string? image, string dir, bool expected) =>
        Assert.Equal(expected, LauncherPlayTimeTracker.IsInside(image, dir));

    // ---- real throw-away processes ----

    private string CopyOfSystemExe(string name, string asName)
    {
        string source = Path.Combine(Environment.SystemDirectory, name);
        Directory.CreateDirectory(Path.Combine(_root, "Game"));
        string copy = Exe(asName);
        File.Copy(source, copy, overwrite: true);
        return copy;
    }

    [Fact]
    public async Task Real_game_process_is_credited_after_the_launcher_has_already_exited()
    {
        if (!OperatingSystem.IsWindows()) return;
        string game = CopyOfSystemExe("ping.exe", "game.exe");
        DateTime requested = DateTime.UtcNow;

        // "Launcher": cmd starts the game detached and exits at once.
        using var launcher = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c start \"\" /b \"" + game + "\" -n 5 127.0.0.1",
            UseShellExecute = false, CreateNoWindow = true
        })!;
        await launcher.WaitForExitAsync();

        var options = new LauncherPlayTimeTracker.Options(
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(600));
        var outcome = await LauncherPlayTimeTracker.TrackAsync(Path.Combine(_root, "Game"), requested,
            new LauncherPlayTimeTracker.WindowsProbe(), () => DateTime.UtcNow, Task.Delay, options, default);

        Assert.True(outcome.Observed);
        Assert.InRange(outcome.Played.TotalSeconds, 2.0, 12.0);
    }

    [Fact]
    public async Task Real_child_started_by_the_game_is_followed_after_the_game_exe_exits()
    {
        if (!OperatingSystem.IsWindows()) return;
        string game = CopyOfSystemExe("cmd.exe", "game.exe");
        DateTime requested = DateTime.UtcNow;

        // game.exe lives about 2 s (its own ping) and starts a detached 6 s ping from System32 (outside the folder).
        using var launcher = Process.Start(new ProcessStartInfo
        {
            FileName = game,
            Arguments = "/c \"start /b ping -n 7 127.0.0.1 >nul & ping -n 3 127.0.0.1 >nul\"",
            UseShellExecute = false, CreateNoWindow = true
        })!;

        var options = new LauncherPlayTimeTracker.Options(
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800));
        var outcome = await LauncherPlayTimeTracker.TrackAsync(Path.Combine(_root, "Game"), requested,
            new LauncherPlayTimeTracker.WindowsProbe(), () => DateTime.UtcNow, Task.Delay, options, default);
        await launcher.WaitForExitAsync();

        Assert.True(outcome.Observed);
        Assert.True(outcome.Played.TotalSeconds >= 4.5, "child must extend the session past the game exe: " + outcome.Played);
    }

    [Fact]
    public async Task Real_nothing_started_reports_not_observed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "Game"));
        var options = new LauncherPlayTimeTracker.Options(
            TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));
        var outcome = await LauncherPlayTimeTracker.TrackAsync(Path.Combine(_root, "Game"), DateTime.UtcNow,
            new LauncherPlayTimeTracker.WindowsProbe(), () => DateTime.UtcNow, Task.Delay, options, default);
        Assert.False(outcome.Observed);
    }
}
