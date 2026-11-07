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
        Assert.Contains("Search installed models", screen, StringComparison.Ordinal);
        Assert.Contains("matching.take(20).toMutableList()", screen, StringComparison.Ordinal);
        Assert.Contains("refine search", screen, StringComparison.Ordinal);
        Assert.Contains("models.firstOrNull { it.fileName.equals(preference.manualModelFileName", screen, StringComparison.Ordinal);
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
        Assert.Contains("Chat_ModelBrowser_Sort_Combo", chat, StringComparison.Ordinal);
        Assert.Contains("ChatModelBrowseSort.SmallestFileFirst", chat, StringComparison.Ordinal);
        Assert.Contains("ChatModelBrowseSort.MeasuredSpeedFirst", chat, StringComparison.Ordinal);
        Assert.Contains("Chat_ModelBrowser_Choices_List", chat, StringComparison.Ordinal);
        Assert.Contains("ChatModelPickerPolicy.Browse(_chatModelCatalog, query.Text, pageIndex, sort)", chat, StringComparison.Ordinal);
        Assert.Contains("await ApplyChatModelChoiceAsync(\"parux-manual:\" + file)", chat, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_chat_daily_snapshots_are_verified_and_do_not_overwrite_existing_complete_data()
    {
        string history = Read("src", "PaRuX.App", "Services", "HistoryStore.cs");
        string safety = Read("src", "PaRuX.App", "Services", "HistoryBackupSafety.cs");
        Assert.Contains("HistoryBackupSafety.MostRecentUsableSnapshot(", history, StringComparison.Ordinal);
        Assert.Contains("catch (SqliteException ex) when (HistoryBackupSafety.IsCorruptionErrorCode(ex.SqliteErrorCode))", history, StringComparison.Ordinal);
        Assert.Contains("if (!RecoverDatabase()) throw;", history, StringComparison.Ordinal);
        Assert.Contains("File.Copy(backup, AppPaths.DatabaseFile, overwrite: false)", history, StringComparison.Ordinal);
        Assert.Contains("modelMutationAttempted", Read("src", "PaRuX.Server", "GroupChatService.cs"), StringComparison.Ordinal);
        Assert.Contains("HistoryBackupSafety.CountRows(source) == 0", history, StringComparison.Ordinal);
        Assert.Contains("HistoryBackupSafety.PromoteSnapshot(staging, backup)", history, StringComparison.Ordinal);
        Assert.Contains("CreateDailyBackup(connection)", history, StringComparison.Ordinal);
        Assert.Contains("if (IsUsableSnapshot(destination)) return false;", safety, StringComparison.Ordinal);
        Assert.Contains("File.Replace(staging, destination, preserved, ignoreMetadataErrors: true)", safety, StringComparison.Ordinal);
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
        Assert.Contains("ValidateGroupPrompt(request.Prompt)", service, StringComparison.Ordinal);
        Assert.Contains("ValidateUniqueParticipants(resolved.Select(model => model.Path))", service, StringComparison.Ordinal);
        Assert.Contains("MaxLength=\"12000\" />", settings, StringComparison.Ordinal);
        Assert.Contains("Scegli modelli diversi", code, StringComparison.Ordinal);
        Assert.Contains("Truncate(turn.Response, MaxVerdictCharactersPerTurn)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_media_gallery_limits_thumbnail_memory_and_android_jobs_bound_inputs()
    {
        string gallery = Read("src", "PaRuX.App", "Views", "MediaGalleryView.cs");
        string policy = Read("src", "PaRuX.App", "Services", "MediaGalleryBrowser.cs");
        string media = Read("src", "PaRuX.App", "Services", "ChatMediaJobReconciler.cs");
        string phone = Read("src", "PaRuX.Android", "app", "src", "main", "java",
            "com", "parux", "android", "ui", "jobs", "JobsScreen.kt");

        Assert.Contains("MediaGalleryBrowser.Paginate(filtered, _pageIndex)", gallery, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_PreviousPage_Button", gallery, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_NextPage_Button", gallery, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = 320", gallery, StringComparison.Ordinal);
        Assert.Contains("for (int offset = 0; offset < artifacts.Length; offset += 4)", gallery, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(batch.Select(LoadArtifactAsync))", gallery, StringComparison.Ordinal);
        Assert.Contains("ct.ThrowIfCancellationRequested();", gallery, StringComparison.Ordinal);
        Assert.Contains("if (!ReferenceEquals(_refreshCts, request)) return;", gallery, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() =>", gallery, StringComparison.Ordinal);
        Assert.Contains("ScanDirectory(AppPaths.GeneratedImagesDir", gallery, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll(artifacts.Select(LoadArtifactAsync))", gallery, StringComparison.Ordinal);
        Assert.Contains("public const int PageSize = 24;", policy, StringComparison.Ordinal);
        Assert.Contains("case \"cancelling\":", media, StringComparison.Ordinal);
        Assert.Contains("job.EtaSeconds is > 0 and <= 86400", media, StringComparison.Ordinal);
        Assert.Contains("MAX_JOB_SOURCE_IMAGE_BYTES = 8 * 1024 * 1024", phone, StringComparison.Ordinal);
        Assert.Contains("while (true)", phone, StringComparison.Ordinal);
        Assert.Contains("imageError = e.message", phone, StringComparison.Ordinal);
        Assert.Contains("coerceIn(0f, 1f)", phone, StringComparison.Ordinal);
        Assert.Contains("onAccepted = { showCreateDialog = false }", phone, StringComparison.Ordinal);
        Assert.Contains("onDispose { viewModel.stopPolling() }", phone, StringComparison.Ordinal);
        Assert.Contains("if (localCore == null)", phone, StringComparison.Ordinal);
        string jobsModel = Read("src", "PaRuX.Android", "app", "src", "main", "java",
            "com", "parux", "android", "ui", "jobs", "JobsViewModel.kt");
        Assert.Contains("val isSubmitting: Boolean", jobsModel, StringComparison.Ordinal);
        Assert.Contains("completionNotifications.newlyCompleted(jobs)", jobsModel, StringComparison.Ordinal);
        Assert.Contains("fun stopPolling()", jobsModel, StringComparison.Ordinal);
        Assert.Contains("onAccepted()", jobsModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Advanced_chat_compare_supports_large_local_catalogues_without_massive_combo_boxes()
    {
        string comparison = Read("src", "PaRuX.App", "Views", "ChatView.ModelRouting.cs");
        Assert.Contains("ChatModelPickerPolicy.Browse(", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_Search", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_Choices", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_PreviousPage", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_NextPage", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_SelectedModels", comparison, StringComparison.Ordinal);
        Assert.Contains("Chat_QuickCompare_Sort", comparison, StringComparison.Ordinal);
        Assert.Contains("models, search.Text, pageIndex, sort", comparison, StringComparison.Ordinal);
        Assert.Contains("ChatModelPickerPolicy.CanCompare(models, one, two)", comparison, StringComparison.Ordinal);
        Assert.Contains("ChatModelPickerPolicy.HasMeasuredSpeed(model)", comparison, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (ManagedModelInfo model in models)", comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void Gaming_virtual_pad_identity_and_hotplug_require_live_complete_snapshot()
    {
        string host = Read("src", "PaRuX.App", "Services", "VirtualPadActivationController.cs");
        string raw = Read("src", "PaRuX.App", "Services", "WindowsRawGamepadSource.cs");
        string verifier = Read("src", "PaRuX.Server", "Orchestration", "Gaming",
            "Providers", "VirtualPad", "PadBinding.cs");
        Assert.Contains("WindowsRawGamepadSource.CountMatchingLiveIdentity(info)", host,
            StringComparison.Ordinal);
        Assert.Contains("RawGameController.RawGameControllers", raw, StringComparison.Ordinal);
        Assert.Contains("PadBindingVerifier.CountMatchingRawControllers(", raw,
            StringComparison.Ordinal);
        Assert.Contains("selectedPresent ? matches : 0", verifier, StringComparison.Ordinal);
        Assert.Contains("finally", host, StringComparison.Ordinal);
        Assert.Contains("Service.Status.Phase is not (VirtualPadPhase.Running or VirtualPadPhase.PausedRemoved)",
            host, StringComparison.Ordinal);
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

    [Fact]
    public void First_run_automatic_setup_renders_Italian_recovery_without_changing_permissions()
    {
        string onboarding = Read("src", "PaRuX.App", "Views", "OnboardingView.AutoSetup.cs");
        string presenter = Read("src", "PaRuX.App", "Services", "OnboardingAutoSetupPresenter.cs");
        Assert.Contains("OnboardingAutoSetupPresenter.Present(snapshot.State,", onboarding, StringComparison.Ordinal);
        Assert.Contains("CurrentUICulture.TwoLetterISOLanguageName == \"it\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("PresentItalian(state)", presenter, StringComparison.Ordinal);
        Assert.Contains("RuntimeRecoveryGuidance.Describe(message, italian: true)", presenter, StringComparison.Ordinal);
        Assert.Contains("AutoSetupPhase.NoModelFits => new AutoSetupViewModel(", presenter, StringComparison.Ordinal);
        Assert.Contains("PaRuX non forzerà il download", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_ai_failure_recovery_is_shared_by_chat_settings_and_tray()
    {
        string runtime = Read("src", "PaRuX.App", "Services", "LocalAiController.cs");
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.xaml.cs");
        string settings = Read("src", "PaRuX.App", "Views", "SettingsView.xaml.cs");
        string main = Read("src", "PaRuX.App", "MainWindow.xaml.cs");
        Assert.Contains("RuntimeRecoveryGuidance.Describe(r?.Message", runtime, StringComparison.Ordinal);
        Assert.Contains("CurrentUICulture.TwoLetterISOLanguageName == \"it\"", runtime, StringComparison.Ordinal);
        Assert.Contains("LocalAiController.FriendlyScriptError(result)", chat, StringComparison.Ordinal);
        Assert.Contains("LocalAiController.FriendlyScriptError(result)", settings, StringComparison.Ordinal);
        Assert.Contains("LocalAiController.FriendlyScriptError(result)", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_hub_search_does_not_promote_an_unverified_balanced_file_to_default()
    {
        string hub = Read("src", "PaRuX.App", "Views", "ModelHubView.cs");
        string variants = Read("src", "PaRuX.App", "Views", "ModelHubView.Compact.cs");
        Assert.Contains("int preferred = fromFirstModelSetup", hub, StringComparison.Ordinal);
        Assert.Contains(": -1;", hub, StringComparison.Ordinal);
        Assert.Contains("quantAdvice.PreferredIndex is int advised", hub, StringComparison.Ordinal);
        Assert.Contains("!fromFirstModelSetup && !canReviewDefault && choices.Count > 0", hub, StringComparison.Ordinal);
        Assert.Contains("ModelHub_QuantAdvice_Text", hub, StringComparison.Ordinal);
        Assert.Contains("ReviewSearchVariantsAsync(repo, choices, preferred)", hub, StringComparison.Ordinal);
        Assert.Contains("_manuallyReviewedQuantByRepo.TryGetValue(repo.RepoId, out string? manualSetName)", hub, StringComparison.Ordinal);
        Assert.Contains("pinnedSetName: fromFirstModelSetup ? null : manualSetName", hub, StringComparison.Ordinal);
        Assert.Contains("_manuallyReviewedQuantByRepo[repo.RepoId] = selectedChoice.SetName", variants, StringComparison.Ordinal);
        Assert.Contains("if (preferredIndex >= 0 && preferredIndex < picker.Items.Count)", variants, StringComparison.Ordinal);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = picker.SelectedItem", variants, StringComparison.Ordinal);
    }
}
