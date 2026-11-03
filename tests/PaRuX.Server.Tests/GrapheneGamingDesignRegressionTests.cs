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
    public void Gaming_library_preserves_explicit_launch_and_detail_flows()
    {
        string home = Source("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        Assert.Contains("await _launch(chosen)", home);
        Assert.Contains("GameHome_Continue_Shelf", home);
        Assert.Contains("GameHome_Grid", home);
        Assert.Contains("PaRuXTextSecondaryBrush", home);
        string detail = Source("src", "PaRuX.App", "Views", "GameDetailPanel.cs");
        Assert.Contains("GameDetail_AlternateNotice", detail);
    }
}
