using System.Security.Cryptography;
using LiteDB;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic Playnite-style library only (invented titles, empty stand-in files); no real Playnite profile is read.</summary>
public sealed class PlayniteLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-playnite-test-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_root, "library");

    public PlayniteLibraryReaderTests() => Directory.CreateDirectory(Library);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static BsonValue Guid_(Guid g) => new BsonValue(g);

    private void Write(string file, string collection, params BsonDocument[] docs)
    {
        using var db = new LiteDatabase(Path.Combine(Library, file));
        var col = db.GetCollection(collection);
        foreach (BsonDocument d in docs) col.Insert(d);
    }

    private string Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    private static BsonDocument Action(string type, string? path = null, Guid? emulator = null, string? profile = null)
    {
        var d = new BsonDocument { ["Type"] = type, ["IsPlayAction"] = true, ["Name"] = "Play" };
        if (path is not null) d["Path"] = path;
        if (emulator is not null) { d["EmulatorId"] = Guid_(emulator.Value); d["EmulatorProfileId"] = profile; }
        return d;
    }

    private (string Hash, string Exe, string Rom) BuildLibrary()
    {
        string exe = Touch(@"games\Alpha\alpha.exe");
        string emulatorExe = Touch(@"emu\Eden\eden.exe");
        string rom = Touch(@"roms\Beta Quest [0100000000000000].nsp");
        string cover = Touch(@"library\files\beta\cover.jpg");
        Guid emulatorId = Guid.NewGuid(), platformId = Guid.NewGuid();
        Write("platforms.db", "Platform", new BsonDocument { ["_id"] = Guid_(platformId), ["Name"] = "Nintendo Switch" });
        Write("emulators.db", "Emulator", new BsonDocument
        {
            ["_id"] = Guid_(emulatorId), ["Name"] = "Switch", ["BuiltInConfigId"] = "yuzu", ["InstallDir"] = Path.GetDirectoryName(emulatorExe),
            ["CustomProfiles"] = new BsonArray { new BsonDocument { ["_id"] = "#custom_1", ["Executable"] = emulatorExe, ["Name"] = "Eden" } },
        });
        Write("games.db", "Game",
            new BsonDocument
            {
                ["_id"] = Guid_(Guid.NewGuid()), ["Name"] = "Alpha Racer", ["InstallDirectory"] = Path.GetDirectoryName(exe),
                ["Favorite"] = true, ["Playtime"] = 5400L, ["LastActivity"] = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
                ["GameActions"] = new BsonArray { Action("File", "{InstallDir}\\alpha.exe") },
                ["PlatformIds"] = new BsonArray { Guid_(platformId) },
                ["PluginId"] = Guid_(Guid.Empty),
            },
            new BsonDocument
            {
                ["_id"] = Guid_(Guid.NewGuid()), ["Name"] = "Beta Quest", ["InstallDirectory"] = Path.GetDirectoryName(rom), ["Hidden"] = true,
                ["CoverImage"] = @"beta\cover.jpg",
                ["GameActions"] = new BsonArray { Action("Emulator", emulator: emulatorId, profile: "#custom_1") },
                ["Roms"] = new BsonArray { new BsonDocument { ["Name"] = "Beta", ["Path"] = "{InstallDir}\\Beta Quest [0100000000000000].nsp" } },
                ["PlatformIds"] = new BsonArray { Guid_(platformId) },
            },
            new BsonDocument
            {
                ["_id"] = Guid_(Guid.NewGuid()), ["Name"] = "Gamma Store", ["PluginId"] = Guid_(Guid.NewGuid()), ["InstallDirectory"] = @"C:\steam\gamma",
            },
            new BsonDocument
            {
                ["_id"] = Guid_(Guid.NewGuid()), ["Name"] = "Delta Link",
                ["GameActions"] = new BsonArray { Action("URL", "steam://rungameid/1") },
            },
            new BsonDocument
            {
                ["_id"] = Guid_(Guid.NewGuid()), ["Name"] = "Escape Cover", ["InstallDirectory"] = Path.GetDirectoryName(exe), ["CoverImage"] = @"..\..\secret.txt",
                ["GameActions"] = new BsonArray { Action("File", "{InstallDir}\\alpha.exe") },
            });
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Library, "games.db"))));
        return (hash, exe, rom);
    }

    [Fact]
    public void Reads_programs_emulator_titles_and_explains_every_skipped_game()
    {
        var (_, exe, rom) = BuildLibrary();
        IReadOnlyList<PlayniteImportGame> games = PlayniteLibraryReader.Read(Library);

        PlayniteImportGame alpha = games.Single(g => g.Name == "Alpha Racer");
        Assert.True(alpha.Importable);
        Assert.Equal("program", alpha.Kind);
        Assert.Equal(exe, alpha.ExecutablePath);
        Assert.True(alpha.Favorite);
        Assert.Equal(5400, alpha.PlaySeconds);
        Assert.Equal("Nintendo Switch", alpha.Platform);
        Assert.Equal(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), alpha.LastPlayedUtc);

        PlayniteImportGame beta = games.Single(g => g.Name == "Beta Quest");
        Assert.True(beta.Importable);
        Assert.Equal("emulator", beta.Kind);
        Assert.Equal("eden", beta.EmulatorId);
        Assert.Equal(rom, beta.GameFilePath);
        Assert.True(beta.Hidden);
        Assert.EndsWith("cover.jpg", beta.CoverPath, StringComparison.OrdinalIgnoreCase);

        Assert.False(games.Single(g => g.Name == "Gamma Store").Importable);
        Assert.Contains("store", games.Single(g => g.Name == "Gamma Store").SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(games.Single(g => g.Name == "Delta Link").Importable);
    }

    [Fact]
    public void Cover_paths_cannot_leave_the_Playnite_files_folder()
    {
        BuildLibrary();
        Touch("secret.txt");
        PlayniteImportGame escape = PlayniteLibraryReader.Read(Library).Single(g => g.Name == "Escape Cover");
        Assert.Equal("", escape.CoverPath);
    }

    [Fact]
    public void Never_modifies_the_Playnite_files_it_reads()
    {
        var (hash, _, _) = BuildLibrary();
        string[] before = Directory.GetFiles(Library).OrderBy(f => f).ToArray();
        _ = PlayniteLibraryReader.Read(Library);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Library, "games.db")))));
        Assert.Equal(before, Directory.GetFiles(Library).OrderBy(f => f).ToArray());
    }

    [Fact]
    public void Refuses_relative_and_network_folders_and_a_missing_database()
    {
        Assert.Throws<InvalidDataException>(() => PlayniteLibraryReader.Read(@"library"));
        Assert.Throws<InvalidDataException>(() => PlayniteLibraryReader.Read(@"\\server\share\library"));
        Assert.Throws<InvalidDataException>(() => PlayniteLibraryReader.Read(Path.Combine(_root, "nowhere")));
    }
}
