using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Cross-encoder rerank inside KnowledgeIndex.Search, with a fake provider (no model, no network). Synthetic folders only.</summary>
public sealed class KnowledgeRerankTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lks-rr-" + Guid.NewGuid().ToString("N"));
    private readonly string _docs;
    private readonly FakeReranker _reranker = new();
    private readonly KnowledgeIndex _index;

    public KnowledgeRerankTests()
    {
        _docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(_docs);
        _index = new KnowledgeIndex(Path.Combine(_root, "knowledge.db"), null, null, _reranker);
    }

    public void Dispose() { _index.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeReranker : IRetrievalRerankerProvider
    {
        public RetrievalRerankerIdentity Identity { get; } = new("test", "fake", "1", "none");
        public bool IsAvailable { get; set; } = true;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        /// <summary>The fake model finds the chunk that mentions "golden" the most relevant, whatever the lexical rank.</summary>
        public Func<string, float> Score { get; set; } = d => d.Contains("golden", StringComparison.OrdinalIgnoreCase) ? 10f : 0f;

        public IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("boom");
            return documents.Select(d => (float?)Score(d)).ToList();
        }
    }

    private async Task Seed()
    {
        // Lexically strong distractors; the right chunk mentions the query words only once.
        for (int i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(_docs, "d" + i + ".txt"), string.Concat(Enumerable.Repeat("calibration procedure calibration procedure ", 6)) + " filler" + i);
        File.WriteAllText(Path.Combine(_docs, "right.txt"), "The golden calibration procedure is described here with many other unrelated words about gaskets and valves.");
        await _index.IndexFolderAsync("s1", _docs, null, null, default);
    }

    [Fact]
    public async Task Reranker_changes_the_fused_order_and_lifts_the_relevant_chunk()
    {
        await Seed();
        _reranker.IsAvailable = false;
        var baseline = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4);
        _reranker.IsAvailable = true;
        var reranked = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4);

        Assert.NotEqual("right.txt", baseline[0].FileName);
        Assert.Equal("right.txt", reranked[0].FileName);
        Assert.Equal(4, reranked.Count);
        Assert.Equal(1, _reranker.Calls);
    }

    [Fact]
    public async Task Auto_mode_without_an_embedder_also_reranks()
    {
        await Seed();
        var reranked = _index.Search("calibration procedure", KnowledgeSearchMode.Auto, 3);
        Assert.Equal("right.txt", reranked[0].FileName);
    }

    [Fact]
    public async Task Throwing_provider_falls_back_to_the_fused_order()
    {
        await Seed();
        _reranker.IsAvailable = false;
        var baseline = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4).Select(h => h.ChunkId).ToList();
        _reranker.IsAvailable = true;
        _reranker.Throw = true;

        var hits = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4);

        Assert.True(_reranker.Calls > 0);
        Assert.Equal(baseline, hits.Select(h => h.ChunkId).ToList());
    }

    [Fact]
    public async Task Cancelled_token_and_gaming_marker_fall_back_without_scoring()
    {
        await Seed();
        _reranker.IsAvailable = false;
        var baseline = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4).Select(h => h.ChunkId).ToList();
        _reranker.IsAvailable = true;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4, null, cts.Token);
        Assert.Equal(baseline, cancelled.Select(h => h.ChunkId).ToList());

        _index.GamingActive = () => true;
        var gaming = _index.Search("calibration procedure", KnowledgeSearchMode.Hybrid, 4);
        Assert.Equal(baseline, gaming.Select(h => h.ChunkId).ToList());
        Assert.Equal(0, _reranker.Calls);
    }

    [Fact]
    public async Task Exact_identifier_hit_stays_first_even_when_the_reranker_prefers_a_distractor()
    {
        await Seed();
        File.WriteAllText(Path.Combine(_docs, "code.txt"), "Part ZX-7731 needs a torque of 85 Nm.");
        await _index.IndexFolderAsync("s1", _docs, null, null, default);

        foreach (KnowledgeSearchMode mode in new[] { KnowledgeSearchMode.Auto, KnowledgeSearchMode.Hybrid })
        {
            var hits = _index.Search("ZX-7731 calibration procedure", mode, 4);
            Assert.Equal("code.txt", hits[0].FileName);
        }
    }

    [Fact]
    public void Candidate_count_is_max_k_times_four_or_24_capped_at_48()
    {
        Assert.Equal(24, KnowledgeIndex.RerankCandidateCount(3));
        Assert.Equal(40, KnowledgeIndex.RerankCandidateCount(10));
        Assert.Equal(48, KnowledgeIndex.RerankCandidateCount(40));
    }
}
