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

    private static readonly string[] AiWorkspaces =
    {
        "Navigation_Chat_Item", "Navigation_ModelHub_Item", "Navigation_Create_Item", "Navigation_Projects_Item",
        "Navigation_Tools_Item", "Navigation_Settings_Item",
    };

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_every_ai_workspace_at_each_geometry()
    {
        const string name = nameof(Capture_every_ai_workspace_at_each_geometry);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            foreach (var (w, h, tag) in Geometries)
            {
                app.ResizeMainWindow(w, h);
                foreach (string id in AiWorkspaces)
                {
                    var item = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
                    if (item == null) continue;
                    var selection = item.Patterns.SelectionItem.PatternOrDefault;
                    if (selection != null) selection.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke();
                    Thread.Sleep(1200);
                    app.CaptureSnapshot($"ai-{tag}-{id.Replace("Navigation_", "").Replace("_Item", "")}");
                }
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_console_tv_and_italian_gaming()
    {
        const string name = nameof(Capture_console_tv_and_italian_gaming);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24, includeCollections: true);
        string settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"OnboardingCompleted\"", "\"UiLanguage\":\"it\",\"OnboardingCompleted\""));
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            if (GamingSectionsPage.TryFind(app.MainWindow, "GameLibrary_FirstRunScan_Dismiss") is { } d) page.Invoke(d);
            app.ResizeMainWindow(1280, 800);
            foreach (string section in new[] { "Library", "Emulators", "Devices" })
            {
                GamingSectionsPage.SelectSection(app.MainWindow, section);
                Thread.Sleep(900);
                app.CaptureSnapshot("it-" + section);
            }
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ConsoleMode_Button"));
            Thread.Sleep(2500);
            app.CaptureSnapshot("tv-Library-it");
            for (int i = 0; i < 3; i++) { FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN); Thread.Sleep(300); }
            app.CaptureSnapshot("tv-Library-it-after-down");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_playnite_migration_with_a_real_library_copy()
    {
        const string name = nameof(Capture_playnite_migration_with_a_real_library_copy);
        string? library = Environment.GetEnvironmentVariable("PARUX_AUDIT_PLAYNITE_COPY");
        if (string.IsNullOrWhiteSpace(library) || !File.Exists(Path.Combine(library, "games.db"))) return;
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(0);
        using var app = new UiTestApplication(name, restartDataRoot: root,
            extraEnvironment: new Dictionary<string, string> { ["PARUX_PLAYNITE_LIBRARY"] = library });
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            app.ResizeMainWindow(1280, 800);
            GamingSectionsPage.SelectSection(app.MainWindow, "Add");
            page.Invoke(UiWait.ById(app.MainWindow, "GameImport_Playnite_Look_Button"));
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Playnite_Import_Button")) != null, "import offered");
            Thread.Sleep(600);
            app.CaptureSnapshot("playnite-preview");
            page.Invoke(UiWait.ById(app.MainWindow, "GameImport_Playnite_Import_Button"));
            UiWait.For(() => (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Playnite_Summary"))?.Name ?? "").StartsWith("Imported", StringComparison.Ordinal), "imported");
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            Thread.Sleep(2500);
            app.CaptureSnapshot("playnite-library-after-import");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_model_hub_start_card()
    {
        const string name = nameof(Capture_model_hub_start_card);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            app.ResizeMainWindow(1280, 800);
            var item = UiWait.ById(app.MainWindow, "Navigation_ModelHub_Item");
            var selection = item.Patterns.SelectionItem.PatternOrDefault;
            if (selection != null) selection.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke();
            Thread.Sleep(4000);
            app.CaptureSnapshot("modelhub-start");
            File.WriteAllLines(Path.Combine(Repository.Root, "TestResults", "UIArtifacts", "modelhub-start-texts.txt"),
                app.MainWindow.FindAllDescendants().Select(e => { try { return e.Name; } catch { return ""; } }).Where(n => n.Contains("Suggested", StringComparison.OrdinalIgnoreCase) || n.Contains("Suggerito", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_ai_workspaces_in_italian()
    {
        const string name = nameof(Capture_ai_workspaces_in_italian);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name, uiLanguage: "it");
        try
        {
            app.ResizeMainWindow(1280, 800);
            foreach (string id in AiWorkspaces)
            {
                var item = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (item == null) continue;
                var selection = item.Patterns.SelectionItem.PatternOrDefault;
                if (selection != null) selection.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke();
                Thread.Sleep(1500);
                app.CaptureSnapshot("it-" + id.Replace("Navigation_", "").Replace("_Item", ""));
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    /// <summary>The detail view can briefly re-raise UIA events while it settles; retry the snapshot instead of failing the audit.</summary>
    private static void Snap(UiTestApplication app, string name)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { app.CaptureSnapshot(name); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { Thread.Sleep(1200); }
        }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_game_detail_with_a_real_library_copy()
    {
        const string name = nameof(Capture_game_detail_with_a_real_library_copy);
        string? library = Environment.GetEnvironmentVariable("PARUX_AUDIT_PLAYNITE_COPY");
        if (string.IsNullOrWhiteSpace(library) || !File.Exists(Path.Combine(library, "games.db"))) return;
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(0);
        using var app = new UiTestApplication(name, restartDataRoot: root,
            extraEnvironment: new Dictionary<string, string> { ["PARUX_PLAYNITE_LIBRARY"] = library });
        try
        {
            var page = new InstallProgressPage(app);
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            app.ResizeMainWindow(1280, 800);
            GamingSectionsPage.SelectSection(app.MainWindow, "Add");
            page.Invoke(UiWait.ById(app.MainWindow, "GameImport_Playnite_Look_Button"));
            page.Invoke(UiWait.ById(app.MainWindow, "GameImport_Playnite_Import_Button"));
            UiWait.For(() => (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameImport_Playnite_Summary"))?.Name ?? "").StartsWith("Imported", StringComparison.Ordinal), "imported");
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            var tile = UiWait.ById(app.MainWindow, "GameTile_mariokart8deluxe");
            UiWait.ForOnScreen(tile, "tile on screen");
            DateTime lastClick = DateTime.MinValue;
            UiWait.For(() =>
            {
                if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameDetail_Panel")) != null) return true;
                if (DateTime.UtcNow - lastClick > TimeSpan.FromSeconds(2))
                {
                    lastClick = DateTime.UtcNow;
                    try { UiWait.ById(app.MainWindow, "GameTile_mariokart8deluxe").Click(); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException) { }
                }
                return false;
            }, "detail panel");
            Thread.Sleep(1500);
            Snap(app, "detail-wide");
            app.ResizeMainWindow(700, 800);
            Thread.Sleep(1500);
            Snap(app, "detail-narrow");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_settings_create_and_model_hub_pages()
    {
        const string name = nameof(Capture_settings_create_and_model_hub_pages);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            void Nav(string id)
            {
                var item = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (item == null) return;
                var selection = item.Patterns.SelectionItem.PatternOrDefault;
                if (selection != null) selection.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke();
                Thread.Sleep(1200);
            }
            void Pick(string id)
            {
                var el = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (el == null) return;
                var sel = el.Patterns.SelectionItem.PatternOrDefault;
                if (sel != null) sel.Select(); else el.Patterns.Invoke.PatternOrDefault?.Invoke();
                Thread.Sleep(900);
            }
            app.ResizeMainWindow(1280, 800);
            Nav("Navigation_Settings_Item");
            foreach (string category in new[] { "Models", "ChatBehavior", "YourPCapps", "MediaGeneration", "Automation", "Agents", "Integrations", "GamingAISharing", "PaRuXPlatform", "PrivacyNetwork", "Data", "Advanced" })
            {
                Pick("Settings_Category_" + category + "_Button");
                Snap(app, "settings-" + category);
            }
            Nav("Navigation_Create_Item");
            foreach (string mode in new[] { "Video", "Music", "Edit", "3D", "Speech", "Workflows", "Gallery" })
            {
                Pick("Create_Mode_" + mode);
                Snap(app, "create-" + mode);
            }
            Nav("Navigation_ModelHub_Item");
            foreach (string tab in new[] { "ModelHub_Tab_Text", "ModelHub_Tab_Media", "ModelHub_Tab_Downloads" })
            {
                Pick(tab);
                Snap(app, "modelhub-" + tab.Replace("ModelHub_Tab_", ""));
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "VisualAudit"), Trait("Category", "FlaUI")]
    public void Capture_new_overlays_palette_prompts_and_insights()
    {
        const string name = nameof(Capture_new_overlays_palette_prompts_and_insights);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(24, includeCollections: true);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            var page = new InstallProgressPage(app);
            app.ResizeMainWindow(1280, 800);
            // Ctrl+K palette
            app.MainWindow.SetForeground();
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_K);
            UiWait.ById(app.MainWindow, "CommandPalette_Search");
            Thread.Sleep(700);
            Snap(app, "overlay-palette");
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);
            // Prompts flyout
            page.Invoke(UiWait.ById(app.MainWindow, "Chat_Prompts_Button"));
            Thread.Sleep(700);
            Snap(app, "overlay-prompts");
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);
            // Gaming insights
            page.Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
            page.Invoke(UiWait.ById(app.MainWindow, "GameHome_Insights_Button"));
            UiWait.ById(app.MainWindow, "GameHome_Insights_Dialog");
            Thread.Sleep(700);
            Snap(app, "overlay-insights");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
