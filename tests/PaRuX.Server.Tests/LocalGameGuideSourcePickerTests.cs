using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure source-picker regressions; real WinUI/FlaUI remain a separate Windows gate.</summary>
public sealed class LocalGameGuideSourcePickerTests
{
    [Fact]
    public void Every_indexed_folder_remains_searchable_beyond_initial_64()
    {
        LocalGameGuideSourceOption[] folders = Enumerable.Range(0, 140)
            .Select(n => new LocalGameGuideSourceOption("source-" + n,
                @"C:\Guides\Volume" + n.ToString("D3")))
            .ToArray();

        LocalGameGuideSourcePage initial = LocalGameGuideSourcePicker.Filter(folders, "");
        Assert.Equal(140, initial.TotalMatches);
        Assert.Equal(64, initial.Visible.Count);
        Assert.DoesNotContain(initial.Visible, x => x.Id == "source-139");

        LocalGameGuideSourcePage filtered = LocalGameGuideSourcePicker.Filter(folders, "volume139");
        Assert.Single(filtered.Visible);
        Assert.Equal("source-139", filtered.Visible[0].Id);
        Assert.Equal(1, filtered.TotalMatches);

        Assert.Equal("source-139", LocalGameGuideSourcePicker.Filter(folders, "SOURCE-139").Visible.Single().Id);
    }

    [Fact]
    public void Explicit_selection_survives_filter_changes_and_can_be_deselected()
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "source-1", true));
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "source-139", true));
        LocalGameGuideSourcePicker.Filter(new[]
        {
            new LocalGameGuideSourceOption("source-1", "Manual One"),
            new LocalGameGuideSourceOption("source-139", "Manual Two")
        }, "Two");
        Assert.Equal(2, selected.Count);
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "source-1", false));
        Assert.Equal(new[] { "source-139" }, selected);
    }

    [Fact]
    public void Excess_or_invalid_selections_are_refused_without_dropping_existing_consent()
    {
        var selected = new HashSet<string>(Enumerable.Range(0, 64).Select(n => "id-" + n),
            StringComparer.Ordinal);
        Assert.False(LocalGameGuideSourcePicker.TrySetSelected(selected, "id-65", true));
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "id-10", true));
        Assert.False(LocalGameGuideSourcePicker.TrySetSelected(selected, "bad\nsource", true));
        Assert.False(LocalGameGuideSourcePicker.TrySetSelected(selected, "", true));
        Assert.Equal(64, selected.Count);
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "id-10", false));
        Assert.True(LocalGameGuideSourcePicker.TrySetSelected(selected, "id-65", true));
        Assert.Equal(64, selected.Count);
    }
}
