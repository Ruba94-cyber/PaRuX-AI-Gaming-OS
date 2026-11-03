using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GrapheneGamingDesignRegressionTests
{
    private static string Source(params string[] parts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Tile_hover_and_selection_are_graphene_and_no_background_work_is_added()
    {
        string code = Source("src", "PaRuX.App", "Views", "GameTileGrid.cs");
        Assert.Contains("PaRuXGrapheneSelectedBrush", code);
        Assert.Contains("PaRuXStrokeStrongBrush", code);
        Assert.Contains("card.PointerEntered", code);
        Assert.Contains("card.PointerExited", code);
        Assert.Contains("PlayRequested?.Invoke", code);
        Assert.Contains("GameTileConfigure_", code);
        Assert.Contains("GameContinueConfigure_", code);
        Assert.Contains("OpenRequested?.Invoke(current.Title)", code);
        Assert.Contains("Visibility = _selectMode ? Visibility.Collapsed : Visibility.Visible", code);
        Assert.Contains("IsEnabled = !_selectMode && GameUi.CanStart(chosen)", code);
        Assert.DoesNotContain("LinearGradientBrush", code);
        Assert.DoesNotContain("Task.Delay", code);
    }

    [Fact]
    public void Changing_graphene_theme_repaints_tiles_without_destroying_focus_or_cover_images()
    {
        string code = Source("src", "PaRuX.App", "Views", "GameTileGrid.cs");
        Assert.Contains("ActualThemeChanged += (_, _) => RepaintTheme()", code);
        Assert.DoesNotContain("ActualThemeChanged += (_, _) => { if (_titles.Count > 0) Render(", code);
        Assert.Contains("parts.RepaintTheme();", code);
        Assert.Contains("RepaintConfigure(configure);", code);
        Assert.Contains("if (!hasImage) surface.Background", code);
        Assert.Contains("badgeInk.Foreground = GameUi.ThemeBrush(this, \"PaRuXOnBrandBrush\")", code);
        Assert.Contains("var bitmap = new BitmapImage", code);
    }

    [Fact]
    public void Detail_theme_switch_must_not_rebuild_or_discard_unsaved_tag_collection_edits()
    {
        string code = Source("src", "PaRuX.App", "Views", "GameDetailPanel.cs");
        Assert.Contains("ActualThemeChanged += (_, _) => ApplyBrushes();", code);
        Assert.DoesNotContain("ActualThemeChanged += (_, _) => { ApplyBrushes(); if (_title is not null) _ = ShowAsync(_title); };", code);
        Assert.Contains("_semanticLabels.Add((block, secondary));", code);
        Assert.Contains("foreach (var (label, secondary) in _semanticLabels)", code);
        Assert.Contains("_fallbackCoverInitials.Foreground = GameUi.ThemeBrush", code);
        Assert.Contains("GameTitle_Tags_", code);
        Assert.Contains("GameTitle_Collections_", code);
    }

    [Fact]
    public void Empty_library_has_a_recoverable_graphene_state_without_mutating_game_entries()
    {
        string home = Source("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        Assert.Contains("GameHome_EmptyState", home);
        Assert.Contains("GameHome_ClearFilters", home);
        Assert.Contains("ClearFiltersAsync()", home);
        Assert.Contains("++_searchEditGeneration", home);
        Assert.Contains("SavePreferences();", home);
        Assert.Contains("_emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed", home);
        Assert.Contains("_emptyState.Visibility = Visibility.Collapsed;", home);
        Assert.Contains("The game list is unavailable right now.", home);
        Assert.Contains("await ReloadAsync();", home);
        Assert.DoesNotContain("RemoveManualGameAsync", home);
    }

    [Fact]
    public void Gaming_library_preserves_explicit_launch_and_detail_flows()
    {
        string home = Source("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        Assert.Contains("await _launch(chosen)", home);
        Assert.Contains("GameHome_Continue_Shelf", home);
        Assert.Contains("GameHome_Grid", home);
        Assert.Contains("PaRuXTextSecondaryBrush", home);
        Assert.Contains("_titleHeading.Foreground = GameUi.ThemeBrush(this, \"PaRuXTextPrimaryBrush\")", home);
        Assert.Contains("_introDescription.Foreground = GameUi.ThemeBrush(this, \"PaRuXTextSecondaryBrush\")", home);
        Assert.Contains("ActualThemeChanged += (_, _) => { ApplyBrushes(); }", home);
        string detail = Source("src", "PaRuX.App", "Views", "GameDetailPanel.cs");
        Assert.Contains("GameDetail_AlternateNotice", detail);
    }
}
