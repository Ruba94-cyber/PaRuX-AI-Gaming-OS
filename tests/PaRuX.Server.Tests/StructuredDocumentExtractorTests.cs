using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using PaRuX.Server.Knowledge;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>All fixtures are synthetic and generated here; nothing is read from outside the temp folder.</summary>
public sealed class StructuredDocumentExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lks-" + Guid.NewGuid().ToString("N"));

    public StructuredDocumentExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Pdf_keeps_page_numbers()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (int i = 1; i <= 3; i++)
            builder.AddPage(595, 842).AddText("Calibration procedure MFC page " + i, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        File.WriteAllBytes(P("a.pdf"), builder.Build());

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("a.pdf"));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal(new int?[] { 1, 2, 3 }, doc.Segments.Select(s => s.Position.Page).ToArray());
        Assert.Contains("MFC page 2", doc.Segments[1].Text);
        Assert.Equal("p. 3", doc.Segments[2].Position.Describe());
    }

    [Fact]
    public void Pdf_without_text_layer_is_metadata_only_with_an_honest_note()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(200, 200);
        File.WriteAllBytes(P("scan.pdf"), builder.Build());

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("scan.pdf"));

        Assert.Equal(DocumentTier.MetadataOnly, doc.Tier);
        Assert.Contains("OCR is not enabled", doc.Note);
    }

    [Fact]
    public void Docx_headings_become_the_position_path_and_tables_are_read()
    {
        using (var document = WordprocessingDocument.Create(P("a.docx"), WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(Heading("Heading1", "Intro"), Para("Overview text."), Heading("Heading2", "Scope"), Para("Scope text."));
            var table = new W.Table(new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("ECU")))), new W.TableCell(new W.Paragraph(new W.Run(new W.Text("ABC_1234"))))));
            body.Append(table);
            main.Document = new W.Document(body);
        }

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("a.docx"));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Contains(doc.Segments, s => s.Position.Heading == "Intro > Scope" && s.Text.Contains("Scope text."));
        Assert.Contains("ECU\tABC_1234", doc.FullText);
    }

    private static W.Paragraph Para(string text) => new(new W.Run(new W.Text(text)));
    private static W.Paragraph Heading(string style, string text) =>
        new(new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }), new W.Run(new W.Text(text)));

    [Fact]
    public void Xlsx_reports_sheet_and_A1_cell_range()
    {
        using (var document = SpreadsheetDocument.Create(P("a.xlsx"), SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            for (uint r = 1; r <= 3; r++)
                data.Append(new Row(
                    new Cell { CellReference = "A" + r, DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("id" + r)) },
                    new Cell { CellReference = "C" + r, DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("val" + r)) }) { RowIndex = r });
            sheetPart.Worksheet = new Worksheet(data);
            workbook.Workbook.Sheets!.Append(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = "Params" });
        }

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("a.xlsx"));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        DocumentSegment segment = Assert.Single(doc.Segments);
        Assert.Equal("Params", segment.Position.Sheet);
        Assert.Equal("A1:C3", segment.Position.CellRange);
        Assert.Equal("Params!A1:C3", segment.Position.Describe());
        // The first row is a header, so rows are rendered Header=value (previously TAB-joined values).
        Assert.Contains("id1=id2 | val1=val2", segment.Text);
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(703, "AAA")]
    public void Column_names_follow_spreadsheet_rules(int index, string expected) =>
        Assert.Equal(expected, StructuredDocumentExtractor.ColumnName(index));

    [Fact]
    public void Text_is_decoded_by_sniffing_not_only_by_extension()
    {
        File.WriteAllText(P("notes.weird"), "plain è utf8", new UTF8Encoding(false));
        File.WriteAllBytes(P("latin.txt"), Encoding.GetEncoding(1252).GetBytes("caffè €"));
        File.WriteAllText(P("u16.txt"), "utf16 è", Encoding.Unicode);

        Assert.Contains("è", StructuredDocumentExtractor.Extract(P("notes.weird")).FullText);
        ExtractedDocument latin = StructuredDocumentExtractor.Extract(P("latin.txt"));
        Assert.Equal("caffè €", latin.FullText);
        Assert.Contains("Windows-1252", latin.Note);
        Assert.Equal("utf16 è", StructuredDocumentExtractor.Extract(P("u16.txt")).FullText);
    }

    [Fact]
    public void Unknown_binary_is_metadata_only_and_never_read_as_text()
    {
        File.WriteAllBytes(P("blob.dat"), Enumerable.Range(0, 4000).Select(i => (byte)(i % 7 == 0 ? 0 : i)).ToArray());
        File.WriteAllBytes(P("pic.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 65, 66, 67 });

        foreach (string name in new[] { "blob.dat", "pic.png" })
        {
            ExtractedDocument doc = StructuredDocumentExtractor.Extract(P(name));
            Assert.Equal(DocumentTier.MetadataOnly, doc.Tier);
            Assert.Contains("File: " + name, doc.FullText);
        }
    }

    [Fact]
    public void Limits_are_visible_never_silent()
    {
        File.WriteAllText(P("big.log"), new string('x', 50_000));

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("big.log"), ExtractionLimits.Default with { MaxCharacters = 10_000, SegmentCharacters = 4_000 });

        Assert.True(doc.Truncated);
        Assert.Equal(10_000, doc.Segments.Sum(s => s.Text.Length));
        Assert.Equal(new long[] { 0, 4_000, 8_000 }, doc.Segments.Select(s => s.Position.CharOffset).ToArray());
    }

    [Fact]
    public void Large_text_is_chunked_beyond_the_old_120k_cap()
    {
        File.WriteAllText(P("long.txt"), new string('a', 300_000));

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(P("long.txt"));

        Assert.False(doc.Truncated);
        Assert.Equal(300_000, doc.Segments.Sum(s => s.Text.Length));
    }

    [Fact]
    public void Corrupt_and_missing_files_fail_with_a_reason_that_holds_no_content()
    {
        File.WriteAllText(P("bad.docx"), "SECRET-CONTENT-NOT-A-ZIP");
        File.WriteAllText(P("bad.pdf"), "SECRET-CONTENT-NOT-A-PDF");

        foreach (string name in new[] { "bad.docx", "bad.pdf" })
        {
            ExtractedDocument doc = StructuredDocumentExtractor.Extract(P(name));
            Assert.Equal(DocumentTier.Failed, doc.Tier);
            Assert.DoesNotContain("SECRET", doc.Note);
        }
        Assert.Equal(DocumentTier.Failed, StructuredDocumentExtractor.Extract(P("missing.txt")).Tier);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        File.WriteAllText(P("c.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StructuredDocumentExtractor.Extract(P("c.txt"), null, cts.Token));
    }

    [Fact]
    public void Extraction_does_not_modify_the_source_file()
    {
        File.WriteAllText(P("ro.txt"), "keep me");
        DateTime before = File.GetLastWriteTimeUtc(P("ro.txt"));
        StructuredDocumentExtractor.Extract(P("ro.txt"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(P("ro.txt")));
        Assert.Equal("keep me", File.ReadAllText(P("ro.txt")));
    }
}
