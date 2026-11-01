using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Fake emulator executables and synthetic homebrew-style files only: no real emulator, ROM, BIOS or firmware.</summary>
public sealed class EmulatorManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-emu-" + Guid.NewGuid().ToString("N"));

    public EmulatorManagerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string File_(string relative, int bytes = 64)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    private static EmulatorDefinition Def(string id) => EmulatorRegistry.Find(id)!;

    [Fact]
    public void Registry_has_the_required_emulators_with_unique_ids_and_sane_templates()
    {
        string[] required = { "eden", "dolphin", "cemu", "pcsx2", "rpcs3", "duckstation", "ppsspp", "retroarch", "mame" };
        foreach (string id in required) Assert.NotNull(EmulatorRegistry.Find(id));
        Assert.Equal(EmulatorRegistry.Builtin.Count, EmulatorRegistry.Builtin.Select(d => d.Id).Distinct().Count());
        foreach (var d in EmulatorRegistry.Builtin)
        {
            Assert.Equal("unverified", d.TemplateStatus); // nothing here has been checked against an installed version
            Assert.NotEmpty(d.ExecutableNames);
            Assert.True(d.Template.Any(t => t is "{rom}" or "{romname}"), d.Id);
            Assert.All(d.ExecutableNames, n => Assert.EndsWith(".exe", n, StringComparison.OrdinalIgnoreCase));
            Assert.All(d.GameExtensions, e => Assert.StartsWith(".", e));
        }
    }

    [Fact]
    public void Eden_command_line_uses_g_and_f_and_quotes_paths_with_spaces()
    {
        string rom = File_("My Games/Homebrew Demo.nro");
        var plan = EmulatorCommandBuilder.Build(Def("eden"), rom, fullscreen: true, extraArguments: null, corePath: null);
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "-f", "-g", rom }, plan.ArgumentList);
        Assert.Equal($"-f -g \"{rom}\"", plan.Arguments);
        Assert.Equal($"-g \"{rom}\"", EmulatorCommandBuilder.Build(Def("eden"), rom, false, null, null).Arguments);
    }

    [Fact]
    public void Emulators_without_a_known_fullscreen_flag_do_not_get_a_made_up_one()
    {
        string rom = File_("a.iso");
        Assert.Null(Def("mame").FullscreenArguments);
        Assert.Null(Def("rpcs3").FullscreenArguments);
        Assert.Equal(new[] { "-b", "-C", "Dolphin.Display.Fullscreen=True", "-e", rom },
            EmulatorCommandBuilder.Build(Def("dolphin"), rom, true, null, null).ArgumentList);
    }

    [Fact]
    public void Pcsx2_and_duckstation_put_the_game_after_the_double_dash()
    {
        string rom = File_("game.iso");
        var plan = EmulatorCommandBuilder.Build(Def("pcsx2"), rom, true, "-nogui", null);
        Assert.Equal(new[] { "-batch", "-fullscreen", "-nogui", "--", rom }, plan.ArgumentList);
    }

    [Theory]
    [InlineData("eden", "-f")]
    [InlineData("cemu", "-f")]
    [InlineData("pcsx2", "-fullscreen")]
    [InlineData("duckstation", "-fullscreen")]
    [InlineData("ppsspp", "--fullscreen")]
    [InlineData("retroarch", "-f")]
    public void Explicit_fullscreen_adds_the_documented_flag(string id, string flag)
    {
        string rom = File_("demo.iso");
        string? core = id == "retroarch" ? File_("cores/core_libretro.dll") : null;
        var plan = EmulatorCommandBuilder.Build(Def(id), rom, true, null, core, EmulatorLaunchModes.Fullscreen);
        Assert.Null(plan.Error);
        Assert.Contains(flag, plan.ArgumentList);
    }

    [Theory]
    [InlineData("dolphin", "Dolphin.Display.Fullscreen=False")]
    [InlineData("pcsx2", "-nofullscreen")]
    [InlineData("duckstation", "-nofullscreen")]
    [InlineData("ppsspp", "--windowed")]
    public void Explicit_windowed_overrides_an_emulators_own_fullscreen_setting(string id, string flag)
    {
        var plan = EmulatorCommandBuilder.Build(Def(id), File_("demo.iso"),
            true, null, null, EmulatorLaunchModes.Windowed);
        Assert.Null(plan.Error);
        Assert.Contains(flag, plan.ArgumentList);
        Assert.DoesNotContain("-fullscreen", plan.ArgumentList);
    }

    [Fact]
    public void Emulator_default_never_injects_a_forced_display_switch()
    {
        string rom = File_("demo.iso");
        var plan = EmulatorCommandBuilder.Build(Def("pcsx2"), rom, true,
            null, null, EmulatorLaunchModes.Default);
        Assert.Null(plan.Error);
        Assert.DoesNotContain("-fullscreen", plan.ArgumentList);
        Assert.DoesNotContain("-nofullscreen", plan.ArgumentList);
    }

    [Theory]
    [InlineData("eden", "windowed")]
    [InlineData("cemu", "windowed")]
    [InlineData("mame", "fullscreen")]
    [InlineData("eden", "unrecognized")]
    public void Unavailable_or_invalid_explicit_display_modes_are_not_silently_ignored(string id, string mode)
    {
        var plan = EmulatorCommandBuilder.Build(Def(id), File_("demo.iso"),
            true, null, null, mode);
        Assert.NotNull(plan.Error);
        Assert.Empty(plan.ArgumentList);
    }

    [Fact]
    public void Mame_uses_the_romset_name_and_its_folder()
    {
        string rom = File_("roms/pacman.zip");
        var plan = EmulatorCommandBuilder.Build(Def("mame"), rom, true, null, null);
        Assert.Equal(new[] { "-rompath", Path.GetDirectoryName(rom)!, "pacman" }, plan.ArgumentList);
    }

    [Fact]
    public void Retroarch_requires_an_absolute_core()
    {
        string rom = File_("game.nes");
        Assert.NotNull(EmulatorCommandBuilder.Build(Def("retroarch"), rom, false, null, null).Error);
        Assert.NotNull(EmulatorCommandBuilder.Build(Def("retroarch"), rom, false, null, "core.dll").Error);
        string core = File_("cores/nestopia_libretro.dll");
        var plan = EmulatorCommandBuilder.Build(Def("retroarch"), rom, true, null, core);
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "-f", "-L", core, rom }, plan.ArgumentList);
    }

    [Theory]
    [InlineData("relative/game.iso")]
    [InlineData("")]
    public void Relative_or_empty_game_paths_are_refused(string rom) =>
        Assert.NotNull(EmulatorCommandBuilder.Build(Def("dolphin"), rom, false, null, null).Error);

    [Fact]
    public void Extra_arguments_are_bounded_and_cannot_carry_control_characters()
    {
        string rom = File_("g.iso");
        Assert.NotNull(EmulatorCommandBuilder.Build(Def("dolphin"), rom, false, "a\r\nb", null).Error);
        Assert.NotNull(EmulatorCommandBuilder.Build(Def("dolphin"), rom, false, new string('x', EmulatorCommandBuilder.MaxExtraArgumentsLength + 1), null).Error);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("trailing\\ slash\\", "\"trailing\\ slash\\\\\"")]
    [InlineData("", "\"\"")]
    public void Quoting_follows_the_windows_argument_rules(string input, string expected) =>
        Assert.Equal(expected, EmulatorCommandBuilder.Quote(input));

    [Fact]
    public void Detection_finds_only_regular_executables_with_the_expected_names()
    {
        File_("PortableApps/Dolphin/Dolphin.exe", 20_000);
        File_("Tools/cemu/Cemu.exe", 20_000);
        File_("Other/notanemulator.exe", 20_000);
        var found = EmulatorDiscovery.Detect(new[] { _root }).ToDictionary(d => d.EmulatorId);
        Assert.Contains("dolphin", found.Keys);
        Assert.Contains("cemu", found.Keys);
        Assert.DoesNotContain("eden", found.Keys);
        Assert.EndsWith("Dolphin.exe", found["dolphin"].ExecutablePath);
    }

    [Fact]
    public void Emulator_detection_respects_a_global_directory_budget()
    {
        File_("a/b/Cemu.exe", 20_000);
        Assert.Empty(EmulatorDiscovery.Detect(new[] { _root }, maxEntries: 1));
        var found = EmulatorDiscovery.Detect(new[] { _root }, maxEntries: EmulatorDiscovery.MaxDetectEntries);
        Assert.Contains(found, emulator => emulator.EmulatorId == "cemu");
        Assert.Throws<ArgumentOutOfRangeException>(() => EmulatorDiscovery.Detect(new[] { _root }, maxEntries: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmulatorDiscovery.Detect(new[] { _root }, maxEntries: EmulatorDiscovery.MaxDetectEntries + 1));
    }

    [Fact]
    public void Detection_does_not_search_deeper_than_two_levels_and_handles_missing_roots()
    {
        File_("a/b/c/d/Cemu.exe", 20_000);
        Assert.Empty(EmulatorDiscovery.Detect(new[] { _root, Path.Combine(_root, "missing") }));
    }

    [Fact]
    public void Scan_lists_supported_game_files_with_clean_titles_and_ignores_others()
    {
        File_("Switch/Cool_Homebrew_v1.2.nro");
        File_("Switch/readme.txt");
        File_("Switch/sub/Other Demo.nsp");
        var result = EmulatorDiscovery.Scan(Def("eden"), Path.Combine(_root, "Switch"));
        Assert.Null(result.Error);
        Assert.Equal(new[] { "Cool Homebrew", "Other Demo" }, result.Games.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void Scan_refuses_a_game_folder_beneath_a_linked_parent()
    {
        // The selected leaf is a normal directory, but a parent is a junction/symlink.
        // Discovery must not walk through a linked ancestor.
        string original = Path.Combine(_root, "real", "games");
        Directory.CreateDirectory(original);
        File_("real/games/Homebrew.nro");
        string alias = Path.Combine(_root, "linked-parent");
        try
        {
            Directory.CreateSymbolicLink(alias, Path.Combine(_root, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // local policy prevents building the synthetic symlink fixture
        }
        var result = EmulatorDiscovery.Scan(Def("eden"), Path.Combine(alias, "games"));
        Assert.NotNull(result.Error);
        Assert.Empty(result.Games);
        Assert.True(File.Exists(Path.Combine(original, "Homebrew.nro")));
    }

    [Fact]
    public void Scan_takes_the_title_of_a_generic_eboot_from_the_game_folder()
    {
        File_("ps3/Space Demo/PS3_GAME/USRDIR/EBOOT.BIN");
        var result = EmulatorDiscovery.Scan(Def("rpcs3"), Path.Combine(_root, "ps3"));
        var game = Assert.Single(result.Games);
        Assert.Equal("Space Demo", game.Title);
    }

    [Fact]
    public void Scan_refuses_unsafe_folders_and_honours_limits()
    {
        Assert.NotNull(EmulatorDiscovery.Scan(Def("eden"), "relative").Error);
        Assert.NotNull(EmulatorDiscovery.Scan(Def("eden"), @"\\server\share").Error);
        Assert.NotNull(EmulatorDiscovery.Scan(Def("eden"), Path.Combine(_root, "nope")).Error);
        Directory.CreateDirectory(Path.Combine(_root, "many"));
        for (int i = 0; i < EmulatorDiscovery.MaxGames + 10; i++) File.WriteAllBytes(Path.Combine(_root, "many", $"g{i}.nro"), new byte[8]);
        var result = EmulatorDiscovery.Scan(Def("eden"), Path.Combine(_root, "many"));
        Assert.True(result.Truncated);
        Assert.True(result.Games.Count <= EmulatorDiscovery.MaxGames);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => EmulatorDiscovery.Scan(Def("eden"), Path.Combine(_root, "many"), cts.Token));
    }

    [Fact]
    public void Import_adds_emulated_entries_that_reuse_the_normal_launch_path()
    {
        string exe = File_("emu/eden.exe", 20_000);
        string rom = File_("games/Demo Game.nro");
        var library = new ManualGameLibrary(Path.Combine(_root, "lib.json"));
        var result = EmulatorEndpoints.Import(new("eden", exe, new() { rom }, Fullscreen: true, ExtraArguments: null, CorePath: null), library);

        Assert.Equal(1, result.Added);
        Assert.Empty(result.Problems);
        var entry = Assert.Single(library.List());
        Assert.Equal("Demo Game", entry.Name);
        Assert.Equal("eden", entry.EmulatorId);
        Assert.Equal("Nintendo Switch", entry.Platform);
        Assert.Equal(Path.GetFullPath(exe), entry.ExecutablePath);
        Assert.Equal($"-f -g \"{rom}\"", entry.Arguments);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(exe)), entry.WorkingDirectory);

        var title = Assert.Single(GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(), library.List(), new Dictionary<string, GameTitleState>()));
        Assert.Equal(GameInstallationSources.Emulator, title.Installations[0].Source);

        var again = EmulatorEndpoints.Import(new("eden", exe, new() { rom }, true, null, null), library);
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Skipped);
    }

    [Fact]
    public void Import_preserves_the_selected_per_game_windowed_option_in_the_library()
    {
        string exe = File_("emu/pcsx2-qt.exe", 20_000);
        string rom = File_("games/Window Test.iso");
        var library = new ManualGameLibrary(Path.Combine(_root, "windowed.json"));
        var result = EmulatorEndpoints.Import(new("pcsx2", exe, new() { rom },
            true, null, null, EmulatorLaunchModes.Windowed), library);
        Assert.Equal(1, result.Added);
        Assert.Empty(result.Problems);
        Assert.Contains("-nofullscreen", Assert.Single(library.List()).Arguments);
    }

    [Fact]
    public void Import_refuses_a_program_that_is_not_the_named_emulator_and_unsupported_files()
    {
        string notEmulator = File_("emu/calc.exe", 20_000);
        string rom = File_("games/g.nro");
        string text = File_("games/notes.txt");
        var library = new ManualGameLibrary(Path.Combine(_root, "lib2.json"));
        Assert.Equal(0, EmulatorEndpoints.Import(new("eden", notEmulator, new() { rom }, false, null, null), library).Added);
        Assert.Equal(0, EmulatorEndpoints.Import(new("nope", notEmulator, new() { rom }, false, null, null), library).Added);

        string exe = File_("emu2/eden.exe", 20_000);
        var mixed = EmulatorEndpoints.Import(new("eden", exe, new() { rom, text, "relative.nro" }, false, null, null), library);
        Assert.Equal(1, mixed.Added);
        Assert.Equal(2, mixed.Problems.Count);
        Assert.Empty(library.List().Where(g => g.Name == "notes"));
    }

    [Fact]
    public void Import_batch_size_is_bounded()
    {
        string exe = File_("emu3/eden.exe", 20_000);
        var library = new ManualGameLibrary(Path.Combine(_root, "lib3.json"));
        var paths = Enumerable.Range(0, EmulatorEndpoints.MaxImportBatch + 1).Select(i => Path.Combine(_root, $"x{i}.nro")).ToList();
        Assert.Equal(0, EmulatorEndpoints.Import(new("eden", exe, paths, false, null, null), library).Added);
        Assert.Equal(0, EmulatorEndpoints.Import(new("eden", exe, new(), false, null, null), library).Added);
    }
}
