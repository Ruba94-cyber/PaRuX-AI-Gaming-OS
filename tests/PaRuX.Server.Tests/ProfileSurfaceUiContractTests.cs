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

    [Fact]
    public void Corporate_home_is_generic_and_does_not_override_the_local_only_message_on_load()
    {
        string home = Read("src", "PaRuX.App", "Views", "ChatView.HomeLocalization.cs");
        Assert.Contains("bool corporate = PaRuX.Contracts.CorporateMode.IsActive;", home, StringComparison.Ordinal);
        Assert.Contains("Ask a question or work with approved documents.", home, StringComparison.Ordinal);
        Assert.Contains("distinguishing facts, assumptions and missing information", home, StringComparison.Ordinal);
        Assert.Contains("key facts, deadlines and items to verify", home, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_workspaces_are_responsive_scoped_and_draft_safe()
    {
        string search = Read("src", "PaRuX.App", "Views", "DocumentSearchView.cs");
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.Documents.cs");
        string client = Read("src", "PaRuX.App", "Services", "KnowledgeClient.cs");
        Assert.Contains("DocSearch_SearchActions_Panel", search, StringComparison.Ordinal);
        Assert.Contains("DocSearch_FolderFilter_TextBox", search, StringComparison.Ordinal);
        Assert.Contains("DocSearch_NextFolderPage_Button", search, StringComparison.Ordinal);
        Assert.Contains("DocumentSourcePicker.Filter(", search, StringComparison.Ordinal);
        Assert.Contains("includeDisabled: true", search, StringComparison.Ordinal);
        Assert.Contains("page.Visible.Where(", search, StringComparison.Ordinal);
        Assert.Contains("_folderFilterDelay.Stop()", search, StringComparison.Ordinal);
        Assert.Contains("Disabled for Chat", search, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim(4)", search, StringComparison.Ordinal);
        Assert.Contains("cached.State is \"running\" or \"paused\"", search, StringComparison.Ordinal);
        Assert.Contains("GridUnitType.Star", search, StringComparison.Ordinal);
        Assert.Contains("if (row.ActionState == st.State) return;", search, StringComparison.Ordinal);
        Assert.Contains("DocSearch_RemoveConfirm_Dialog", search, StringComparison.Ordinal);
        Assert.Contains("DocSearch_FolderProgress_", search, StringComparison.Ordinal);
        Assert.Contains("int? CurrentPage = null, int? TotalPages = null", client, StringComparison.Ordinal);
        Assert.Contains("Chat_Documents_Sources_Scroll", chat, StringComparison.Ordinal);
        Assert.Contains("Turning Documents on with no selection uses all enabled folders.", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_Documents_SourceFilter_TextBox", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_Documents_NextPage_Button", chat, StringComparison.Ordinal);
        Assert.Contains("DocumentSourcePicker.Filter(", chat, StringComparison.Ordinal);
        Assert.Contains("ChatDocumentScopeState.Prune(", chat, StringComparison.Ordinal);
        Assert.Contains("DocumentSearchPage.OnHidden()", Read("src", "PaRuX.App", "MainWindow.Workspaces.cs"), StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(InputBox.Text)", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_Documents_Skill_Summary", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void Android_chat_only_discards_draft_after_successful_local_submission_admission()
    {
        string screen = Read("src", "PaRuX.Android", "app", "src", "main", "java", "com", "parux", "android", "ui", "chat", "ChatScreen.kt");
        string model = Read("src", "PaRuX.Android", "app", "src", "main", "java", "com", "parux", "android", "ui", "chat", "ChatViewModel.kt");
        Assert.Contains("if (accepted)", screen, StringComparison.Ordinal);
        Assert.Contains("rememberSaveable { mutableStateOf(\"\") }", screen, StringComparison.Ordinal);
        Assert.Contains("localCore == null", screen, StringComparison.Ordinal);
        Assert.Contains("your unsent text stays here", screen, StringComparison.Ordinal);
        Assert.Contains("val localCoreConnection = container.client", model, StringComparison.Ordinal);
        Assert.Contains("enabled = text.isNotBlank() || hasAttachment", screen, StringComparison.Ordinal);
        Assert.Contains("MAX_CHAT_IMAGE_BYTES = 8 * 1024 * 1024", screen, StringComparison.Ordinal);
        Assert.Contains("ByteArrayOutputStream()", screen, StringComparison.Ordinal);
        Assert.Contains("fun sendMessage(text: String, image: PendingImage? = null, oneShotSkillId: String? = null): Boolean", model, StringComparison.Ordinal);
        Assert.Contains("if (_state.value.gamingMode)", model, StringComparison.Ordinal);
        Assert.Contains("if (container.client.value == null)", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_and_portable_chat_has_a_bounded_local_model_menu_and_full_search_browser()
    {
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.SimpleMode.cs");
        Assert.Contains("ChatModelPickerPolicy.Quick(_chatModelCatalog, pinnedFile)", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_SimpleMode_ModelPicker_BrowseAll", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_ModelBrowser_Filter_TextBox", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_ModelBrowser_Choices_List", chat, StringComparison.Ordinal);
        Assert.Contains("ChatModelPickerPolicy.Browse(_chatModelCatalog, query.Text, pageIndex)", chat, StringComparison.Ordinal);
        Assert.Contains("await ApplyChatModelChoiceAsync(\"parux-manual:\" + file)", chat, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_chat_daily_snapshots_are_verified_and_do_not_overwrite_existing_complete_data()
    {
        string history = Read("src", "PaRuX.App", "Services", "HistoryStore.cs");
        string safety = Read("src", "PaRuX.App", "Services", "HistoryBackupSafety.cs");
        Assert.Contains("HistoryBackupSafety.MostRecentUsableSnapshot(", history, StringComparison.Ordinal);
        Assert.Contains("HistoryBackupSafety.CountRows(source) == 0", history, StringComparison.Ordinal);
        Assert.Contains("HistoryBackupSafety.PromoteSnapshot(staging, backup)", history, StringComparison.Ordinal);
        Assert.Contains("CreateDailyBackup(connection)", history, StringComparison.Ordinal);
        Assert.Contains("if (IsUsableSnapshot(destination)) return false;", safety, StringComparison.Ordinal);
        Assert.Contains("File.Move(destination, preserved)", safety, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_group_chat_shares_only_explicitly_pasted_bounded_context()
    {
        string settings = Read("src", "PaRuX.App", "Views", "SettingsView.xaml");
        string code = Read("src", "PaRuX.App", "Views", "SettingsView.xaml.cs");
        string service = Read("src", "PaRuX.Server", "GroupChatService.cs");
        Assert.Contains("x:Name=\"GroupChatSharedContextBox\"", settings, StringComparison.Ordinal);
        Assert.Contains("MaxLength=\"12000\"", settings, StringComparison.Ordinal);
        Assert.Contains("GroupChatSharedContextHint", settings, StringComparison.Ordinal);
        Assert.Contains("SharedContext = GroupChatSharedContextBox?.Text?.Trim()", code, StringComparison.Ordinal);
        Assert.Contains("NormalizeSharedContext(request.SharedContext)", service, StringComparison.Ordinal);
        Assert.Contains("BuildTurnPrompt(prompt, sharedContext, entry)", service, StringComparison.Ordinal);
        Assert.Contains("TryBuildVerdictAsync(prompt, sharedContext, turns, ct)", service, StringComparison.Ordinal);
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
