using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DatasetQualityAnalyzerTests
{
    private static Dictionary<string, string?> Chat(string json) => new() { ["messages"] = json };
    private static string Msgs(params (string Role, string Content)[] m) =>
        "[" + string.Join(",", m.Select(x => $"{{\"role\":\"{x.Role}\",\"content\":\"{x.Content}\"}}")) + "]";

    private static DatasetQualityReport One(string json) => DatasetQualityAnalyzer.Analyze(new[] { Chat(json) });

    [Fact]
    public void A_clean_conversation_has_no_issues()
    {
        var report = One(Msgs(("system", "be brief"), ("user", "hi"), ("assistant", "hello"), ("user", "bye"), ("assistant", "ciao")));
        Assert.Equal(1, report.ConversationRows);
        Assert.Empty(report.IssueCounts);
    }

    [Theory]
    [InlineData("user,user,assistant", "repeated-role")]
    [InlineData("assistant,user", "starts-with-assistant")]
    [InlineData("user,assistant,user", "ends-without-assistant")]
    [InlineData("user", "no-assistant-reply")]
    [InlineData("user,wizard,assistant", "unknown-role")]
    public void Template_problems_are_counted_by_kind(string roles, string expected)
    {
        var turns = roles.Split(',').Select(r => (r, "text " + r)).ToArray();
        Assert.Contains(expected, One(Msgs(turns)).IssueCounts.Keys);
    }

    [Fact]
    public void Empty_content_empty_conversation_and_broken_json_are_reported()
    {
        Assert.Contains("empty-content", One(Msgs(("user", " "), ("assistant", "ok"))).IssueCounts.Keys);
        Assert.Contains("empty-conversation", One("[]").IssueCounts.Keys);
        Assert.Contains("unparseable-conversation", One("{not json").IssueCounts.Keys);
        Assert.Contains("unparseable-conversation", One("{\"role\":\"user\"}").IssueCounts.Keys);
    }

    [Fact]
    public void ShareGPT_style_turns_are_understood()
    {
        var report = One("[{\"from\":\"human\",\"value\":\"q\"},{\"from\":\"gpt\",\"value\":\"a\"}]");
        Assert.Empty(report.IssueCounts);
    }

    [Fact]
    public void Issue_samples_are_bounded_and_name_the_row()
    {
        var rows = Enumerable.Range(0, 100).Select(_ => Chat(Msgs(("user", "only a question")))).ToList();
        var report = DatasetQualityAnalyzer.Analyze(rows);
        Assert.Equal(100, report.IssueCounts["no-assistant-reply"]);
        Assert.Equal(DatasetQualityAnalyzer.MaxIssueSamples, report.IssueSamples.Count);
        Assert.StartsWith("row 1:", report.IssueSamples[0]);
    }

    [Fact]
    public void Length_distribution_uses_estimated_tokens_and_buckets()
    {
        var rows = new List<Dictionary<string, string?>>
        {
            new() { ["text"] = new string('a', 400) },      // ~100 tokens
            new() { ["text"] = new string('b', 2000) },     // ~500
            new() { ["text"] = new string('c', 100_000) },  // ~25000 -> overflow
        };
        var report = DatasetQualityAnalyzer.Analyze(rows);
        Assert.Equal(500, report.EstimatedTokensP50);
        Assert.Equal(25_000, report.EstimatedTokensMax);
        Assert.Equal(1, report.OverflowRows);
        Assert.Equal(1, report.EstimatedTokenBuckets.First(b => b.UpToEstimatedTokens == 128).Rows);
        Assert.Equal(1, report.EstimatedTokenBuckets.First(b => b.UpToEstimatedTokens == 512).Rows);
    }

    [Fact]
    public void Empty_input_gives_a_zeroed_report()
    {
        var report = DatasetQualityAnalyzer.Analyze(Array.Empty<Dictionary<string, string?>>());
        Assert.Equal(0, report.EstimatedTokensMax);
        Assert.Equal(0, report.NearDuplicateRows);
        Assert.Equal(DatasetQualityAnalyzer.TokenBuckets.Count, report.EstimatedTokenBuckets.Count);
    }

    [Fact]
    public void Near_duplicates_are_found_but_unrelated_and_identical_rows_are_not_flagged_as_near()
    {
        string baseText = "The quick brown fox jumps over the lazy dog while the sun sets slowly behind the distant mountains tonight";
        var rows = new List<Dictionary<string, string?>>
        {
            new() { ["text"] = baseText },
            new() { ["text"] = baseText.Replace("tonight", "again") },
            new() { ["text"] = "Completely different content about database indexing strategies and query planning in large systems" },
            new() { ["text"] = baseText },
        };
        var report = DatasetQualityAnalyzer.Analyze(rows);
        Assert.Equal(1, report.NearDuplicateRows);
        Assert.Equal(new[] { 2 }, report.NearDuplicateSamples);
    }

    [Fact]
    public void MinHash_is_stable_and_similarity_tracks_overlap()
    {
        string a = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega";
        var sa = DatasetQualityAnalyzer.MinHashSignature(a);
        Assert.Equal(sa, DatasetQualityAnalyzer.MinHashSignature(a));
        Assert.Equal(1.0, DatasetQualityAnalyzer.Similarity(sa, sa));
        double close = DatasetQualityAnalyzer.Similarity(sa, DatasetQualityAnalyzer.MinHashSignature(a + " extra"));
        double far = DatasetQualityAnalyzer.Similarity(sa, DatasetQualityAnalyzer.MinHashSignature("one two three four five six seven eight nine ten eleven twelve thirteen fourteen"));
        Assert.True(close > 0.75, "close=" + close);
        Assert.True(far < 0.2, "far=" + far);
    }

    [Fact]
    public void Many_unrelated_rows_do_not_produce_false_near_duplicates()
    {
        var rows = Enumerable.Range(0, 300).Select(i => new Dictionary<string, string?>
        {
            ["text"] = string.Join(" ", Enumerable.Range(0, 30).Select(j => "w" + ((i * 7919 + j * 104729) % 100003)))
        }).ToList();
        Assert.Equal(0, DatasetQualityAnalyzer.Analyze(rows).NearDuplicateRows);
    }

    [Fact]
    public async Task Inspect_reports_quality_for_a_jsonl_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-dq-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            string one = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["messages"] = Msgs(("user", "hi"))
            });
            string two = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["messages"] = Msgs(("user", "hi"), ("assistant", "yo"))
            });
            await File.WriteAllLinesAsync(path, new[] { one, two });
            var report = await new DatasetCurationService().InspectAsync(path, CancellationToken.None);
            Assert.Null(report.Error);
            Assert.NotNull(report.Quality);
            Assert.Equal(2, report.Quality!.ConversationRows);
            Assert.Equal(1, report.Quality.IssueCounts["no-assistant-reply"]);
        }
        finally { File.Delete(path); }
    }
}
