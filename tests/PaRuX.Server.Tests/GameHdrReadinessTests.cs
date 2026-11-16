using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Offline, cross-platform pure HDR color-policy tests. No real Windows display,
/// TV, HDR toggle, registry write or game process is changed by this suite.
/// </summary>
public sealed class GameHdrReadinessTests
{
    private static GamingDisplayModeDto D(string id, string mode, bool? supported, bool? enabled,
        bool primary = false) =>
        new(id, primary, primary ? 0 : -3840, 0, 3840, 2160, 120)
        { ColorMode = mode, HdrSupported = supported, HdrEnabled = enabled };

    private static GamingDisplaySnapshotDto Screens(params GamingDisplayModeDto[] displays) =>
        new(true, "snapshot", displays);

    [Fact]
    public void Native_64_bit_CCD_color_layout_must_match_real_Windows_offsets()
    {
        if (IntPtr.Size == 8)
            Assert.True(GamingHdrDisplayProbe.NativeX64LayoutValid());
        else
            Assert.False(GamingHdrDisplayProbe.NativeX64LayoutValid());
    }

    [Theory]
    [InlineData(0x10u, 2, true, true, "hdr")]
    [InlineData(0x10u, 1, true, false, "wcg")]
    [InlineData(0x10u, 0, true, false, "sdr")]
    [InlineData(0x00u, 1, false, false, "wcg")]
    [InlineData(0x00u, 2, null, null, "unknown")]
    [InlineData(0x10u, 99, null, null, "unknown")]
    public void Modern_windows_color_info_never_conflates_HDR_and_WCG(
        uint flags, int mode, bool? supported, bool? enabled, string expectedMode)
    {
        var actual = GamingHdrDisplayProbe.FromV2(flags, mode);
        Assert.Equal(supported, actual.HdrSupported);
        Assert.Equal(enabled, actual.HdrEnabled);
        Assert.Equal(expectedMode, actual.ColorMode);
    }

    [Theory]
    [InlineData(0x03u, "advanced-color-unknown")]
    [InlineData(0x01u, "unknown")]
    [InlineData(0u, "unknown")]
    public void Legacy_advanced_color_is_never_reported_as_verified_HDR(uint bits, string mode)
    {
        var info = GamingHdrDisplayProbe.FromV1(bits);
        Assert.Null(info.HdrEnabled);
        Assert.Equal(mode, info.ColorMode);
    }

    [Fact]
    public void Legacy_profiles_never_probe_or_force_HDR()
    {
        Assert.True(GameHdrReadinessPolicy.Check("system", null, null).MayLaunch);
        Assert.Equal("", GameHdrReadinessPolicy.Check(null, null, null).Status);
        Assert.Equal("system", new GameLaunchProfile().HdrMode);
        Assert.Null(GameLaunchProfileValidator.Validate(new GameLaunchProfile()));
    }

    [Fact]
    public void Require_only_accepts_the_exact_selected_monitor_with_explicit_HDR()
    {
        var topology = Screens(
            D(@"\\.\DISPLAY1", "hdr", true, true, primary: true),
            D(@"\\.\DISPLAY2", "wcg", true, false));

        var correct = GameHdrReadinessPolicy.Check("require", @"\\.\DISPLAY1", topology);
        Assert.True(correct.MayLaunch);
        Assert.Equal("hdr-ready", correct.Status);

        var other = GameHdrReadinessPolicy.Check("require", @"\\.\DISPLAY2", topology);
        Assert.False(other.MayLaunch);
        Assert.Contains("WCG", other.Refusal);

        Assert.False(GameHdrReadinessPolicy.Check("require", null, topology).MayLaunch);
        Assert.False(GameHdrReadinessPolicy.Check("require", @"\\.\DISPLAY3", topology).MayLaunch);
        Assert.False(GameHdrReadinessPolicy.Check("require", @"\\.\DISPLAY1",
            new GamingDisplaySnapshotDto(false, "driver refused", topology.Displays)).MayLaunch);
    }

    [Theory]
    [InlineData("wcg", true, false)]
    [InlineData("sdr", true, false)]
    [InlineData("unknown", null, null)]
    [InlineData("advanced-color-unknown", null, null)]
    public void Optional_prefer_warns_but_never_changes_system_or_refuses_game(
        string mode, bool? supported, bool? enabled)
    {
        var topology = Screens(D("tv", mode, supported, enabled, primary: true));
        var preferred = GameHdrReadinessPolicy.Check("prefer", "tv", topology);
        Assert.True(preferred.MayLaunch);
        Assert.Equal("hdr-preference-not-ready", preferred.Status);
        Assert.Null(preferred.Refusal);
    }

    [Fact]
    public void Duplicated_mirrored_target_never_authorizes_a_required_HDR_game()
    {
        var t = Screens(D("tv", "hdr", true, true), D("TV", "hdr", true, true));
        Assert.False(GameHdrReadinessPolicy.Check("require", "tv", t).MayLaunch);
        Assert.False(GameHdrReadinessPolicy.Check("require", null, t).MayLaunch);
    }

    [Fact]
    public void Invalid_saved_hdr_mode_is_rejected_before_any_game_start()
    {
        var profile = new GameLaunchProfile { HdrMode = "force_windows_hdr" };
        Assert.Contains("HDR mode", GameLaunchProfileValidator.Validate(profile));
        Assert.False(GameHdrReadinessPolicy.Check(profile.HdrMode, "tv",
            Screens(D("tv", "hdr", true, true))).MayLaunch);
    }

    [Fact]
    public void New_profile_field_survives_serialization_and_legacy_json_stays_system()
    {
        var profile = new GameLaunchProfile { HdrMode = "require", DisplayId = "tv" };
        string json = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.Equal("require", System.Text.Json.JsonSerializer
            .Deserialize<GameLaunchProfile>(json)!.HdrMode);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<GameLaunchProfile>(
            """{"Fullscreen":true,"ControllerPreset":"auto","PerformanceProfile":"balanced","LaunchArgs":[],"AiConcurrency":"hard_off"}""");
        Assert.Equal("system", legacy!.HdrMode);
        Assert.Null(GameLaunchProfileValidator.Validate(legacy));
    }

    [Fact]
    public void HDR_observation_is_read_only_and_rechecked_before_process_creation()
    {
        string probe = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "GamingHdrDisplayProbe.cs");
        string launcher = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "ManualGameSessionService.cs");
        string topology = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "GamingDisplayTopologyService.cs");
        string ui = Read("src", "PaRuX.App", "Views", "GameLaunchProfileSection.cs");
        Assert.Contains("QueryDisplayConfig", probe);
        Assert.Contains("DisplayConfigGetColorInfo2", probe);
        Assert.DoesNotContain("DisplayConfigSetDeviceInfo", probe);
        Assert.DoesNotContain("SetDisplayConfig(", probe);
        Assert.Contains("GamingHdrDisplayProbe.Read(info.Device)", topology);
        Assert.Contains("GameHdrReadinessPolicy.Check(", launcher);
        Assert.Contains("if (!hdr.MayLaunch)", launcher);
        Assert.Contains("Setup(_hdrMode,", ui);
        Assert.Contains("HdrMode", ui);
        Assert.Contains("next.HdrMode =", ui);
    }

    private static string Read(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray()));
    }
}
