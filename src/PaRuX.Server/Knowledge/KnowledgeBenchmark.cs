using System.Diagnostics;
using System.Text;

namespace PaRuX.Server.Knowledge;

public sealed record KnowledgeBenchmarkOptions(int Documents = 120, int TopK = 5, int Seed = 7, KnowledgeResourcePlan? Plan = null);

/// <summary>Aggregate numbers only: no document text, no paths.</summary>
public sealed record KnowledgeBenchmarkReport(
    int Documents, int Queries, int TopK, double PrecisionAtK, double RecallAtK, double MeanReciprocalRank, double NdcgAtK,
    double MedianLatencyMs, double P95LatencyMs, double IndexSeconds, double DocumentsPerSecond, string Profile,
    int NegativeQueries, int NegativeQueriesAnsweredWithNoEvidence);

/// <summary>
/// Reproducible retrieval benchmark on a synthetic corpus generated here (never real documents). Queries are of three
/// kinds: exact identifiers, topical words, and questions the corpus cannot answer (these must yield "not enough
/// information"). Metrics come from <see cref="RetrievalEvaluationService"/> plus precision@k.
/// </summary>
public static class KnowledgeBenchmark
{
    private static readonly string[] Topics =
    {
        "coolant pump", "brake caliper", "battery balancer", "gearbox oil", "wiring harness", "firmware loader", "sensor calibration",
        "torque wrench", "valve timing", "heat exchanger", "fuel injector", "software release", "diagnostic session", "gateway module"
    };
    private static readonly string[] Verbs = { "inspect", "replace", "calibrate", "measure", "document", "validate", "configure", "archive" };

    public sealed record SyntheticCorpus(string Folder, IReadOnlyList<RetrievalEvalCase> Cases, IReadOnlyList<string> UnanswerableQueries);

    /// <summary>Writes <paramref name="documents"/> small text/markdown/csv files with unique codes; deterministic for a seed.</summary>
    public static SyntheticCorpus GenerateCorpus(string folder, int documents, int seed)
    {
        Directory.CreateDirectory(folder);
        var rng = new Random(seed);
        var cases = new List<RetrievalEvalCase>();
        for (int i = 0; i < documents; i++)
        {
            string topic = Topics[i % Topics.Length];
            string verb = Verbs[(i / Topics.Length + i) % Verbs.Length];
            string code = "ZQ" + (1000 + i) + "_" + Convert.ToHexString(BitConverter.GetBytes(rng.Next())).Substring(0, 4);
            string unique = "marker" + (char)('a' + i % 26) + (char)('a' + i / 26 % 26) + (char)('a' + i / 676 % 26);
            string name = "doc-" + i.ToString("D4");
            var sb = new StringBuilder();
            sb.Append("# Procedure ").Append(code).Append('\n');
            sb.Append("This synthetic note explains how to ").Append(verb).Append(" the ").Append(topic).Append(" in the test workshop.\n");
            sb.Append("Reference identifier ").Append(code).Append(" and keyword ").Append(unique).Append(" belong only to this note.\n");
            for (int k = 0; k < 6; k++) sb.Append("Filler sentence ").Append(k).Append(" about generic workshop planning and scheduling.\n");
            string ext = (i % 3) switch { 0 => ".md", 1 => ".txt", _ => ".csv" };
            string text = ext == ".csv" ? "id,topic,code\n" + i + "," + topic + "," + code + "\n" + sb : sb.ToString();
            File.WriteAllText(Path.Combine(folder, name + ext), text);
            cases.Add(new RetrievalEvalCase("exact-" + i, "where is " + code + " described", new Dictionary<string, int> { [name + ext] = 3 }));
            if (i % 2 == 0)
                cases.Add(new RetrievalEvalCase("word-" + i, unique + " " + verb + " " + topic, new Dictionary<string, int> { [name + ext] = 3 }));
        }
        var unanswerable = new List<string>
        {
            "what is the torque of the quantum flux capacitor", "who won the regional chess championship in 1987",
            "describe the migration of arctic terns", "list the ingredients of the saffron risotto recipe"
        };
        return new SyntheticCorpus(folder, cases, unanswerable);
    }

    public static async Task<KnowledgeBenchmarkReport> RunAsync(string workFolder, KnowledgeBenchmarkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new KnowledgeBenchmarkOptions();
        string docs = Path.Combine(workFolder, "corpus");
        SyntheticCorpus corpus = GenerateCorpus(docs, options.Documents, options.Seed);
        var index = new KnowledgeIndex(Path.Combine(workFolder, "bench.db"), new KnowledgeIndexOptions { EmbedChunks = false, Plan = options.Plan });
        Stopwatch sw = Stopwatch.StartNew();
        await index.IndexFolderAsync("bench", docs, null, null, ct).ConfigureAwait(false);
        sw.Stop();

        var found = new List<(RetrievalEvalCase c, IReadOnlyList<string> docs)>();
        IReadOnlyList<string> Retrieve(string query, int k)
        {
            IReadOnlyList<KnowledgeHit> hits = index.Search(query, KnowledgeSearchMode.Auto, k * 3, new[] { "bench" });
            return hits.Select(h => h.FileName).Distinct().Take(k).ToList();
        }
        RetrievalEvalMetrics metrics = new RetrievalEvaluationService().Evaluate(
            "knowledge-" + (options.Plan?.Profile.ToString() ?? "default"), corpus.Cases, Retrieve, options.TopK, GC.GetTotalMemory(false));

        double precisionSum = 0;
        foreach (RetrievalEvalCase c in corpus.Cases)
        {
            IReadOnlyList<string> top = Retrieve(c.Query, options.TopK);
            precisionSum += top.Count == 0 ? 0 : (double)top.Count(d => c.RelevanceByDocumentId.ContainsKey(d)) / top.Count;
        }

        int negativeOk = 0;
        foreach (string q in corpus.UnanswerableQueries)
        {
            IReadOnlyList<KnowledgeHit> hits = index.Search(q, KnowledgeSearchMode.Auto, 6, new[] { "bench" });
            if (DocumentChatPlanner.Plan(q, hits).Kind == DocumentChatKind.NotEnoughInformation) negativeOk++;
        }
        return new KnowledgeBenchmarkReport(options.Documents, corpus.Cases.Count, options.TopK, precisionSum / corpus.Cases.Count,
            metrics.RecallAtK, metrics.MeanReciprocalRank, metrics.NdcgAtK, metrics.MedianLatencyMilliseconds, metrics.P95LatencyMilliseconds,
            sw.Elapsed.TotalSeconds, options.Documents / Math.Max(0.001, sw.Elapsed.TotalSeconds), options.Plan?.Profile.ToString() ?? "unthrottled",
            corpus.UnanswerableQueries.Count, negativeOk);
    }
}
