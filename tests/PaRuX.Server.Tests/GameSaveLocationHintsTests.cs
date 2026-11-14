using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>GAM-005: read-only save-folder hints over a synthetic profile. Nothing here touches the real user profile.</summary>
public sealed class GameSaveLocationHintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-save-hints-" + Guid.NewGuid().ToString("N"));
    private readonly SaveHintRoots _roots;
    private bool _gaming;

    public GameSaveLocationHintsTests()
    {
        string P(string name) => Path.Combine(_root, name);
        foreach (string name in new[] { "Documents", "Saved Games", "Local", "Roaming", "LocalLow", "Steam" })
            Directory.CreateDirectory(P(name));
        _roots = new SaveHintRoots(P("Documents"), P("Saved Games"), P("Local"), P("Roaming"), P("LocalLow"), new[] { P("Steam") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Save(string relativeToRoot, int files = 2)
    {
        string folder = Path.Combine(_root, relativeToRoot);
        Directory.CreateDirectory(folder);
        for (int i = 0; i < files; i++) File.WriteAllText(Path.Combine(folder, "slot" + i + ".sav"), "synthetic " + i);
        return folder;
    }

    private GameSaveBackupService Service() =>
        new(Path.Combine(_root, "Managed"), () => _gaming, () => CancellationToken.None) { HintRootsForTests = _roots };

    [Fact]
    public void Finds_existing_save_folders_by_game_name_in_the_usual_user_locations()
    {
        string docs = Save(@"Documents\My Games\Synthetic Quest");
        string saved = Save(@"Saved Games\Synthetic Quest");
        string roaming = Save(@"Roaming\Synthetic Quest", files: 3);
        Save(@"Local\Another Game");

        IReadOnlyList<GameSaveHintDto> hints = GameSaveLocationHints.Find("Synthetic Quest", null, _roots);

        Assert.Equal(new[] { docs, saved, roaming }.Order(StringComparer.OrdinalIgnoreCase),
            hints.Select(h => h.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3, hints.Single(h => h.Source == "appdata-roaming").Files);
        Assert.All(hints, h => Assert.NotNull(h.NewestWriteUtc));
    }

    [Fact]
    public void Missing_or_empty_folders_produce_no_hint_and_never_get_created()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Documents", "Empty Game"));
        Assert.Empty(GameSaveLocationHints.Find("Empty Game", null, _roots));
        Assert.Empty(GameSaveLocationHints.Find("Never Installed", null, _roots));
        Assert.False(Directory.Exists(Path.Combine(_root, "Documents", "Never Installed")));
    }

    [Fact]
    public void A_known_title_with_a_different_folder_name_is_found_case_insensitively()
    {
        string stardew = Save(@"Roaming\StardewValley\Saves");
        IReadOnlyList<GameSaveHintDto> hints = GameSaveLocationHints.Find("  STARDEW   Valley ", null, _roots);
        GameSaveHintDto hint = Assert.Single(hints);
        Assert.Equal(stardew, hint.Path, ignoreCase: true);
        Assert.Equal("known-title", hint.Source);
    }

    [Fact]
    public void Steam_cloud_folder_is_found_for_a_numeric_app_id_only()
    {
        string cloud = Save(@"Steam\userdata\12345\480\remote");
        IReadOnlyList<GameSaveHintDto> hints = GameSaveLocationHints.Find("Unrelated Name", "480", _roots);
        Assert.Equal(cloud, Assert.Single(hints).Path, ignoreCase: true);
        Assert.Equal("steam-cloud", hints[0].Source);
        Assert.Empty(GameSaveLocationHints.Find("Unrelated Name", @"..\480", _roots));
        Assert.Empty(GameSaveLocationHints.Find("Unrelated Name", "48x", _roots));
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    [InlineData("a/b")]
    [InlineData("con:")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void Names_that_could_escape_a_folder_are_rejected_or_neutralised(string name)
    {
        string? clean = GameSaveLocationHints.CleanName(name);
        if (clean is not null)
        {
            Assert.DoesNotContain('\\', clean);
            Assert.DoesNotContain('/', clean);
            Assert.NotEqual("..", clean);
        }
        // Whatever the name, every hint stays inside the synthetic roots.
        foreach (GameSaveHintDto hint in GameSaveLocationHints.Find(name, null, _roots))
            Assert.StartsWith(_root, hint.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folder_that_is_a_link_is_never_suggested()
    {
        string target = Save(@"Elsewhere\Real Saves");
        string link = Path.Combine(_root, "Documents", "Linked Game");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a symbolic link needs developer mode or elevation; a junction does not.
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false });
            junction!.WaitForExit(10_000);
        }
        if (!Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) return; // cannot build a link here
        Assert.Empty(GameSaveLocationHints.Find("Linked Game", null, _roots));
    }

    [Fact]
    public void The_file_count_is_capped_and_labelled()
    {
        string big = Save(@"Documents\Big Game", files: GameSaveLocationHints.MaxCountedFiles + 25);
        GameSaveHintDto hint = Assert.Single(GameSaveLocationHints.Find("Big Game", null, _roots));
        Assert.Equal(GameSaveLocationHints.MaxCountedFiles, hint.Files);
        Assert.True(hint.FilesCapped);
        Assert.Equal(GameSaveLocationHints.MaxCountedFiles + 25, Directory.GetFiles(big).Length); // nothing was removed
    }

    [Fact]
    public void Hints_read_nothing_but_directory_metadata_and_change_nothing()
    {
        string folder = Save(@"Documents\Quiet Game");
        string[] before = Directory.GetFiles(folder).Select(f => f + "|" + File.GetLastWriteTimeUtc(f).Ticks).Order().ToArray();
        _ = GameSaveLocationHints.Find("Quiet Game", null, _roots);
        string[] after = Directory.GetFiles(folder).Select(f => f + "|" + File.GetLastWriteTimeUtc(f).Ticks).Order().ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_service_refuses_hints_during_Gaming_and_validates_the_request()
    {
        Save(@"Documents\Synthetic Quest");
        GameSaveBackupService service = Service();
        Assert.Single(service.Hints(new GameSaveHintRequest("Synthetic Quest")));

        Assert.Throws<ArgumentException>(() => service.Hints(new GameSaveHintRequest("   ")));
        Assert.Throws<ArgumentException>(() => service.Hints(new GameSaveHintRequest("Synthetic Quest", "12;34")));

        _gaming = true;
        Assert.Throws<InvalidOperationException>(() => service.Hints(new GameSaveHintRequest("Synthetic Quest")));
    }

    [Fact]
    public async Task A_suggested_folder_can_be_backed_up_through_the_existing_service()
    {
        string folder = Save(@"Documents\My Games\Backup Quest", files: 3);
        GameSaveBackupService service = Service();
        GameSaveHintDto hint = Assert.Single(service.Hints(new GameSaveHintRequest("Backup Quest")));
        GameSaveBackupDto backup = await service.CreateAsync(new GameSaveBackupRequest(hint.Path, "Backup Quest"), CancellationToken.None);
        Assert.Equal(3, backup.Files);
        Assert.True((await service.VerifyAsync(backup.Id, CancellationToken.None)).Valid);
        Assert.Equal(3, Directory.GetFiles(folder).Length);
    }
}
