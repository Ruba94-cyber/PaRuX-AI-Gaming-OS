using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorkbookEditProposalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-review-" + Guid.NewGuid().ToString("N"));
    private readonly DocumentWriteService _documents;
    private readonly WorkbookEditProposalService _proposals;
    public WorkbookEditProposalTests()
    {
        Directory.CreateDirectory(_dir);
        _documents = new DocumentWriteService(Path.Combine(_dir, "state"));
        _documents.SetOutputFolder(_dir);
        _documents.WriteWorkbook("source.xlsx", new WorkbookContent { Sheets = { new WorkbookSheet
        { Name = "Sheet1", StructuredCells = { new WorkbookCell { Reference = "A1", Value = "old", DataType = "string" } } } } });
        _proposals = new WorkbookEditProposalService(_documents);
    }
    private static WorkbookContent Patch() => new() { Sheets = { new WorkbookSheet { Name = "Sheet1",
        StructuredCells = { new WorkbookCell { Reference = "A1", Value = "new", DataType = "string" } } } } };
    [Fact]
    public void PreviewRequiresExplicitSaveAndPreservesSourceAndCollision()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(_dir, "source.xlsx"));
        var proposal = _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None);
        Assert.Contains("old", proposal.Cells[0].Before); Assert.Contains("new", proposal.Cells[0].After);
        Assert.False(File.Exists(Path.Combine(_dir, "result.xlsx")));
        Assert.Empty(_proposals.List("other"));
        File.WriteAllText(Path.Combine(_dir, "result.xlsx"), "untouched");
        var saved = _proposals.Save("chat", proposal.Id);
        Assert.Equal("result (2).xlsx", saved.FileName);
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(_dir, "result.xlsx")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_dir, "source.xlsx")));
        Assert.True(File.Exists(saved.Path));
        Assert.Throws<KeyNotFoundException>(() => _proposals.Save("chat", proposal.Id));
    }
    [Fact]
    public void RejectionReplacementAndStaleSourceCannotSave()
    {
        var first = _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None));
        Assert.True(_proposals.Delete("chat", first.Id));
        var second = _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None);
        Assert.Throws<KeyNotFoundException>(() => _proposals.Save("chat", first.Id));
        File.AppendAllText(Path.Combine(_dir, "source.xlsx"), "changed");
        Assert.Throws<InvalidOperationException>(() => _proposals.Save("chat", second.Id));
        Assert.True(_proposals.Delete("chat", second.Id));
        Assert.False(File.Exists(Path.Combine(_dir, "result.xlsx")));
    }
    [Fact]
    public void InvalidAndUnsupportedEditsFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => _proposals.Propose("", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => _proposals.Propose("chat", "../source.xlsx", "result.xlsx", Patch(), CancellationToken.None));
        var patch = Patch(); patch.Sheets[0].StructuredCells[0].Style = new WorkbookCellStyle();
        Assert.Throws<InvalidDataException>(() => _proposals.Propose("chat", "source.xlsx", "result.xlsx", patch, CancellationToken.None));
        patch = Patch(); patch.Sheets[0].StructuredCells[0].Formula = "WEBSERVICE(\"https://example.com\")";
        Assert.Throws<InvalidDataException>(() => _proposals.Propose("chat", "source.xlsx", "result.xlsx", patch, CancellationToken.None));
        Assert.Empty(_proposals.List("chat"));
    }
    [Fact]
    public void TurnMustCompleteBeforeReviewAndFailureDiscardsOnlyItsProposal()
    {
        var pending = _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None, "turn1");
        Assert.Empty(_proposals.List("chat"));
        Assert.Throws<KeyNotFoundException>(() => _proposals.Save("chat", pending.Id));
        _proposals.CompleteTurn("other", false);
        _proposals.CompleteTurn("turn1", true);
        Assert.Single(_proposals.List("chat"));
        Assert.True(_proposals.Delete("chat", pending.Id));
        _proposals.Propose("chat", "source.xlsx", "result.xlsx", Patch(), CancellationToken.None, "turn2");
        _proposals.CompleteTurn("turn2", false);
        Assert.Empty(_proposals.List("chat"));
    }
    [Fact]
    public async Task ModelToolRequiresReviewAndRejectsNonScalarAndFormulaValues()
    {
        var tool = new DocumentWriteTools(_documents);
        const string prefix = "{\"sourceFileName\":\"source.xlsx\",\"fileName\":\"result.xlsx\",\"sheets\":[{\"name\":\"Sheet1\",\"structuredCells\":[{\"reference\":\"A1\",";
        const string suffix = "}]}]}";
        var invalid = await tool.CallAsync(DocumentWriteTools.EditWorkbookToolName, prefix + "\"value\":{}" + suffix, CancellationToken.None, _proposals, "chat", "turn");
        Assert.StartsWith("Document tool failed:", invalid);
        var formula = await tool.CallAsync(DocumentWriteTools.EditWorkbookToolName, prefix + "\"formula\":\"1+1\"" + suffix, CancellationToken.None, _proposals, "chat", "turn");
        Assert.StartsWith("Document tool failed:", formula);
        var unavailable = await tool.CallAsync(DocumentWriteTools.EditWorkbookToolName, prefix + "\"value\":\"new\"" + suffix, CancellationToken.None);
        Assert.StartsWith("Document tool failed:", unavailable);
        var good = await tool.CallAsync(DocumentWriteTools.EditWorkbookToolName, prefix + "\"value\":\"=1+1\",\"dataType\":\"string\"" + suffix, CancellationToken.None, _proposals, "chat", "turn");
        Assert.Contains("prepared for review", good);
        Assert.DoesNotContain("=1+1", good);
        Assert.Empty(_proposals.List("chat"));
        _proposals.CompleteTurn("turn", true);
        Assert.Contains("=1+1", Assert.Single(_proposals.List("chat")).Cells[0].After);
        Assert.False(File.Exists(Path.Combine(_dir, "result.xlsx")));
    }
    [Fact]
    public async Task ConcurrentGeneratedAndReviewedOutputsNeverOverwriteEachOther()
    {
        var proposal = _proposals.Propose("chat", "source.xlsx", "same.xlsx", Patch(), CancellationToken.None);
        var results = await Task.WhenAll(
            Task.Run(() => _proposals.Save("chat", proposal.Id)),
            Task.Run(() => _documents.WriteWorkbook("same.xlsx", Patch())));
        Assert.NotEqual(results[0].Path, results[1].Path);
        Assert.All(results, r => Assert.True(File.Exists(r.Path)));
        Assert.All(results, r => Assert.True(new FileInfo(r.Path).Length > 0));
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
