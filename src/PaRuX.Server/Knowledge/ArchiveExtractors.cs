using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Safe archive containers (zip, tar, tar.gz/tgz, gz, 7z): lists entries and indexes text-like / supported entries under
/// virtual paths "archive.zip!inner/path". Nothing is written to disk; entries are decoded in memory under hard limits:
/// nesting depth 2, entry count, per-entry and total expanded size, compression-ratio check, no links, no unsafe paths.
/// </summary>
internal sealed class ArchiveExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".zip", ".tar", ".gz", ".tgz", ".7z");
    private const int MaxDepth = 2;
    private const int MaxEntries = 20_000;
    private const int MaxListed = 5_000;
    private const int MaxRatio = 200;

    public override string Name => "archive";
    protected override IReadOnlySet<string> Extensions => Exts;

    internal sealed record Item(string Name, long Size, long CompressedSize, bool IsDirectory, bool IsLink, Func<Stream> Open);

    /// <summary>Normalises an entry name; null when it is rooted, drive-qualified, contains "..", control characters or is too long.</summary>
    internal static string? Normalize(string name)
    {
        name = name.Replace('\\', '/');
        if (name.Length == 0 || name.Length > 1024 || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl)) return null;
        var parts = new List<string>();
        foreach (string p in name.Split('/'))
        {
            if (p == "..") return null;
            if (p.Length == 0 || p == ".") continue;
            parts.Add(p);
        }
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        if (sink.ArchiveDepth >= MaxDepth) return;
        string outerPrefix = sink.Prefix ?? fileName;
        string? savedPrefix = sink.Prefix;
        sink.ArchiveDepth++;
        long totalBudget = Math.Clamp(sink.Limits.MaxCharacters * 2, 1L << 20, 512L << 20);
        long entryCap = Math.Clamp(sink.Limits.MaxCharacters, 64L << 10, 32L << 20);
        var listing = new List<string>();
        int seen = 0, unsafeNames = 0, links = 0, failed = 0, suspicious = 0, tooDeep = 0;
        bool stopped = false;

        bool Visit(Item it)
        {
            sink.Ct.ThrowIfCancellationRequested();
            if (++seen > MaxEntries) { sink.Truncated = true; stopped = true; return false; }
            if (it.IsLink) { links++; return true; }
            string? safe = Normalize(it.Name);
            if (safe == null) { unsafeNames++; return true; }
            if (it.IsDirectory) return true;
            if (listing.Count < MaxListed) listing.Add(safe + (it.Size >= 0 ? " (" + it.Size + " bytes)" : ""));
            else sink.Truncated = true;

            StreamExtractor? ex = GenericExtractors.Find(safe);
            bool plain = ex == null && SourceCodeExtractor.IsPlainText(safe);
            if (ex == null && !plain) return true;
            if (ex is ArchiveExtractor && sink.ArchiveDepth >= MaxDepth) { tooDeep++; return true; }
            if (sink.ExpandedBytes >= totalBudget) { sink.Truncated = true; stopped = true; return false; }
            if (it.Size > (1 << 20) && it.CompressedSize > 0 && it.Size / it.CompressedSize > MaxRatio) { suspicious++; sink.Truncated = true; return true; }

            long cap = Math.Min(entryCap, totalBudget - sink.ExpandedBytes);
            using var ms = new MemoryStream();
            bool more;
            using (Stream src = it.Open())
            {
                byte[] buf = new byte[64 * 1024];
                int n;
                while (ms.Length < cap && (n = src.Read(buf, 0, (int)Math.Min(buf.Length, cap - ms.Length))) > 0)
                {
                    ms.Write(buf, 0, n);
                    sink.Ct.ThrowIfCancellationRequested();
                }
                more = ms.Length >= cap && src.ReadByte() >= 0;
            }
            sink.ExpandedBytes += ms.Length;
            if (more) sink.Truncated = true;
            ms.Position = 0;
            string virtualPath = outerPrefix + "!" + safe;
            string? before = sink.Prefix;
            sink.Section(null);
            sink.Prefix = virtualPath;
            try
            {
                (ex ?? new SourceCodeExtractor()).Read(ms, Path.GetFileName(safe), sink);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failed++; }
            finally { sink.Section(null); sink.Prefix = before; sink.LineRanges = false; sink.Slide = null; }
            return !sink.Exhausted;
        }

        try
        {
            sink.Prefix = outerPrefix;
            Walk(stream, fileName, sink, Visit);
        }
        catch (InvalidDataException) { sink.Truncated = true; sink.Note = Join(sink.Note, "Archive expansion limit reached or archive is damaged; partial content only."); }
        finally { sink.ArchiveDepth--; }

        sink.Prefix = outerPrefix;
        sink.Section("contents");
        foreach (string l in listing) sink.Line(l);
        sink.Section(null);
        sink.Prefix = savedPrefix;

        var notes = new List<string>();
        if (unsafeNames > 0) notes.Add(unsafeNames + " entries with unsafe paths were skipped");
        if (links > 0) notes.Add(links + " link entries were skipped");
        if (suspicious > 0) notes.Add(suspicious + " entries skipped by the compression-ratio guard");
        if (failed > 0) notes.Add(failed + " entries could not be read");
        if (tooDeep > 0) notes.Add(tooDeep + " nested archives beyond depth " + MaxDepth + " were listed only");
        if (stopped) notes.Add("entry or size limit reached");
        if (notes.Count > 0) sink.Note = Join(sink.Note, "Archive: " + string.Join("; ", notes) + ".");
    }

    private static string Join(string? a, string b) => a == null ? b : a + " " + b;

    private static void Walk(Stream stream, string name, ExtractionSink sink, Func<Item, bool> visit)
    {
        string lower = name.ToLowerInvariant();
        stream.Position = 0;
        if (lower.EndsWith(".zip"))
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 1_000_000) throw new InvalidDataException("Too many entries.");
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                bool link = ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000;
                if (!visit(new Item(e.FullName, e.Length, e.CompressedLength, e.FullName.EndsWith('/'), link, e.Open))) return;
            }
        }
        else if (lower.EndsWith(".tar")) WalkTar(stream, sink, visit);
        else if (lower.EndsWith(".tgz") || lower.EndsWith(".tar.gz") || lower.EndsWith(".gz"))
        {
            long budget = Math.Clamp(sink.Limits.MaxCharacters * 8, 4L << 20, 2048L << 20);
            using var gz = new BoundedReadStream(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), budget);
            if (lower.EndsWith(".gz") && !lower.EndsWith(".tar.gz"))
            {
                string inner = name[..^3];
                visit(new Item(inner, -1, 0, false, false, () => gz));
            }
            else WalkTar(gz, sink, visit);
        }
        else if (lower.EndsWith(".7z"))
        {
            using IArchive archive = ArchiveFactory.OpenArchive(stream);
            using IReader reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (!visit(new Item(entry.Key ?? "", entry.Size, entry.CompressedSize, entry.IsDirectory, false, reader.OpenEntryStream))) return;
            }
        }
    }

    private static void WalkTar(Stream stream, ExtractionSink sink, Func<Item, bool> visit)
    {
        using var tar = new TarReader(stream, leaveOpen: true);
        TarEntry? e;
        while ((e = tar.GetNextEntry(copyData: false)) != null)
        {
            bool link = e.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink;
            bool dir = e.EntryType == TarEntryType.Directory;
            TarEntry current = e;
            if (!visit(new Item(e.Name, e.Length, 0, dir, link, () => current.DataStream ?? Stream.Null))) return;
        }
    }
}
