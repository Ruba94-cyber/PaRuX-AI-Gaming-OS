using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DocumentOpenPlannerTests
{
    private static string? None(string _) => null;
    private static Func<string, string?> Only(params string[] names) => exe => names.Contains(exe) ? @"X:\viewers\" + exe : null;
    private const string Pdf = @"X:\docs\manual.pdf";

    [Fact]
    public void Pdf_page_opens_in_acrobat_when_present()
    {
        OpenPlan plan = DocumentOpenPlanner.Plan(Pdf, 4, null, null, null, null, Only("Acrobat.exe", "msedge.exe"), false, false);
        Assert.Equal(OpenStrategy.PdfAcrobat, plan.Strategy);
        Assert.True(plan.Positioned);
        Assert.Equal(new[] { "/A", "page=4", Pdf }, plan.Arguments);
    }

    [Fact]
    public void Pdf_page_falls_back_to_sumatra_then_edge_with_a_page_fragment()
    {
        Assert.Equal(new[] { "-page", "7", Pdf }, DocumentOpenPlanner.Plan(Pdf, 7, null, null, null, null, Only("SumatraPDF.exe", "msedge.exe"), false, false).Arguments);
        OpenPlan edge = DocumentOpenPlanner.Plan(Pdf, 7, null, null, null, null, Only("msedge.exe"), false, false);
        Assert.Equal(OpenStrategy.PdfEdge, edge.Strategy);
        Assert.EndsWith("manual.pdf#page=7", edge.Arguments[0]);
        Assert.StartsWith("file:///", edge.Arguments[0]);
    }

    [Fact]
    public void Pdf_without_any_known_viewer_just_opens_the_file_and_says_where_to_look()
    {
        OpenPlan plan = DocumentOpenPlanner.Plan(Pdf, 4, null, null, null, null, None, false, false);
        Assert.Equal(OpenStrategy.Shell, plan.Strategy);
        Assert.False(plan.Positioned);
        Assert.Contains("page 4", plan.Message);
        Assert.Contains("cannot jump", plan.Message);
    }

    [Fact]
    public void Excel_sheet_and_range_use_office_automation_only_when_excel_is_installed()
    {
        OpenPlan withExcel = DocumentOpenPlanner.Plan(@"X:\docs\data.xlsx", null, "Sheet1", "A1:F50", null, null, None, true, false);
        Assert.Equal(OpenStrategy.ExcelCom, withExcel.Strategy);
        Assert.Equal(new[] { @"X:\docs\data.xlsx", "Sheet1", "A1:F50" }, withExcel.Arguments);
        Assert.Contains("read-only", withExcel.Message);
        OpenPlan without = DocumentOpenPlanner.Plan(@"X:\docs\data.xlsx", null, "Sheet1", "A1:F50", null, null, None, false, false);
        Assert.Equal(OpenStrategy.Shell, without.Strategy);
        Assert.Contains("Sheet1!A1:F50", without.Message);
    }

    [Fact]
    public void Powerpoint_slide_uses_automation_and_word_headings_are_reported_not_faked()
    {
        Assert.Equal(OpenStrategy.PowerPointCom, DocumentOpenPlanner.Plan(@"X:\d\deck.pptx", null, null, null, 3, null, None, false, true).Strategy);
        OpenPlan word = DocumentOpenPlanner.Plan(@"X:\d\spec.docx", null, null, null, null, "Intro > Scope", None, true, true);
        Assert.Equal(OpenStrategy.Shell, word.Strategy);
        Assert.False(word.Positioned);
        Assert.Contains("Intro > Scope", word.Message);
    }

    [Fact]
    public void No_position_means_a_plain_open()
    {
        OpenPlan plan = DocumentOpenPlanner.Plan(@"X:\d\notes.txt", null, null, null, null, null, None, true, true);
        Assert.Equal(OpenStrategy.Shell, plan.Strategy);
        Assert.Equal("Opened the file.", plan.Message);
    }

    [Fact]
    public void Automation_scripts_take_values_as_parameters_not_as_spliced_text()
    {
        Assert.Contains("param($Path,$Sheet,$Cell)", DocumentOpenPlanner.ExcelScript);
        Assert.Contains("Open($Path,0,$true)", DocumentOpenPlanner.ExcelScript);   // read-only
        Assert.Contains("param($Path,$Slide)", DocumentOpenPlanner.PowerPointScript);
        Assert.Contains("Open($Path,-1,0,-1)", DocumentOpenPlanner.PowerPointScript);   // read-only
    }
}
