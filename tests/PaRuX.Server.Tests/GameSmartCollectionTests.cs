using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSmartCollectionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-smart-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "game-smart-collections.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static GameTitleDto Title(string name, long hours = 0, int score = 0, string status = "", int playedDaysAgo = -1,
        bool favorite = false, bool hidden = false, params string[] tags) =>
        new("k:" + name, name, favorite, tags, hours * 3600, playedDaysAgo >= 0 ? Now.AddDays(-playedDaysAgo) : null, null, Array.Empty<GameInstallationDto>())
        { Score = score, Status = status, Hidden = hidden, AddedUtc = Now.AddDays(-40) };

    private static GameSmartCollection Rules(bool all, params (string f, string o, string v)[] rules) => new()
    {
        Name = "Test", MatchAll = all, Rules = rules.Select(r => new GameSmartRule { Field = r.f, Op = r.o, Value = r.v }).ToList(),
    };

    [Fact]
    public void All_rules_must_match_in_match_all_and_any_rule_in_match_any()
    {
        GameTitleDto both = Title("A", hours: 20, score: 9);
        GameTitleDto one = Title("B", hours: 20, score: 3);
        var all = Rules(true, ("playHours", "gte", "10"), ("score", "gte", "8"));
        var any = Rules(false, ("playHours", "gte", "10"), ("score", "gte", "8"));

        Assert.True(GameSmartCollectionRules.Matches(both, all, Now));
        Assert.False(GameSmartCollectionRules.Matches(one, all, Now));
        Assert.True(GameSmartCollectionRules.Matches(one, any, Now));
        Assert.False(GameSmartCollectionRules.Matches(Title("C", hours: 1, score: 1), any, Now));
    }

    [Fact]
    public void Days_since_played_needs_a_play_and_a_never_played_title_only_matches_neq()
    {
        var stale = Rules(true, ("daysSincePlayed", "gte", "90"));
        Assert.True(GameSmartCollectionRules.Matches(Title("Old", playedDaysAgo: 100), stale, Now));
        Assert.False(GameSmartCollectionRules.Matches(Title("Recent", playedDaysAgo: 5), stale, Now));
        Assert.False(GameSmartCollectionRules.Matches(Title("Never"), stale, Now));
        Assert.True(GameSmartCollectionRules.Matches(Title("Never"), Rules(true, ("daysSincePlayed", "neq", "3")), Now));
    }

    [Fact]
    public void Text_rules_compare_status_tags_and_name_ignoring_case()
    {
        GameTitleDto game = Title("Hollow Knight", status: "Playing", tags: new[] { "Metroidvania", "Indie" });
        Assert.True(GameSmartCollectionRules.Matches(game, Rules(true, ("status", "eq", "playing")), Now));
        Assert.True(GameSmartCollectionRules.Matches(game, Rules(true, ("tag", "eq", "indie")), Now));
        Assert.True(GameSmartCollectionRules.Matches(game, Rules(true, ("name", "contains", "KNIGHT")), Now));
        Assert.False(GameSmartCollectionRules.Matches(game, Rules(true, ("tag", "neq", "Indie")), Now));
        Assert.True(GameSmartCollectionRules.Matches(game, Rules(true, ("status", "neq", "Completed")), Now));
    }

    [Fact]
    public void A_hidden_title_never_matches_unless_a_rule_asks_for_hidden()
    {
        GameTitleDto hidden = Title("Secret", hours: 50, hidden: true);
        Assert.False(GameSmartCollectionRules.Matches(hidden, Rules(true, ("playHours", "gte", "1")), Now));
        Assert.True(GameSmartCollectionRules.Matches(hidden, Rules(true, ("hidden", "eq", "true")), Now));
    }

    [Theory]
    [InlineData("playHours", "contains", "1")]
    [InlineData("playHours", "gte", "abc")]
    [InlineData("playHours", "gte", "-1")]
    [InlineData("favorite", "gte", "true")]
    [InlineData("favorite", "eq", "maybe")]
    [InlineData("status", "gte", "x")]
    [InlineData("nope", "eq", "x")]
    public void Invalid_rules_are_refused(string field, string op, string value) =>
        Assert.NotNull(GameSmartCollectionRules.Validate(Rules(true, (field, op, value))));

    [Fact]
    public void A_collection_needs_a_name_and_between_one_and_eight_rules()
    {
        Assert.NotNull(GameSmartCollectionRules.Validate(new GameSmartCollection { Name = "", Rules = new() { new() { Field = "score", Op = "gte", Value = "1" } } }));
        Assert.NotNull(GameSmartCollectionRules.Validate(new GameSmartCollection { Name = "x" }));
        var nine = Rules(true, Enumerable.Repeat(("score", "gte", "1"), 9).ToArray());
        Assert.NotNull(GameSmartCollectionRules.Validate(nine));
        Assert.Null(GameSmartCollectionRules.Validate(Rules(true, ("score", "gte", "1"))));
    }

    [Fact]
    public void The_store_persists_replaces_by_id_and_refuses_duplicate_names()
    {
        var store = new GameSmartCollectionStore(FilePath);
        var (first, error) = store.Save(Rules(true, ("score", "gte", "8")));
        Assert.Null(error);
        Assert.False(string.IsNullOrWhiteSpace(first!.Id));

        var other = Rules(true, ("score", "gte", "5"));
        Assert.NotNull(store.Save(other).Error);   // same name "Test"

        GameSmartCollection edited = Rules(false, ("score", "gte", "9"));
        edited.Id = first.Id;
        Assert.Null(store.Save(edited).Error);

        var reopened = new GameSmartCollectionStore(FilePath);
        GameSmartCollection loaded = Assert.Single(reopened.List());
        Assert.False(loaded.MatchAll);
        Assert.Equal("9", loaded.Rules[0].Value);
        Assert.True(reopened.Delete(loaded.Id));
        Assert.False(reopened.Delete(loaded.Id));
    }

    [Fact]
    public void The_store_bounds_the_count_and_survives_a_damaged_file()
    {
        var store = new GameSmartCollectionStore(FilePath);
        for (int i = 0; i < GameSmartCollectionStore.MaximumCollections; i++)
        {
            GameSmartCollection c = Rules(true, ("score", "gte", "1"));
            c.Name = "c" + i;
            Assert.Null(store.Save(c).Error);
        }
        GameSmartCollection extra = Rules(true, ("score", "gte", "1"));
        extra.Name = "extra";
        Assert.NotNull(store.Save(extra).Error);

        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new GameSmartCollectionStore(FilePath).List());
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }
}
