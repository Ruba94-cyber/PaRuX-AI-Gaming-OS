using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>XLSX rows keep their column names in every block. Synthetic workbooks generated here only.</summary>
public sealed class KnowledgeSpreadsheetHeaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lks-xh-" + Guid.NewGuid().ToString("N"));

    public KnowledgeSpreadsheetHeaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Cell Text(string reference, string value) =>
        new() { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(value)) };

    private string Workbook(string sheetName, IReadOnlyList<string[]> rows)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            WorksheetPart sheetPart = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = new Row { RowIndex = (uint)(r + 1) };
                for (int c = 0; c < rows[r].Length; c++)
                    if (rows[r][c].Length > 0) row.Append(Text(StructuredDocumentExtractor.ColumnName(c + 1) + (r + 1), rows[r][c]));
                data.Append(row);
            }
            sheetPart.Worksheet = new Worksheet(data);
            workbook.Workbook.Sheets!.Append(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = sheetName });
        }
        return path;
    }

    [Fact]
    public void Middle_block_names_its_columns_and_keeps_cell_ranges()
    {
        var rows = new List<string[]> { new[] { "Item", "Price", "Note" } };
        for (int i = 1; i <= 100; i++)
            rows.Add(new[] { "item" + i, (i * 1.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), i % 2 == 0 ? "even" : "" });
        string path = Workbook("Stock", rows);

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(path);

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal(new[] { "A1:C40", "A41:C80", "A81:C101" }, doc.Segments.Select(s => s.Position.CellRange));
        Assert.All(doc.Segments, s => Assert.Equal("Stock", s.Position.Sheet));
        Assert.StartsWith("columns: Item | Price | Note", doc.Segments[0].Text);
        DocumentSegment middle = doc.Segments[1];
        Assert.DoesNotContain('\t', middle.Text);
        Assert.Contains("Item=item50 | Price=75.0 | Note=even", middle.Text);
        Assert.Contains("Item=item51 | Price=76.5", middle.Text);
        Assert.DoesNotContain("item51 | Price=76.5 | Note", middle.Text); // empty cells are omitted
    }

    [Fact]
    public void Sheet_without_a_header_row_keeps_tab_joined_values()
    {
        var rows = new List<string[]>();
        for (int i = 1; i <= 5; i++) rows.Add(new[] { i.ToString(), (i * 10).ToString(), (i * 100).ToString() });
        string path = Workbook("Numbers", rows);

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(path);

        DocumentSegment segment = Assert.Single(doc.Segments);
        Assert.Equal("A1:C5", segment.Position.CellRange);
        Assert.Contains("2\t20\t200", segment.Text);
        Assert.DoesNotContain("=", segment.Text);
        Assert.DoesNotContain("columns:", segment.Text);
    }
}
