using FlaUI.Core.AutomationElements;
using LiteDB;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Synthetic Playnite library only (invented titles, empty stand-in files): preview, explicit import, library result.</summary>
[Collection(UiTestCollection.Name)]
public sealed class PlayniteImportUiTests
{
    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static BsonValue G(Guid g) => new(g);

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Playnite_library_is_previewed_then_imported_only_after_the_import_click()
    {
        const string name = nameof(Playnite_library_is_previewed_then_imported_only_after_the_import_click);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(0);
        string library = Path.Combine(root, "playnite-copy", "library");
        Directory.CreateDirectory(library);
        string exe = Path.Combine(root, "games", "Alpha", "alpha.exe");
        string emu = Path.Combine(root, "emu", "eden.exe");
        string rom = Path.Combine(root, "roms", "Beta Quest.nsp");
        string rom2 = Path.Combine(root, "roms", "Beta Quest Two.nsp");
        foreach (string f in new[] { exe, emu, rom, rom2 }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, "fixture: stand-in file"); }
        Guid emulatorId = Guid.NewGuid();
        using (var db = new LiteDatabase(Path.Combine(library, "emulators.db")))
            db.GetCollection("Emulator").Insert(new BsonDocument
            {
                ["_id"] = G(emulatorId), ["Name"] = "Switch", ["InstallDir"] = Path.GetDirectoryName(emu),
                ["CustomProfiles"] = new BsonArray { new BsonDocument { ["_id"] = "#c1", ["Executable"] = emu } },
            });
        using (var db = new LiteDatabase(Path.Combine(library, "games.db")))
            db.GetCollection("Game").InsertBulk(new[]
            {
                new BsonDocument
                {
                    ["_id"] = G(Guid.NewGuid()), ["Name"] = "Alpha Racer", ["InstallDirectory"] = Path.GetDirectoryName(exe), ["Favorite"] = true,
                    ["Playtime"] = 3600L, ["PluginId"] = G(Guid.Empty),
                    ["GameActions"] = new BsonArray { new BsonDocument { ["Type"] = "File", ["Path"] = "{InstallDir}\\alpha.exe", ["IsPlayAction"] = true } },
                },
                new BsonDocument
                {
                    ["_id"] = G(Guid.NewGuid()), ["Name"] = "Beta Quest", ["InstallDirectory"] = Path.GetDirectoryName(rom),
                    ["GameActions"] = new BsonArray { new BsonDocument { ["Type"] = "Emulator", ["EmulatorId"] = G(emulatorId), ["EmulatorProfileId"] = "#c1", ["IsPlayAction"] = true } },
                    ["Roms"] = new BsonArray { new BsonDocument { ["Path"] = "{InstallDir}\\Beta Quest.nsp" } },
                },
                new BsonDocument
                {
                    ["_id"] = G(Guid.NewGuid()), ["Name"] = "Beta Quest Two", ["InstallDirectory"] = Path.GetDirectoryName(rom2),
                    ["GameActions"] = new BsonArray { new BsonDocument { ["Type"] = "Emulator", ["EmulatorId"] = G(emulatorId), ["EmulatorProfileId"] = "#c1", ["IsPlayAction"] = true } },
                    ["Roms"] = new BsonArray { new BsonDocument { ["Path"] = "{InstallDir}\\Beta Quest Two.nsp" } },
                },
                new BsonDocument { ["_id"] = G(Guid.NewGuid()), ["Name"] = "Gamma Store", ["PluginId"] = G(Guid.NewGuid()) },
            });
        using var app = new UiTestApplication(name, restartDataRoot: root,
            extraEnvironment: new Dictionary<string, string> { ["PARUX_PLAYNITE_LIBRARY"] = library });
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            GamingSectionsPage.SelectSection(app.MainWindow, "Add");
            Press(app, "GameImport_Playnite_Look_Button");
            UiWait.For(() => Summary(app).Contains("Found 4 games: 3 can be imported", StringComparison.Ordinal), "preview counts");
            string libraryFile = Path.Combine(root, "server", "manual-games.json");
            Assert.False(File.Exists(libraryFile) && SharedFile.ReadAllText(libraryFile).Contains("Alpha Racer"), "preview must not add games");
            Press(app, "GameImport_Playnite_Import_Button");
            UiWait.For(() => Summary(app).StartsWith("Imported 3 games", StringComparison.Ordinal), "import result");
            Assert.Contains("Alpha Racer", SharedFile.ReadAllText(libraryFile));
            Assert.Contains("\"EmulatorId\": \"eden\"", SharedFile.ReadAllText(libraryFile));
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            UiWait.ById(app.MainWindow, "GameTile_alpharacer");
            UiWait.ById(app.MainWindow, "GameTile_betaquest");
            UiWait.ById(app.MainWindow, "GameTile_betaquesttwo");
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameTile_gammastore")));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    private static string Summary(UiTestApplication app) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Playnite_Summary"))?.Name ?? "";
}
