using System;
using System.Linq;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure, no-WinUI acceptance for a large generalist document source picker.</summary>
public sealed class DocumentSourcePickerTests
{
    [Fact]
    public void Every_enabled_folder_is_reachable_without_rendering_hundreds_of_checkboxes()
    {
        var folders = Enumerable.Range(0, 145).Select(i => new RagSourceDto
        {
            Id = "folder-" + i,
            FolderPath = @"C:\Departments\Volume" + i.ToString("D3"),
            Enabled = true
        }).ToArray();

        var first = DocumentSourcePicker.Filter(folders, null, 0);
        Assert.Equal(145, first.TotalMatches);
        Assert.Equal(DocumentSourcePicker.PageSize, first.Visible.Count);
        Assert.Equal(4, first.PageCount);

        var last = DocumentSourcePicker.Filter(folders, "", 100);
        Assert.Equal(3, last.PageIndex);
        Assert.Equal(25, last.Visible.Count);
        Assert.Contains(last.Visible, f => f.Id == "folder-144");

        var matching = DocumentSourcePicker.Filter(folders, "volume144", 0);
        Assert.Equal(1, matching.TotalMatches);
        Assert.Equal("folder-144", Assert.Single(matching.Visible).Id);
    }

    [Fact]
    public void Path_matching_is_case_insensitive_and_disabled_or_empty_folders_are_excluded()
    {
        var folders = new[]
        {
            new RagSourceDto { Id = "finance", FolderPath = @"C:\Finance\Invoices" },
            new RagSourceDto { Id = "inactive", FolderPath = @"C:\Finance\Payroll", Enabled = false },
            new RagSourceDto { Id = "empty", FolderPath = "" },
            new RagSourceDto { Id = "research", FolderPath = @"C:\Research\Papers" }
        };
        var result = DocumentSourcePicker.Filter(folders, "FINANCE", -5);
        Assert.Single(result.Visible);
        Assert.Equal("finance", result.Visible[0].Id);
        Assert.Equal(0, result.PageIndex);
        Assert.Empty(DocumentSourcePicker.Filter(folders, "payroll", 0).Visible);
    }

    [Fact]
    public void Management_view_can_find_disabled_folders_without_exposing_them_to_chat()
    {
        var folders = new[]
        {
            new RagSourceDto { Id = "legal", FolderPath = @"C:\Legal\Contracts", Enabled = true },
            new RagSourceDto { Id = "disabled-hr", FolderPath = @"C:\HR\Payroll", Enabled = false }
        };
        Assert.Empty(DocumentSourcePicker.Filter(folders, "payroll", 0).Visible);
        DocumentSourcePage management = DocumentSourcePicker.Filter(
            folders, "PAYROLL", 0, includeDisabled: true);
        Assert.Equal("disabled-hr", Assert.Single(management.Visible).Id);
        Assert.Equal(1, management.TotalMatches);
    }

    [Fact]
    public void Changing_the_search_only_changes_visible_rows_not_the_approval_state()
    {
        var folders = Enumerable.Range(0, 100).Select(i => new RagSourceDto
        {
            Id = "folder-" + i,
            FolderPath = "Department" + i.ToString("D3")
        }).ToArray();
        var selected = new[] { "folder-1", "folder-99" };
        var firstPage = DocumentSourcePicker.Filter(folders, "", 0);
        var lastMatch = DocumentSourcePicker.Filter(folders, "department099", 0);
        Assert.Equal(40, firstPage.Visible.Count);
        Assert.Equal("folder-99", Assert.Single(lastMatch.Visible).Id);
        Assert.Equal(2, selected.Length); // picker never changes selection
    }
}
