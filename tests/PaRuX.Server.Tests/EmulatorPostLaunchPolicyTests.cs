using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pure policy tests, using synthetic local emulator paths only. Never launch or
/// download an emulator, ROM, BIOS or console keys.
/// </summary>
public sealed class EmulatorPostLaunchPolicyTests
{
    [Theory]
    [InlineData("eden", "eden.exe", ".nro")]
    [InlineData("dolphin", "Dolphin.exe", ".rvz")]
    [InlineData("cemu", "Cemu.exe", ".wua")]
    [InlineData("pcsx2", "pcsx2-qt.exe", ".iso")]
    [InlineData("duckstation", "duckstation.exe", ".chd")]
    [InlineData("ppsspp", "PPSSPPWindows64.exe", ".iso")]
    [InlineData("retroarch", "retroarch.exe", ".nes")]
    [InlineData("mame", "mame.exe", ".zip")]
    public void Native_fullscreen_flags_skip_the_extra_alt_enter_toggle(
        string emulatorId, string exeName, string romExt)
    {
        EmulatorDefinition def = EmulatorRegistry.Find(emulatorId)!;
        string rom = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", "game" + romExt);
        string exe = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", exeName);
        string? core = def.RequiresCore ? Path.Combine(Path.GetTempPath(), "core_libretro.dll") : null;
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(def, rom, true, null, core,
            EmulatorLaunchModes.Fullscreen);
        Assert.Null(plan.Error);
        var game = new ManualGameDto("emulator-game", "Game", exe, plan.Arguments, "", "game",
            def.Platform, emulatorId, rom);
        Assert.True(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(game));

        var selected = new GamePostLaunchSettings
        {
            BringToForeground = true,
            RequestAltEnterFullscreen = true,
            ClickClientCenter = true,
            DelayMs = 750
        };
        EmulatorPostLaunchPolicy.Decision decision = EmulatorPostLaunchPolicy.Resolve(game, selected);
        Assert.True(decision.NativeFullscreenRequested);
        Assert.True(decision.Actions.BringToForeground);
        Assert.False(decision.Actions.RequestAltEnterFullscreen);
        Assert.True(decision.Actions.ClickClientCenter);
        Assert.Equal(750, decision.Actions.DelayMs);
        Assert.True(selected.RequestAltEnterFullscreen); // saved user profile is never mutated
    }

    [Theory]
    [InlineData("eden", "eden.exe", ".nro")]
    [InlineData("pcsx2", "pcsx2-qt.exe", ".iso")]
    [InlineData("dolphin", "Dolphin.exe", ".rvz")]
    [InlineData("mame", "mame.exe", ".zip")]
    public void Windowed_or_unforced_import_does_not_claim_native_fullscreen(
        string emulatorId, string exeName, string ext)
    {
        EmulatorDefinition def = EmulatorRegistry.Find(emulatorId)!;
        string rom = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", "game" + ext);
        string exe = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", exeName);
        string? core = def.RequiresCore ? Path.Combine(Path.GetTempPath(), "core_libretro.dll") : null;
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(def, rom, false, null, core,
            EmulatorLaunchModes.Default);
        Assert.Null(plan.Error);
        var game = new ManualGameDto("g", "Game", exe, plan.Arguments, "", "game", def.Platform, emulatorId, rom);

        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(game));
        Assert.True(EmulatorPostLaunchPolicy.Resolve(game, new GamePostLaunchSettings
        {
            BringToForeground = true, RequestAltEnterFullscreen = true
        }).Actions.RequestAltEnterFullscreen);
    }

    [Fact]
    public void Rpcs3_without_a_supported_native_flag_stays_best_effort()
    {
        string rom = Path.Combine(Path.GetTempPath(), "EBOOT.BIN");
        EmulatorDefinition def = EmulatorRegistry.Find("rpcs3")!;
        string exe = Path.Combine(Path.GetTempPath(), "rpcs3.exe");
        var plan = EmulatorCommandBuilder.Build(def, rom, true, null, null);
        var game = new ManualGameDto("g", "Game", exe, plan.Arguments, "", "game", def.Platform,
            "rpcs3", rom);
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(game));
    }

    [Fact]
    public void Spoofed_emulator_identity_or_unrelated_executable_does_not_claim_flags()
    {
        string rom = Path.Combine(Path.GetTempPath(), "Game.iso");
        var fake = new ManualGameDto("g", "Game",
            Path.Combine(Path.GetTempPath(), "not-a-pcsx2.exe"),
            "-fullscreen -- " + EmulatorCommandBuilder.Quote(rom), "", "game", "",
            "pcsx2", rom);
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(fake));
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(fake with { EmulatorId = "" }));
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(fake with { GameFilePath = "" }));
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(fake with { Kind = "application" }));
    }

    [Fact]
    public void Windowed_switch_or_superstring_is_not_treated_as_fullscreen()
    {
        string rom = Path.Combine(Path.GetTempPath(), "Game.iso");
        string exe = Path.Combine(Path.GetTempPath(), "pcsx2-qt.exe");
        var game = new ManualGameDto("g", "Game", exe, "-fullscreen=false -- " +
            EmulatorCommandBuilder.Quote(rom), "", "game", "", "pcsx2", rom);
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(game));
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(
            game with { Arguments = "-nofullscreen -- " + EmulatorCommandBuilder.Quote(rom) }));
        Assert.False(EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(
            game with { Arguments = "-fullscreen -- " + EmulatorCommandBuilder.Quote("other.iso") }));
    }

    [Theory]
    [InlineData("pcsx2", "pcsx2-qt.exe", ".iso", "-fullscreen -nofullscreen", false)]
    [InlineData("pcsx2", "pcsx2-qt.exe", ".iso", "-nofullscreen -fullscreen", true)]
    [InlineData("dolphin", "Dolphin.exe", ".rvz", "-C Dolphin.Display.Fullscreen=True -C Dolphin.Display.Fullscreen=False", false)]
    [InlineData("dolphin", "Dolphin.exe", ".rvz", "-C Dolphin.Display.Fullscreen=False -C Dolphin.Display.Fullscreen=True", true)]
    [InlineData("cemu", "Cemu.exe", ".wua", "-f --fullscreen=false", false)]
    [InlineData("cemu", "Cemu.exe", ".wua", "--fullscreen=false -f", true)]
    [InlineData("mame", "mame.exe", ".zip", "-nowindow -window", false)]
    [InlineData("mame", "mame.exe", ".zip", "-window -nowindow", true)]
    public void Last_native_display_switch_wins_even_if_advanced_args_conflict(
        string emulatorId, string exeName, string ext, string flags, bool expectedFullscreen)
    {
        string rom = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", "game" + ext);
        string emulator = Path.Combine(Path.GetTempPath(), "parux-emulator-readiness", exeName);
        string command = flags + " " + (emulatorId == "mame"
            ? "game" : EmulatorCommandBuilder.Quote(rom));
        var game = new ManualGameDto("g", "Game", emulator, command, "", "game", "",
            emulatorId, rom);
        Assert.Equal(expectedFullscreen, EmulatorPostLaunchPolicy.HasNativeFullscreenRequest(game));
    }

    [Fact]
    public void Launcher_session_keeps_emulator_decision_inside_the_exact_verified_process()
    {
        string source = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "ManualGameSessionService.cs");
        Assert.Contains("VerifyOwnedProcess(process, path)", source, StringComparison.Ordinal);
        Assert.Contains("EmulatorPostLaunchPolicy.Resolve(", source, StringComparison.Ordinal);
        Assert.Contains("emulator.Actions, observationToken", source, StringComparison.Ordinal);
        Assert.Contains("GameEmulatorDisplayStates.NativeFullscreenArgument", source, StringComparison.Ordinal);
        string windows = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "OwnedGamePostLaunchService.cs");
        Assert.DoesNotContain("SendInput(", windows, StringComparison.Ordinal);
    }

    private static string Read(params string[] components)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(components).ToArray()));
    }
}
