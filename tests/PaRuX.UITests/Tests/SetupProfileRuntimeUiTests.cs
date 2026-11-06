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
    public void Corporate_edition_keeps_ai_only_and_explains_why_a_wider_choice_was_not_kept()
    {
        const string step = nameof(Corporate_edition_keeps_ai_only_and_explains_why_a_wider_choice_was_not_kept);
        using var app = new UiTestApplication(step, extraEnvironment: new Dictionary<string, string> { ["PARUX_CORPORATE"] = "1" });
        try
        {
            var page = new SetupProfilePage(app);
            UiWait.For(() => page.IsShown(ChatNav) && page.ModeTabsHidden && !page.IsShown(GamesNav), "the Corporate AI-only shell");
            page.OpenSettingsGeneral();
            UiWait.For(() => page.SelectedItemId == AiItem, "AI only shown as the active profile");
            string description = page.HintText;

            int handled = 0;
            foreach (string wider in new[] { BothItem, GamingItem })
            {
                page.Choose(wider);
                handled++;
                // Every choice is processed once (one log line each) ...
                UiWait.For(() => page.LogCount("Setup profile switch ai -> ai: success=True, steps=0") == handled,
                    "the switch to " + wider + " being resolved to AI only");
                // ... the row snaps back to the profile that is really active AND tells the person why (no silent revert).
                UiWait.For(() => page.SelectedItemId == AiItem, "the row back on AI only after choosing " + wider);
                UiWait.For(() => page.HintText.Contains("Corporate", StringComparison.Ordinal),
                    "the explanation that Corporate Lite keeps AI only after choosing " + wider);
                Assert.True(page.ModeTabsHidden, "Corporate never shows the AI | Gaming tabs");
                Assert.False(page.IsShown(GamesNav), "Corporate never shows the Games workspace");
                string? saved = page.SavedProfile();
                Assert.True(saved is null or "ai", "a wider profile was saved in Corporate: " + saved);
                Assert.NotEqual(description, page.HintText);
            }
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
