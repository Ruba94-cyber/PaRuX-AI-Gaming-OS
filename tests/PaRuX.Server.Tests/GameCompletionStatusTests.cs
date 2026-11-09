using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameCompletionStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-status-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "titles.json");
    public GameCompletionStatusTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Status_and_score_persist_across_instances_and_normalize_case()
    {
        Assert.True(new GameTitleStore(File_).SetStatus("hades", " Completed "));
        Assert.True(new GameTitleStore(File_).SetScore("hades", 9));
        GameTitleState state = new GameTitleStore(File_).Snapshot()["hades"];
        Assert.Equal(GameCompletionStatuses.Completed, state.Status);
        Assert.Equal(9, state.Score);
    }

    [Fact]
    public void Unknown_status_and_out_of_range_score_are_rejected_without_changing_state()
    {
        var store = new GameTitleStore(File_);
        Assert.True(store.SetStatus("hades", GameCompletionStatuses.Playing));
        Assert.True(store.SetScore("hades", 4));
        Assert.False(store.SetStatus("hades", "finished-ish"));
        Assert.False(store.SetScore("hades", 11));
        Assert.False(store.SetScore("hades", -1));
        GameTitleState state = store.Snapshot()["hades"];
        Assert.Equal(GameCompletionStatuses.Playing, state.Status);
        Assert.Equal(4, state.Score);
    }

    [Fact]
    public void Empty_status_clears_and_old_files_load_as_unset()
    {
        File.WriteAllText(File_, "{\"Version\":1,\"Titles\":{\"hades\":{\"Favorite\":true,\"Tags\":[],\"Collections\":[],\"PlaySeconds\":5}}}");
        var store = new GameTitleStore(File_);
        Assert.Equal(string.Empty, store.Snapshot()["hades"].Status);
        Assert.Equal(0, store.Snapshot()["hades"].Score);
        Assert.True(store.SetStatus("hades", GameCompletionStatuses.Dropped));
        Assert.True(store.SetStatus("hades", ""));
        Assert.Equal(string.Empty, store.Snapshot()["hades"].Status);
    }

    [Fact]
    public void Score_sort_orders_highest_first_then_by_name_and_is_an_allowed_sort()
    {
        Assert.True(GameTitleSorts.IsValid("score"));
        GameTitleDto Make(string name, int score) => new(GameTitleCatalog.KeyFor(name, name), name, false,
            Array.Empty<string>(), 0, null, null, Array.Empty<GameInstallationDto>()) { Score = score };
        var sorted = GameTitleCatalog.Sort(new[] { Make("Bravo", 5), Make("Alpha", 5), Make("Zed", 9), Make("None", 0) }, GameTitleSorts.Score);
        Assert.Equal(new[] { "Zed", "Alpha", "Bravo", "None" }, sorted.Select(t => t.Name));
    }

    [Fact]
    public void Allowlist_accepts_only_known_labels()
    {
        Assert.True(GameCompletionStatuses.IsValid(null));
        Assert.True(GameCompletionStatuses.IsValid(""));
        foreach (string s in GameCompletionStatuses.All) Assert.True(GameCompletionStatuses.IsValid(s));
        Assert.False(GameCompletionStatuses.IsValid("<script>"));
    }
}
