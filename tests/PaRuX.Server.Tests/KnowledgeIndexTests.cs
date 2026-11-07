using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic folders generated here; no real documents. Embeddings are not needed for any of these (no generative or
/// embedding model is loaded), which is itself part of what is verified.</summary>
public sealed class KnowledgeIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lks-idx-" + Guid.NewGuid().ToString("N"));
    private readonly string _docs;
    private readonly KnowledgeIndex _index;

    public KnowledgeIndexTests()
    {
        _docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(_docs);
        _index = new KnowledgeIndex(Path.Combine(_root, "knowledge.db"));
    }

    public void Dispose() { _index.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }

    private string W(string rel, string text)
    {
        string full = Path.Combine(_docs, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private Task<IndexProgress> Index(IndexControl? control = null, CancellationToken ct = default) => _index.IndexFolderAsync("s1", _docs, null, control, ct);

    [Fact]
    public void Fts5_is_available_in_the_bundled_sqlite() => Assert.True(_index.HasFts5);

    [Fact]
    public async Task First_index_then_unchanged_files_are_not_touched()
    {
        W("a.txt", "Calibration of the mass flow controller");
        W("b.md", "Other notes");

        IndexProgress first = await Index();
        IndexProgress second = await Index();

        Assert.Equal(2, first.Indexed);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.Indexed);
    }

    [Fact]
    public async Task Modified_file_is_reindexed_and_deleted_file_is_removed()
    {
        string a = W("a.txt", "alpha content");
        W("b.txt", "beta content");
        await Index();

        await Task.Delay(30);
        File.WriteAllText(a, "gamma replacement text");
        File.Delete(Path.Combine(_docs, "b.txt"));
        IndexProgress p = await Index();

        Assert.Equal(1, p.Indexed);
        Assert.Equal(1, p.Removed);
        Assert.Empty(_index.Search("alpha", KnowledgeSearchMode.Lexical));
        Assert.Empty(_index.Search("beta", KnowledgeSearchMode.Lexical));
        Assert.Single(_index.Search("gamma", KnowledgeSearchMode.Lexical));
    }

    [Fact]
    public async Task Rename_keeps_the_index_without_reextracting_and_duplicates_are_flagged()
    {
        string a = W("old.txt", "unique rename payload");
        await Index();
        File.Move(a, Path.Combine(_docs, "new.txt"));
        IndexProgress renamed = await Index();
        Assert.Equal(1, renamed.Renamed);
        Assert.Equal(0, renamed.Indexed);
        Assert.Equal("new.txt", _index.Search("rename payload", KnowledgeSearchMode.Lexical)[0].FileName);

        W("copy.txt", "unique rename payload");
        IndexProgress dup = await Index();
        Assert.Equal(1, dup.Duplicates);
    }

    [Fact]
    public async Task Exact_identifier_beats_fuzzy_words_and_partial_identifiers_are_found()
    {
        W("params.txt", "Parameter ABC_1234 sets the idle speed.\nParameter ABC_9999 sets the limit.");
        W("prose.txt", "A calibration procedure for ABC systems with idle behaviour explained at length.");
        await Index();

        IReadOnlyList<KnowledgeHit> exact = _index.Search("Trova il parametro esatto ABC_1234");
        Assert.NotEmpty(exact);
        Assert.All(exact, h => Assert.Contains("ABC_1234", h.Text));
        Assert.Equal("exact", exact[0].Match);

        IReadOnlyList<KnowledgeHit> partial = _index.Search("ABC_12", KnowledgeSearchMode.Exact);
        Assert.Contains(partial, h => h.Text.Contains("ABC_1234"));
        Assert.DoesNotContain(partial, h => h.Text.Contains("ABC_9999") && !h.Text.Contains("ABC_1234"));
    }

    [Theory]
    [InlineData("Where is parameter ABC_1234?", "ABC_1234")]
    [InlineData("DTC P0420 meaning", "P0420")]
    [InlineData("address 0x7E0 request", "0x7E0")]
    [InlineData("find \"idle speed\" quickly", "idle speed")]
    public void Router_extracts_identifiers_and_quoted_phrases(string query, string expected) =>
        Assert.Contains(expected, KnowledgeIndex.ExactTerms(query));

    [Fact]
    public void Plain_questions_have_no_exact_terms() =>
        Assert.Empty(KnowledgeIndex.ExactTerms("how do I calibrate the controller"));

    [Fact]
    public async Task Lexical_search_works_without_any_model_and_file_names_are_searchable()
    {
        W("procedure.txt", "The MFC calibration procedure for the Daily vehicle.");
        File.WriteAllBytes(Path.Combine(_docs, "diagram-Wiring_X77.bin"), new byte[] { 0, 1, 2, 0, 3 });
        await Index();

        Assert.Equal("procedure.txt", _index.Search("calibration procedure vehicle")[0].FileName);
        Assert.Contains(_index.Search("Wiring_X77"), h => h.FileName == "diagram-Wiring_X77.bin");
    }

    [Fact]
    public async Task Corporate_selected_source_scopes_FTS_before_topK_even_with_thousands_of_other_matches()
    {
        // The old query applied source scope AFTER the global LIMIT.
        // A large unselected corpus could suppress an exact result in source B.
        for (int i = 0; i < 80; i++)
            W("unselected-" + i + ".txt",
                "DID_AB45 diagnostic identifier found in unrelated source A, item " + i);
        await _index.IndexFolderAsync("source-A", _docs);
        string selectedFolder = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(selectedFolder);
        File.WriteAllText(Path.Combine(selectedFolder, "approved-spec.txt"),
            "DID_AB45: selected ECU response is a 2-byte positive confirmation.");
        await _index.IndexFolderAsync("source-B", selectedFolder);

        IReadOnlyList<KnowledgeHit> exact = _index.Search("DID_AB45",
            KnowledgeSearchMode.Exact, 1, new[] { "source-B" });
        KnowledgeHit selected = Assert.Single(exact);
        Assert.Equal("approved-spec.txt", selected.FileName);
        Assert.Contains("2-byte", selected.Text);

        IReadOnlyList<KnowledgeHit> lexical = _index.Search("DID_AB45",
            KnowledgeSearchMode.Lexical, 1, new[] { "source-B" });
        Assert.All(lexical, h => Assert.Equal("approved-spec.txt", h.FileName));
        Assert.Empty(_index.Search("DID_AB45", KnowledgeSearchMode.Exact,
            5, Array.Empty<string>()));
    }

    [Fact]
    public void Bundled_sqlite_supports_parameterized_json_table_scopes()
    {
        // The knowledge filter uses a single bound JSON array rather than
        // >900 SQL parameters. Explicitly gate the required bundled feature.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM json_each($sourceIds)";
        query.Parameters.AddWithValue("$sourceIds", "[\"s1\",\"s2\",\"s3\"]");
        Assert.Equal(3L, (long)query.ExecuteScalar()!);
    }

    [Fact]
    public async Task Corporate_scope_with_more_than_1200_folders_finds_only_approved_evidence()
    {
        // Only one folder needs a document; the other IDs model a large
        // registry. The old >900-parameter guard made ALL-folder mode fail.
        string finance = Path.Combine(_root, "finance");
        Directory.CreateDirectory(finance);
        File.WriteAllText(Path.Combine(finance, "invoice.md"),
            "Invoice INV_2026_884 totals EUR 420.00 and is payable on 30 November 2026.");
        const string approved = "finance/quoted\\path,\"東京\"";
        await _index.IndexFolderAsync(approved, finance);

        string[] unrelated = Enumerable.Range(0, 1250)
            .Select(i => "no-evidence-" + i).ToArray();
        string[] selected = unrelated.Append(approved).ToArray();

        Assert.True(_index.HasAnyIndexedChunks(selected));
        Assert.False(_index.HasAnyIndexedChunks(unrelated));
        Assert.False(_index.HasAnyIndexedChunks(Array.Empty<string>()));

        IReadOnlyList<KnowledgeHit> exact = _index.Search("INV_2026_884",
            KnowledgeSearchMode.Exact, 5, selected);
        Assert.Single(exact);
        Assert.Contains("EUR 420.00", exact[0].Text);

        IReadOnlyList<KnowledgeHit> lexical = _index.Search("invoice payable",
            KnowledgeSearchMode.Lexical, 5, selected);
        Assert.Contains(lexical, h => h.FileName == "invoice.md");

        Assert.Empty(_index.Search("INV_2026_884",
            KnowledgeSearchMode.Exact, 5, unrelated));
        Assert.Empty(_index.Search("invoice payable",
            KnowledgeSearchMode.Lexical, 5, unrelated));
    }

    [Fact]
    public async Task Multi_invoice_search_reserves_a_result_for_a_rare_second_code()
    {
        for (int i = 0; i < 18; i++)
            W("frequent-" + i + ".txt",
                "Invoice INV_2026_884 is due in November. Reminder #" + i + ".");
        W("rare.txt", "Invoice INV_2026_885 was paid in December and has zero balance.");
        await Index();

        // The exact router must not fill all three results with a frequent
        // identifier and then tell Document Chat the rare one is missing.
        var hits = _index.Search("Compare INV_2026_884 and INV_2026_885",
            KnowledgeSearchMode.Auto, 3);
        Assert.Contains(hits, h => h.Text.Contains("INV_2026_884"));
        Assert.Contains(hits, h => h.Text.Contains("INV_2026_885"));
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task Positions_survive_into_citations()
    {
        W("long.txt", new string('x', 2000) + " NEEDLE_77 " + new string('y', 500));
        await Index();

        KnowledgeHit hit = _index.Search("NEEDLE_77")[0];

        Assert.Contains("long.txt", hit.Citation);
        Assert.True(hit.Position.CharOffset > 0);
    }

    [Fact]
    public async Task Excluded_folders_lock_files_and_hidden_files_are_skipped()
    {
        W("keep.txt", "keep me");
        W(".git/config.txt", "git internals");
        W("node_modules/x.txt", "dependency");
        W("~$lock.txt", "office lock");

        IndexProgress p = await Index();

        Assert.Equal(1, p.Indexed);
        Assert.Empty(_index.Search("dependency", KnowledgeSearchMode.Lexical));
    }

    [Fact]
    public async Task Corrupt_and_unreadable_files_are_listed_without_content()
    {
        File.WriteAllText(Path.Combine(_docs, "bad.docx"), "TOPSECRET-not-a-zip");
        W("fine.txt", "fine");

        IndexProgress p = await Index();

        Assert.Equal(1, p.Failed);
        IndexFileProblem problem = Assert.Single(_index.Problems("s1"), x => x.Kind == "failed");
        Assert.Equal("bad.docx", problem.RelativePath);
        Assert.DoesNotContain("TOPSECRET", problem.Reason);
    }

    [Fact]
    public async Task Cancellation_stops_the_job_and_a_rerun_completes_it()
    {
        for (int i = 0; i < 20; i++) W($"f{i}.txt", "content " + i);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index(null, cts.Token));

        IndexProgress done = await Index();
        Assert.Equal(20, done.Indexed);
    }

    [Fact]
    public async Task Pause_blocks_progress_until_resume()
    {
        W("p.txt", "paused file");
        var control = new IndexControl();
        control.Pause();
        Task<IndexProgress> job = Index(control);
        await Task.Delay(300);
        Assert.False(job.IsCompleted);
        control.Resume();
        Assert.Equal(1, (await job).Indexed);
    }

    [Fact]
    public async Task Removing_a_source_deletes_only_the_index_never_the_documents()
    {
        string f = W("keep.txt", "stays on disk");
        await Index();

        _index.RemoveSource("s1");

        Assert.True(File.Exists(f));
        Assert.Equal((0, 0), _index.Stats("s1"));
        Assert.Empty(_index.Search("stays", KnowledgeSearchMode.Lexical));
    }

    [Fact]
    public async Task Existing_data_is_preserved_when_the_index_is_reopened()
    {
        W("persist.txt", "persistent knowledge");
        await Index();
        using var reopened = new KnowledgeIndex(Path.Combine(_root, "knowledge.db"));
        Assert.Single(reopened.Search("persistent knowledge", KnowledgeSearchMode.Lexical));
    }

    [Fact]
    public async Task Missing_folder_fails_clearly()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _index.IndexFolderAsync("s1", Path.Combine(_root, "nope")));
    }

    /// <summary>Tiny deterministic embedder: one dimension per concept word.</summary>
    private sealed class ConceptEmbedder : IRetrievalEmbeddingProvider
    {
        private static readonly string[] Concepts = { "pallet", "badge", "laptop", "invoice" };
        public RetrievalEmbeddingIdentity Identity { get; } = new("test", "concepts", "1", 4, "none");
        public bool IsAvailable => true;
        public float RelevanceThreshold => 0.5f;
        public float[]? TryEmbed(string text)
        {
            var v = Concepts.Select(c => text.Contains(c, StringComparison.OrdinalIgnoreCase) ? 1f : 0f).ToArray();
            float n = MathF.Sqrt(v.Sum(x => x * x));
            return n == 0 ? v : v.Select(x => x / n).ToArray();
        }
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) => texts.Select(TryEmbed).ToList();
    }

    [Fact]
    public async Task Default_index_factory_wires_the_embedder_so_semantic_search_finds_chunks()
    {
        // Regression: KnowledgeJobService built its index without an embedder, so Semantic search silently returned nothing.
        W("a.txt", "Stock on each pallet is rotated every two weeks.");
        W("b.txt", "Weather was pleasant during the picnic.");
        using KnowledgeIndex wired = KnowledgeJobService.CreateIndex(Path.Combine(_root, "wired.db"), new ConceptEmbedder());
        await wired.IndexFolderAsync("s1", _docs, null, null, default);
        IReadOnlyList<KnowledgeHit> hits = wired.Search("where are pallets kept", KnowledgeSearchMode.Semantic, 5, null);
        Assert.Contains(hits, h => h.FileName == "a.txt");
        Assert.DoesNotContain(hits, h => h.FileName == "b.txt");
        using KnowledgeIndex plain = KnowledgeJobService.CreateIndex(Path.Combine(_root, "plain.db"), null);
        await plain.IndexFolderAsync("s1", _docs, null, null, default);
        Assert.Empty(plain.Search("where are pallets kept", KnowledgeSearchMode.Semantic, 5, null));
    }
}
