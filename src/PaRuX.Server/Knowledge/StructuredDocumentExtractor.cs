using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using ExcelDataReader;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PaRuX.Server.Knowledge;

/// <summary>How far a file could be read.</summary>
public enum DocumentTier
{
    /// <summary>A structured parser read it (PDF, Office): positions are real page/sheet/slide/heading coordinates.</summary>
    Structured,
    /// <summary>Plain text with a detected encoding.</summary>
    Text,
    /// <summary>Not readable as text (images, video, unknown binaries): only name, type, size and dates are indexed.</summary>
    MetadataOnly,
    /// <summary>A reader exists but failed (corrupt, encrypted, too large): the reason is in <see cref="ExtractedDocument.Note"/>.</summary>
    Failed
}

/// <summary>Where a piece of text came from. Only the fields that apply are set.</summary>
public sealed record DocumentPosition(int? Page = null, string? Sheet = null, string? CellRange = null, int? Slide = null,
    string? Heading = null, long CharOffset = 0)
{
    /// <summary>Human-readable citation such as "p. 4", "Sheet1!A1:F50", "slide 3", "§ Intro > Scope".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Page.HasValue) parts.Add("p. " + Page.Value);
        if (Sheet != null) parts.Add(CellRange != null ? Sheet + "!" + CellRange : Sheet);
        if (Slide.HasValue) parts.Add("slide " + Slide.Value);
        if (!string.IsNullOrWhiteSpace(Heading)) parts.Add("§ " + Heading);
        return parts.Count == 0 ? "offset " + CharOffset : string.Join(", ", parts);
    }
}

public sealed record DocumentSegment(string Text, DocumentPosition Position);

public sealed record ExtractedDocument(
    string Path, DocumentTier Tier, IReadOnlyList<DocumentSegment> Segments, bool Truncated, string? Note, string? Extractor)
{
    public string FullText => string.Join("\n", Segments.Select(s => s.Text));
}

/// <summary>Resource limits; all configurable, none silent (a hit limit sets <see cref="ExtractedDocument.Truncated"/>).</summary>
public sealed record ExtractionLimits(
    long MaxCharacters = 20_000_000,
    int MaxSpreadsheetRows = 500_000,
    long MaxStructuredFileBytes = 1024L * 1024 * 1024,
    int SegmentCharacters = 8_000,
    int SpreadsheetRowsPerSegment = 40)
{
    public static ExtractionLimits Default { get; } = new();
}

public interface IDocumentExtractor
{
    string Name { get; }
    bool CanHandle(string path);
    ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct);
}

/// <summary>
/// Registry of extractors, first match wins. Any file ends in an explicit tier: structured, text (by sniffing, not only by
/// extension), metadata-only (unknown binaries are never interpreted as text) or failed (with a reason that never contains
/// document content). Documents are only read, never modified.
/// </summary>
public static class StructuredDocumentExtractor
{
    private static readonly IDocumentExtractor[] Extractors =
    {
        new PdfExtractor(), new WordExtractor(), new PresentationExtractor(), new OpenXmlSpreadsheetExtractor(),
        new LegacyWorkbookExtractor(),
        new DbcExtractor(), new ArxmlExtractor(), new OdxExtractor(), new CddExtractor(), new A2lExtractor(), new TextFileExtractor()
    };

    public static IReadOnlyList<IDocumentExtractor> Registered => Extractors;

    public static ExtractedDocument Extract(string path, ExtractionLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= ExtractionLimits.Default;
        ct.ThrowIfCancellationRequested();
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return Failed(path, "File not found.", null); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(path, "File is not accessible: " + ex.GetType().Name, null); }

        foreach (IDocumentExtractor extractor in Extractors)
        {
            if (!extractor.CanHandle(path)) continue;
            if (extractor is not TextFileExtractor && info.Length > limits.MaxStructuredFileBytes)
                return Failed(path, "File is larger than the configured limit for structured parsing.", extractor.Name);
            try
            {
                ExtractedDocument result = extractor.Extract(path, limits, ct);
                // A reader that found nothing to read (scanned PDF, binary) still leaves the file findable by name and type.
                if (result.Tier == DocumentTier.MetadataOnly && result.Segments.Count == 0)
                {
                    ExtractedDocument meta = MetadataOnly(path, info);
                    return meta with { Note = result.Note ?? meta.Note, Extractor = result.Extractor };
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Only the exception type is kept: a message can quote document content.
                return Failed(path, "Could not be read (" + ex.GetType().Name + "): corrupt, encrypted or unsupported.", extractor.Name);
            }
        }
        return MetadataOnly(path, info);
    }

    internal static ExtractedDocument Failed(string path, string note, string? extractor) =>
        new(path, DocumentTier.Failed, Array.Empty<DocumentSegment>(), false, note, extractor);

    internal static ExtractedDocument MetadataOnly(string path, FileInfo info)
    {
        string text = $"File: {Path.GetFileName(path)}\nType: {Path.GetExtension(path).TrimStart('.').ToLowerInvariant()}\n"
            + $"Size: {info.Length} bytes\nModified: {info.LastWriteTimeUtc:O}";
        return new ExtractedDocument(path, DocumentTier.MetadataOnly,
            new[] { new DocumentSegment(text, new DocumentPosition()) }, false, "Content is not readable as text; indexed by name and metadata only.", null);
    }

    /// <summary>1 -> A, 27 -> AA.</summary>
    public static string ColumnName(int oneBased)
    {
        var sb = new StringBuilder();
        for (int n = oneBased; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }
}

// ------------------------------------------------------------------------------------------------------------------ PDF
internal sealed class PdfExtractor : IDocumentExtractor
{
    public string Name => "pdf";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var segments = new List<DocumentSegment>();
        long total = 0, offset = 0;
        bool truncated = false;
        int emptyPages = 0, pages = 0;
        using PdfDocument document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            pages++;
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); }
            catch (Exception ex) when (ex is not OperationCanceledException) { text = page.Text; }
            text = text.Trim();
            if (text.Length == 0) { emptyPages++; continue; }
            if (total + text.Length > limits.MaxCharacters) { text = text[..(int)Math.Max(0, limits.MaxCharacters - total)]; truncated = true; }
            segments.Add(new DocumentSegment(text, new DocumentPosition(Page: page.Number, CharOffset: offset)));
            total += text.Length; offset += text.Length + 1;
            if (truncated) break;
        }
        string? note = emptyPages > 0 && emptyPages == pages
            ? "No text layer (scanned PDF); OCR is not enabled, so only the file name is searchable."
            : emptyPages > 0 ? emptyPages + " of " + pages + " pages have no text layer." : null;
        DocumentTier tier = segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured;
        return new ExtractedDocument(path, tier, segments, truncated, note, Name);
    }
}

// ------------------------------------------------------------------------------------------------------------------ Word
internal sealed class WordExtractor : IDocumentExtractor
{
    public string Name => "docx";
    public bool CanHandle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm";

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var segments = new List<DocumentSegment>();
        var headings = new List<string>();
        var buffer = new StringBuilder();
        long total = 0, offset = 0, sectionStart = 0;
        bool truncated = false;
        using WordprocessingDocument document = WordprocessingDocument.Open(path, false);

        void Flush()
        {
            if (buffer.Length == 0) return;
            segments.Add(new DocumentSegment(buffer.ToString().TrimEnd(),
                new DocumentPosition(Heading: headings.Count == 0 ? null : string.Join(" > ", headings), CharOffset: sectionStart)));
            buffer.Clear();
        }

        IEnumerable<OpenXmlElement> blocks = document.MainDocumentPart?.Document?.Body?.ChildElements ?? Enumerable.Empty<OpenXmlElement>();
        foreach (OpenXmlElement block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= limits.MaxCharacters) { truncated = true; break; }
            if (block is Paragraph paragraph)
            {
                string text = paragraph.InnerText.Trim();
                if (text.Length == 0) continue;
                int level = HeadingLevel(paragraph);
                if (level > 0)
                {
                    Flush();
                    while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                    headings.Add(text);
                    sectionStart = offset;
                }
                if (buffer.Length == 0) sectionStart = offset;
                buffer.AppendLine(text);
                total += text.Length + 1; offset += text.Length + 1;
            }
            else if (block is DocumentFormat.OpenXml.Wordprocessing.Table table)
            {
                foreach (TableRow row in table.Elements<TableRow>())
                {
                    string line = string.Join("\t", row.Elements<TableCell>().Select(c => c.InnerText.Trim()));
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (buffer.Length == 0) sectionStart = offset;
                    buffer.AppendLine(line);
                    total += line.Length + 1; offset += line.Length + 1;
                }
            }
            if (buffer.Length >= limits.SegmentCharacters) Flush();
        }
        Flush();
        return new ExtractedDocument(path, segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured, segments, truncated, null, Name);
    }

    private static int HeadingLevel(Paragraph paragraph)
    {
        string? style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style == null) return 0;
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out int level)) return Math.Clamp(level, 1, 9);
        return 0;
    }
}

// ------------------------------------------------------------------------------------------------------------------ PowerPoint
internal sealed class PresentationExtractor : IDocumentExtractor
{
    public string Name => "pptx";
    public bool CanHandle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pptx" or ".pptm";

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var segments = new List<DocumentSegment>();
        long total = 0, offset = 0;
        bool truncated = false;
        using PresentationDocument document = PresentationDocument.Open(path, false);
        var presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? Enumerable.Empty<DocumentFormat.OpenXml.Presentation.SlideId>();
        int number = 0;
        foreach (var slideId in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (presentationPart!.GetPartById(slideId.RelationshipId!) is not SlidePart part) continue;
            var sb = new StringBuilder();
            foreach (var text in part.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                if (!string.IsNullOrWhiteSpace(text.Text)) sb.AppendLine(text.Text);
            var notes = part.NotesSlidePart?.NotesSlide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (notes is { Count: > 0 }) { sb.AppendLine("[Notes]"); foreach (string n in notes) sb.AppendLine(n); }
            string slideText = sb.ToString().TrimEnd();
            if (slideText.Length == 0) continue;
            if (total + slideText.Length > limits.MaxCharacters) { truncated = true; break; }
            segments.Add(new DocumentSegment(slideText, new DocumentPosition(Slide: number, CharOffset: offset)));
            total += slideText.Length; offset += slideText.Length + 1;
        }
        return new ExtractedDocument(path, segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured, segments, truncated, null, Name);
    }
}

// ------------------------------------------------------------------------------------------------------------------ XLSX (A1 coordinates)
internal sealed class OpenXmlSpreadsheetExtractor : IDocumentExtractor
{
    public string Name => "xlsx";
    public bool CanHandle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var segments = new List<DocumentSegment>();
        long total = 0, offset = 0;
        bool truncated = false;
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        WorkbookPart workbook = document.WorkbookPart ?? throw new InvalidDataException("No workbook.");
        string[] shared = workbook.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? Array.Empty<string>();
        foreach (Sheet sheet in workbook.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
        {
            if (workbook.GetPartById(sheet.Id!) is not WorksheetPart part) continue;
            string sheetName = sheet.Name?.Value ?? "Sheet";
            var block = new List<string>();
            int firstRow = 0, lastRow = 0, maxCol = 1, rows = 0;

            void Flush()
            {
                if (block.Count == 0) return;
                string text = string.Join("\n", block);
                segments.Add(new DocumentSegment(text, new DocumentPosition(Sheet: sheetName,
                    CellRange: "A" + firstRow + ":" + StructuredDocumentExtractor.ColumnName(maxCol) + lastRow, CharOffset: offset)));
                total += text.Length; offset += text.Length + 1;
                block.Clear(); maxCol = 1;
            }

            using OpenXmlReader reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
                ct.ThrowIfCancellationRequested();
                if (rows >= limits.MaxSpreadsheetRows || total >= limits.MaxCharacters) { truncated = true; break; }
                var row = (Row)reader.LoadCurrentElement()!;
                var cells = new List<(int Col, string Value)>();
                foreach (Cell cell in row.Elements<Cell>())
                {
                    string value = CellText(cell, shared);
                    if (value.Length == 0) continue;
                    cells.Add((ColumnIndex(cell.CellReference?.Value), value));
                }
                if (cells.Count == 0) continue;
                int rowNumber = (int)(row.RowIndex?.Value ?? (uint)(lastRow + 1));
                if (block.Count == 0) firstRow = rowNumber;
                lastRow = rowNumber; rows++;
                maxCol = Math.Max(maxCol, cells.Max(c => c.Col));
                block.Add(string.Join("\t", cells.Select(c => c.Value)));
                if (block.Count >= limits.SpreadsheetRowsPerSegment) Flush();
            }
            Flush();
            if (truncated) break;
        }
        return new ExtractedDocument(path, segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured, segments, truncated, null, Name);
    }

    private static string CellText(Cell cell, string[] shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out int index) && index >= 0 && index < shared.Length)
            return shared[index].Trim();
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText.Trim() ?? string.Empty;
        return cell.CellValue?.Text?.Trim() ?? string.Empty;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 1;
        int column = 0;
        foreach (char c in reference) { if (!char.IsLetter(c)) break; column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1); }
        return Math.Max(1, column);
    }
}

// ------------------------------------------------------------------------------------------------------------------ XLS / XLSB
internal sealed class LegacyWorkbookExtractor : IDocumentExtractor
{
    public string Name => "xls";
    public bool CanHandle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xls" or ".xlsb";

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var segments = new List<DocumentSegment>();
        long total = 0, offset = 0;
        bool truncated = false;
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            string sheetName = reader.Name;
            var block = new List<string>();
            int row = 0, firstRow = 0, maxCol = 1, rows = 0;
            void Flush()
            {
                if (block.Count == 0) return;
                string text = string.Join("\n", block);
                segments.Add(new DocumentSegment(text, new DocumentPosition(Sheet: sheetName,
                    CellRange: "A" + firstRow + ":" + StructuredDocumentExtractor.ColumnName(maxCol) + row, CharOffset: offset)));
                total += text.Length; offset += text.Length + 1;
                block.Clear(); maxCol = 1;
            }
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                row++;
                if (rows >= limits.MaxSpreadsheetRows || total >= limits.MaxCharacters) { truncated = true; break; }
                var values = new List<string>();
                int lastFilled = 0;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string v = reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty;
                    values.Add(v);
                    if (v.Length > 0) lastFilled = i + 1;
                }
                if (lastFilled == 0) continue;
                if (block.Count == 0) firstRow = row;
                rows++;
                maxCol = Math.Max(maxCol, lastFilled);
                block.Add(string.Join("\t", values.Take(lastFilled)));
                if (block.Count >= limits.SpreadsheetRowsPerSegment) Flush();
            }
            Flush();
            if (truncated) break;
        } while (reader.NextResult());
        return new ExtractedDocument(path, segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured, segments, truncated, null, Name);
    }
}

// ------------------------------------------------------------------------------------------------------------------ Text (sniffed)
internal sealed class TextFileExtractor : IDocumentExtractor
{
    public string Name => "text";

    // Known binary containers that must never be read as text even if a sniff of the first bytes happens to look printable.
    private static readonly HashSet<string> NeverText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".bin", ".iso", ".img", ".zip", ".7z", ".rar", ".gz", ".tar", ".png", ".jpg", ".jpeg", ".gif",
        ".bmp", ".ico", ".webp", ".mp3", ".mp4", ".mkv", ".avi", ".wav", ".flac", ".ogg", ".pdf", ".doc", ".ppt", ".db", ".sqlite",
        ".gguf", ".safetensors", ".onnx", ".pt", ".pth", ".ckpt", ".mf4", ".mdf", ".blf", ".pst", ".ost"
    };

    public bool CanHandle(string path) => !NeverText.Contains(Path.GetExtension(path));

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] sample = new byte[(int)Math.Min(65536, stream.Length)];
        int read = stream.Read(sample, 0, sample.Length);
        Encoding? encoding = Detect(sample.AsSpan(0, read), stream.Length > read, out int bomLength);
        if (encoding == null) return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false, null, Name);
        if (read == 0) return new ExtractedDocument(path, DocumentTier.Text, Array.Empty<DocumentSegment>(), false, "Empty file.", Name);

        stream.Position = bomLength;
        using var reader = new StreamReader(stream, encoding, false, 64 * 1024);
        var segments = new List<DocumentSegment>();
        char[] buffer = new char[limits.SegmentCharacters];
        long total = 0;
        bool truncated = false;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (total + n > limits.MaxCharacters) { n = (int)Math.Max(0, limits.MaxCharacters - total); truncated = true; }
            if (n > 0) segments.Add(new DocumentSegment(new string(buffer, 0, n), new DocumentPosition(CharOffset: total)));
            total += n;
            if (truncated) break;
        }
        string? note = encoding.CodePage == 1252 ? "Decoded as Windows-1252 (not valid UTF-8)." : null;
        return new ExtractedDocument(path, DocumentTier.Text, segments, truncated, note, Name);
    }

    /// <summary>BOM first, then strict UTF-8, then Windows-1252 if the sample looks like text; null = binary.</summary>
    internal static Encoding? Detect(ReadOnlySpan<byte> sample, bool sampleIsPartial, out int bomLength)
    {
        bomLength = 0;
        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF) { bomLength = 3; return new UTF8Encoding(false); }
        if (sample.Length >= 4 && sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0 && sample[3] == 0) { bomLength = 4; return Encoding.UTF32; }
        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE) { bomLength = 2; return Encoding.Unicode; }
        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF) { bomLength = 2; return Encoding.BigEndianUnicode; }
        int control = 0;
        foreach (byte b in sample)
        {
            if (b == 0) return null;
            if (b < 9 || (b > 13 && b < 32)) control++;
        }
        if (sample.Length > 0 && control * 100 / sample.Length > 2) return null;
        try
        {
            // A multi-byte sequence cut at the end of the sample is not an error.
            new UTF8Encoding(false, true).GetString(sampleIsPartial ? sample[..Math.Max(0, sample.Length - 3)] : sample);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252); }
    }
}
