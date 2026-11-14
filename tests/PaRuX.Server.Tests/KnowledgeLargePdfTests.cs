using Microsoft.Data.Sqlite;
using PaRuX.Server.Knowledge;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Synthetic PDF fixtures exercise the real page staging, SQLite checkpoint,
/// atomic visibility, resume and cited retrieval without a local AI model.
/// 30,000-page memory/throughput remains a separate Windows benchmark gate.
/// </summary>
public sealed class KnowledgeLargePdfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-large-pdf-" + Guid.NewGuid().ToString("N"));
    private string Docs => Path.Combine(_root, "docs");
    private string Database => Path.Combine(_root, "knowledge.db");

    public KnowledgeLargePdfTests() => Directory.CreateDirectory(Docs);

    private KnowledgeIndex Index(IOcrEngine? ocr = null) => new(Database, new KnowledgeIndexOptions
    {
        LargePdfPageThreshold = 2,
        MaxFileBytes = 256, // Even a tiny Eco file cap must not silently truncate a big PDF.
        Limits = ExtractionLimits.Default with { MaxCharacters = 100, Ocr = ocr },
        EmbedChunks = false
    });

    private sealed class ControlledOcr : IOcrEngine
    {
        public string Name => "synthetic-offline-ocr";
        public OcrAvailability CheckAvailability() =>
            new(true, null, Array.Empty<string>());
        public OcrPageResult RecognizeImage(string path, string? languageTag, CancellationToken ct) =>
            new(1, "SIMULATED_OCR", true);
        public IReadOnlyList<OcrPageResult> RecognizePdfPages(
            string path, IReadOnlyList<int> pages, string? languageTag, CancellationToken ct) =>
            pages.Select(p => new OcrPageResult(p, "OCR_SERVICE_PAGE_" + p.ToString("D4"), true)).ToArray();
    }

    private string MakePdf(string name, int pages, string prefix, int? blankPage = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (int i = 1; i <= pages; i++)
        {
            var page = builder.AddPage(595, 842);
            if (i != blankPage)
                page.AddText(prefix + "_PAGE_" + i.ToString("D4")
                    + " Diagnostic calibration procedure from page " + i, 12,
                    new PdfPoint(45, 740), font);
        }
        string path = Path.Combine(Docs, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private sealed class ImmediateProgress : IProgress<IndexProgress>
    {
        private readonly Action<IndexProgress> _callback;
        public ImmediateProgress(Action<IndexProgress> callback) => _callback = callback;
        public void Report(IndexProgress progress) => _callback(progress);
    }

    private static int StagedRows(string dbPath, string table)
    {
        using var db = new SqliteConnection("Data Source=" + dbPath);
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Paged_PDF_indexes_last_page_beyond_Eco_text_cap_with_exact_citation()
    {
        MakePdf("huge-manual.pdf", 48, "ECU_AA", blankPage: 7);
        using KnowledgeIndex index = Index();
        var seen = new List<IndexProgress>();
        IndexProgress status = await index.IndexFolderAsync("corporate", Docs,
            new ImmediateProgress(seen.Add));

        Assert.Equal(1, status.Indexed);
        Assert.True(index.Stats("corporate").chunks > 30);
        KnowledgeHit match = Assert.Single(index.Search("ECU_AA_PAGE_0048", KnowledgeSearchMode.Exact));
        Assert.Equal(48, match.Position.Page);
        Assert.Contains("p. 48", match.Citation);
        Assert.Contains(seen, p => p.CurrentPage == 16 && p.TotalPages == 48);
        Assert.Contains(seen, p => p.CurrentPage == 32);
        Assert.Contains(index.Problems("corporate"), p => p.Kind == "partial" && p.Reason.Contains("1 PDF page(s)"));
        Assert.Equal(0, StagedRows(Database, "kn_pdf_progress"));
        Assert.Equal(0, StagedRows(Database, "kn_pdf_staging"));
    }

    [Fact]
    public async Task Cancelled_large_PDF_resumes_without_exposing_partial_pages_to_search()
    {
        MakePdf("resume.pdf", 35, "CONTROL_OLD");
        using KnowledgeIndex index = Index();
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(p =>
        {
            if (p.CurrentPage == 16) cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.IndexFolderAsync("corporate", Docs, progress, null, cancel.Token));
        Assert.Equal(1, StagedRows(Database, "kn_pdf_progress"));
        Assert.True(StagedRows(Database, "kn_pdf_staging") > 0);
        Assert.Empty(index.Search("CONTROL_OLD_PAGE_0016"));

        IndexProgress resumed = await index.IndexFolderAsync("corporate", Docs);
        Assert.Equal(1, resumed.Indexed);
        Assert.Equal(35, Assert.Single(index.Search("CONTROL_OLD_PAGE_0035")).Position.Page);
        Assert.Equal(0, StagedRows(Database, "kn_pdf_progress"));
    }

    [Fact]
    public async Task Enabling_opt_in_OCR_reindexes_only_missing_pages_with_exact_source_positions()
    {
        string path = MakePdf("mixed.pdf", 35, "MIXED_ECU", blankPage: 7);
        using (KnowledgeIndex initial = Index())
        {
            await initial.IndexFolderAsync("corporate", Docs);
            Assert.Empty(initial.Search("OCR_SERVICE_PAGE_0007"));
            Assert.Contains(initial.Problems("corporate"),
                p => p.Kind == "partial" && p.Reason.Contains("1 PDF page(s)"));
        }
        using KnowledgeIndex withOcr = Index(new ControlledOcr());
        IndexProgress updated = await withOcr.IndexFolderAsync("corporate", Docs);
        Assert.Equal(1, updated.Indexed);
        KnowledgeHit citation = Assert.Single(
            withOcr.Search("OCR_SERVICE_PAGE_0007", KnowledgeSearchMode.Exact));
        Assert.Equal(7, citation.Position.Page);
        Assert.DoesNotContain(withOcr.Problems("corporate"),
            p => p.Kind == "partial");
        Assert.True(File.Exists(path));
    }

    private sealed class GamingProbeOcr : IOcrEngine
    {
        public string Name => "gaming-probe-ocr";
        public OcrAvailability CheckAvailability()
        {
            var refusal = new UnavailableDuringGamingException("Gaming Mode is active, so local ocr work is unavailable.");
            throw new OperationCanceledException(refusal.Message, refusal);
        }
        public OcrPageResult RecognizeImage(string path, string? languageTag, CancellationToken ct) => throw new InvalidOperationException();
        public IReadOnlyList<OcrPageResult> RecognizePdfPages(string path, IReadOnlyList<int> pages, string? languageTag, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_Gaming_refused_OCR_probe_cancels_the_run_and_the_pdf_is_retried_with_OCR_afterwards()
    {
        MakePdf("probed-in-transition.pdf", 35, "GAME_ECU", blankPage: 7);
        using (KnowledgeIndex interrupted = Index(new GamingProbeOcr()))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.IndexFolderAsync("corporate", Docs));
            // Never staged without OCR: no page was indexed as "no text" and the document is not reported complete.
            Assert.Empty(interrupted.Search("GAME_ECU_PAGE_0035"));
        }
        using KnowledgeIndex resumed = Index(new ControlledOcr());
        await resumed.IndexFolderAsync("corporate", Docs);
        Assert.Equal(7, Assert.Single(resumed.Search("OCR_SERVICE_PAGE_0007", KnowledgeSearchMode.Exact)).Position.Page);
    }

    [Fact]
    public void The_Windows_OCR_probe_is_refused_uncached_while_Gaming_is_active_and_the_refusal_is_recognised()
    {
        bool gaming = true;
        using var scope = new GamingChildScope("ocr", () => gaming, null);
        var engine = new WindowsOcrEngine();
        using (GamingChildGate.UseForCurrentFlow(scope))
        {
            OperationCanceledException first = Assert.Throws<OperationCanceledException>(() => engine.CheckAvailability());
            Assert.True(WindowsOcrEngine.IsGamingRefusal(first));
            Assert.IsType<UnavailableDuringGamingException>(first.InnerException);
            // Not cached as "unavailable": the next probe is refused for the same reason, not answered from a stale result.
            Assert.Throws<OperationCanceledException>(() => engine.CheckAvailability());
        }
        Assert.False(WindowsOcrEngine.IsGamingRefusal(new InvalidOperationException()));
    }

    [Fact]
    public async Task Changing_OCR_setting_discards_incompatible_incomplete_pdf_checkpoint()
    {
        MakePdf("mixed-pending.pdf", 35, "MIXED_ECU", blankPage: 7);
        using var cancel = new CancellationTokenSource();
        using (KnowledgeIndex initial = Index())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                initial.IndexFolderAsync("corporate", Docs, new ImmediateProgress(p =>
                {
                    if (p.CurrentPage == 16) cancel.Cancel();
                }), null, cancel.Token));
        }
        Assert.Equal(1, StagedRows(Database, "kn_pdf_progress"));
        using KnowledgeIndex reconfigured = Index(new ControlledOcr());
        await reconfigured.IndexFolderAsync("corporate", Docs);
        Assert.Equal(7, Assert.Single(reconfigured.Search(
            "OCR_SERVICE_PAGE_0007", KnowledgeSearchMode.Exact)).Position.Page);
        Assert.Equal(0, StagedRows(Database, "kn_pdf_progress"));
    }

    [Fact]
    public async Task Existing_complete_corpus_stays_visible_until_modified_manual_is_committed()
    {
        string path = MakePdf("revision.pdf", 35, "OLD_DID");
        using KnowledgeIndex index = Index();
        await index.IndexFolderAsync("corporate", Docs);
        Assert.NotEmpty(index.Search("OLD_DID_PAGE_0035"));

        DateTime modified = File.GetLastWriteTimeUtc(path);
        MakePdf("revision.pdf", 35, "NEW_DID");
        File.SetLastWriteTimeUtc(path, modified.AddSeconds(5));

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.IndexFolderAsync("corporate", Docs, new ImmediateProgress(p =>
            {
                if (p.CurrentPage == 16) cancellation.Cancel();
            }), null, cancellation.Token));
        Assert.NotEmpty(index.Search("OLD_DID_PAGE_0035"));
        Assert.Empty(index.Search("NEW_DID_PAGE_0035"));

        await index.IndexFolderAsync("corporate", Docs);
        Assert.Empty(index.Search("OLD_DID_PAGE_0035"));
        Assert.Equal(35, Assert.Single(index.Search("NEW_DID_PAGE_0035")).Position.Page);
    }

    [Fact]
    public async Task Source_removal_scrubs_incomplete_staged_PDF_text_without_touching_original()
    {
        string path = MakePdf("staged.pdf", 35, "PRIVATE_ECU");
        using KnowledgeIndex index = Index();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.IndexFolderAsync("corporate", Docs, new ImmediateProgress(p =>
            {
                if (p.CurrentPage == 16) cancellation.Cancel();
            }), null, cancellation.Token));
        Assert.True(StagedRows(Database, "kn_pdf_staging") > 0);
        index.RemoveSource("corporate");
        Assert.Equal(0, StagedRows(Database, "kn_pdf_staging"));
        Assert.Equal(0, StagedRows(Database, "kn_pdf_progress"));
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
