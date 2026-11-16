using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source/policy regressions for optional game-window startup actions. No real
/// desktop input is sent; a physical Windows acceptance run remains mandatory.
/// </summary>
public sealed class OwnedGamePostLaunchPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-owned-postlaunch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static OwnedGamePostLaunchService.Candidate Window(
        int handle, int pid = 42, bool visible = true, bool owner = false,
        bool tool = false, bool minimized = false, bool enabled = true) =>
        new(new IntPtr(handle), pid, visible, owner, tool, minimized, enabled);

    [Fact]
    public void Unique_verified_pid_may_use_captionless_or_borderless_game_window()
    {
        // The post-launch selector does not require WS_CAPTION or reject maximized
        // windows as the *window placement* selector intentionally does.
        Assert.Equal(new IntPtr(7), OwnedGamePostLaunchService.SelectUniqueWindow(
            new[]
            {
                Window(1, pid: 17), Window(2, visible: false),
                Window(3, owner: true), Window(4, tool: true),
                Window(5, minimized: true), Window(6, enabled: false),
                Window(7)
            }, 42));
    }

    [Fact]
    public void Ambiguous_secondary_windows_or_foreign_processes_never_receive_actions()
    {
        Assert.Equal(IntPtr.Zero, OwnedGamePostLaunchService.SelectUniqueWindow(
            new[] { Window(1), Window(2) }, 42));
        Assert.Equal(IntPtr.Zero, OwnedGamePostLaunchService.SelectUniqueWindow(
            new[] { Window(1, pid: 123) }, 42));
        Assert.Equal(IntPtr.Zero, OwnedGamePostLaunchService.SelectUniqueWindow(
            Array.Empty<OwnedGamePostLaunchService.Candidate>(), 42));
        Assert.Equal(IntPtr.Zero, OwnedGamePostLaunchService.SelectUniqueWindow(
            new[] { Window(0) }, 42));
    }

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1, 400, -1, -1)]
    [InlineData(400, 0, -1, -1)]
    [InlineData(32768, 720, -1, -1)]
    public void Center_click_is_inside_and_bounded_to_verified_client_coordinates(
        int width, int height, int x, int y)
    {
        var center = OwnedGamePostLaunchService.CenterPoint(width, height);
        if (x < 0) Assert.Null(center);
        else
        {
            Assert.NotNull(center);
            Assert.Equal((x, y), center.Value);
        }
    }

    [Fact]
    public void Mouse_coordinate_packing_does_not_depend_on_global_display_position()
    {
        // Client coordinates, not absolute screen coordinates or a global mouse move.
        Assert.Equal(new IntPtr(unchecked((int)0x021C03C0)),
            OwnedGamePostLaunchService.MouseLParam(960, 540));
    }

    [Fact]
    public void Legacy_profile_never_attempts_a_foreground_fullscreen_or_click_action()
    {
        var p = new GameLaunchProfile { Fullscreen = true };
        Assert.False(p.PostLaunch.Requested);
        Assert.False(p.PostLaunch.BringToForeground);
        Assert.False(p.PostLaunch.RequestAltEnterFullscreen);
        Assert.False(p.PostLaunch.ClickClientCenter);
        Assert.Equal("preference_only", p.FullscreenSupport);
        Assert.Null(GameLaunchProfileValidator.Validate(p));
        Assert.Equal(GamePostLaunchStates.NotRequested,
            System.Text.Json.JsonSerializer.Deserialize<ManualGameLaunchResult>(
                """{"Launched":true,"AutomaticReturnOnExit":true,"Message":"Game started"}""")!.PostLaunch);
    }

    [Fact]
    public void Opt_in_actions_round_trip_and_old_profiles_migrate_without_ever_enabling_actions()
    {
        string path = Path.Combine(_root, "profiles.json");
        var store = new GameLaunchProfileStore(path);
        var p = new GameLaunchProfile
        {
            PostLaunch = new GamePostLaunchSettings
            {
                BringToForeground = true,
                RequestAltEnterFullscreen = true,
                ClickClientCenter = true,
                DelayMs = 1500
            }
        };
        Assert.Null(store.Save("abc123", p));
        GamePostLaunchSettings restored = new GameLaunchProfileStore(path).Get("abc123")!.PostLaunch;
        Assert.True(restored.Requested);
        Assert.True(restored.BringToForeground);
        Assert.True(restored.RequestAltEnterFullscreen);
        Assert.True(restored.ClickClientCenter);
        Assert.Equal(1500, restored.DelayMs);

        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """{"Version":1,"Profiles":{"abc123":{"Fullscreen":true}}}""");
        var old = new GameLaunchProfileStore(path);
        Assert.Null(old.LoadError);
        Assert.False(old.Get("abc123")!.PostLaunch.Requested);
    }

    [Theory]
    [InlineData(false, true, false, 1200)]
    [InlineData(false, false, true, 1200)]
    [InlineData(false, true, true, 1200)]
    [InlineData(true, false, false, -1)]
    [InlineData(true, false, false, 8001)]
    public void Unsafe_or_out_of_range_actions_are_refused_without_replacing_good_profile(
        bool focus, bool full, bool click, int delayMs)
    {
        string path = Path.Combine(_root, "profiles.json");
        var store = new GameLaunchProfileStore(path);
        Assert.Null(store.Save("abc123", new GameLaunchProfile()));
        string before = File.ReadAllText(path);

        Assert.NotNull(store.Save("abc123", new GameLaunchProfile
        {
            PostLaunch = new GamePostLaunchSettings
            {
                BringToForeground = focus, RequestAltEnterFullscreen = full,
                ClickClientCenter = click, DelayMs = delayMs
            }
        }));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(store.Get("abc123")!.PostLaunch.Requested);
    }

    [Fact]
    public void Direct_ownership_and_foreground_are_required_before_posting_input()
    {
        string service = Read("src", "PaRuX.Server", "Orchestration", "Gaming",
            "OwnedGamePostLaunchService.cs");
        string session = Read("src", "PaRuX.Server", "Orchestration", "Gaming",
            "ManualGameSessionService.cs");
        string ui = Read("src", "PaRuX.App", "Views", "GameLaunchProfileSection.cs");

        Assert.Contains("VerifyOwnedProcess(owned, verifiedImagePath)", service, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow() != candidate", service, StringComparison.Ordinal);
        Assert.Contains("SelectUniqueWindow(FindWindows(process.Id), process.Id)", service, StringComparison.Ordinal);
        Assert.Contains("keyUpPosted = PostMessage", service, StringComparison.Ordinal);
        Assert.Contains("mouseUpPosted = PostMessage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachThreadInput(", service, StringComparison.Ordinal);
        Assert.Contains("launchProfile?.PostLaunch?.Requested == true", session, StringComparison.Ordinal);
        Assert.Contains("targetConfirmed && _postLaunch != null", session, StringComparison.Ordinal);
        Assert.Contains("_postLaunch.TryApplyAsync(process, path", session, StringComparison.Ordinal);
        Assert.Contains("GameLaunchProfile_PostLaunch_", ui, StringComparison.Ordinal);
        Assert.Contains('"' + "PostFocus" + '"', ui, StringComparison.Ordinal);
    }

    private static string Read(params string[] components)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            new[] { directory!.FullName }.Concat(components).ToArray()));
    }
}
