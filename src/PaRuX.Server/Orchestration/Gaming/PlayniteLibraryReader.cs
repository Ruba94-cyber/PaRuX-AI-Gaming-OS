using LiteDB;
using PaRuX.Server.Orchestration.Gaming.Emulators;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>One Playnite game translated into what PaRuX can launch and remember.</summary>
public sealed record PlayniteImportGame(
    string Id,
    string Name,
    string Platform,
    /// <summary>"emulator", "program", "store" (Steam/Epic/GOG: PaRuX discovers those itself) or "unsupported".</summary>
    string Kind,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string EmulatorId,
    string GameFilePath,
    bool Favorite,
    bool Hidden,
    long PlaySeconds,
    DateTime? LastPlayedUtc,
    string CoverPath,
    string SkipReason)
{
    public bool Importable => SkipReason.Length == 0;
}

/// <summary>
/// Read-only reader for a Playnite library folder (<c>%APPDATA%\Playnite\library</c> or a portable copy). Playnite
/// (MIT licensed) keeps its data in LiteDB files; this reader copies the few files it needs into a private temporary
/// folder, opens the COPY and reads untyped BSON documents only (no object mapping, no <c>$type</c> resolution), so the
/// user's Playnite data is never opened for writing, upgraded in place or locked. Nothing is launched and no path is
/// executed here; every path is validated again by <see cref="ManualGameLibrary"/> when it is imported.
/// </summary>
public static class PlayniteLibraryReader
{
    public const int MaxGames = 10_000;
    private const long MaxDbBytes = 256L * 1024 * 1024;

    public static string? DefaultFolder()
    {
        // Lets tests and portable setups point at a specific library copy instead of the user's profile.
        string? overridePath = Environment.GetEnvironmentVariable("PARUX_PLAYNITE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(Path.Combine(overridePath, "games.db")) ? overridePath : null;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = Path.Combine(appData, "Playnite", "library");
        return File.Exists(Path.Combine(path, "games.db")) ? path : null;
    }

    public static IReadOnlyList<PlayniteImportGame> Read(string libraryFolder)
    {
        if (string.IsNullOrWhiteSpace(libraryFolder) || !Path.IsPathFullyQualified(libraryFolder))
            throw new InvalidDataException("The Playnite library folder must be a full local path.");
        string folder = Path.GetFullPath(libraryFolder);
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Network folders are not read.");
        string gamesDb = Path.Combine(folder, "games.db");
        RequireRegularFile(gamesDb);

        string temp = Path.Combine(Path.GetTempPath(), "parux-playnite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            BsonDocument[] games = LoadCollection(folder, temp, "games.db", "Game");
            BsonDocument[] emulators = LoadCollection(folder, temp, "emulators.db", "Emulator", optional: true);
            BsonDocument[] platforms = LoadCollection(folder, temp, "platforms.db", "Platform", optional: true);
            if (games.Length > MaxGames) throw new InvalidDataException("The Playnite library has more games than PaRuX imports at once.");

            var platformNames = platforms.Where(p => p.ContainsKey("_id")).ToDictionary(
                p => p["_id"].RawValue?.ToString() ?? "", p => Text(p, "Name"), StringComparer.OrdinalIgnoreCase);
            var emulatorById = emulators.Where(e => e.ContainsKey("_id")).ToDictionary(
                e => e["_id"].RawValue?.ToString() ?? "", e => e, StringComparer.OrdinalIgnoreCase);

            var result = new List<PlayniteImportGame>();
            foreach (BsonDocument game in games)
            {
                PlayniteImportGame? item = Translate(game, folder, platformNames, emulatorById);
                if (item is not null) result.Add(item);
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static BsonDocument[] LoadCollection(string folder, string temp, string file, string collection, bool optional = false)
    {
        string source = Path.Combine(folder, file);
        if (!File.Exists(source))
        {
            if (optional) return Array.Empty<BsonDocument>();
            throw new FileNotFoundException("Playnite file not found.", file);
        }
        RequireRegularFile(source);
        string copy = Path.Combine(temp, file);
        using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var to = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            from.CopyTo(to);
        // Upgrade=true lets LiteDB 5 read Playnite's LiteDB 4 files; it rewrites only our private copy.
        using var db = new LiteDatabase(new ConnectionString { Filename = copy, Upgrade = true, Connection = ConnectionType.Direct });
        if (!db.CollectionExists(collection)) return Array.Empty<BsonDocument>();
        return db.GetCollection(collection).FindAll().ToArray();
    }

    private static void RequireRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            throw new InvalidDataException("A Playnite database file is missing or is a link.");
        if (info.Length > MaxDbBytes) throw new InvalidDataException("A Playnite database file is too large.");
    }

    private static PlayniteImportGame? Translate(
        BsonDocument game, string libraryFolder, Dictionary<string, string> platformNames, Dictionary<string, BsonDocument> emulators)
    {
        string name = Text(game, "Name");
        string id = game.ContainsKey("_id") ? game["_id"].RawValue?.ToString() ?? "" : "";
        if (name.Length == 0 || name.Length > 200 || name.Any(char.IsControl) || id.Length == 0) return null;

        string installDir = Text(game, "InstallDirectory");
        string platform = "";
        if (game.TryGetValue("PlatformIds", out BsonValue plats) && plats.IsArray && plats.AsArray.Count > 0
            && platformNames.TryGetValue(plats.AsArray[0].RawValue?.ToString() ?? "", out string? pn)) platform = pn;
        string pluginId = game.ContainsKey("PluginId") ? game["PluginId"].RawValue?.ToString() ?? "" : "";
        bool hasPlugin = pluginId.Length > 0 && pluginId != Guid.Empty.ToString();

        long playSeconds = 0;
        if (game.TryGetValue("Playtime", out BsonValue pt) && pt.IsNumber) playSeconds = Math.Max(0, pt.AsInt64);
        DateTime? last = game.TryGetValue("LastActivity", out BsonValue la) && la.IsDateTime ? la.AsDateTime.ToUniversalTime() : null;

        BsonDocument? play = null;
        if (game.TryGetValue("GameActions", out BsonValue actions) && actions.IsArray)
        {
            foreach (BsonValue a in actions.AsArray)
            {
                if (!a.IsDocument) continue;
                BsonDocument doc = a.AsDocument;
                if (play is null) play = doc;
                if (doc.TryGetValue("IsPlayAction", out BsonValue ip) && ip.IsBoolean && ip.AsBoolean) { play = doc; break; }
            }
        }

        string cover = ResolveCover(libraryFolder, Text(game, "CoverImage"));
        bool favorite = game.TryGetValue("Favorite", out BsonValue f) && f.IsBoolean && f.AsBoolean;
        bool hidden = game.TryGetValue("Hidden", out BsonValue h) && h.IsBoolean && h.AsBoolean;

        PlayniteImportGame Make(string kind, string exe, string args, string wd, string emu, string file, string skip) =>
            new(id, name, platform, kind, exe, args, wd, emu, file, favorite, hidden, playSeconds, last, cover, skip);

        string type = play is null ? "" : Text(play, "Type");
        if (type == "File")
        {
            string exe = Expand(Text(play!, "Path"), installDir, installDir);
            string wd = Expand(Text(play!, "WorkingDir"), installDir, installDir);
            if (!Path.IsPathFullyQualified(exe)) return Make("unsupported", "", "", "", "", "", "The program path is relative or empty.");
            return Make("program", exe, Text(play!, "Arguments"), wd, "", "", "");
        }
        if (type == "Emulator")
        {
            string emulatorId = play!.ContainsKey("EmulatorId") ? play["EmulatorId"].RawValue?.ToString() ?? "" : "";
            if (!emulators.TryGetValue(emulatorId, out BsonDocument? emulator))
                return Make("unsupported", "", "", "", "", "", "The emulator is not defined in this Playnite library.");
            string? exe = FindEmulatorExecutable(emulator, play.ContainsKey("EmulatorProfileId") ? play["EmulatorProfileId"].RawValue?.ToString() ?? "" : "");
            string rom = "";
            if (game.TryGetValue("Roms", out BsonValue roms) && roms.IsArray && roms.AsArray.Count > 0 && roms.AsArray[0].IsDocument)
                rom = Expand(Text(roms.AsArray[0].AsDocument, "Path"), installDir, installDir);
            if (exe is null) return Make("unsupported", "", "", "", "", rom, "The emulator program is not set in Playnite.");
            EmulatorDefinition? definition = EmulatorRegistry.Builtin.FirstOrDefault(d =>
                d.ExecutableNames.Any(n => string.Equals(n, Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)));
            if (definition is null) return Make("unsupported", exe, "", "", "", rom, "PaRuX has no launch recipe for this emulator.");
            if (rom.Length == 0 || !Path.IsPathFullyQualified(rom)) return Make("unsupported", exe, "", "", definition.Id, rom, "The game file path is empty.");
            return Make("emulator", exe, "", "", definition.Id, rom, "");
        }
        if (hasPlugin) return Make("store", "", "", "", "", "", "Managed by a store library (Steam, Epic or GOG): PaRuX discovers installed store games itself.");
        return Make("unsupported", "", "", "", "", "", type == "URL" ? "Starts through a web link, which PaRuX does not run." : "Playnite has no launch action for this game.");
    }

    private static string? FindEmulatorExecutable(BsonDocument emulator, string profileId)
    {
        string installDir = Text(emulator, "InstallDir");
        if (emulator.TryGetValue("CustomProfiles", out BsonValue profiles) && profiles.IsArray)
            foreach (BsonValue p in profiles.AsArray)
            {
                if (!p.IsDocument) continue;
                BsonDocument profile = p.AsDocument;
                if (profileId.Length > 0 && !string.Equals(profile["_id"].RawValue?.ToString(), profileId, StringComparison.Ordinal)) continue;
                string exe = Expand(Text(profile, "Executable"), installDir, installDir);
                if (exe.Length > 0 && Path.IsPathFullyQualified(exe)) return exe;
            }
        // A built-in Playnite profile has no stored executable: look for a known emulator program in its folder.
        if (installDir.Length > 0 && Path.IsPathFullyQualified(installDir) && Directory.Exists(installDir))
            foreach (EmulatorDefinition d in EmulatorRegistry.Builtin)
                foreach (string exeName in d.ExecutableNames)
                {
                    string candidate = Path.Combine(installDir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
        return null;
    }

    private static string ResolveCover(string libraryFolder, string relative)
    {
        if (relative.Length == 0 || relative.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return "";
        string root = Path.GetFullPath(Path.Combine(libraryFolder, "files")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
        var info = new FileInfo(full);
        return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is null ? full : "";
    }

    /// <summary>Resolves the Playnite variables that matter for launching ({InstallDir}, {EmulatorDir}); anything else stays literal and fails validation later.</summary>
    private static string Expand(string value, string installDir, string emulatorDir) =>
        value.Replace("{InstallDir}", installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
             .Replace("{EmulatorDir}", emulatorDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Text(BsonDocument doc, string key) =>
        doc.TryGetValue(key, out BsonValue v) && v.IsString ? v.AsString.Trim() : "";
}
