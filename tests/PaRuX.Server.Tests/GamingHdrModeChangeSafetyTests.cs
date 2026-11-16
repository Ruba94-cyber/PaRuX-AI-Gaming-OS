using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Display safety fixtures: simulated topology/driver, zero OS mode writes.</summary>
public sealed class GamingHdrModeChangeSafetyTests
{
    private static GamingDisplayModeDto Display(string id, bool primary, string color) =>
        new(id, primary, primary ? 0 : -3840, 0, 3840, 2160, 120)
        {
            HdrSupported = color == "hdr",
            HdrEnabled = color == "hdr",
            ColorMode = color
        };

    private static GamingDisplaySnapshotDto Snapshot(params GamingDisplayModeDto[] displays) =>
        new(true, "synthetic", displays);

    private static readonly ConsoleDisplayAssignment Tv =
        new("tv", ConsoleDisplaySelectionPolicy.TvRole);

    [Fact]
    public void Mode_editor_targets_configured_TV_even_when_primary_monitor_is_different()
    {
        var screens = Snapshot(Display("pc", true, "sdr"), Display("tv", false, "hdr"));
        Assert.Equal("tv", GameDisplayModeTargetPolicy.Select(screens, new[] { Tv }, "tv")?.DisplayId);
        Assert.Equal("pc", GameDisplayModeTargetPolicy.Select(screens, new[] { Tv }, "")?.DisplayId);
        Assert.Null(GameDisplayModeTargetPolicy.Select(screens,
            Array.Empty<ConsoleDisplayAssignment>(), "tv"));
        Assert.Null(GameDisplayModeTargetPolicy.Select(screens, new[] { Tv }, "disconnected"));
        Assert.Null(GameDisplayModeTargetPolicy.Select(new(false, "driver offline", screens.Displays),
            new[] { Tv }, "tv"));
    }

    [Theory]
    [InlineData("hdr", true)]
    [InlineData("wcg", true)]
    [InlineData("advanced-color-unknown", true)]
    [InlineData("sdr", false)]
    [InlineData("unknown", false)]
    public void Advanced_color_modes_are_not_passed_to_uncoordinated_resolution_mutation(
        string mode, bool protectedMode)
    {
        var d = Display("tv", true, mode);
        Assert.Equal(protectedMode, GameDisplayModeTargetPolicy.NeedsColorSafeTransaction(d));
    }

    private sealed class FakeModeApi : IDisplayModeApi
    {
        internal int Tests, Applies;
        internal DisplayModeEntry CurrentMode = new(1920, 1080, 60, 32, 0, 0);
        public DisplayModeEntry? Current(string _) => CurrentMode;
        public IReadOnlyList<DisplayModeEntry> Supported(string _) =>
            new DisplayModeEntry[] { CurrentMode, new(3840, 2160, 120, 32, 0, 0) };
        public bool Test(string _, DisplayModeEntry __) { Tests++; return true; }
        public bool ApplyTemporary(string _, DisplayModeEntry __) { Applies++; return true; }
        public bool ApplyPermanent(string _, DisplayModeEntry __) => true;
    }

    [Theory]
    [InlineData("hdr")]
    [InlineData("wcg")]
    [InlineData("advanced-color-unknown")]
    public void Core_refuses_unsafe_display_change_before_driver_test_or_restore_journal(string mode)
    {
        var api = new FakeModeApi();
        string record = Path.Combine(Path.GetTempPath(),
            "parux-hdr-mode-" + Guid.NewGuid().ToString("N") + ".json");
        using var service = new GamingDisplayModeService(api,
            new GamingDisplayTopologyService(() => new[] { Display("tv", true, mode) }),
            () => false, record, TimeSpan.FromSeconds(15));

        Assert.Throws<InvalidOperationException>(() =>
            service.Request(new GamingDisplayModeChangeRequest("tv", 3840, 2160, 120)));
        Assert.Equal(0, api.Tests);
        Assert.Equal(0, api.Applies);
        Assert.False(File.Exists(record));
        Assert.False(service.Status().Pending);
    }

    [Fact]
    public void HDR_turning_on_between_mode_driver_test_and_apply_aborts_the_transaction()
    {
        // Driver's test is allowed only when source was SDR; if an external
        // user turns HDR on meanwhile, do not apply a mode that could undo it.
        string color = "sdr";
        var api = new TestCallbackModeApi(() => color = "hdr");
        string record = Path.Combine(Path.GetTempPath(),
            "parux-hdr-mode-hot-" + Guid.NewGuid().ToString("N") + ".json");
        using var service = new GamingDisplayModeService(api,
            new GamingDisplayTopologyService(() => new[] { Display("tv", true, color) }),
            () => false, record, TimeSpan.FromSeconds(15));

        Assert.Throws<InvalidOperationException>(() =>
            service.Request(new GamingDisplayModeChangeRequest("tv", 3840, 2160, 120)));
        Assert.Equal(1, api.Tests);
        Assert.Equal(0, api.Applies);
        Assert.False(File.Exists(record));
    }

    private sealed class TestCallbackModeApi(Action onTest) : IDisplayModeApi
    {
        internal int Tests, Applies;
        public DisplayModeEntry? Current(string _) => new DisplayModeEntry(1920, 1080, 60, 32, 0, 0);
        public IReadOnlyList<DisplayModeEntry> Supported(string _) => new[]
        {
            new DisplayModeEntry(1920, 1080, 60, 32, 0, 0),
            new DisplayModeEntry(3840, 2160, 120, 32, 0, 0)
        };
        public bool Test(string _, DisplayModeEntry __) { Tests++; onTest(); return true; }
        public bool ApplyTemporary(string _, DisplayModeEntry __) { Applies++; return true; }
        public bool ApplyPermanent(string _, DisplayModeEntry __) => true;
    }

    [Fact]
    public void Mode_editor_source_does_not_silently_fall_back_to_primary_on_missing_TV()
    {
        string panel = Read("src", "PaRuX.App", "Views", "GamingDevicesPanel.cs");
        Assert.Contains("GameDisplayModeTargetPolicy.Select(", panel);
        Assert.Contains("ConsoleTargetDisplayId", panel);
        Assert.Contains("GameDisplayModeTargetPolicy.NeedsColorSafeTransaction(selected)", panel);
        Assert.DoesNotContain("Load primary display modes", panel);
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
