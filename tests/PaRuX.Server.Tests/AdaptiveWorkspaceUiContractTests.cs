using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AdaptiveWorkspaceUiContractTests
{
    [Fact]
    public void Settings_has_an_accessible_compact_category_selector_without_removing_the_rail()
    {
        XDocument doc = XDocument.Load(SourcePath("SettingsView.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(doc.Descendants(ui + "Grid")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsLayoutGrid"));
        Assert.Equal("SettingsLayoutGrid_SizeChanged", (string?)grid.Attribute("SizeChanged"));
        // Contract update (29eedfd): the picker lives in the header StackPanel (Row 0, Column 1) that
        // also holds the settings search box, so it is no longer a direct child of the grid.
        var picker = Assert.Single(grid.Descendants(ui + "ComboBox")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsCompactCategoryPicker"));
        XElement header = Assert.IsType<XElement>(picker.Parent);
        Assert.Equal(ui + "StackPanel", header.Name);
        Assert.Same(grid, header.Parent);
        Assert.Equal("0", (string?)header.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)header.Attribute("Grid.Column"));
        Assert.Equal("Collapsed", (string?)picker.Attribute("Visibility"));
        Assert.Equal("SettingsCompactCategoryPicker_SelectionChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Single(grid.Elements(ui + "ScrollViewer")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsRailScroll"));
        Assert.Single(grid.Elements(ui + "ScrollViewer")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsContentScroll"));
    }

    [Fact]
    public void Settings_switches_category_without_eagerly_refreshing_hidden_panels()
    {
        string code = File.ReadAllText(SourcePath("SettingsView.xaml.cs"));
        int onShown = code.IndexOf("public void OnShown()", StringComparison.Ordinal);
        int refreshMethod = code.IndexOf("private void RefreshVisibleSettingsCategory()", onShown, StringComparison.Ordinal);
        Assert.True(onShown > 0 && refreshMethod > onShown);
        string body = code.Substring(onShown, refreshMethod - onShown);
        Assert.Contains("RefreshVisibleSettingsCategory();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshManagedModelsAsync()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshMediaCheckpointsAsync()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshAgentTasksAsync()", body, StringComparison.Ordinal);

        Assert.Contains("SettingsRailColumn.Width = new GridLength", code, StringComparison.Ordinal);
        Assert.Contains("SettingsCompactCategoryPicker.SelectedIndex = selectedIndex", code, StringComparison.Ordinal);
        Assert.Contains("if (_settingsCategoryRefreshEnabled)", code, StringComparison.Ordinal);
        foreach (string category in new[]
        {
            "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation",
            "Automation", "Agents", "Integrations", "Gaming & AI Sharing", "PaRuX Platform",
            "Privacy & Network", "Data", "Advanced"
        })
            Assert.Contains("case \"" + category + "\":", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_keeps_the_primary_steps_visible_and_detailed_setup_optional()
    {
        XDocument doc = XDocument.Load(SourcePath("OnboardingView.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expanders = doc.Descendants(ui + "Expander").ToArray();
        Assert.Contains(expanders, element => (string?)element.Attribute("Header") == "What's included in PaRuX"
            && (string?)element.Attribute("IsExpanded") == "False");
        Assert.Contains(expanders, element => (string?)element.Attribute("Header") == "Advanced: runtime storage folder"
            && (string?)element.Attribute("IsExpanded") == "False");
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "AutoSetupStartButton"));
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "FirstModelInstallButton"));
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "VerifyLocalChatButton"));

        // The short guided path is visible without scrolling through native runtime inventory.
        var manual = Assert.Single(expanders.Where(element =>
            (string?)element.Attribute(x + "Name") == "ManualSetupExpander"));
        Assert.Equal("False", (string?)manual.Attribute("IsExpanded"));
        var components = Assert.Single(doc.Descendants(ui + "StackPanel").Where(element =>
            (string?)element.Attribute(x + "Name") == "ComponentsList"));
        var modelButton = Assert.Single(doc.Descendants(ui + "Button").Where(element =>
            (string?)element.Attribute(x + "Name") == "FirstModelInstallButton"));
        var ready = Assert.Single(doc.Descendants(ui + "Button").Where(element =>
            (string?)element.Attribute(x + "Name") == "VerifyLocalChatButton"));
        Assert.Contains(manual, components.Ancestors());
        Assert.Contains(manual, modelButton.Ancestors());
        Assert.DoesNotContain(manual, ready.Ancestors());
        var scroll = Assert.Single(doc.Descendants(ui + "ScrollViewer"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void Manual_first_model_browsing_is_neutral_and_empty_handoff_loads_hub()
    {
        string firstModel = File.ReadAllText(SourcePath("OnboardingView.FirstModel.cs"));
        string nav = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath("OnboardingView.xaml"))!, "..", "MainWindow.Workspaces.cs"));
        Assert.DoesNotContain("_selectedFirstModelId = advice.RecommendedId", firstModel, StringComparison.Ordinal);
        Assert.Contains("SuggestedQuantHintForHandoff = null;", firstModel, StringComparison.Ordinal);
        Assert.Contains("FindModelRequested?.Invoke(string.Empty)", firstModel, StringComparison.Ordinal);
        Assert.Contains("Browse models in Model Hub", firstModel, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrWhiteSpace(repoId))", nav, StringComparison.Ordinal);
        Assert.Contains("else ModelHubPage.OnShownBrowsingChatModels();", nav, StringComparison.Ordinal);
        Assert.Contains("ModelHubPage.OnShown();", nav, StringComparison.Ordinal);
        string hub = File.ReadAllText(SourcePath("ModelHubView.cs"));
        int start = hub.IndexOf("public async void OnShownBrowsingChatModels()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = hub.IndexOf("public async void OnShownSearching(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string browse = hub.Substring(start, end - start);
        Assert.Contains("ShowHubTab(ModelHubTab.Text)", browse, StringComparison.Ordinal);
        Assert.Contains("_search.Text = string.Empty", browse, StringComparison.Ordinal);
        Assert.Contains("InvalidatePendingSearch()", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAsync(", browse, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_first_run_uses_preinstalled_cpu_and_hides_generic_auto_installer()
    {
        XDocument doc = XDocument.Load(SourcePath("OnboardingView.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string group in new[] { "AutoSetupSection", "BundledDetailsSection", "StorageDetailsSection" })
            Assert.Single(doc.Descendants(ui + "StackPanel").Where(e => (string?)e.Attribute(x + "Name") == group));
        var manage = Assert.Single(doc.Descendants(ui + "Button")
            .Where(e => (string?)e.Attribute(x + "Name") == "CorporateManageModelsButton"));
        Assert.Equal("Collapsed", (string?)manage.Attribute("Visibility"));

        string setup = File.ReadAllText(SourcePath("OnboardingView.SetupProfile.cs"));
        string page = File.ReadAllText(SourcePath("OnboardingView.xaml.cs"));
        string nav = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath("OnboardingView.xaml"))!, "..", "MainWindow.Workspaces.cs"));
        Assert.Contains("AutoSetupSection.Visibility = Visibility.Collapsed", setup, StringComparison.Ordinal);
        Assert.Contains("ManualSetupExpander.Visibility = Visibility.Collapsed", setup, StringComparison.Ordinal);
        Assert.Contains("CorporateManageModelsButton.Visibility = Visibility.Visible", setup, StringComparison.Ordinal);
        Assert.Contains("FindModelRequested?.Invoke(string.Empty)", page, StringComparison.Ordinal);
        Assert.Contains("if (PaRuX.Contracts.CorporateMode.IsActive)", page, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrWhiteSpace(repoId))", nav, StringComparison.Ordinal);
        Assert.Contains("ModelHubPage.OnShown();", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_download_progress_preserves_rows_and_keyboard_focus_during_polling()
    {
        string code = File.ReadAllText(SourcePath("OnboardingView.xaml.cs"));
        Assert.Contains("_downloadRowsById.TryGetValue(job.Id", code, StringComparison.Ordinal);
        Assert.Contains("PositionDownloadRow(previous.Row, index)", code, StringComparison.Ordinal);
        Assert.Contains("if (previous.Summary.Text != summaryText)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstModelDownloadRows.Children.Clear()", code, StringComparison.Ordinal);
        Assert.Contains("while (FirstModelDownloadRows.Children.Count > ordered.Count)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_hub_keeps_responsive_search_and_only_loads_advanced_evaluation_on_demand()
    {
        string source = File.ReadAllText(SourcePath("ModelHubView.cs"));
        Assert.Contains("var searchRow = new StackPanel { Spacing = 8 };", source, StringComparison.Ordinal);
        Assert.Contains("_search.KeyDown += async", source, StringComparison.Ordinal);
        Assert.Contains("Windows.System.VirtualKey.Enter", source, StringComparison.Ordinal);
        Assert.Contains("bool becameAdvanced = advanced && !_isAdvancedMode;", source, StringComparison.Ordinal);
        Assert.Contains("Task.FromResult<CandidateUtilityPolicyStatus?>(null)", source, StringComparison.Ordinal);
        Assert.Contains("Task.FromResult<CandidateRecommendationStatusResponse?>(null)", source, StringComparison.Ordinal);
        Assert.Contains("Task.FromResult<List<CandidateEvaluationJobStatus>?>(null)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_checkpoint_form_stacks_inputs_instead_of_overflowing_a_small_display()
    {
        string source = File.ReadAllText(SourcePath("ModelHubView.Tabs.cs"));
        Assert.Contains("var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };",
            source, StringComparison.Ordinal);
        Assert.Contains("_hfRepo.MinWidth = 0;", source, StringComparison.Ordinal);
        Assert.Contains("_hfFile.MinWidth = 0;", source, StringComparison.Ordinal);
        Assert.Contains("_hfRepo.HorizontalAlignment = HorizontalAlignment.Stretch;", source, StringComparison.Ordinal);
        Assert.Contains("_hfFile.HorizontalAlignment = HorizontalAlignment.Stretch;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_never_completes_gaming_mode_after_a_failed_service_switch()
    {
        string source = File.ReadAllText(SourcePath("OnboardingView.SetupProfile.cs"));
        Assert.Contains("if (!result.Success || result.EffectiveProfile != SetupProfile.GamingOnly)", source, StringComparison.Ordinal);
        Assert.Contains("if (_setupProfileInitialising || _setupProfileSwitchBusy", source, StringComparison.Ordinal);
        Assert.Contains("|| PaRuX.Contracts.CorporateMode.IsActive) return;", source, StringComparison.Ordinal);
        Assert.Contains("SetupProfileChoice.IsEnabled = false;", source, StringComparison.Ordinal);
        Assert.Contains("Mode change failed; previous mode retained.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_model_picker_keeps_real_model_identity_and_hides_technical_lane_settings()
    {
        string code = File.ReadAllText(SourcePath("MediaModelRow.cs"));
        Assert.Contains("MediaModelPickerPresenter.CompactDisplayLabel(item.Label)", code, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(option, item.Label)", code, StringComparison.Ordinal);
        Assert.Contains("idPrefix + \"_ModelDetails_Button\"", code, StringComparison.Ordinal);
        Assert.Contains("_idPrefix + \"_FullModelIdentity_TextBox\"", code, StringComparison.Ordinal);
        Assert.Contains("idPrefix + \"_ContentLane_Expander\"", code, StringComparison.Ordinal);
        Assert.Contains("IsExpanded = false", code, StringComparison.Ordinal);
        Assert.Contains("MediaModelPickerPresenter.ActionFor(_items[_model.SelectedIndex])", code, StringComparison.Ordinal);
    }

    private static string SourcePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        directory ??= new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("PaRuX.sln could not be located.");
        return Path.Combine(directory.FullName, "src", "PaRuX.App", "Views", fileName);
    }
}
