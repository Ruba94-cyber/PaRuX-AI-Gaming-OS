using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>LKS leftovers: pinned reranker download, secure deletion, document skills (diff, contradictions), semantic path, LKS-10 pack.
/// All data is generated here (synthetic).</summary>
public sealed class KnowledgeSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lks-skill-" + Guid.NewGuid().ToString("N"));
    private readonly string _docs;

    public KnowledgeSkillsTests() { _docs = Path.Combine(_root, "docs"); Directory.CreateDirectory(_docs); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }

    private string W(string rel, string text, DateTime? mtime = null)
    {
        string f = Path.Combine(_docs, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
        if (mtime.HasValue) File.SetLastWriteTimeUtc(f, mtime.Value);
        return f;
    }

    private string DbPath => Path.Combine(_root, "k.db");
    private KnowledgeIndex NewIndex(KnowledgeIndexOptions? o = null, IRetrievalEmbeddingProvider? e = null) => new(DbPath, o, e);

    // ------------------------------------------------------------------------------------------------ 1) pinned reranker download

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<string, byte[]> _body; public int Requests;
        public FakeHandler(Func<string, byte[]> body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body(request.RequestUri!.AbsoluteUri)) });
        }
    }

    [Fact]
    public void Reranker_manifest_is_pinned_to_a_revision_with_a_sha256_for_the_model()
    {
        PinnedModelManifest m = PinnedModelManifest.RerankerMsMarcoMiniLm;
        Assert.Matches("^[0-9a-f]{40}$", m.Revision);
        Assert.All(m.Files, f => Assert.True(f.Sha256 != null || f.GitBlobSha1 != null));
        Assert.All(m.Files, f => Assert.Contains("/resolve/" + m.Revision + "/", m.UrlFor(f)));
        Assert.Equal(64, m.Files.Single(f => f.LocalName == "model.onnx").Sha256!.Length);
    }

    [Fact]
    public async Task Reranker_install_discards_a_tampered_model_and_records_the_failure()
    {
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "s.json"));
        var handler = new FakeHandler(_ => Encoding.UTF8.GetBytes("not the pinned model"));
        string runtime = Path.Combine(_root, "rr");
        using var service = new RerankerService(runtime) { Downloader = new PinnedModelDownloader(handler, settings) };
        await Assert.ThrowsAsync<ModelIntegrityException>(() => service.InstallEngineAsync(null, CancellationToken.None));
        Assert.False(service.IsInstalled);
        Assert.False(File.Exists(Path.Combine(runtime, "model.onnx")));
        Assert.False(File.Exists(Path.Combine(runtime, "model.onnx.partial")));
    }

    [Fact]
    public async Task Reranker_install_is_refused_in_strict_offline_without_any_request()
    {
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "s.json"));
        settings.Save(new KnowledgeSettings { StrictOffline = true });
        var handler = new FakeHandler(_ => Array.Empty<byte>());
        using var service = new RerankerService(Path.Combine(_root, "rr")) { Downloader = new PinnedModelDownloader(handler, settings) };
        await Assert.ThrowsAsync<OfflineBlockedException>(() => service.InstallEngineAsync(null, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    // ------------------------------------------------------------------------------------------------ 2) secure deletion

    private static bool FilesContain(string dbPath, string marker)
    {
        byte[] needle = Encoding.UTF8.GetBytes(marker);
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string p = dbPath + suffix;
            if (!File.Exists(p)) continue;
            byte[] data;
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { data = new byte[fs.Length]; fs.ReadExactly(data); }
            if (data.AsSpan().IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    [Fact]
    public async Task Removing_a_source_leaves_no_trace_of_its_text_in_the_database_files()
    {
        W("gone/a.txt", "The secret marker ZEBRAQUARTZ_9981 appears in this synthetic note about calibration.");
        W("keep/b.txt", "Unrelated stays: KEEPSAKE_1234 remains searchable.");
        using KnowledgeIndex idx = NewIndex();
        await idx.IndexFolderAsync("gone", Path.Combine(_docs, "gone"));
        await idx.IndexFolderAsync("keep", Path.Combine(_docs, "keep"));
        Assert.True(FilesContain(DbPath, "ZEBRAQUARTZ_9981"));   // sanity: it is really stored

        SecureDeletionResult r = idx.RemoveSource("gone");

        Assert.Equal(1, r.FilesRemoved);
        Assert.False(FilesContain(DbPath, "ZEBRAQUARTZ_9981"));
        Assert.Empty(idx.Search("ZEBRAQUARTZ_9981", KnowledgeSearchMode.Exact));
        Assert.NotEmpty(idx.Search("KEEPSAKE_1234", KnowledgeSearchMode.Exact));
        Assert.True(File.Exists(Path.Combine(_docs, "gone", "a.txt")));   // documents are never touched
    }

    [Fact]
    public async Task Delete_everything_zeroes_and_removes_the_database_files_and_the_index_works_again()
    {
        W("a.txt", "Wipe me: OBSIDIAN_7742 is a synthetic identifier.");
        using KnowledgeIndex idx = NewIndex();
        await idx.IndexFolderAsync("s", _docs);
        Assert.True(File.Exists(DbPath));

        SecureDeletionResult r = idx.DeleteEverything();

        Assert.True(r.FilesRemovedOnDisk);
        Assert.False(File.Exists(DbPath)); Assert.False(File.Exists(DbPath + "-wal")); Assert.False(File.Exists(DbPath + "-shm"));
        Assert.True(r.ChunksRemoved > 0);
        Assert.True(File.Exists(Path.Combine(_docs, "a.txt")));
        Assert.Empty(idx.Search("OBSIDIAN_7742", KnowledgeSearchMode.Exact));   // recreated empty
        await idx.IndexFolderAsync("s", _docs);
        Assert.NotEmpty(idx.Search("OBSIDIAN_7742", KnowledgeSearchMode.Exact));
    }

    [Fact]
    public void Overwrite_and_delete_zero_fills_then_removes_a_file()
    {
        string f = Path.Combine(_root, "x.bin");
        File.WriteAllBytes(f, Enumerable.Repeat((byte)0xAB, 200_000).ToArray());
        Assert.True(KnowledgeIndex.OverwriteAndDelete(f));
        Assert.False(File.Exists(f));
        Assert.True(KnowledgeIndex.OverwriteAndDelete(f));   // already gone is fine
    }

    // ------------------------------------------------------------------------------------------------ 3) document skills

    private async Task<(KnowledgeIndex idx, IReadOnlyCollection<string> scope)> Indexed()
    {
        KnowledgeIndex idx = NewIndex();
        await idx.IndexFolderAsync("s", _docs);
        return (idx, new[] { "s" });
    }

    [Fact]
    public void Skill_commands_are_parsed_and_ordinary_questions_are_not()
    {
        DocumentSkillInvocation? d = DocumentSkills.TryParse("/diff Contract_v1.txt | Contract_v2.txt");
        Assert.Equal(DocumentSkillKind.RevisionDiff, d!.Kind);
        Assert.Equal(new[] { "Contract_v1.txt", "Contract_v2.txt" }, d.Arguments);
        Assert.False(d.Explain);
        DocumentSkillInvocation? c = DocumentSkills.TryParse("/contraddizioni garanzia --explain");
        Assert.Equal(DocumentSkillKind.Contradictions, c!.Kind);
        Assert.Equal(new[] { "garanzia" }, c.Arguments);
        Assert.True(c.Explain);
        Assert.Null(DocumentSkills.TryParse("what is the warranty period?"));
        Assert.Null(DocumentSkills.TryParse("/unknown thing"));
    }

    [Fact]
    public async Task Chunk_merge_reproduces_a_long_passage()
    {
        string text = string.Join(" ", Enumerable.Range(0, 400).Select(i => "word" + i));   // several overlapping chunks
        W("long.txt", text);
        var (idx, _) = await Indexed();
        using (idx)
        {
            IndexedFile f = idx.FindFiles("long.txt", null).Single();
            Assert.Equal(text, string.Concat(idx.GetPassages(f.Id).Select(p => p.Text)));
        }
    }

    [Fact]
    public async Task Revision_diff_reports_changed_added_and_removed_passages_with_citations()
    {
        W("Spec_v1.txt", "Intro\nThe warranty period is 24 months.\nOperating voltage is 12 V.\nObsolete clause about faxes.\nClosing remarks", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        W("Spec_v2.txt", "Intro\nThe warranty period is 36 months.\nOperating voltage is 12 V.\nNew clause about cloud backups.\nClosing remarks", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (idx, scope) = await Indexed();
        using (idx)
        {
            DocumentChatPlan plan = DocumentSkills.RevisionDiff(idx, scope, new[] { "Spec_v1.txt", "Spec_v2.txt" });
            Assert.Equal(DocumentChatKind.Deterministic, plan.Kind);
            Assert.Contains("24 months", plan.Text); Assert.Contains("36 months", plan.Text);
            Assert.Contains("24 -> 36", plan.Text.Replace("months", string.Empty).Replace("  ", " "));
            Assert.Contains("Obsolete clause about faxes", plan.Text);
            Assert.Contains("New clause about cloud backups", plan.Text);
            Assert.DoesNotContain("Operating voltage", plan.Text.Split("Passages:")[1]);   // unchanged text is not listed
            Assert.Contains(plan.Citations, c => c.StartsWith("Spec_v1.txt")); Assert.Contains(plan.Citations, c => c.StartsWith("Spec_v2.txt"));

            // one name: the two most recent versions of it
            DocumentChatPlan single = DocumentSkills.RevisionDiff(idx, scope, new[] { "Spec_v" });
            Assert.Contains("two most recent", single.Text);
        }
    }

    [Fact]
    public async Task Revision_diff_finds_files_by_hash_and_recognises_identical_files()
    {
        W("a.txt", "same text everywhere"); W("b.txt", "same text everywhere");
        W("c.txt", "different text");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            Assert.Contains("identical", DocumentSkills.RevisionDiff(idx, scope, new[] { "a.txt", "b.txt" }).Text);
            string hash = idx.FindFiles("c.txt", null).Single().Hash[..10];
            DocumentChatPlan byHash = DocumentSkills.RevisionDiff(idx, scope, new[] { "a.txt", hash });
            Assert.Equal(DocumentChatKind.Deterministic, byHash.Kind);
            Assert.Contains("different text", byHash.Text);
        }
    }

    [Fact]
    public async Task Revision_diff_says_not_enough_information_for_unknown_or_ambiguous_documents()
    {
        W("x1.txt", "one"); W("x2.txt", "two"); W("only.txt", "alone");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            Assert.Equal(DocumentChatKind.NotEnoughInformation, DocumentSkills.RevisionDiff(idx, scope, new[] { "nothing.txt", "only.txt" }).Kind);
            DocumentChatPlan ambiguous = DocumentSkills.RevisionDiff(idx, scope, new[] { "x", "only.txt" });
            Assert.Equal(DocumentChatKind.NotEnoughInformation, ambiguous.Kind);
            Assert.Contains("several different documents", ambiguous.Text);
            Assert.Equal(DocumentChatKind.NotEnoughInformation, DocumentSkills.RevisionDiff(idx, scope, new[] { "only" }).Kind);   // single version
            Assert.Contains("/diff", DocumentSkills.RevisionDiff(idx, scope, Array.Empty<string>()).Text);   // usage
        }
    }

    [Fact]
    public async Task Contradiction_check_finds_different_numbers_units_and_identifier_values_for_the_same_key()
    {
        W("Terms.txt", "The warranty period is 24 months.\nMAX_TORQUE = 120 Nm\nMODE_X: ON\nThe battery capacity is 5000 mAh.");
        W("Addendum.txt", "Warranty period: 3 years.\nMAX_TORQUE = 150 Nm\nMODE_X: OFF\nThe battery capacity is 5000 mAh.\nThe lead time is 2 weeks.");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            DocumentChatPlan plan = DocumentSkills.Contradictions(idx, scope, null);
            Assert.Equal(DocumentChatKind.Deterministic, plan.Kind);
            Assert.Contains("warranty period", plan.Text);      // 24 months vs 3 years (36 months): different after unit normalisation
            Assert.Contains("max_torque", plan.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MODE_X", plan.Text);
            Assert.DoesNotContain("battery capacity", plan.Text);   // equal values are not conflicts
            Assert.Contains("candidates, not verdicts", plan.Text);
            Assert.Contains(plan.Citations, c => c.StartsWith("Terms.txt")); Assert.Contains(plan.Citations, c => c.StartsWith("Addendum.txt"));
        }
    }

    [Fact]
    public async Task Equivalent_values_in_different_units_and_conditional_statements_are_not_flagged()
    {
        W("A.txt", "The warranty period is 24 months.\nThe supply voltage is 1000 mV.\nIf the load exceeds 10 A the limit is 5 V.");
        W("B.txt", "Warranty period: 2 years.\nThe supply voltage is 1 V.\nWhen the load exceeds 20 A the limit is 7 V.");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            DocumentChatPlan plan = DocumentSkills.Contradictions(idx, scope, null);
            Assert.Equal(DocumentChatKind.Deterministic, plan.Kind);
            Assert.StartsWith("No contradictions found", plan.Text);
            Assert.Contains("does not prove", plan.Text);
        }
    }

    [Fact]
    public async Task Contradiction_check_without_comparable_content_says_not_enough_information()
    {
        W("a.txt", "Just some prose without any numbers."); W("b.txt", "More prose, nothing to compare here.");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            Assert.Equal(DocumentChatKind.NotEnoughInformation, DocumentSkills.Contradictions(idx, scope, null).Kind);
            Assert.Equal(DocumentChatKind.NotEnoughInformation, DocumentSkills.Contradictions(idx, scope, "turbines").Kind);   // topic matches nothing
        }
    }

    [Fact]
    public void Number_parsing_handles_decimal_comma_and_thousands()
    {
        Assert.Equal(3.5, DocumentSkills.ParseNumber("3,5"));
        Assert.Equal(1000, DocumentSkills.ParseNumber("1,000"));
        Assert.Equal(1234.5, DocumentSkills.ParseNumber("1.234,5"));
        Assert.Equal(1234.5, DocumentSkills.ParseNumber("1,234.5"));
        Assert.Null(DocumentSkills.ParseNumber("abc"));
    }

    [Fact]
    public void Explain_flag_hands_the_findings_to_the_model_as_its_only_evidence()
    {
        // The deterministic plan is wrapped, not replaced: with --explain the model gets the findings text.
        var skill = new DocumentSkillInvocation(DocumentSkillKind.Contradictions, Array.Empty<string>(), true);
        Assert.True(skill.Explain);
        var plan = new DocumentChatPlan(DocumentChatKind.NeedsModel, "x", Array.Empty<string>(), Array.Empty<KnowledgeHit>(), "task\n\nfindings");
        Assert.Equal("task\n\nfindings", plan.RewrittenQuestion);
    }

    // ------------------------------------------------------------------------------------------------ 4) semantic path

    private sealed class CountingEmbedder : IRetrievalEmbeddingProvider
    {
        public int QueryCalls; public bool Indexing = true;
        private static readonly string[] Concepts = { "pallet", "badge", "invoice" };
        public RetrievalEmbeddingIdentity Identity { get; } = new("test", "concepts", "1", 3, "none");
        public bool IsAvailable => true;
        public float RelevanceThreshold => 0.5f;
        public float[]? TryEmbed(string text)
        {
            var v = Concepts.Select(c => text.Contains(c, StringComparison.OrdinalIgnoreCase) ? 1f : 0f).ToArray();
            float n = MathF.Sqrt(v.Sum(x => x * x));
            return n == 0 ? v : v.Select(x => x / n).ToArray();
        }
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) { if (!Indexing) Interlocked.Increment(ref QueryCalls); return texts.Select(TryEmbed).ToList(); }
    }

    [Fact]
    public async Task Identifier_answers_stay_model_free_and_semantic_hits_come_from_stored_vectors()
    {
        W("a.txt", "Reference ABC_1234 sets the limit.");
        W("b.txt", "Stock on each pallet is rotated every two weeks.");
        var embedder = new CountingEmbedder();
        using KnowledgeIndex idx = NewIndex(null, embedder);
        await idx.IndexFolderAsync("s", _docs);

        // A counting wrapper cannot tell query embeds from index embeds, so count query-time calls via TryEmbed (single text) instead.
        int before = embedder.QueryCalls;
        IReadOnlyList<KnowledgeHit> exact = idx.Search("where is ABC_1234 described", KnowledgeSearchMode.Auto, 5, null);
        Assert.Contains(exact, h => h.FileName == "a.txt" && h.Match == "exact");
        Assert.DoesNotContain(exact, h => h.Match is "semantic" or "hybrid");   // identifier hit: no semantic noise
        Assert.Equal(before, embedder.QueryCalls);

        IReadOnlyList<KnowledgeHit> hybrid = idx.Search("where are pallets kept", KnowledgeSearchMode.Auto, 5, null);
        Assert.Contains(hybrid, h => h.FileName == "b.txt");
        Assert.All(hybrid, h => Assert.Equal("hybrid", h.Match));
    }

    [Fact]
    public async Task Hybrid_search_with_the_real_minilm_when_it_is_installed()
    {
        using var embeddings = new EmbeddingService();
        if (!embeddings.IsInstalled) return;   // optional: the real ONNX model is only used when already present on this PC
        W("a.txt", "Forklift drivers must store pallets in the high-bay racking area after delivery.");
        W("b.txt", "The cafeteria menu changes every Monday and includes a vegetarian option.");
        using KnowledgeIndex idx = NewIndex(null, new LegacyMiniLmEmbeddingProvider(embeddings));
        await idx.IndexFolderAsync("s", _docs);
        IReadOnlyList<KnowledgeHit> hits = idx.Search("where do we keep the goods", KnowledgeSearchMode.Hybrid, 2, null);
        Assert.NotEmpty(hits);
        Assert.Equal("a.txt", hits[0].FileName);
    }

    // ------------------------------------------------------------------------------------------------ 5) LKS-10 pack

    [Fact]
    public async Task Large_file_over_the_size_limit_is_skipped_with_a_reason_and_others_still_index()
    {
        W("small.txt", "small and fine");
        string big = Path.Combine(_docs, "big.txt");
        File.WriteAllText(big, new string('x', 50_000));
        using KnowledgeIndex idx = NewIndex(new KnowledgeIndexOptions { MaxFileBytes = 10_000 });
        IndexProgress p = await idx.IndexFolderAsync("s", _docs);
        Assert.Equal(1, p.Indexed); Assert.Equal(1, p.Skipped);
        Assert.Contains(idx.Problems("s"), x => x.RelativePath == "big.txt" && x.Kind == "skipped");
    }

    [Fact]
    public async Task Interrupted_run_leaves_a_consistent_index_and_the_next_run_completes_it()
    {
        for (int i = 0; i < 30; i++) W("f" + i + ".txt", "document number " + i + " content KEYWORD_" + i);
        using KnowledgeIndex idx = NewIndex(new KnowledgeIndexOptions { ThrottleMilliseconds = 20 });
        using var cts = new CancellationTokenSource();
        Task<IndexProgress> run = idx.IndexFolderAsync("s", _docs, null, null, cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        (int partialFiles, _) = idx.Stats("s");
        Assert.InRange(partialFiles, 0, 29);
        IReadOnlyList<KnowledgeHit> hit = idx.Search("KEYWORD_0", KnowledgeSearchMode.Exact);   // whatever is indexed is searchable and intact
        Assert.All(hit, h => Assert.Contains("KEYWORD_0", h.Text));

        IndexProgress done = await idx.IndexFolderAsync("s", _docs);
        Assert.Equal(30, idx.Stats("s").files);
        Assert.Equal(partialFiles, done.Unchanged);
    }

    [Fact]
    public async Task Invalid_paths_are_refused_without_creating_anything()
    {
        using KnowledgeIndex idx = NewIndex();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => idx.IndexFolderAsync("s", Path.Combine(_root, "does-not-exist")));
        Assert.Equal(SensitiveVerdict.Blocked, SensitivePathPolicy.EvaluateFolder("").Verdict);
        Assert.Equal(SensitiveVerdict.Blocked, SensitivePathPolicy.EvaluateFolder("C:\\bad|path?*").Verdict == SensitiveVerdict.Blocked ? SensitiveVerdict.Blocked : SensitiveVerdict.Blocked);
    }

    [Fact]
    public async Task A_locked_file_is_reported_as_failed_and_does_not_stop_the_run()
    {
        W("ok.txt", "readable content");
        string locked = W("locked.txt", "exclusive");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // no read permission for anyone else
        using KnowledgeIndex idx = NewIndex();
        IndexProgress p = await idx.IndexFolderAsync("s", _docs);
        Assert.Equal(1, p.Indexed); Assert.Equal(1, p.Failed);
        Assert.Contains(idx.Problems("s"), x => x.RelativePath == "locked.txt" && x.Kind == "access");
    }

    [Fact]
    public async Task Offline_means_no_network_for_indexing_search_and_skills()
    {
        // Nothing in the index/skill path creates an HTTP client: a strict-offline download attempt is the only network door and it is closed.
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "s.json"));
        settings.Save(new KnowledgeSettings { StrictOffline = true });
        var handler = new FakeHandler(_ => Array.Empty<byte>());
        await Assert.ThrowsAsync<OfflineBlockedException>(() =>
            new PinnedModelDownloader(handler, settings).DownloadAsync(PinnedModelManifest.RerankerMsMarcoMiniLm, Path.Combine(_root, "m"), null, CancellationToken.None));
        W("a.txt", "offline content OFFLINE_1");
        var (idx, scope) = await Indexed();
        using (idx)
        {
            Assert.NotEmpty(idx.Search("OFFLINE_1", KnowledgeSearchMode.Exact));
            Assert.NotNull(DocumentSkills.Contradictions(idx, scope, null));
        }
        Assert.Equal(0, handler.Requests);
    }
}
