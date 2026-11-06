using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;
using static PaRuX.UITests.PageObjects.SetupProfilePage;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Run-time AI only / Gaming only / Both switch in Settings &gt; General, against the real app and the real Core: the shell
/// (mode tabs, Games and Chat workspaces) changes at once and the process is never restarted; the Corporate edition keeps
/// AI only and says so; a step that fails puts everything back and shows the profile that is really active.
/// Nothing is downloaded, installed or deleted; the isolated data root is thrown away with the test.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SetupProfileRuntimeUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "SetupProfile"), Trait("Category", "FlaUI")]
    public void Switching_profile_in_settings_changes_the_shell_at_once_without_a_restart()
    {
        const string step = nameof(Switching_profile_in_settings_changes_the_shell_at_once_without_a_restart);
        using var app = new UiTestApplication(step);
        try
        {
            var page = new SetupProfilePage(app);
            int processId = app.MainWindow.Properties.ProcessId.Value;

            // Both is the default: the AI | Gaming tabs exist and the app opens on the AI workspace.
            UiWait.For(() => page.ModeTabsShown && page.IsShown(ChatNav), "the default Both shell");
            page.OpenSettingsGeneral();
            UiWait.For(() => page.SelectedItemId == BothItem, "Both selected by default");

            // AI only: the mode tabs go away, Chat stays, Games stays hidden; the person stays in Settings.
            page.Choose(AiItem);
            UiWait.For(() => page.SavedProfile() == "ai", "AI only saved");
            UiWait.For(() => page.ModeTabsHidden && page.IsShown(ChatNav) && !page.IsShown(GamesNav), "the AI-only shell");
            UiWait.For(() => page.LogContains("Setup profile switch both -> ai: success=True, steps=1"),
                "the runtime stopping the gaming service");
            Assert.True(page.IsShown(ComboId), "the switch happens in place; Settings stays open");

            // Gaming only: AI workspaces vanish, the Games library opens and is the only home.
            page.Choose(GamingItem);
            UiWait.For(() => page.SavedProfile() == "gaming", "Gaming only saved");
            UiWait.For(() => page.ModeTabsHidden && page.IsShown(GamesNav) && !page.IsShown(ChatNav), "the Gaming-only shell");
            UiWait.For(() => page.Exists("GameLibrary_Add_Button"), "the Games library opened");
            UiWait.For(() => !page.IsShown("GameLibrary_ReturnToAi_Button")
                && !page.IsShown("GameHome_LocalGuide_Button")
                && !page.IsShown("GameHome_AssetStudio_Open"),
                "Gaming-only hides AI-dependent actions inside the Games page");
            UiWait.For(() => page.LogContains("Setup profile switch ai -> gaming: success=True, steps=3"),
                "the runtime stopping AI and agent services and starting the gaming service");

            // Back to Both: tabs return and the AI workspace is reachable again, all in the same process.
            page.OpenSettingsGeneral();
            UiWait.For(() => page.SelectedItemId == GamingItem, "Gaming only shown as the active profile");
            page.Choose(BothItem);
            UiWait.For(() => page.SavedProfile() == "both", "Both saved");
            UiWait.For(() => page.ModeTabsShown, "the AI | Gaming tabs are back");
            UiWait.For(() => page.LogContains("Setup profile switch gaming -> both: success=True, steps=2"),
                "the runtime starting AI and agent services again");
            page.SelectTab(AiTab);
            UiWait.For(() => page.IsShown(ChatNav) && !page.IsShown(GamesNav), "the AI workspace is reachable again");

            Assert.Equal(processId, app.MainWindow.Properties.ProcessId.Value);
            Assert.Equal(3, page.LogCount("Setup profile switch"));
            Assert.Equal(0, page.LogCount("Setup profile switch", "success=False"));   // (a failed Ensure warm-up is not a switch)
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "SetupProfile"), Trait("Category", "FlaUI")]
    public void Gaming_only_choice_survives_a_restart_and_opens_the_games_library()
    {
        const string step = nameof(Gaming_only_choice_survives_a_restart_and_opens_the_games_library);
        using var initial = new UiTestApplication(step);
        SetupProfilePage first = new(initial);
        try
        {
            first.OpenSettingsGeneral();
            first.Choose(GamingItem);
            UiWait.For(() => first.SavedProfile() == "gaming", "Gaming only saved before the restart");
            UiWait.For(() => first.ModeTabsHidden && first.IsShown(GamesNav), "the Gaming-only shell");
        }
        catch (Exception ex) { initial.CaptureFailure(step, ex); throw; }

        using var restarted = initial.Restart(step + "-restarted");
        try
        {
            var page = new SetupProfilePage(restarted);
            UiWait.For(() => page.Exists("GameLibrary_Add_Button"), "the Games library is the start page");
            Assert.True(page.ModeTabsHidden, "Gaming only has no AI | Gaming tabs");
            Assert.True(page.IsShown(GamesNav));
            Assert.False(page.IsShown(ChatNav), "AI workspaces stay hidden after the restart");
            page.OpenSettingsGeneral();
            UiWait.For(() => page.SelectedItemId == GamingItem, "the Settings row shows the saved profile");
        }
        catch (Exception ex) { restarted.CaptureFailure(step + "-restarted", ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "SetupProfile"), Trait("Category", "FlaUI")]
    public void Corporate_edition_is_chat_first_without_an_unavailable_setup_switch()
    {
        const string step = nameof(Corporate_edition_is_chat_first_without_an_unavailable_setup_switch);
        using var app = new UiTestApplication(step, extraEnvironment: new Dictionary<string, string>
        {
            ["PARUX_CORPORATE"] = "1"
        });
        try
        {
            var page = new SetupProfilePage(app);
            UiWait.For(() => page.IsShown(ChatNav) && page.ModeTabsHidden && !page.IsShown(GamesNav),
                "the Corporate Chat-only shell");
            Assert.True(page.IsShown("Navigation_DocumentSearch_Item"));
            Assert.False(page.IsShown("Navigation_Create_Item"));
            Assert.False(page.IsShown("Navigation_Projects_Item"));
            Assert.False(page.IsShown("Navigation_ModelHub_Item"));
            Assert.False(page.IsShown("Navigation_Runtime_Item"));
            Assert.False(page.IsShown("MainWindow_ConsoleMode_Button"));

            page.OpenSettingsGeneral();
            UiWait.For(() => page.HintText.Contains("Corporate", StringComparison.OrdinalIgnoreCase)
                || page.HintText.Contains("Chat", StringComparison.OrdinalIgnoreCase),
                "the fixed Corporate Chat setup description");

            AutomationElement? combo = app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId(ComboId));
            Assert.True(combo == null || combo.Properties.IsOffscreen.ValueOrDefault,
                "Corporate must not show an unchangeable AI/Gaming/Both combo.");
            Assert.False(page.IsShown("Settings_Category_GamingAISharing_Button"));
            Assert.False(page.IsShown("Settings_Category_MediaGeneration_Button"));
            Assert.False(page.IsShown("Settings_Category_Agents_Button"));
            Assert.True(page.IsShown("Settings_Category_Models_Button"));
            Assert.True(page.ModeTabsHidden);
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "SetupProfile"), Trait("Category", "FlaUI")]
    public void A_failed_switch_snaps_back_to_the_profile_that_is_really_active()
    {
        const string step = nameof(A_failed_switch_snaps_back_to_the_profile_that_is_really_active);
        // A reachable Core that refuses every runtime action: stopping the local AI cannot succeed, so Gaming only must fail.
        using var app = new UiTestApplication(step, enableRuntimeActionRefusalFixture: true);
        try
        {
            var page = new SetupProfilePage(app);
            UiWait.For(() => page.ModeTabsShown && page.IsShown(ChatNav), "the default Both shell");
            page.OpenSettingsGeneral();
            UiWait.For(() => page.SelectedItemId == BothItem, "Both selected by default");
            string before = page.HintText;

            page.Choose(GamingItem);
            UiWait.For(() => app.RefusedRuntimeActions.Contains("MediaStop"), "the switch asking Core to stop the local AI");
            // The failure is reported (the first failing step names its service) and the row shows what is really active.
            UiWait.For(() => page.HintText.Contains("LocalAi", StringComparison.Ordinal), "the failure message in the hint");
            UiWait.For(() => page.SelectedItemId == BothItem, "the row snapped back to Both");
            Assert.NotEqual(before, page.HintText);
            Assert.NotEqual("gaming", page.SavedProfile());
            Assert.True(page.ModeTabsShown, "the AI | Gaming tabs stay because nothing changed");
            Assert.True(page.IsShown(ChatNav), "the AI workspace is untouched");
            Assert.False(page.IsShown(GamesNav));
            Assert.True(page.LogContains("Setup profile switch both -> gaming: success=False"), "the failure is logged honestly");
            // The combo is usable again: a second attempt is possible (and fails the same honest way).
            Assert.True(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(ComboId))!.Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }
}
