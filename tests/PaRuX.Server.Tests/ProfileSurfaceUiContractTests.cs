using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Source-level contracts for the profile-aware shell. Visual behavior and
/// focus order still require real WinUI/FlaUI checks on the Windows developer PC.</summary>
public sealed class ProfileSurfaceUiContractTests
{
    [Fact]
    public void Shell_and_deep_links_share_the_same_profile_filter()
    {
        string mode = Read("src", "PaRuX.App", "MainWindow.ModeTabs.cs");
        string workspaces = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");
        Assert.Contains("GetOrderedAutomationIds(studio, gaming, selected, corporate)", mode, StringComparison.Ordinal);
        Assert.Contains("EnsureWorkspaceFitsSetupProfile()", mode, StringComparison.Ordinal);
        Assert.Contains("IsWorkspaceAllowedForSetup(tag)", workspaces, StringComparison.Ordinal);
        Assert.Contains("if (!IsWorkspaceAllowedForSetup(NavigationItemFor(workspace).Tag as string))", workspaces, StringComparison.Ordinal);
        Assert.Contains("selected.Visibility != Visibility.Visible", workspaces, StringComparison.Ordinal);
        Assert.Contains("WorkspaceSectionHeader.Content = corporate", mode, StringComparison.Ordinal);
    }

    [Fact]
    public void Keyboard_shortcuts_and_the_compact_advanced_control_obey_profile()
    {
        string accessibility = Read("src", "PaRuX.App", "MainWindow.Accessibility.cs");
        string responsive = Read("src", "PaRuX.App", "MainWindow.Responsive.cs");
        string bubble = Read("src", "PaRuX.App", "Windows", "BubbleWindow.xaml.cs");
        Assert.Contains("!PaRuX.Contracts.CorporateMode.IsActive", accessibility, StringComparison.Ordinal);
        Assert.Contains("CurrentSetupPlan.ShowAiWorkspaces", accessibility, StringComparison.Ordinal);
        Assert.Contains("activeProfile, corporate", accessibility, StringComparison.Ordinal);
        Assert.Contains("SetupProfile.GamingOnly", responsive, StringComparison.Ordinal);
        Assert.Contains("AiBubbleAllowedForSetup", bubble, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_hides_inapplicable_chat_controls_but_keeps_documents()
    {
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.SimpleMode.cs");
        string xaml = Read("src", "PaRuX.App", "Views", "ChatView.xaml");
        Assert.Contains("HomeImageSuggestion.Visibility = Visibility.Collapsed", chat, StringComparison.Ordinal);
        Assert.Contains("HomeCodeSuggestion.Visibility = Visibility.Collapsed", chat, StringComparison.Ordinal);
        Assert.Contains("AttachAudioButton.Visibility = Visibility.Collapsed", chat, StringComparison.Ordinal);
        Assert.Contains("VoiceModeButton.Visibility = Visibility.Collapsed", chat, StringComparison.Ordinal);
        Assert.Contains("ProfileSelectorScrollViewer.Visibility = Visibility.Collapsed", chat, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DocumentsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SimpleModeModelPickerButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_filters_categories_search_and_complex_corporate_panels()
    {
        string settings = Read("src", "PaRuX.App", "Views", "SettingsView.xaml.cs");
        string corporate = Read("src", "PaRuX.App", "Views", "SettingsView.CorporateLayout.cs");
        string profile = Read("src", "PaRuX.App", "Views", "SettingsView.SetupProfile.cs");
        Assert.Contains("SettingsCategoryPolicy.VisibleCategories(", settings, StringComparison.Ordinal);
        Assert.Contains("AvailableSettingsCategories", settings, StringComparison.Ordinal);
        Assert.Contains("IsSettingsSectionHidden(element)", settings, StringComparison.Ordinal);
        Assert.Contains("Settings_Corporate_OpenModelHub_Button", corporate, StringComparison.Ordinal);
        Assert.Contains("Settings_CorporatePrivacy_Panel", corporate, StringComparison.Ordinal);
        Assert.Contains("SetupProfileCombo.Visibility = PaRuX.Contracts.CorporateMode.IsActive", profile, StringComparison.Ordinal);
        string mixed = Read("src", "PaRuX.App", "Views", "SettingsView.ProfileSections.cs");
        Assert.Contains("SettingsSectionVisibilityPolicy.IsVisible(", mixed, StringComparison.Ordinal);
        Assert.Contains("Settings_CorporateModels_Panel", mixed, StringComparison.Ordinal);
        Assert.Contains("Settings_CorporatePrivacy_Panel", mixed, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        directory ??= new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("PaRuX.sln missing.");
        return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray()));
    }
}
