namespace PaRuX.Server.Orchestration.Gaming.Emulators;

/// <summary>
/// How an emulator is started. <see cref="Template"/> is an argument LIST (never a shell string) with these tokens:
/// {rom} full path of the game file, {romdir} its folder, {romname} file name without extension, {core} libretro core
/// (RetroArch), {fullscreen} expands to the documented fullscreen/windowed switches selected by the user,
/// {input-profile} inserts the optional sanitized Eden controller profile, and {extra} inserts explicitly
/// provided advanced arguments before PaRuX's display selection. The launch syntax is sourced from project docs,
/// but <see cref="TemplateStatus"/> stays "unverified" until the installed emulator build is tested.
/// </summary>
public sealed record EmulatorDefinition(
    string Id,
    string Name,
    string Platform,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> GameExtensions,
    IReadOnlyList<string> GameFileNames,
    IReadOnlyList<string> Template,
    IReadOnlyList<string>? FullscreenArguments,
    bool RequiresCore,
    string TemplateStatus = "unverified",
    IReadOnlyList<string>? WindowedArguments = null);

/// <summary>
/// Built-in emulator definitions. PaRuX does not download, bundle or configure emulator firmware, BIOS images, keys
/// or game dumps; it only starts an emulator the user installed with game files the user already has.
/// </summary>
public static class EmulatorRegistry
{
    private static readonly string[] None = Array.Empty<string>();

    public static readonly IReadOnlyList<EmulatorDefinition> Builtin = new EmulatorDefinition[]
    {
        new("eden", "Eden", "Nintendo Switch", new[] { "eden.exe" },
            new[] { ".nsp", ".xci", ".nro", ".nso", ".nca" }, None,
            new[] { "{extra}", "{fullscreen}", "{input-profile}", "-g", "{rom}" }, new[] { "-f" }, false),
        new("eden-cli", "Eden CLI", "Nintendo Switch", new[] { "eden-cli.exe" },
            new[] { ".nsp", ".xci", ".nro", ".nso", ".nca" }, None,
            new[] { "{extra}", "{fullscreen}", "{input-profile}", "--game", "{rom}" },
            new[] { "--fullscreen" }, false),
        new("dolphin", "Dolphin", "GameCube / Wii", new[] { "Dolphin.exe" },
            new[] { ".iso", ".gcm", ".gcz", ".rvz", ".wbfs", ".wad", ".dol", ".elf" }, None,
            new[] { "-b", "{extra}", "{fullscreen}", "-e", "{rom}" },
            new[] { "-C", "Dolphin.Display.Fullscreen=True" }, false,
            WindowedArguments: new[] { "-C", "Dolphin.Display.Fullscreen=False" }),
        new("cemu", "Cemu", "Wii U", new[] { "Cemu.exe" },
            new[] { ".wua", ".wud", ".wux", ".rpx" }, None,
            new[] { "{extra}", "{fullscreen}", "-g", "{rom}" }, new[] { "-f" }, false,
            WindowedArguments: new[] { "--fullscreen=false" }),
        new("pcsx2", "PCSX2", "PlayStation 2", new[] { "pcsx2-qt.exe", "pcsx2.exe" },
            new[] { ".iso", ".chd", ".cso", ".bin", ".elf", ".gz" }, None,
            new[] { "-batch", "{extra}", "{fullscreen}", "--", "{rom}" }, new[] { "-fullscreen" }, false,
            WindowedArguments: new[] { "-nofullscreen" }),
        new("rpcs3", "RPCS3", "PlayStation 3", new[] { "rpcs3.exe" },
            new[] { ".elf" }, new[] { "EBOOT.BIN" },
            new[] { "--no-gui", "{extra}", "{rom}" }, null, false),
        new("duckstation", "DuckStation", "PlayStation", new[] { "duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt.exe", "duckstation.exe" },
            new[] { ".cue", ".chd", ".m3u", ".pbp", ".iso" }, None,
            new[] { "-batch", "{extra}", "{fullscreen}", "--", "{rom}" }, new[] { "-fullscreen" }, false,
            WindowedArguments: new[] { "-nofullscreen" }),
        new("ppsspp", "PPSSPP", "PlayStation Portable", new[] { "PPSSPPWindows64.exe", "PPSSPPWindows.exe" },
            new[] { ".iso", ".cso", ".pbp", ".elf" }, None,
            new[] { "{extra}", "{fullscreen}", "{rom}" }, new[] { "--fullscreen" }, false,
            WindowedArguments: new[] { "--windowed" }),
        new("retroarch", "RetroArch", "Retro systems (needs a core)", new[] { "retroarch.exe" },
            new[] { ".nes", ".sfc", ".smc", ".gb", ".gbc", ".gba", ".md", ".gen", ".sms", ".gg", ".pce", ".a26", ".n64", ".z64" }, None,
            new[] { "{extra}", "{fullscreen}", "-L", "{core}", "{rom}" }, new[] { "-f" }, true),
        new("mame", "MAME", "Arcade", new[] { "mame.exe" },
            new[] { ".zip", ".7z" }, None,
            new[] { "-rompath", "{romdir}", "{extra}", "{fullscreen}", "{romname}" },
            new[] { "-nowindow" }, false,
            WindowedArguments: new[] { "-window" }),
    };

    public static EmulatorDefinition? Find(string? id) =>
        Builtin.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the file looks like a game for this emulator (extension or well-known file name).</summary>
    public static bool IsGameFile(EmulatorDefinition definition, string path)
    {
        string name = Path.GetFileName(path);
        if (definition.GameFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        string extension = Path.GetExtension(path);
        return extension.Length > 0 && definition.GameExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
