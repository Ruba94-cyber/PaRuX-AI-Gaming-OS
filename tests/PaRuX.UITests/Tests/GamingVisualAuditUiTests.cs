using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Visual audit, not a regression gate: captures every Gaming section at several window geometries and writes a clipping report
/// (visible controls whose rectangle leaves the window). Run on demand with <c>--filter Category=VisualAudit</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingVisualAuditUiTests
{
    private static readonly (int W, int H, string Tag)[] Geometries =
    {
        (1280, 800, "wide"), (900, 700, "mid"), (560, 800, "narrow"),
    };

    private static readonly string[] Sections = { "Library", "Add", "Emulators", "Downloads", "Devices" };

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_every_gaming_section_at_each_geometry()
    {
        const string name = nameof(Capture_every_gaming_section_at_each_geometry);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24, includeCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        var report = new List<string>();
        try
        {
            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            foreach (string dismiss in new[] { "GameLibrary_FirstRunScan_Dismiss" })
                if (GamingSectionsPage.TryFind(app.MainWindow, dismiss) is { } d) new InstallProgressPage(app).Invoke(d);
            foreach (var (w, h, tag) in Geometries)
            {
                app.ResizeMainWindow(w, h);
                foreach (string section in Sections)
                {
                    GamingSectionsPage.SelectSection(app.MainWindow, section);
                    Thread.Sleep(900);
                    string shot = $"{tag}-{section}";
                    app.CaptureSnapshot(shot);
                    var window = app.MainWindow.BoundingRectangle;
                    report.Add($"## {shot} window={window}");
                    foreach (var el in app.MainWindow.FindAllDescendants())
                    {
                        try
                        {
                            if (el.Properties.IsOffscreen.ValueOrDefault) continue;
                            string id = el.Properties.AutomationId.ValueOrDefault ?? "";
                            if (id.Length == 0) continue;
                            var r = el.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            if (r.Right > window.Right + 1 || r.Left < window.Left - 1)
                                report.Add($"HCLIP {id} {r} (window {window.Left}..{window.Right})");
                        }
                        catch { }
                    }
                }
            }
            string dir = Path.Combine(Repository.Root, "TestResults", "UIArtifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "gaming-visual-audit-clipping.txt"), report);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
