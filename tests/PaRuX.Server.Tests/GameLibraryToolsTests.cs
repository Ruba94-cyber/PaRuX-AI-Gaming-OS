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
}
