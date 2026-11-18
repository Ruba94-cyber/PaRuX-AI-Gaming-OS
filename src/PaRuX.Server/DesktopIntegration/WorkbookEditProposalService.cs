using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using X = DocumentFormat.OpenXml.Spreadsheet;
using PaRuX.Contracts;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>Bounded, ephemeral, owner-reviewed workbook candidates. Model tools can propose only.</summary>
public sealed class WorkbookEditProposalService
{
    private const int MaxWorkbookBytes = 8 * 1024 * 1024;
    private readonly DocumentWriteService _documents;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _proposalAdmission = new(1, 1);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private sealed record Pending(string Folder, string SourcePath, byte[] SourceHash,
        byte[] Candidate, WorkbookEditProposal Preview, DateTime Expires, string TurnId, bool Ready);

    public WorkbookEditProposalService(DocumentWriteService documents) => _documents = documents;
    public static bool ValidConversation(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    public WorkbookEditProposal Propose(string conversation, string sourceName, string outputName, WorkbookContent patch, CancellationToken ct, string? turnId = null)
    {
        if (!ValidConversation(conversation)) throw new InvalidDataException("A valid conversation is required.");
        lock (_gate) { Prune(); if (_pending.ContainsKey(conversation)) throw new InvalidOperationException("Review or discard the existing workbook proposal first."); }
        if (!_proposalAdmission.Wait(0, ct)) throw new InvalidOperationException("Another workbook proposal is being prepared.");
        try { return ProposeCore(conversation, sourceName, outputName, patch, ct, turnId); }
        finally { _proposalAdmission.Release(); }
    }

    private WorkbookEditProposal ProposeCore(string conversation, string sourceName, string outputName, WorkbookContent patch, CancellationToken ct, string? turnId)
    {
        if (!ValidConversation(conversation)) throw new InvalidDataException("A valid conversation is required.");
        if (!PaRuXPathSafety.IsSimpleFileName(sourceName) || !sourceName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || !PaRuXPathSafety.IsSimpleFileName(outputName) || !outputName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source and output must be plain .xlsx filenames.");
        if (patch.Sheets.Count is < 1 or > 10 || patch.Sheets.Any(s => s.Rows.Count != 0 || s.Charts.Count != 0 || s.StructuredCells.Count == 0))
            throw new InvalidDataException("Only explicit cell edits are supported in a reviewable proposal.");
        var cells = patch.Sheets.SelectMany(s => s.StructuredCells.Select(c => (Sheet: s.Name, Cell: c))).ToList();
        if (cells.Count is < 1 or > 100 || cells.Any(x => string.IsNullOrWhiteSpace(x.Sheet) || x.Sheet.Length > 31
            || !Regex.IsMatch(x.Cell.Reference ?? "", "^[A-Za-z]{1,3}[1-9][0-9]{0,6}$")
            || x.Cell.Style != null || (x.Cell.Value == null || x.Cell.Formula != null) ||
            !new[] { "auto", "string", "number", "boolean", "date" }.Contains(x.Cell.DataType, StringComparer.OrdinalIgnoreCase) || (x.Cell.Value?.Length ?? 0) > 4096 || (x.Cell.Formula?.Length ?? 0) > 4096
            || (x.Cell.Value != null && x.Cell.Formula != null))
            || cells.Select(x => (x.Sheet.ToUpperInvariant(), x.Cell.Reference.ToUpperInvariant())).Distinct().Count() != cells.Count)
            throw new InvalidDataException("Invalid, duplicated, styled or oversized cell edit.");
        string folder = Path.GetFullPath(_documents.OutputFolder);
        if (new Uri(folder).IsUnc || new DriveInfo(Path.GetPathRoot(folder)!).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidDataException("Network or unavailable workbook folders are not supported.");
        string sourcePath = Path.Combine(folder, sourceName);
        RejectReparse(folder); RejectReparse(sourcePath);
        using var sourceFile = OpenSource(sourcePath);
        byte[] source = ReadSource(sourceFile, ct);
        ValidateArchive(source, ct);
        ct.ThrowIfCancellationRequested();
        var before = ReadCells(source, cells);
        StructuredWorkbookResult result = StructuredWorkbookWriter.Patch(source, patch);
        result = WorkbookPatchStylePreserver.Apply(source, result, patch);
        if (result.Bytes.Length > MaxWorkbookBytes) throw new InvalidDataException("Candidate workbook is too large.");
        var after = ReadCells(result.Bytes, cells);
        if (before.Any(x => x.Length > 4096) || after.Any(x => x.Length > 4096))
            throw new InvalidDataException("A reviewed cell is too large to display safely.");
        ct.ThrowIfCancellationRequested();
        var preview = new WorkbookEditProposal { Id = Guid.NewGuid().ToString("N"), SourceFileName = sourceName,
            OutputFileName = outputName, CalculationStatus = result.CalculationStatus, OpenXmlValidated = result.Validated,
            ValidationWarnings = result.ValidationWarnings.ToList(), Cells = cells.Select((x, i) => new WorkbookEditCellPreview
            { Sheet = x.Sheet, Cell = x.Cell.Reference.ToUpperInvariant(), Before = before[i], After = after[i] }).ToList() };
        lock (_gate)
        {
            Prune();
            if (_pending.ContainsKey(conversation)) throw new InvalidOperationException("Review or discard the existing workbook proposal first.");
            if (_pending.Count >= 4) throw new InvalidOperationException("Too many pending workbook proposals.");
            _pending[conversation] = new(folder, sourcePath, SHA256.HashData(source), result.Bytes, preview, DateTime.UtcNow.AddMinutes(30), turnId ?? "", turnId == null);
        }
        return preview;
    }

    public IReadOnlyList<WorkbookEditProposal> List(string conversation)
    {
        lock (_gate) { Prune(); return ValidConversation(conversation) && _pending.TryGetValue(conversation, out var p) && p.Ready ? new[] { p.Preview } : Array.Empty<WorkbookEditProposal>(); }
    }
    public bool Delete(string conversation, string id)
    {
        lock (_gate) { Prune(); return _pending.TryGetValue(conversation ?? "", out var p) && p.Preview.Id == id && p.Ready && _pending.Remove(conversation); }
    }
    public DocumentWriteResult Save(string conversation, string id)
    {
        lock (_gate)
        {
            Prune();
            if (!_pending.TryGetValue(conversation ?? "", out var p) || p.Preview.Id != id || !p.Ready) throw new KeyNotFoundException();
            if (!string.Equals(Path.GetFullPath(_documents.OutputFolder), p.Folder, PaRuXPathSafety.PathComparison)) throw new InvalidOperationException("Output folder changed; propose again.");
            RejectReparse(p.Folder); RejectReparse(p.SourcePath);
            using var sourceFile = OpenSource(p.SourcePath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(ReadSource(sourceFile, CancellationToken.None)), p.SourceHash))
                throw new InvalidOperationException("Source workbook changed; propose again.");
            string baseName = Path.GetFileNameWithoutExtension(p.Preview.OutputFileName);
            DocumentWriteResult result = _documents.PublishNew(baseName, ".xlsx", p.Candidate, p.Folder);
            _pending.Remove(conversation);
            result.SourceWorkbookPreserved = true;
            result.CalculationStatus = p.Preview.CalculationStatus;
            result.OpenXmlValidated = p.Preview.OpenXmlValidated;
            result.ValidationWarnings = p.Preview.ValidationWarnings.ToList();
            return result;
        }
    }
    public void CompleteTurn(string turnId, bool success)
    {
        lock (_gate)
        {
            foreach (var pair in _pending.Where(x => x.Value.TurnId == turnId).ToArray())
                if (success) _pending[pair.Key] = pair.Value with { Ready = true };
                else _pending.Remove(pair.Key);
        }
    }
    private static FileStream OpenSource(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxWorkbookBytes) { file.Dispose(); throw new InvalidDataException("Workbook is too large."); }
        return file;
    }
    private static byte[] ReadSource(FileStream file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = file.Read(block)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length + count > MaxWorkbookBytes) throw new InvalidDataException("Workbook is too large.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }
    private static void ValidateArchive(byte[] bytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1024) throw new InvalidDataException("Workbook has too many parts.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            expanded = checked(expanded + entry.Length);
            if (entry.Length > 16 * 1024 * 1024 || expanded > 64 * 1024 * 1024)
                throw new InvalidDataException("Workbook expands beyond the review limit.");
        }
    }
    private void Prune() { foreach (string key in _pending.Where(x => x.Value.Expires <= DateTime.UtcNow).Select(x => x.Key).ToArray()) _pending.Remove(key); }
    private static void RejectReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse paths are not supported for workbook review.");
    }
    private static List<string> ReadCells(byte[] bytes, List<(string Sheet, WorkbookCell Cell)> cells)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false);
        var part = document.WorkbookPart ?? throw new InvalidDataException("Workbook has no workbook part.");
        var sheets = part.Workbook.Sheets?.Elements<X.Sheet>().ToList() ?? new();
        var values = new List<string>();
        var indexes = new Dictionary<string, Dictionary<string, X.Cell>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in cells)
        {
            var sheet = sheets.SingleOrDefault(s => string.Equals(s.Name?.Value, item.Sheet, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Every edited sheet must already exist.");
            if (!indexes.TryGetValue(item.Sheet, out var index))
            {
                var ws = (WorksheetPart)part.GetPartById(sheet.Id!);
                var wanted = cells.Where(c => string.Equals(c.Sheet, item.Sheet, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Cell.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
                index = new Dictionary<string, X.Cell>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in ws.Worksheet.Descendants<X.Cell>())
                    if (existing.CellReference?.Value is string reference && wanted.Contains(reference))
                        index.TryAdd(reference, existing);
                indexes.Add(item.Sheet, index);
            }
            index.TryGetValue(item.Cell.Reference, out var cell);
            if (cell == null) { values.Add("(empty)"); continue; }
            if (cell.CellFormula != null) { values.Add("=" + cell.CellFormula.Text); continue; }
            string value = cell.DataType?.Value == X.CellValues.InlineString ? cell.InlineString?.InnerText ?? "" : cell.CellValue?.Text ?? "";
            if (cell.DataType?.Value == X.CellValues.SharedString && int.TryParse(value, out int sharedIndex))
                value = part.SharedStringTablePart?.SharedStringTable?.Elements<X.SharedStringItem>().ElementAtOrDefault(sharedIndex)?.InnerText ?? throw new InvalidDataException("Invalid shared string.");
            values.Add((cell.DataType?.Value.ToString() ?? "Number") + ": " + value);
        }
        return values;
    }
}
