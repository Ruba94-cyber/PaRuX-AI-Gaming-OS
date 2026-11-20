using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic titles only (invented names, no paths).</summary>
public sealed class GameLibraryToolsTests
{
    private static GameInstallationDto Install(string source = "manual", string platform = "PC") =>
        new("manual:" + Guid.NewGuid().ToString("N"), source, "x", "game", "Game")
        { Platform = platform, InstallState = GameInstallStates.Installed };

    private static GameTitleDto Title(string name, long seconds = 0, string status = "", int score = 0, bool favorite = false,
        bool hidden = false, DateTime? last = null, params GameInstallationDto[] installs) =>
        new(name.ToLowerInvariant().Replace(" ", ""), name, favorite, Array.Empty<string>(), seconds, last, null,
            installs.Length == 0 ? new[] { Install() } : installs)
        { Status = status, Score = score, Hidden = hidden };

    [Fact]
    public void Insights_count_play_time_status_rating_and_leave_hidden_titles_out()
    {
        var titles = new[]
        {
            Title("Alpha", 7200, GameCompletionStatuses.Completed, 8, favorite: true, last: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            Title("Beta", 3600, GameCompletionStatuses.Playing, 6, last: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            Title("Gamma"),
            Title("Secret", 99999, hidden: true),
        };
        GameLibraryInsights i = GameLibraryInsightsPolicy.Compute(titles);
        Assert.Equal(3, i.Titles);
        Assert.Equal(2, i.Played);
        Assert.Equal(1, i.NeverPlayed);
        Assert.Equal(1, i.Favorites);
        Assert.Equal(10800, i.TotalPlaySeconds);
        Assert.Equal(2, i.RatedTitles);
        Assert.Equal(7.0, i.AverageScore);
        Assert.Equal("Beta", i.LastPlayedName);
        Assert.Equal(new[] { "Alpha", "Beta" }, i.MostPlayed.Select(m => m.Name));
        Assert.Equal(1, i.ByStatus[GameCompletionStatuses.Completed]);
        Assert.Equal(1, i.ByStatus[string.Empty]);
    }

    [Fact]
    public void Insights_of_an_empty_library_are_zero_not_an_error()
    {
        GameLibraryInsights i = GameLibraryInsightsPolicy.Compute(null);
        Assert.Equal(0, i.Titles);
        Assert.Equal(0, i.AverageScore);
        Assert.Empty(i.MostPlayed);
    }

    [Fact]
    public void Picker_never_returns_hidden_or_unstartable_titles_and_is_null_when_nothing_qualifies()
    {
        var unstartable = Title("Store only", installs: new GameInstallationDto("s:1", "steam", "1", "game", "Store only")
            { InstallState = GameInstallStates.MetadataOnly });
        var rng = new Random(1);
        Assert.Null(GameRandomPicker.Pick(new[] { Title("Hidden", hidden: true), unstartable }, rng));
        for (int n = 0; n < 50; n++)
            Assert.Equal("Visible", GameRandomPicker.Pick(new[] { Title("Hidden", hidden: true), unstartable, Title("Visible") }, rng)!.Name);
    }

    [Fact]
    public void Picker_favours_backlog_and_unplayed_over_finished_titles()
    {
        var titles = new[]
        {
            Title("Finished", 5000, GameCompletionStatuses.Completed),
            Title("Backlog", 0, GameCompletionStatuses.Backlog),
        };
        var rng = new Random(7);
        int backlog = Enumerable.Range(0, 2000).Count(_ => GameRandomPicker.Pick(titles, rng)!.Name == "Backlog");
        Assert.InRange(backlog, 1500, 1900);
    }

    [Fact]
    public void Csv_export_quotes_cells_and_neutralizes_spreadsheet_formulas()
    {
        string csv = GameLibraryExport.ToCsv(new[] { Title("=cmd|calc"), Title("Comma, \"Quote\"", 3600) });
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Name,Platform,Source", lines[0]);
        Assert.Contains("\"Comma, \"\"Quote\"\"\"", csv);
        Assert.Contains("'=cmd|calc", csv);
        Assert.DoesNotContain(",=cmd", csv);
        Assert.Contains(",1,", csv); // 3600 s = 1 play hour
    }

    [Fact]
    public void Notes_are_normalized_limited_and_refuse_control_characters()
    {
        Assert.Equal("a\nb", GameTitleNotes.Clean("  a\r\nb \r"));
        Assert.Equal("", GameTitleNotes.Clean(null));
        Assert.Null(GameTitleNotes.Clean(new string('x', GameTitleNotes.MaxLength + 1)));
        Assert.NotNull(GameTitleNotes.Clean(new string('x', GameTitleNotes.MaxLength)));
        Assert.Null(GameTitleNotes.Clean("bell\a"));
        Assert.Equal("tab\tok", GameTitleNotes.Clean("tab\tok"));
    }

    [Fact]
    public void Notes_persist_per_title_and_an_invalid_note_changes_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-notes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PaRuX.Server.Orchestration.Gaming.GameTitleStore(path);
            Assert.True(store.SetNotes("alpha", "second boss needs fire"));
            Assert.False(store.SetNotes("alpha", new string('x', GameTitleNotes.MaxLength + 1)));
            var reopened = new PaRuX.Server.Orchestration.Gaming.GameTitleStore(path);
            Assert.Equal("second boss needs fire", reopened.Snapshot()["alpha"].Notes);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void An_export_reads_back_with_every_personal_field_and_bad_entries_are_counted_not_trusted()
    {
        string json = GameLibraryExport.ToJson(new[]
        {
            Title("Alpha", 7200, GameCompletionStatuses.Completed, 8, favorite: true, last: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
        }, new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc));
        var entries = GameLibraryRestoreParser.Parse(json, out int invalid)!;
        Assert.Equal(0, invalid);
        GameLibraryRestoreEntry alpha = Assert.Single(entries);
        Assert.Equal("Alpha", alpha.Name);
        Assert.True(alpha.Favorite);
        Assert.Equal(GameCompletionStatuses.Completed, alpha.Status);
        Assert.Equal(8, alpha.Score);
        Assert.Equal(7200, alpha.PlaySeconds);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), alpha.LastPlayedUtc);

        const string messy = "{\"format\":\"parux-game-library\",\"version\":1,\"titles\":[{\"name\":\"\"},{\"name\":\"Ok\",\"status\":\"bogus\",\"score\":99,\"playSeconds\":-5},5]}";
        var tolerant = GameLibraryRestoreParser.Parse(messy, out int bad)!;
        Assert.Equal(2, bad);
        Assert.Equal("", tolerant[0].Status);
        Assert.Equal(0, tolerant[0].Score);
        Assert.Equal(0, tolerant[0].PlaySeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"version\":1,\"titles\":[]}")]
    [InlineData("{\"format\":\"parux-game-library\",\"version\":2,\"titles\":[]}")]
    public void Anything_that_is_not_a_PaRuX_export_is_refused(string? json) =>
        Assert.Null(GameLibraryRestoreParser.Parse(json, out _));

    [Fact]
    public void Restore_merge_fills_gaps_unions_lists_grows_play_time_and_never_overwrites()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-restore-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PaRuX.Server.Orchestration.Gaming.GameTitleStore(path);
            store.SetScore("alpha", 5);
            store.SetTags("alpha", new[] { "mine" });
            store.SetNotes("alpha", "my own note");
            var entry = new GameLibraryRestoreEntry("Alpha", true, GameCompletionStatuses.Completed, 9, 3600,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), new[] { "imported", "MINE" }, new[] { "Racing" }, "their note");
            Assert.Equal(1, store.RestoreMerge(new[] { ("alpha", entry) }));
            var state = new PaRuX.Server.Orchestration.Gaming.GameTitleStore(path).Snapshot()["alpha"];
            Assert.True(state.Favorite);
            Assert.Equal(5, state.Score);                              // the owner's own rating wins
            Assert.Equal("my own note", state.Notes);                  // notes only fill an empty field
            Assert.Equal(GameCompletionStatuses.Completed, state.Status); // an empty status is filled
            Assert.Equal(new[] { "mine", "imported" }, state.Tags);      // united, case-insensitive
            Assert.Equal(new[] { "Racing" }, state.Collections);
            Assert.Equal(3600, state.PlaySeconds);
            // The same restore again changes nothing, and smaller play time never shrinks the total.
            Assert.Equal(0, store.RestoreMerge(new[] { ("alpha", entry with { PlaySeconds = 60 }) }));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
