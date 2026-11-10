using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Cross-file contracts preventing hidden AI/Gaming pages from doing work.
 /// These checks are source-only; FlaUI and real Windows performance measurements
 /// remain required before release.</summary>
public sealed class ProfileLifecycleSourceContractTests
{
    [Fact]
    public void Gaming_library_cancels_queries_and_retries_when_hidden()
    {
        string home = Read("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        string wrapper = Read("src", "PaRuX.App", "Views", "UniversalLibraryPanel.cs");
        string page = Read("src", "PaRuX.App", "Views", "ManualGameLibraryView.cs");
        string shell = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");

        Assert.Contains("internal void CancelPendingQueries()", home, StringComparison.Ordinal);
        Assert.Contains("++_searchEditGeneration", home, StringComparison.Ordinal);
        Assert.Contains("++_generation", home, StringComparison.Ordinal);
        Assert.Contains("_cancellation?.Cancel()", home, StringComparison.Ordinal);
        Assert.Contains("internal Task RefreshWhenShownAsync()", home, StringComparison.Ordinal);
        Assert.Contains("_home.CancelPendingQueries()", wrapper, StringComparison.Ordinal);
        Assert.Contains("_universal?.CancelPendingQueries()", page, StringComparison.Ordinal);
        Assert.Contains("await _universal.RefreshVisibleTitlesAsync()", page, StringComparison.Ordinal);
        Assert.Contains("GameLibraryPage.CancelExpensiveWork()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Gaming_only_hides_every_ai_dependent_command_inside_the_gaming_workspace()
    {
        string home = Read("src", "PaRuX.App", "Views", "GamingHomeView.cs");
        string page = Read("src", "PaRuX.App", "Views", "ManualGameLibraryView.cs");
        string wrapper = Read("src", "PaRuX.App", "Views", "UniversalLibraryPanel.cs");
        Assert.Contains("_localGuide.Visibility = aiEnabled", home, StringComparison.Ordinal);
        Assert.Contains("_gameAssetStudio.Visibility = aiEnabled", home, StringComparison.Ordinal);
        Assert.Contains("_returnToAi.Visibility = gamingOnly", page, StringComparison.Ordinal);
        Assert.Contains("if (GamingOnlySetup || _busy || !_returnToAiAvailable)", page, StringComparison.Ordinal);
        Assert.Contains("_universal?.ApplySetupProfilePresentation()", page, StringComparison.Ordinal);
        Assert.Contains("_home.ApplySetupProfilePresentation()", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_hub_cancels_background_polls_before_hide_and_only_starts_one_initial_refresh()
    {
        string hub = Read("src", "PaRuX.App", "Views", "ModelHubView.cs");
        string shell = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");

        // Hiding stops background polls but keeps a user-started repository check; only a real Unload cancels it.
        Assert.Contains("public void OnHidden(bool keepExplicitDiscovery = true)", hub, StringComparison.Ordinal);
        Assert.Contains("Unloaded += (_, _) => OnHidden(keepExplicitDiscovery: false);", hub, StringComparison.Ordinal);
        Assert.Contains("_downloadPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("_candidateEvaluationPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("_startPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("!_refreshBusy && !_startHubLoaded", hub, StringComparison.Ordinal);
        Assert.Contains("ModelHubPage.OnHidden()", shell, StringComparison.Ordinal);
        Assert.Contains("if (CorporateMode.IsActive) advanced = false", hub, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_default_chat_recovers_missing_saved_role_only_after_text_model_validation()
    {
        string runtime = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string roles = Read("src", "PaRuX.App", "Views", "RoleProfilePanel.cs");

        Assert.Contains("CorporateTextModelChoicePolicy.Resolve(", runtime, StringComparison.Ordinal);
        Assert.Contains("CorporateModelImportPolicy.IsAcceptableModel(", runtime, StringComparison.Ordinal);
        Assert.Contains("Saved Chat model was unavailable", runtime, StringComparison.Ordinal);
        Assert.Contains("CorporateModelImportPolicy.IsAcceptableModel(m.FileName, m.SizeBytes)", roles, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray()));
    }
}
