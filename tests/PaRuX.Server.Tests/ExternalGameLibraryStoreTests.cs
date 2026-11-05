using System;
using System.IO;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic third-party JSON only, isolated local metadata file; no games launched.</summary>
public sealed class ExternalGameLibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-external-import-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "imported.json");
    public ExternalGameLibraryStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Persists_only_titles_and_ids_without_importing_launch_paths_or_passwords()
    {
        const string secret = "DONT-PERSIST-SYNTHETIC-LAUNCH-SECRET";
        string json = "{\"games\":[{\"name\":\"Synthetic RPG\",\"slug\":\"rpg-01\"," +
            "\"path\":\"" + secret + "\",\"launchArguments\":\"" + secret + "\"}]}";
        var games = ExternalGameLibraryExportParser.Parse("lutris", json);
        var first = new ExternalGameLibraryStore(StorePath);
        var outcome = first.Import(games);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(0, outcome.AlreadyPresent);
        var imported = Assert.Single(new ExternalGameLibraryStore(StorePath).List());
        Assert.Equal("lutris", imported.Provider);
        Assert.Equal("rpg-01", imported.ExternalId);
        Assert.Equal("Synthetic RPG", imported.Title);
        Assert.DoesNotContain(secret, File.ReadAllText(StorePath));
        Assert.DoesNotContain("launchArguments", File.ReadAllText(StorePath));
    }

    [Fact]
    public void Reimport_does_not_overwrite_name_and_selected_remove_preserves_other_titles()
    {
        var store = new ExternalGameLibraryStore(StorePath);
        store.Import(new[] {
            new ExternalLibraryGame("playnite", "1", "Original"),
            new ExternalLibraryGame("heroic", "2", "Keep")
        });
        var duplicate = store.Import(new[] { new ExternalLibraryGame("playnite", "1", "Renamed") });
        Assert.Equal(0, duplicate.Added);
        Assert.Equal(1, duplicate.AlreadyPresent);
        Assert.Equal("Original", store.List().Single(x => x.ExternalId == "1").Title);
        Assert.True(store.Remove("playnite", "1"));
        Assert.False(store.Remove("playnite", "1"));
        Assert.Equal("Keep", Assert.Single(store.List()).Title);
    }

    [Fact]
    public void Validation_failure_keeps_original_saved_metadata_intact()
    {
        var store = new ExternalGameLibraryStore(StorePath);
        store.Import(new[] { new ExternalLibraryGame("lutris", "safe", "Existing") });
        byte[] before = File.ReadAllBytes(StorePath);
        Assert.Throws<ArgumentException>(() => store.Import(new[] {
            new ExternalLibraryGame("untrusted", "id", "Bad")
        }));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Throws<ArgumentException>(() => store.Remove("untrusted", "id"));
    }

    [Fact]
    public void Imported_metadata_is_never_eligible_for_quick_play()
    {
        var discovered = new DiscoveredGameDto("lutris", "synthetic", "Synthetic RPG",
            "", GameInstallStates.MetadataOnly);
        var title = Assert.Single(GameTitleCatalog.Build(new[] { discovered },
            Array.Empty<ManualGameDto>(), new System.Collections.Generic.Dictionary<string, GameTitleState>()));
        Assert.False(GameTitleLaunchPolicy.CanStart(Assert.Single(title.Installations)));
        Assert.Equal("lutris", title.Installations[0].Source);
    }

    [Theory]
    [InlineData(GameInstallationSources.Playnite)]
    [InlineData(GameInstallationSources.Heroic)]
    [InlineData(GameInstallationSources.Lutris)]
    public void External_metadata_source_cannot_be_launched_even_if_marked_installed_by_stale_payload(string source)
    {
        var candidate = new GameInstallationDto(source + ":synthetic", source,
            "synthetic", ManualEntryKinds.Game, "Synthetic")
        { InstallState = GameInstallStates.Installed };
        Assert.False(GameTitleLaunchPolicy.CanStart(candidate));
        Assert.False(GameTitleLaunchPolicy.CanStart(candidate with { InstallState = GameInstallStates.Unknown }));
    }

    [Fact]
    public void Corrupt_persisted_entries_fail_closed_instead_of_dropping_them_silently()
    {
        File.WriteAllText(StorePath, "{\"Version\":1,\"Entries\":[{\"Provider\":\"x\",\"ExternalId\":\"1\",\"Title\":\"Bad\"}]}");
        Assert.Throws<InvalidDataException>(() => new ExternalGameLibraryStore(StorePath).List());
        Assert.Throws<InvalidDataException>(() => new ExternalGameLibraryStore(StorePath).Import(
            new[] { new ExternalLibraryGame("heroic", "safe", "Good") }));
    }
}
