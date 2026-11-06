using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Out-of-core, checkpointed text-layer PDF ingestion for giant technical manuals.
/// A small page batch is committed to a private SQLite staging area; it is never
/// visible to search. When every page has been read and the file identity is
/// unchanged, staged content atomically replaces the previous searchable index.
/// On cancellation or crash, the next scan resumes at the last committed page.
/// The user's PDF is always read-only; scanned pages need a separate OCR pass.
/// </summary>
public sealed partial class KnowledgeIndex
{
    private const int PdfPagesPerCheckpoint = 16;
    private const int PdfChunksPerPromotion = 256;
    private const int PdfMaximumCharactersPerPage = 250_000;

    private sealed record PdfCheckpoint(int NextPage, int NextOrdinal, int EmptyPages);
    private sealed record PdfStagedChunk(int Ordinal, int Page, long Offset, string Text, float[]? Vector);
    private sealed record LargePdfIndexResult(int TotalPages, int EmptyPages);

    private static bool IsLargePdf(string file, FileInfo info, int pageThreshold)
    {
        if (!Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        if (info.Length > 64L * 1024 * 1024) return true;
        try
        {
            using PdfDocument pdf = PdfDocument.Open(file);
            return pdf.NumberOfPages >= Math.Max(2, pageThreshold);
        }
        catch { return false; } // Let the usual extractor report an unreadable PDF.
    }

    private PdfCheckpoint GetOrCreatePdfCheckpoint(string sourceId, string path, string hash, int pages, bool ocrMode)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteTransaction tx = db.BeginTransaction();
            using SqliteCommand read = db.CreateCommand();
            read.Transaction = tx;
            read.CommandText = @"SELECT hash,total_pages,next_page,next_ord,empty_pages,ocr_mode
FROM kn_pdf_progress WHERE source_id=$s AND path=$p;";
            read.Parameters.AddWithValue("$s", sourceId);
            read.Parameters.AddWithValue("$p", path);
            string? previousHash = null;
            int previousTotal = 0;
            PdfCheckpoint? checkpoint = null;
            using (SqliteDataReader cursor = read.ExecuteReader())
            {
                if (cursor.Read())
                {
                    previousHash = cursor.GetString(0);
                    previousTotal = cursor.GetInt32(1);
                    checkpoint = new PdfCheckpoint(cursor.GetInt32(2), cursor.GetInt32(3), cursor.GetInt32(4));
                }
            }
            bool previousOcr = false;
            // An old checkpoint cannot be reused with different OCR settings:
            // otherwise skipped pages would silently persist after enabling OCR.
            if (checkpoint != null)
                previousOcr = CheckStageOcrMode(db, tx, sourceId, path);
            if (checkpoint != null && previousHash == hash && previousTotal == pages
                && previousOcr == ocrMode)
            {
                tx.Commit();
                return checkpoint;
            }
            // A changed PDF invalidates staged bytes, never the previously
            // published version in kn_files.
            ExecPdf(db, tx, "DELETE FROM kn_pdf_staging WHERE source_id=$s AND path=$p;", sourceId, path);
            ExecPdf(db, tx, "DELETE FROM kn_pdf_progress WHERE source_id=$s AND path=$p;", sourceId, path);
            ExecPdf(db, tx, @"INSERT INTO kn_pdf_progress
(source_id,path,hash,total_pages,next_page,next_ord,empty_pages,ocr_mode)
VALUES($s,$p,$h,$n,1,0,0," + (ocrMode ? "1" : "0") + ");", sourceId, path, hash, pages);
            tx.Commit();
            return new PdfCheckpoint(1, 0, 0);
        }
    }

    private static bool CheckStageOcrMode(SqliteConnection db, SqliteTransaction tx,
        string sourceId, string path)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT ocr_mode FROM kn_pdf_progress WHERE source_id=$s AND path=$p;";
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$p", path);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void ExecPdf(SqliteConnection db, SqliteTransaction tx, string sql,
        string sourceId, string path, string? hash = null, int pages = 0)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$p", path);
        if (hash != null) command.Parameters.AddWithValue("$h", hash);
        if (sql.Contains("$n", StringComparison.Ordinal)) command.Parameters.AddWithValue("$n", pages);
        command.ExecuteNonQuery();
    }

    private PdfCheckpoint StagePdfPages(string sourceId, string path,
        PdfCheckpoint previous, int nextPage, int emptyInBatch, List<(int page, long offset, string text)> passages)
    {
        IReadOnlyList<float[]?> vectors = Embed(passages.Select(p => p.text).ToList());
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteTransaction tx = db.BeginTransaction();
            using SqliteCommand insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO kn_pdf_staging
(source_id,path,ord,text,page,char_offset,embedding)
VALUES($s,$p,$o,$t,$pg,$co,$e);";
            insert.Parameters.AddWithValue("$s", sourceId);
            insert.Parameters.AddWithValue("$p", path);
            var ord = insert.Parameters.Add("$o", SqliteType.Integer);
            var text = insert.Parameters.Add("$t", SqliteType.Text);
            var page = insert.Parameters.Add("$pg", SqliteType.Integer);
            var offset = insert.Parameters.Add("$co", SqliteType.Integer);
            var embedding = insert.Parameters.Add("$e", SqliteType.Blob);
            for (int i = 0; i < passages.Count; i++)
            {
                ord.Value = previous.NextOrdinal + i;
                text.Value = passages[i].text;
                page.Value = passages[i].page;
                offset.Value = passages[i].offset;
                embedding.Value = i < vectors.Count && vectors[i] != null
                    ? ToBlob(vectors[i]!) : DBNull.Value;
                insert.ExecuteNonQuery();
            }
            using SqliteCommand advance = db.CreateCommand();
            advance.Transaction = tx;
            advance.CommandText = @"UPDATE kn_pdf_progress SET next_page=$n,next_ord=$o,empty_pages=$e
WHERE source_id=$s AND path=$p;";
            advance.Parameters.AddWithValue("$n", nextPage);
            advance.Parameters.AddWithValue("$o", previous.NextOrdinal + passages.Count);
            advance.Parameters.AddWithValue("$e", previous.EmptyPages + emptyInBatch);
            advance.Parameters.AddWithValue("$s", sourceId);
            advance.Parameters.AddWithValue("$p", path);
            if (advance.ExecuteNonQuery() != 1)
                throw new InvalidDataException("Large PDF staging identity disappeared while indexing.");
            tx.Commit();
            return new PdfCheckpoint(nextPage, previous.NextOrdinal + passages.Count,
                previous.EmptyPages + emptyInBatch);
        }
    }

    private LargePdfIndexResult IndexLargePdf(
        string sourceId, string path, FileInfo info, long ticks, string hash,
        long? duplicateOf, long? existingId, IndexControl? control,
        IProgress<IndexProgress>? progress, Func<string, string?, IndexProgress> snap,
        ExtractionLimits limits, bool ocrMode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using PdfDocument pdf = PdfDocument.Open(path);
        int totalPages = pdf.NumberOfPages;
        if (totalPages == 0)
            throw new InvalidDataException("Large PDF contains no pages.");
        PdfCheckpoint state = GetOrCreatePdfCheckpoint(sourceId, path, hash, totalPages, ocrMode);
        var pending = new List<(int page, long offset, string text)>();
        var blankPages = new List<int>();
        int processed = 0, currentPage = state.NextPage - 1;

        // OCR stays strictly opt-in. Only pages whose embedded text layer is
        // empty are rendered and recognised; each Windows OCR call is bounded
        // to at most 16 pages and a documented timeout. No 30k-page list or
        // per-page bitmap is ever retained in managed memory.
        IOcrEngine? ocr = limits.Ocr;
        try
        {
            if (ocr != null && !ocr.CheckAvailability().Available) ocr = null;
        }
        catch (Exception) { ocr = null; } // Bad OCR availability cannot block readable PDF text.
        KnowledgeResourcePlan? resourcePlan = _options.Plan;
        var memoryBudget = resourcePlan == null ? null
            : new MemoryBudgetGuard(resourcePlan.ManagedMemoryBudgetBytes);
        long batchStarted = Stopwatch.GetTimestamp();

        void FlushCheckpoint()
        {
            ct.ThrowIfCancellationRequested();
            int stillBlank = blankPages.Count;
            if (ocr != null && blankPages.Count > 0)
            {
                try
                {
                    IReadOnlyList<OcrPageResult> recognized = ocr.RecognizePdfPages(
                        path, blankPages, limits.OcrLanguage, ct);
                    var byPage = recognized.GroupBy(r => r.Page)
                        .ToDictionary(g => g.Key, g => g.First());
                    foreach (int number in blankPages)
                    {
                        if (!byPage.TryGetValue(number, out OcrPageResult? result) || !result.Ok
                            || string.IsNullOrWhiteSpace(result.Text)) continue;
                        string text = result.Text.Trim();
                        if (text.Length > PdfMaximumCharactersPerPage)
                            throw new InvalidDataException("OCR text of PDF page " + number
                                + " is larger than the per-page memory limit.");
                        foreach ((string chunk, long offset) in Split(text))
                            pending.Add((number, offset, chunk));
                        stillBlank--;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (Exception)
                {
                    // The index must never invent OCR content or report a page
                    // as covered if Windows OCR crashed or timed out. Every
                    // affected page contributes to the documented gap count.
                    stillBlank = blankPages.Count;
                }
            }
            pending.Sort((a, b) => a.page != b.page
                ? a.page.CompareTo(b.page) : a.offset.CompareTo(b.offset));
            state = StagePdfPages(sourceId, path, state, currentPage + 1, stillBlank, pending);
            pending.Clear();
            blankPages.Clear();
            processed = 0;
            progress?.Report(snap(control?.IsPaused == true ? "paused" : "running",
                Path.GetFileName(path) + " · PDF page " + currentPage + "/" + totalPages)
                with { CurrentPage = currentPage, TotalPages = totalPages });
            // A 30k-page PDF is effectively thousands of small jobs. Apply the
            // Corporate Eco/Balanced duty cycle AND memory headroom between
            // checkpoints, not merely between different filenames.
            if (resourcePlan is { DutyCycle: > 0 and < 1 })
            {
                double usedMs = Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds;
                int restMs = (int)Math.Min(2000,
                    usedMs * (1.0 / resourcePlan.DutyCycle - 1.0));
                if (restMs > 0) Task.Delay(restMs, ct).GetAwaiter().GetResult();
            }
            memoryBudget?.WaitForHeadroomAsync(ct).GetAwaiter().GetResult();
            batchStarted = Stopwatch.GetTimestamp();
        }

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (page.Number < state.NextPage) continue;
            control?.WaitIfPausedAsync(ct).GetAwaiter().GetResult();
            currentPage = page.Number;
            string extracted;
            try { extracted = ContentOrderTextExtractor.GetText(page).Trim(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { extracted = page.Text.Trim(); }
                catch { extracted = string.Empty; }
            }
            if (extracted.Length > PdfMaximumCharactersPerPage)
                throw new InvalidDataException("PDF page " + currentPage
                    + " has more than 250,000 extracted characters. Content was not silently truncated.");
            if (extracted.Length == 0) blankPages.Add(currentPage);
            else
                foreach ((string text, long offset) in Split(extracted))
                    pending.Add((currentPage, offset, text));
            processed++;
            if (processed == PdfPagesPerCheckpoint) FlushCheckpoint();
        }
        if (processed > 0) FlushCheckpoint();
        if (state.NextPage != totalPages + 1)
            throw new InvalidDataException("PDF page extraction stopped before the final page. The checkpoint was retained.");
        ct.ThrowIfCancellationRequested();

        // Detect replacement/edit during the many-minute extraction before any
        // previously searchable document content is removed.
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            string after = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, after, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PDF changed during indexing. Restart to discard stale page checkpoints.");
        }
        ct.ThrowIfCancellationRequested();
        // The document is read, but SQLite FTS/trigram publication can still
        // take time for millions of chunks. A 100% page bar is NOT the same
        // as a fully searchable index: show a truthful indeterminate phase.
        progress?.Report(snap("running", Path.GetFileName(path)
            + " · publishing verified search index (previous complete version remains searchable)"));
        PromotePdf(sourceId, path, info, ticks, hash, duplicateOf, existingId, state, totalPages, ocrMode, ct);
        return new LargePdfIndexResult(totalPages, state.EmptyPages);
    }

    private void PromotePdf(string sourceId, string path, FileInfo info, long ticks,
        string hash, long? duplicateOf, long? existingId, PdfCheckpoint state,
        int totalPages, bool ocrMode, CancellationToken ct)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteTransaction tx = db.BeginTransaction();
            // This single commit keeps old content searchable until the complete
            // replacement (chunks AND FTS) is durable.
            if (existingId.HasValue)
            {
                DeleteChunks(db, tx, existingId.Value);
                using SqliteCommand del = db.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM kn_files WHERE id=$i;";
                del.Parameters.AddWithValue("$i", existingId.Value);
                del.ExecuteNonQuery();
            }
            string note = state.EmptyPages == 0
                ? "All " + totalPages + " page(s) have an extracted text layer or verified local OCR text."
                : "PDF indexed across " + totalPages + " pages, but " + state.EmptyPages
                    + " page(s) have no readable text even after available opt-in OCR."
                    + " Those pages are NOT searchable; no technical conclusion is inferred from them.";
            using SqliteCommand insertFile = db.CreateCommand();
            insertFile.Transaction = tx;
            insertFile.CommandText = @"INSERT INTO kn_files
(source_id,path,name,size,mtime_ticks,hash,tier,extractor,note,duplicate_of,indexed_utc,ocr_mode)
VALUES($s,$p,$n,$z,$m,$h,$t,'pdf-paged',$o,$d,$u,$ocr);
SELECT last_insert_rowid();";
            insertFile.Parameters.AddWithValue("$s", sourceId);
            insertFile.Parameters.AddWithValue("$p", path);
            insertFile.Parameters.AddWithValue("$n", Path.GetFileName(path));
            insertFile.Parameters.AddWithValue("$z", info.Length);
            insertFile.Parameters.AddWithValue("$m", ticks);
            insertFile.Parameters.AddWithValue("$h", hash);
            insertFile.Parameters.AddWithValue("$t", state.NextOrdinal > 0 ? "Structured" : "MetadataOnly");
            insertFile.Parameters.AddWithValue("$o", note);
            insertFile.Parameters.AddWithValue("$d", (object?)duplicateOf ?? DBNull.Value);
            insertFile.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
            insertFile.Parameters.AddWithValue("$ocr", ocrMode ? 1 : 0);
            long fileId = (long)insertFile.ExecuteScalar()!;
            int ord = 0;
            InsertPdfIndexedChunk(db, tx, fileId, ord++, "File: " + Path.GetFileName(path),
                new DocumentPosition(Heading: "file name"), null);
            int last = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var slice = new List<PdfStagedChunk>(PdfChunksPerPromotion);
                using (SqliteCommand select = db.CreateCommand())
                {
                    select.Transaction = tx;
                    select.CommandText = @"SELECT ord,text,page,char_offset,embedding FROM kn_pdf_staging
WHERE source_id=$s AND path=$p AND ord>$last ORDER BY ord LIMIT $n;";
                    select.Parameters.AddWithValue("$s", sourceId);
                    select.Parameters.AddWithValue("$p", path);
                    select.Parameters.AddWithValue("$last", last);
                    select.Parameters.AddWithValue("$n", PdfChunksPerPromotion);
                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                        slice.Add(new PdfStagedChunk(reader.GetInt32(0), reader.GetInt32(2),
                            reader.GetInt64(3), reader.GetString(1),
                            reader.IsDBNull(4) ? null : FromBlob((byte[])reader.GetValue(4))));
                }
                if (slice.Count == 0) break;
                foreach (PdfStagedChunk chunk in slice)
                {
                    ct.ThrowIfCancellationRequested();
                    InsertPdfIndexedChunk(db, tx, fileId, ord++, chunk.Text,
                        new DocumentPosition(Page: chunk.Page, CharOffset: chunk.Offset),
                        chunk.Vector);
                    last = chunk.Ordinal;
                }
            }
            ExecPdf(db, tx, "DELETE FROM kn_pdf_staging WHERE source_id=$s AND path=$p;", sourceId, path);
            ExecPdf(db, tx, "DELETE FROM kn_pdf_progress WHERE source_id=$s AND path=$p;", sourceId, path);
            tx.Commit();
        }
    }

    private void InsertPdfIndexedChunk(SqliteConnection db, SqliteTransaction tx,
        long fileId, int ordinal, string text, DocumentPosition pos, float[]? vector)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"INSERT INTO kn_chunks
(file_id,ord,text,heading,page,sheet,cell_range,slide,char_offset,embedding)
VALUES($f,$o,$t,$h,$pg,NULL,NULL,NULL,$offset,$v);
SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$f", fileId);
        command.Parameters.AddWithValue("$o", ordinal);
        command.Parameters.AddWithValue("$t", text);
        command.Parameters.AddWithValue("$h", (object?)pos.Heading ?? DBNull.Value);
        command.Parameters.AddWithValue("$pg", (object?)pos.Page ?? DBNull.Value);
        command.Parameters.AddWithValue("$offset", pos.CharOffset);
        command.Parameters.AddWithValue("$v", vector == null ? DBNull.Value : ToBlob(vector));
        long id = (long)command.ExecuteScalar()!;
        if (!_hasFts5) return;
        using SqliteCommand lex = db.CreateCommand();
        lex.Transaction = tx;
        lex.CommandText = "INSERT INTO kn_fts(rowid,text,heading) VALUES($i,$t,$h);";
        lex.Parameters.AddWithValue("$i", id);
        lex.Parameters.AddWithValue("$t", text);
        lex.Parameters.AddWithValue("$h", (object?)pos.Heading ?? DBNull.Value);
        lex.ExecuteNonQuery();
        using SqliteCommand trigram = db.CreateCommand();
        trigram.Transaction = tx;
        trigram.CommandText = "INSERT INTO kn_tri(rowid,text) VALUES($i,$t);";
        trigram.Parameters.AddWithValue("$i", id);
        trigram.Parameters.AddWithValue("$t", text);
        trigram.ExecuteNonQuery();
    }
}
