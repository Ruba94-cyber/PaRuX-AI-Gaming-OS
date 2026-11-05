using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PaRuX.Server.Knowledge;

public enum KnowledgeSearchMode { Auto, Exact, Lexical, Semantic, Hybrid }

public sealed record KnowledgeHit(long ChunkId, string FilePath, string FileName, string Text, DocumentPosition Position, double Score, string Match)
{
    /// <summary>"Report.pdf, p. 4" - what a citation shows.</summary>
    public string Citation => FileName + ", " + Position.Describe();
}

public sealed record IndexProgress(
    string State, int FilesSeen, int Indexed, int Unchanged, int MetadataOnly, int Failed, int Skipped,
    int Removed, int Renamed, int Duplicates, string? Current);

/// <summary>A file that could not be indexed. Holds a relative path and a reason, never document content.</summary>
public sealed record IndexFileProblem(string RelativePath, string Kind, string Reason);

/// <summary>One indexed file (identity for the chat skills).</summary>
public sealed record IndexedFile(long Id, string SourceId, string Path, string Name, string Hash, long MtimeTicks, long Size)
{
    public string ShortHash => Hash.Length >= 8 ? Hash[..8].ToLowerInvariant() : Hash.ToLowerInvariant();
}

public sealed record IndexedPassage(string Text, DocumentPosition Position);

/// <summary>What a secure deletion removed and whether the database file(s) were scrubbed/removed.</summary>
public sealed record SecureDeletionResult(int FilesRemoved, int ChunksRemoved, bool Vacuumed, bool FilesRemovedOnDisk);

public sealed class KnowledgeIndexOptions
{
    public string[] ExcludedFolderNames { get; init; } =
        { ".git", ".svn", ".hg", "node_modules", "$RECYCLE.BIN", "System Volume Information", ".vs", "__pycache__", "Temp", "tmp", ".cache" };
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int ChunkChars { get; init; } = 900;
    public int ChunkOverlap { get; init; } = 150;
    /// <summary>Embed chunks at index time when an embedding provider is available (semantic search needs them).</summary>
    public bool EmbedChunks { get; init; } = true;
    public int EmbedBatchSize { get; init; } = 16;
    /// <summary>Pause between files so indexing stays light on small PCs (0 = none).</summary>
    public int ThrottleMilliseconds { get; init; }
    public ExtractionLimits Limits { get; init; } = ExtractionLimits.Default;
    /// <summary>Resource plan (profile): duty-cycle throttle, worker priority, memory budget. Null = unthrottled (tests, Performance).</summary>
    public KnowledgeResourcePlan? Plan { get; init; }
    /// <summary>Folders never entered while walking (PaRuX's own data root by default), even when a parent folder is indexed.</summary>
    public string[] ProtectedFolders { get; init; } = { PaRuX.Contracts.PaRuXDataRoot.Path };
}

/// <summary>Pause/resume handle for a running index job.</summary>
public sealed class IndexControl
{
    private volatile bool _paused;
    public bool IsPaused => _paused;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    internal async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (_paused) await Task.Delay(100, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Local document index with positions: SQLite + FTS5 (word index tuned for identifiers, plus a trigram index for partial
/// identifiers), incremental and resumable indexing, and a query router that prefers exact matches for technical codes.
/// It never needs a generative model; embeddings are optional and only add semantic ranking. Documents are only read.
/// The text is stored once (kn_chunks); both FTS tables are external-content views of it.
/// </summary>
public sealed class KnowledgeIndex : IDisposable
{
    private readonly string _dbPath;
    private volatile KnowledgeIndexOptions _options;
    private readonly IRetrievalEmbeddingProvider? _embedder;
    private readonly object _gate = new();
    private bool _hasFts5;
    private bool _ready;

    public KnowledgeIndex(string dbPath, KnowledgeIndexOptions? options = null, IRetrievalEmbeddingProvider? embedder = null)
    {
        _dbPath = dbPath;
        _options = options ?? new KnowledgeIndexOptions();
        _embedder = embedder;
    }

    /// <summary>Replaces the options used by the next indexing run (settings or profile changed).</summary>
    public void UpdateOptions(KnowledgeIndexOptions options) => _options = options;

    public bool HasFts5 { get { EnsureSchema(); return _hasFts5; } }
    public void Dispose() { }

    private SqliteConnection Open()
    {
        string? dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            if (_ready) return;
            using SqliteConnection c = Open();
            string? protectedDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(protectedDir)) LocalFileProtection.RestrictToCurrentUser(protectedDir);   // index text is as sensitive as the documents
            Exec(c, @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS kn_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT NOT NULL, path TEXT NOT NULL, name TEXT NOT NULL,
    size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, hash TEXT NOT NULL, tier TEXT NOT NULL, extractor TEXT,
    note TEXT, duplicate_of INTEGER, indexed_utc TEXT NOT NULL, UNIQUE(source_id, path));
CREATE INDEX IF NOT EXISTS ix_kn_files_hash ON kn_files(source_id, hash);
CREATE TABLE IF NOT EXISTS kn_chunks (
    id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, ord INTEGER NOT NULL, text TEXT NOT NULL, heading TEXT,
    page INTEGER, sheet TEXT, cell_range TEXT, slide INTEGER, char_offset INTEGER NOT NULL DEFAULT 0, embedding BLOB);
CREATE INDEX IF NOT EXISTS ix_kn_chunks_file ON kn_chunks(file_id);
CREATE TABLE IF NOT EXISTS kn_problems (source_id TEXT NOT NULL, rel_path TEXT NOT NULL, kind TEXT NOT NULL, reason TEXT NOT NULL);");
            try
            {
                Exec(c, @"
CREATE VIRTUAL TABLE IF NOT EXISTS kn_fts USING fts5(text, heading, content='kn_chunks', content_rowid='id', tokenize=""unicode61 tokenchars '-_.'"");
CREATE VIRTUAL TABLE IF NOT EXISTS kn_tri USING fts5(text, content='kn_chunks', content_rowid='id', tokenize='trigram');");
                _hasFts5 = true;
            }
            catch (SqliteException) { _hasFts5 = false; }   // scan fallback is used for search
            _ready = true;
        }
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    // ------------------------------------------------------------------------------------------------------ indexing

    public async Task<IndexProgress> IndexFolderAsync(string sourceId, string folder, IProgress<IndexProgress>? progress = null,
        IndexControl? control = null, CancellationToken ct = default)
    {
        EnsureSchema();
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Folder not found.");

        int seen = 0, indexed = 0, unchanged = 0, metaOnly = 0, failed = 0, skipped = 0, removed = 0, renamed = 0, dupes = 0;
        IndexProgress Snap(string state, string? current = null) =>
            new(state, seen, indexed, unchanged, metaOnly, failed, skipped, removed, renamed, dupes, current);

        KnowledgeResourcePlan? plan = _options.Plan;
        long maxFileBytes = plan == null ? _options.MaxFileBytes : Math.Min(_options.MaxFileBytes, plan.MaxFileBytes);
        ExtractionLimits limits = plan == null ? _options.Limits : _options.Limits with { MaxCharacters = Math.Min(_options.Limits.MaxCharacters, plan.MaxCharacters) };
        ThreadPriority priority = plan?.WorkerPriority ?? ThreadPriority.Normal;
        var memory = plan == null ? null : new MemoryBudgetGuard(plan.ManagedMemoryBudgetBytes);
        var skippedByPolicy = new List<(string rel, string reason)>();
        var failedDirs = new List<string>();
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        Walk(root, root, files, failedDirs, skippedByPolicy, ct);
        foreach (string f in files) onDisk.Add(f);

        lock (_gate) { using SqliteConnection c = Open(); Exec(c, "DELETE FROM kn_problems WHERE source_id=$s;", ("$s", sourceId)); }
        foreach (string dir in failedDirs) AddProblem(sourceId, Rel(root, dir), "folder", "Folder is not accessible; its files were left as they were.");
        foreach ((string rel, string reason) in skippedByPolicy) { skipped++; AddProblem(sourceId, rel, "skipped(policy)", reason); }

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (control != null) await control.WaitIfPausedAsync(ct).ConfigureAwait(false);
            seen++;
            progress?.Report(Snap(control?.IsPaused == true ? "paused" : "running", Path.GetFileName(file)));
            int fixedPause = plan?.ThrottleMilliseconds ?? _options.ThrottleMilliseconds;
            if (fixedPause > 0) await Task.Delay(fixedPause, ct).ConfigureAwait(false);
            if (memory != null) await memory.WaitForHeadroomAsync(ct).ConfigureAwait(false);
            long workStart = Stopwatch.GetTimestamp();

            FileInfo info;
            try { info = new FileInfo(file); if (!info.Exists) { skipped++; continue; } }
            catch (Exception) { failed++; AddProblem(sourceId, Rel(root, file), "access", "File is not accessible."); continue; }
            if (info.Length > maxFileBytes) { skipped++; AddProblem(sourceId, Rel(root, file), "skipped", "Larger than the configured size limit."); continue; }

            long ticks = info.LastWriteTimeUtc.Ticks;
            (long id, long size, long mtime, string hash)? existing = Lookup(sourceId, file);
            if (existing is { } e && e.size == info.Length && e.mtime == ticks) { unchanged++; continue; }

            string hash;
            try { hash = await HashAsync(file, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failed++; AddProblem(sourceId, Rel(root, file), "access", "File could not be read (in use or no permission)."); continue; }

            if (existing is { } same && same.hash == hash) { Touch(same.id, info.Length, ticks); unchanged++; continue; }

            // Same bytes under another path: a rename if the old path is gone, a duplicate otherwise.
            (long id, string path)? twin = FindByHash(sourceId, hash, file);
            long? duplicateOf = null;
            if (twin is { } t)
            {
                if (!File.Exists(t.path) && existing == null)
                {
                    Rename(t.id, file, info.Length, ticks);
                    renamed++;
                    continue;
                }
                duplicateOf = t.id;
                dupes++;
            }

            ExtractedDocument doc = await LowPriorityRunner.Run(() => StructuredDocumentExtractor.Extract(file, limits, ct), priority, ct).ConfigureAwait(false);
            if (doc.Tier == DocumentTier.Failed) { failed++; AddProblem(sourceId, Rel(root, file), "failed", doc.Note ?? "Could not be read."); }
            else if (doc.Tier == DocumentTier.MetadataOnly) { metaOnly++; if (doc.Note != null) AddProblem(sourceId, Rel(root, file), "metadata-only", doc.Note); }
            else { indexed++; if (doc.Note != null) AddProblem(sourceId, Rel(root, file), "partial", doc.Note); }
            if (doc.Truncated) AddProblem(sourceId, Rel(root, file), "truncated", "Read up to the configured limit; the rest is not indexed.");

            Store(sourceId, file, info, ticks, hash, doc, duplicateOf, existing?.id);
            // Duty cycle: after real work, rest for (1/duty - 1) times as long, so a light PC stays responsive.
            if (plan is { DutyCycle: > 0 and < 1 })
            {
                double workedMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
                int rest = (int)Math.Min(2000, workedMs * (1.0 / plan.DutyCycle - 1.0));
                if (rest > 0) await Task.Delay(rest, ct).ConfigureAwait(false);
            }
        }
        // Deleted files are removed after the walk so a rename is recognised first; an inaccessible folder is not a deletion.
        removed += RemoveMissing(sourceId, onDisk, failedDirs);
        IndexProgress final = Snap(ct.IsCancellationRequested ? "cancelled" : "done");
        progress?.Report(final);
        return final;
    }

    private void Walk(string root, string dir, List<string> files, List<string> failedDirs, List<(string rel, string reason)> policySkips, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (Exception) { failedDirs.Add(dir); return; }
        foreach (string entry in entries)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); } catch (Exception) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;           // never follow links out of the folder
            string name = Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (_options.ExcludedFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (_options.ProtectedFolders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)), StringComparison.OrdinalIgnoreCase)))
                { policySkips.Add((Rel(root, entry), "Skipped: PaRuX's own data folder.")); continue; }
                SensitiveFinding sensitiveDir = SensitivePathPolicy.EvaluateEntry(Rel(root, entry), true);
                if (sensitiveDir.Verdict == SensitiveVerdict.Blocked) { policySkips.Add((Rel(root, entry), sensitiveDir.Reason ?? "Skipped by policy.")); continue; }
                Walk(root, entry, files, failedDirs, policySkips, ct);
            }
            else
            {
                if (name.StartsWith("~$", StringComparison.Ordinal)) continue;       // Office lock files
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                SensitiveFinding sensitiveFile = SensitivePathPolicy.EvaluateEntry(Rel(root, entry), false);
                if (sensitiveFile.Verdict == SensitiveVerdict.Blocked) { policySkips.Add((Rel(root, entry), sensitiveFile.Reason ?? "Skipped by policy.")); continue; }
                files.Add(entry);
            }
        }
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private void AddProblem(string sourceId, string rel, string kind, string reason)
    {
        lock (_gate) { using SqliteConnection c = Open(); Exec(c, "INSERT INTO kn_problems VALUES($s,$p,$k,$r);", ("$s", sourceId), ("$p", rel), ("$k", kind), ("$r", reason)); }
    }

    private (long id, long size, long mtime, string hash)? Lookup(string sourceId, string path)
    {
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id,size,mtime_ticks,hash FROM kn_files WHERE source_id=$s AND path=$p;";
            cmd.Parameters.AddWithValue("$s", sourceId); cmd.Parameters.AddWithValue("$p", path);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)) : null;
        }
    }

    private (long id, string path)? FindByHash(string sourceId, string hash, string excludePath)
    {
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id,path FROM kn_files WHERE source_id=$s AND hash=$h AND path<>$p ORDER BY id LIMIT 1;";
            cmd.Parameters.AddWithValue("$s", sourceId); cmd.Parameters.AddWithValue("$h", hash); cmd.Parameters.AddWithValue("$p", excludePath);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
        }
    }

    private void Touch(long id, long size, long ticks)
    {
        lock (_gate) { using SqliteConnection c = Open(); Exec(c, "UPDATE kn_files SET size=$z, mtime_ticks=$m WHERE id=$i;", ("$z", size), ("$m", ticks), ("$i", id)); }
    }

    private void Rename(long id, string newPath, long size, long ticks)
    {
        lock (_gate)
        {
            using SqliteConnection c = Open();
            Exec(c, "UPDATE kn_files SET path=$p, name=$n, size=$z, mtime_ticks=$m WHERE id=$i;",
                ("$p", newPath), ("$n", Path.GetFileName(newPath)), ("$z", size), ("$m", ticks), ("$i", id));
        }
    }

    private int RemoveMissing(string sourceId, HashSet<string> onDisk, List<string> failedDirs)
    {
        int removed = 0;
        lock (_gate)
        {
            using SqliteConnection c = Open();
            var gone = new List<long>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id,path FROM kn_files WHERE source_id=$s;";
                cmd.Parameters.AddWithValue("$s", sourceId);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string path = r.GetString(1);
                    if (onDisk.Contains(path)) continue;
                    if (failedDirs.Any(d => path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                    gone.Add(r.GetInt64(0));
                }
            }
            foreach (long id in gone) { DeleteFile(c, id); removed++; }
        }
        return removed;
    }

    private void DeleteFile(SqliteConnection c, long fileId)
    {
        using SqliteTransaction tx = c.BeginTransaction();
        DeleteChunks(c, tx, fileId);
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM kn_files WHERE id=$i;";
        cmd.Parameters.AddWithValue("$i", fileId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private void DeleteChunks(SqliteConnection c, SqliteTransaction tx, long fileId)
    {
        if (_hasFts5)
        {
            var rows = new List<(long id, string text, string? heading)>();
            using (SqliteCommand read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT id,text,heading FROM kn_chunks WHERE file_id=$f;";
                read.Parameters.AddWithValue("$f", fileId);
                using SqliteDataReader r = read.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
            foreach ((long id, string text, string? heading) in rows)
            {
                using SqliteCommand d1 = c.CreateCommand();
                d1.Transaction = tx;
                d1.CommandText = "INSERT INTO kn_fts(kn_fts,rowid,text,heading) VALUES('delete',$i,$t,$h);";
                d1.Parameters.AddWithValue("$i", id); d1.Parameters.AddWithValue("$t", text); d1.Parameters.AddWithValue("$h", (object?)heading ?? DBNull.Value);
                d1.ExecuteNonQuery();
                using SqliteCommand d2 = c.CreateCommand();
                d2.Transaction = tx;
                d2.CommandText = "INSERT INTO kn_tri(kn_tri,rowid,text) VALUES('delete',$i,$t);";
                d2.Parameters.AddWithValue("$i", id); d2.Parameters.AddWithValue("$t", text);
                d2.ExecuteNonQuery();
            }
        }
        using SqliteCommand del = c.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM kn_chunks WHERE file_id=$f;";
        del.Parameters.AddWithValue("$f", fileId);
        del.ExecuteNonQuery();
    }

    private void Store(string sourceId, string path, FileInfo info, long ticks, string hash, ExtractedDocument doc, long? duplicateOf, long? existingId)
    {
        var chunks = new List<(string text, DocumentPosition pos)>();
        foreach (DocumentSegment segment in doc.Segments)
            foreach ((string text, long offset) in Split(segment.Text))
                chunks.Add((text, segment.Position with { CharOffset = segment.Position.CharOffset + offset }));
        IReadOnlyList<float[]?> vectors = Embed(chunks.Select(c => c.text).ToList());

        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteTransaction tx = c.BeginTransaction();
            if (existingId.HasValue) { DeleteChunks(c, tx, existingId.Value); using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM kn_files WHERE id=$i;"; del.Parameters.AddWithValue("$i", existingId.Value); del.ExecuteNonQuery(); }
            long fileId;
            using (SqliteCommand ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO kn_files(source_id,path,name,size,mtime_ticks,hash,tier,extractor,note,duplicate_of,indexed_utc)
VALUES($s,$p,$n,$z,$m,$h,$t,$e,$o,$d,$u); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$s", sourceId); ins.Parameters.AddWithValue("$p", path); ins.Parameters.AddWithValue("$n", Path.GetFileName(path));
                ins.Parameters.AddWithValue("$z", info.Length); ins.Parameters.AddWithValue("$m", ticks); ins.Parameters.AddWithValue("$h", hash);
                ins.Parameters.AddWithValue("$t", doc.Tier.ToString()); ins.Parameters.AddWithValue("$e", (object?)doc.Extractor ?? DBNull.Value);
                ins.Parameters.AddWithValue("$o", (object?)doc.Note ?? DBNull.Value); ins.Parameters.AddWithValue("$d", (object?)duplicateOf ?? DBNull.Value);
                ins.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
                fileId = (long)ins.ExecuteScalar()!;
            }
            // File name is searchable even for metadata-only files: it is stored as its own first chunk.
            var all = new List<(string text, DocumentPosition pos, float[]? vec)> { ("File: " + Path.GetFileName(path), new DocumentPosition(Heading: "file name"), null) };
            for (int i = 0; i < chunks.Count; i++) all.Add((chunks[i].text, chunks[i].pos, i < vectors.Count ? vectors[i] : null));
            int ord = 0;
            foreach ((string text, DocumentPosition pos, float[]? vec) in all)
            {
                long chunkId;
                using (SqliteCommand ic = c.CreateCommand())
                {
                    ic.Transaction = tx;
                    ic.CommandText = @"INSERT INTO kn_chunks(file_id,ord,text,heading,page,sheet,cell_range,slide,char_offset,embedding)
VALUES($f,$o,$t,$h,$pg,$sh,$cr,$sl,$co,$e); SELECT last_insert_rowid();";
                    ic.Parameters.AddWithValue("$f", fileId); ic.Parameters.AddWithValue("$o", ord++); ic.Parameters.AddWithValue("$t", text);
                    ic.Parameters.AddWithValue("$h", (object?)pos.Heading ?? DBNull.Value); ic.Parameters.AddWithValue("$pg", (object?)pos.Page ?? DBNull.Value);
                    ic.Parameters.AddWithValue("$sh", (object?)pos.Sheet ?? DBNull.Value); ic.Parameters.AddWithValue("$cr", (object?)pos.CellRange ?? DBNull.Value);
                    ic.Parameters.AddWithValue("$sl", (object?)pos.Slide ?? DBNull.Value); ic.Parameters.AddWithValue("$co", pos.CharOffset);
                    ic.Parameters.AddWithValue("$e", vec == null ? DBNull.Value : ToBlob(vec));
                    chunkId = (long)ic.ExecuteScalar()!;
                }
                if (!_hasFts5) continue;
                using SqliteCommand f1 = c.CreateCommand();
                f1.Transaction = tx; f1.CommandText = "INSERT INTO kn_fts(rowid,text,heading) VALUES($i,$t,$h);";
                f1.Parameters.AddWithValue("$i", chunkId); f1.Parameters.AddWithValue("$t", text); f1.Parameters.AddWithValue("$h", (object?)pos.Heading ?? DBNull.Value);
                f1.ExecuteNonQuery();
                using SqliteCommand f2 = c.CreateCommand();
                f2.Transaction = tx; f2.CommandText = "INSERT INTO kn_tri(rowid,text) VALUES($i,$t);";
                f2.Parameters.AddWithValue("$i", chunkId); f2.Parameters.AddWithValue("$t", text);
                f2.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private IEnumerable<(string text, long offset)> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        int step = Math.Max(1, _options.ChunkChars - _options.ChunkOverlap);
        for (int pos = 0; pos < text.Length; pos += step)
        {
            int len = Math.Min(_options.ChunkChars, text.Length - pos);
            yield return (text.Substring(pos, len), pos);
            if (pos + len >= text.Length) yield break;
        }
    }

    private IReadOnlyList<float[]?> Embed(List<string> texts)
    {
        var result = new List<float[]?>(texts.Count);
        if (!_options.EmbedChunks || _embedder is not { IsAvailable: true } || texts.Count == 0)
            return Enumerable.Repeat<float[]?>(null, texts.Count).ToList();
        for (int i = 0; i < texts.Count; i += _options.EmbedBatchSize)
        {
            var batch = texts.Skip(i).Take(_options.EmbedBatchSize).ToList();
            try
            {
                IReadOnlyList<float[]?> v = _embedder.TryEmbedBatch(batch);
                if (v.Count != batch.Count) v = batch.Select(_ => (float[]?)null).ToList();
                result.AddRange(v.Select(x => x != null && x.Length == _embedder.Identity.Dimensions && x.All(float.IsFinite) ? x : null));
            }
            catch (Exception) { result.AddRange(batch.Select(_ => (float[]?)null)); }
        }
        return result;
    }

    private static byte[] ToBlob(float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    private static float[] FromBlob(byte[] b) { var v = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, v, 0, b.Length); return v; }

    // ------------------------------------------------------------------------------------------------------ status

    public IReadOnlyList<IndexFileProblem> Problems(string sourceId)
    {
        EnsureSchema();
        var list = new List<IndexFileProblem>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT rel_path,kind,reason FROM kn_problems WHERE source_id=$s;";
            cmd.Parameters.AddWithValue("$s", sourceId);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new IndexFileProblem(r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return list;
    }

    public (int files, int chunks) Stats(string sourceId)
    {
        EnsureSchema();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            int files = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_files WHERE source_id=$s;", ("$s", sourceId)));
            int chunks = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_chunks WHERE file_id IN (SELECT id FROM kn_files WHERE source_id=$s);", ("$s", sourceId)));
            return (files, chunks);
        }
    }

    /// <summary>
    /// Deletes everything indexed for a source (the documents themselves are never touched) and scrubs what SQLite would otherwise
    /// leave behind: rows, word/trigram index entries and vectors are removed with secure_delete on (freed pages are zeroed),
    /// the FTS segments are rebuilt without the removed text, the WAL is folded in and truncated, and the file is vacuumed.
    /// </summary>
    public SecureDeletionResult RemoveSource(string sourceId)
    {
        EnsureSchema();
        int files, chunks;
        lock (_gate)
        {
            using SqliteConnection c = Open();
            Exec(c, "PRAGMA secure_delete=ON;");
            files = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_files WHERE source_id=$s;", ("$s", sourceId)));
            chunks = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_chunks WHERE file_id IN (SELECT id FROM kn_files WHERE source_id=$s);", ("$s", sourceId)));
            var ids = new List<long>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM kn_files WHERE source_id=$s;";
                cmd.Parameters.AddWithValue("$s", sourceId);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
            }
            foreach (long id in ids) DeleteFile(c, id);
            Exec(c, "DELETE FROM kn_problems WHERE source_id=$s;", ("$s", sourceId));
            Scrub(c);
        }
        return new SecureDeletionResult(files, chunks, true, true);
    }

    /// <summary>Rebuilds the FTS segments from the remaining rows (drops the removed text from them), folds the WAL in and vacuums.</summary>
    private void Scrub(SqliteConnection c)
    {
        if (_hasFts5)
        {
            Exec(c, "INSERT INTO kn_fts(kn_fts) VALUES('rebuild');");
            Exec(c, "INSERT INTO kn_tri(kn_tri) VALUES('rebuild');");
        }
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(c, "VACUUM;");
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>
    /// "Delete everything": every indexed row, word/trigram entry and vector is removed, the tables are dropped, the database is
    /// vacuumed, then the database file and its -wal/-shm/-journal sidecars are overwritten with zeros and deleted. The index
    /// is recreated empty on next use. Documents are never touched.
    /// </summary>
    public SecureDeletionResult DeleteEverything()
    {
        EnsureSchema();
        int files, chunks;
        bool sidecarsGone = true;
        lock (_gate)
        {
            using (SqliteConnection c = Open())
            {
                Exec(c, "PRAGMA secure_delete=ON;");
                files = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_files;"));
                chunks = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_chunks;"));
                if (_hasFts5)
                {
                    Exec(c, "INSERT INTO kn_fts(kn_fts) VALUES('delete-all');");
                    Exec(c, "INSERT INTO kn_tri(kn_tri) VALUES('delete-all');");
                    Exec(c, "DROP TABLE IF EXISTS kn_fts;");
                    Exec(c, "DROP TABLE IF EXISTS kn_tri;");
                }
                Exec(c, "DELETE FROM kn_chunks;");
                Exec(c, "DELETE FROM kn_files;");
                Exec(c, "DELETE FROM kn_problems;");
                Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
                Exec(c, "VACUUM;");
                Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
                if (!OverwriteAndDelete(_dbPath + suffix)) sidecarsGone = false;
            _ready = false;   // next use recreates an empty schema
        }
        return new SecureDeletionResult(files, chunks, true, sidecarsGone);
    }

    /// <summary>Zero-fills a file in place, then deletes it. Best effort on SSDs (wear levelling is outside software control); returns false when the file is still there.</summary>
    internal static bool OverwriteAndDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.SetAttributes(path, FileAttributes.Normal);
            long length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[81920];
                for (long done = 0; done < length;)
                {
                    int n = (int)Math.Min(zeros.Length, length - done);
                    stream.Write(zeros, 0, n);
                    done += n;
                }
                stream.Flush(true);
            }
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception) { try { File.Delete(path); } catch (Exception) { } return !File.Exists(path); }
    }

    // ------------------------------------------------------------------------------------------------------ document access (skills)

    /// <summary>
    /// Indexed files whose name equals / contains the text, or whose content hash starts with it (6+ hex characters).
    /// Newest first. Used by the chat skills to find two versions of a document by name or hash.
    /// </summary>
    public IReadOnlyList<IndexedFile> FindFiles(string nameOrHash, IReadOnlyCollection<string>? sourceIds, int limit = 25)
    {
        EnsureSchema();
        string needle = (nameOrHash ?? string.Empty).Trim().Trim('"', '\\');
        var list = new List<IndexedFile>();
        if (needle.Length == 0) return list;
        bool hex = needle.Length >= 6 && needle.All(Uri.IsHexDigit);
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT id,source_id,path,name,hash,mtime_ticks,size FROM kn_files
WHERE (name=$n COLLATE NOCASE OR name LIKE $like ESCAPE '\' OR ($hex=1 AND hash LIKE $h)) ORDER BY mtime_ticks DESC, id DESC LIMIT 500;";
            cmd.Parameters.AddWithValue("$n", needle);
            cmd.Parameters.AddWithValue("$like", "%" + needle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            cmd.Parameters.AddWithValue("$hex", hex ? 1 : 0);
            cmd.Parameters.AddWithValue("$h", needle.ToUpperInvariant() + "%");
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (sourceIds != null && !sourceIds.Contains(r.GetString(1))) continue;
                list.Add(new IndexedFile(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
            }
        }
        // An exact name beats a partial one.
        var exact = list.Where(f => f.Name.Equals(needle, StringComparison.OrdinalIgnoreCase) || (hex && f.Hash.StartsWith(needle, StringComparison.OrdinalIgnoreCase))).ToList();
        return (exact.Count > 0 ? exact : list).Take(limit).ToList();
    }

    /// <summary>The text of one indexed file as passages (overlapping chunks are merged back, the synthetic file-name chunk is left out).</summary>
    public IReadOnlyList<IndexedPassage> GetPassages(long fileId)
    {
        EnsureSchema();
        var result = new List<IndexedPassage>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT text,heading,page,sheet,cell_range,slide,char_offset FROM kn_chunks WHERE file_id=$f ORDER BY ord;";
            cmd.Parameters.AddWithValue("$f", fileId);
            using SqliteDataReader r = cmd.ExecuteReader();
            DocumentPosition? groupPos = null;
            var merged = new StringBuilder();
            long groupStart = 0;
            void Flush() { if (groupPos != null && merged.Length > 0) result.Add(new IndexedPassage(merged.ToString(), groupPos)); groupPos = null; merged.Clear(); }
            while (r.Read())
            {
                string text = r.GetString(0);
                var pos = new DocumentPosition(r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(6));
                if (pos.Heading == "file name" && text.StartsWith("File: ", StringComparison.Ordinal)) continue;
                bool continues = groupPos != null && SamePlace(groupPos, pos) && pos.CharOffset >= groupStart && pos.CharOffset <= groupStart + merged.Length;
                if (!continues)
                {
                    Flush();
                    groupPos = pos; groupStart = pos.CharOffset; merged.Append(text);
                    continue;
                }
                int skip = (int)Math.Min(text.Length, groupStart + merged.Length - pos.CharOffset);
                merged.Append(text, skip, text.Length - skip);
            }
            Flush();
        }
        return result;
    }

    private static bool SamePlace(DocumentPosition a, DocumentPosition b) =>
        a.Page == b.Page && a.Sheet == b.Sheet && a.CellRange == b.CellRange && a.Slide == b.Slide && a.Heading == b.Heading;

    /// <summary>Up to <paramref name="max"/> chunks of the chosen sources, in file order, as hits with a "scan" match label. Bounded; for whole-corpus checks.</summary>
    public IReadOnlyList<KnowledgeHit> ScanChunks(IReadOnlyCollection<string>? sourceIds, int max)
    {
        EnsureSchema();
        var hits = new List<KnowledgeHit>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT k.id,k.text,k.heading,k.page,k.sheet,k.cell_range,k.slide,k.char_offset,f.path,f.name,f.source_id
FROM kn_chunks k JOIN kn_files f ON f.id=k.file_id WHERE f.duplicate_of IS NULL ORDER BY f.id,k.ord;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read() && hits.Count < max)
            {
                if (sourceIds != null && !sourceIds.Contains(r.GetString(10))) continue;
                if (r.IsDBNull(2) ? false : r.GetString(2) == "file name") continue;
                var pos = new DocumentPosition(r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(7));
                hits.Add(new KnowledgeHit(r.GetInt64(0), r.GetString(8), r.GetString(9), r.GetString(1), pos, 1.0, "scan"));
            }
        }
        return hits;
    }

    // ------------------------------------------------------------------------------------------------------ search

    private static readonly Regex IdentifierPattern = new(
        @"(?<![A-Za-z0-9])(0x[0-9A-Fa-f]{2,}|[A-Za-z][A-Za-z0-9]*[_\-.][A-Za-z0-9][A-Za-z0-9_\-.]*|[PCBU][0-9A-Fa-f]{4}(?:-[0-9A-Fa-f]{2})?|[A-Za-z]{2,}\d{3,}|\d+[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9])",
        RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\"([^\"]{2,})\"", RegexOptions.Compiled);

    /// <summary>The identifiers (codes, parameters, DTCs, quoted phrases) a query asks for exactly.</summary>
    public static IReadOnlyList<string> ExactTerms(string query)
    {
        var terms = new List<string>();
        foreach (Match m in Quoted.Matches(query)) terms.Add(m.Groups[1].Value.Trim());
        string rest = Quoted.Replace(query, " ");
        foreach (Match m in IdentifierPattern.Matches(rest)) terms.Add(m.Value);
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Phrase(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    public IReadOnlyList<KnowledgeHit> Search(string query, KnowledgeSearchMode mode = KnowledgeSearchMode.Auto, int topK = 10,
        IReadOnlyCollection<string>? sourceIds = null)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<KnowledgeHit>();
        IReadOnlyList<string> exactTerms = ExactTerms(query);
        bool semanticPossible = _embedder is { IsAvailable: true };

        if (mode == KnowledgeSearchMode.Auto)
        {
            if (exactTerms.Count > 0)
            {
                var exact = SearchExact(exactTerms, topK, sourceIds);
                if (exact.Count > 0) return exact;       // identifiers win: no semantic noise around an exact code
            }
            mode = semanticPossible ? KnowledgeSearchMode.Hybrid : KnowledgeSearchMode.Lexical;
        }
        switch (mode)
        {
            case KnowledgeSearchMode.Exact:
                return SearchExact(exactTerms.Count > 0 ? exactTerms : new[] { query.Trim() }, topK, sourceIds);
            case KnowledgeSearchMode.Lexical:
                return SearchLexical(query, topK, sourceIds, "lexical");
            case KnowledgeSearchMode.Semantic:
                return SearchSemantic(query, topK, sourceIds);
            default:
            {
                var lexical = SearchLexical(query, topK * 3, sourceIds, "lexical");
                var semantic = SearchSemantic(query, topK * 3, sourceIds);
                return Fuse(topK, lexical, semantic);
            }
        }
    }

    private static IReadOnlyList<KnowledgeHit> Fuse(int topK, params IReadOnlyList<KnowledgeHit>[] lists)
    {
        const double k = 60;
        var score = new Dictionary<long, double>();
        var first = new Dictionary<long, KnowledgeHit>();
        foreach (var list in lists)
            for (int rank = 0; rank < list.Count; rank++)
            {
                KnowledgeHit h = list[rank];
                score[h.ChunkId] = score.GetValueOrDefault(h.ChunkId) + 1.0 / (k + rank + 1);
                first.TryAdd(h.ChunkId, h);
            }
        return score.OrderByDescending(p => p.Value).Take(topK).Select(p => first[p.Key] with { Score = p.Value, Match = "hybrid" }).ToList();
    }

    private IReadOnlyList<KnowledgeHit> SearchExact(IReadOnlyList<string> terms, int topK, IReadOnlyCollection<string>? sources)
    {
        var hits = new Dictionary<long, KnowledgeHit>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            foreach (string term in terms)
            {
                if (_hasFts5)
                {
                    Collect(c, "SELECT rowid, bm25(kn_fts,1.0,3.0) FROM kn_fts WHERE kn_fts MATCH $q ORDER BY 2 LIMIT $n;", Phrase(term), topK * 3, sources, hits, "exact", 2.0);
                    if (term.Length >= 3)
                        Collect(c, "SELECT rowid, bm25(kn_tri) FROM kn_tri WHERE kn_tri MATCH $q ORDER BY 2 LIMIT $n;", Phrase(term), topK * 3, sources, hits, "exact", 1.0);
                }
                else CollectScan(c, term, topK * 3, sources, hits);
            }
        }
        return hits.Values.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    private IReadOnlyList<KnowledgeHit> SearchLexical(string query, int topK, IReadOnlyCollection<string>? sources, string label)
    {
        string[] words = Regex.Matches(query, @"[\p{L}\p{N}_\-.]{2,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (words.Length == 0) return Array.Empty<KnowledgeHit>();
        var hits = new Dictionary<long, KnowledgeHit>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            if (_hasFts5)
                Collect(c, "SELECT rowid, bm25(kn_fts,1.0,3.0) FROM kn_fts WHERE kn_fts MATCH $q ORDER BY 2 LIMIT $n;", string.Join(" OR ", words.Select(Phrase)), topK, sources, hits, label, 1.0);
            else foreach (string w in words) CollectScan(c, w, topK, sources, hits);
        }
        return hits.Values.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    private IReadOnlyList<KnowledgeHit> SearchSemantic(string query, int topK, IReadOnlyCollection<string>? sources)
    {
        if (_embedder is not { IsAvailable: true }) return Array.Empty<KnowledgeHit>();
        float[]? q = _embedder.TryEmbed(query);
        if (q == null || q.Length != _embedder.Identity.Dimensions) return Array.Empty<KnowledgeHit>();
        var best = new List<(long id, double score)>();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, embedding FROM kn_chunks WHERE embedding IS NOT NULL;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                float[] v = FromBlob((byte[])r.GetValue(1));
                if (v.Length != q.Length) continue;
                double s = Dot(q, v);
                if (s < _embedder.RelevanceThreshold) continue;
                best.Add((r.GetInt64(0), s));
                if (best.Count > topK * 20) { best.Sort((a, b) => b.score.CompareTo(a.score)); best.RemoveRange(topK * 10, best.Count - topK * 10); }
            }
            best.Sort((a, b) => b.score.CompareTo(a.score));
            var hits = new Dictionary<long, KnowledgeHit>();
            foreach ((long id, double s) in best.Take(topK * 3))
                Hydrate(c, id, s, "semantic", sources, hits);
            return hits.Values.OrderByDescending(h => h.Score).Take(topK).ToList();
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        int n = a.Length, i = 0, w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (; i <= n - w; i += w) acc += new Vector<float>(a, i) * new Vector<float>(b, i);
        double sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    private void Collect(SqliteConnection c, string sql, string match, int limit, IReadOnlyCollection<string>? sources,
        Dictionary<long, KnowledgeHit> hits, string label, double boost)
    {
        var ranked = new List<(long id, double bm25)>();
        try
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$q", match); cmd.Parameters.AddWithValue("$n", limit);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) ranked.Add((r.GetInt64(0), r.GetDouble(1)));
        }
        catch (SqliteException) { return; }   // an unparsable MATCH string never breaks a search
        foreach ((long id, double bm25) in ranked)
            Hydrate(c, id, boost * (1.0 + Math.Min(Math.Max(-bm25, 0), 50) / 50.0), label, sources, hits);   // bm25() is negative: lower = better
    }

    private void CollectScan(SqliteConnection c, string term, int limit, IReadOnlyCollection<string>? sources, Dictionary<long, KnowledgeHit> hits)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM kn_chunks WHERE text LIKE $q ESCAPE '\\' LIMIT $n;";
        cmd.Parameters.AddWithValue("$q", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Parameters.AddWithValue("$n", limit);
        var ids = new List<long>();
        using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (long id in ids) Hydrate(c, id, 1.0, "exact", sources, hits);
    }

    private void Hydrate(SqliteConnection c, long chunkId, double score, string label, IReadOnlyCollection<string>? sources, Dictionary<long, KnowledgeHit> hits)
    {
        if (hits.TryGetValue(chunkId, out KnowledgeHit? known)) { hits[chunkId] = known with { Score = known.Score + score }; return; }
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT k.text,k.heading,k.page,k.sheet,k.cell_range,k.slide,k.char_offset,f.path,f.name,f.source_id
FROM kn_chunks k JOIN kn_files f ON f.id=k.file_id WHERE k.id=$i;";
        cmd.Parameters.AddWithValue("$i", chunkId);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return;
        if (sources != null && !sources.Contains(r.GetString(9))) return;
        var pos = new DocumentPosition(r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(6));
        hits[chunkId] = new KnowledgeHit(chunkId, r.GetString(7), r.GetString(8), r.GetString(0), pos, score, label);
    }
}
