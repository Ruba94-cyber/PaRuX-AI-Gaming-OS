using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Chat Documents flyout skill buttons and the Settings "Delete everything (secure)" button in the Corporate edition
/// (PARUX_CORPORATE=1), against the real app and Core with an isolated data root. Folders hold synthetic text written here and are
/// registered through the Core API (the native folder picker is outside UI automation). No language model runs: the
/// answers under test are the deterministic ones the Core gives without one.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class DocumentSkillsAndRagWipeUiTests
{
    private static readonly IReadOnlyDictionary<string, string> Corporate = new Dictionary<string, string> { ["PARUX_CORPORATE"] = "1" };
    private const string DiffTemplate = "/diff  |  ";
    private const string ContradictionsTemplate = "/contradictions ";

    private static string SyntheticFolder()
    {
        string folder = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-docs", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "spec_v1.txt"),
            "# Pump spec\nThe coolant flow limit is 12 litres per minute.\nReplace the filter every 500 hours.");
        File.WriteAllText(Path.Combine(folder, "spec_v2.txt"),
            "# Pump spec\nThe coolant flow limit is 18 litres per minute.\nReplace the filter every 500 hours.\nA bypass valve was added.");
        File.WriteAllText(Path.Combine(folder, "Terms.txt"), "The warranty period is 24 months.\nMAX_TORQUE = 120 Nm\nThe lead time is 2 weeks.");
        File.WriteAllText(Path.Combine(folder, "Addendum.txt"), "Warranty period: 3 years.\nMAX_TORQUE = 150 Nm\nThe lead time is 2 weeks.");
        return folder;
    }

    private static void Run(string step, Action<UiTestApplication, DocumentSearchUiTests.Core, string> action)
    {
        string folder = SyntheticFolder();
        using var app = new UiTestApplication(step, extraEnvironment: Corporate);
        try
        {
            using var core = new DocumentSearchUiTests.Core(app);
            action(app, core, folder);
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    private static string[] AllText(UiTestApplication app) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(e => { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }).ToArray();

    // ---- Chat Documents flyout -------------------------------------------------------------------------------------------

    private static void OpenChat(UiTestApplication app) =>
        new MainWindowPage(app.MainWindow).Click("Navigation_Chat_Item", ControlType.ListItem);

    private static bool FlyoutOpen(UiTestApplication app) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Documents_Switch")) != null;

    private static AutomationElement OpenFlyout(UiTestApplication app)
    {
        app.MainWindow.SetForeground();
        UiWait.ById(app.MainWindow, "Chat_Documents_Button", ControlType.Button).AsButton().Invoke();
        return UiWait.ById(app.MainWindow, "Chat_Documents_Switch", ControlType.Button);
    }

    private static void CloseFlyout(UiTestApplication app) =>
        UiWait.InputUntil(() => { if (FlyoutOpen(app)) Keyboard.Press(VirtualKeyShort.ESCAPE); }, () => !FlyoutOpen(app), "the Documents flyout closing");

    private static ToggleState DocumentsSwitch(AutomationElement toggle) => toggle.Patterns.Toggle.Pattern.ToggleState.Value;

    /// <summary>Starts from "Documents off" (the state a person who never used it has), presses the skill button and returns.</summary>
    private static void PressSkillFromDocumentsOff(UiTestApplication app, string skillId)
    {
        AutomationElement toggle = OpenFlyout(app);
        UiWait.For(() =>
        {
            if (DocumentsSwitch(toggle) != ToggleState.Off) toggle.Patterns.Toggle.Pattern.Toggle();
            return DocumentsSwitch(toggle) == ToggleState.Off;
        }, "Documents switched off before the skill button");
        UiWait.ById(app.MainWindow, skillId, ControlType.Button).AsButton().Invoke();
        UiWait.For(() => !FlyoutOpen(app), "the flyout closing after the skill button");
    }

    /// <summary>After a skill button: the template is in the composer, the composer has keyboard focus (so typing goes on straight
    /// away) and the Documents button reports On. Does not reopen the flyout, which would take the focus away again.</summary>
    private static void AssertTemplateInComposerAndDocumentsOn(UiTestApplication app, string expectedComposerText)
    {
        var chat = new ChatPage(app.MainWindow);
        UiWait.For(() => chat.Prompt.Text == expectedComposerText, "the command template '" + expectedComposerText + "' in the composer");
        UiWait.For(() => chat.Prompt.Properties.HasKeyboardFocus.ValueOrDefault,
            "the composer keeping keyboard focus after the flyout closed");
        var button = UiWait.ById(app.MainWindow, "Chat_Documents_Button", ControlType.Button);
        Assert.Equal("On", button.Properties.HelpText.ValueOrDefault);   // the skill turned Documents on
    }

    private static void AssertDocumentsSwitchOn(UiTestApplication app, string expectedComposerText)
    {
        AutomationElement toggle = OpenFlyout(app);
        Assert.Equal(ToggleState.On, DocumentsSwitch(toggle));
        CloseFlyout(app);
        Assert.Equal(expectedComposerText, new ChatPage(app.MainWindow).Prompt.Text);   // nothing was sent or altered
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "DocSearch"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void Contradictions_skill_button_turns_documents_on_inserts_the_template_and_it_runs_as_is() => Run(
        nameof(Contradictions_skill_button_turns_documents_on_inserts_the_template_and_it_runs_as_is), (app, core, folder) =>
    {
        core.IndexAndWait(core.AddFolder(folder));
        OpenChat(app);
        PressSkillFromDocumentsOff(app, "Chat_Documents_Skill_Contradictions");
        AssertTemplateInComposerAndDocumentsOn(app, ContradictionsTemplate);
        AssertDocumentsSwitchOn(app, ContradictionsTemplate);

        // The template needs no further input: send it as it stands.
        var chat = new ChatPage(app.MainWindow);
        chat.Send.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => AllText(app).Any(t => t.Contains("candidates, not verdicts", StringComparison.Ordinal)),
            "the deterministic contradiction findings", TimeSpan.FromSeconds(60));
        Assert.Contains(AllText(app), t => t.Contains("MAX_TORQUE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AllText(app), t => t.Contains("warranty period", StringComparison.OrdinalIgnoreCase));
    });

    [Fact, Trait("Category", "Regression"), Trait("Category", "DocSearch"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void Diff_skill_button_turns_documents_on_inserts_the_template_and_the_completed_command_compares_two_versions() => Run(
        nameof(Diff_skill_button_turns_documents_on_inserts_the_template_and_the_completed_command_compares_two_versions), (app, core, folder) =>
    {
        core.IndexAndWait(core.AddFolder(folder));
        OpenChat(app);
        PressSkillFromDocumentsOff(app, "Chat_Documents_Skill_Diff");
        AssertTemplateInComposerAndDocumentsOn(app, DiffTemplate);

        // The template is "/diff <A> | <B>": the caret starts in the first slot, so the first name typed lands before the "|".
        var chat = new ChatPage(app.MainWindow);
        UiWait.InputUntil(() => Keyboard.Type("spec_v1.txt"), () => chat.Prompt.Text.Contains("spec_v1.txt", StringComparison.Ordinal),
            "typing the first name straight after the skill button");
        Assert.Equal("/diff spec_v1.txt |  ", chat.Prompt.Text);
        UiWait.InputUntil(() => { Keyboard.Press(VirtualKeyShort.END); Keyboard.Type("spec_v2.txt"); },
            () => chat.Prompt.Text.EndsWith("spec_v2.txt", StringComparison.Ordinal), "typing the second name");
        Assert.Equal("/diff spec_v1.txt |  spec_v2.txt", chat.Prompt.Text);
        chat.Send.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => AllText(app).Any(t => t.Contains("Revision comparison", StringComparison.Ordinal)),
            "the deterministic revision comparison", TimeSpan.FromSeconds(60));
        string[] text = AllText(app);
        Assert.Contains(text, t => t.StartsWith("A [spec_v1.txt", StringComparison.Ordinal) && t.Contains("12 litres per minute", StringComparison.Ordinal));
        Assert.Contains(text, t => t.StartsWith("B [spec_v2.txt", StringComparison.Ordinal) && t.Contains("18 litres per minute", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("12 litres -> 18 litres", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("bypass valve", StringComparison.Ordinal));
        Assert.Contains(text, t => t.StartsWith("Retrieved context", StringComparison.Ordinal) && t.Contains("spec_v1.txt", StringComparison.Ordinal));
    });

    // ---- Settings > Chat Behavior > Delete everything (secure) -----------------------------------------------------------

    private static bool DialogOpen(UiTestApplication app) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")) != null;

    private static void OpenWipeDialog(UiTestApplication app)
    {
        var wipe = UiWait.ById(app.MainWindow, "Settings_Rag_DeleteEverything_Button", ControlType.Button);
        SettingsCategoriesPage.ScrollIntoView(wipe);
        wipe.AsButton().Invoke();
        UiWait.For(() => DialogOpen(app), "the delete-everything confirmation dialog");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "DocSearch"), Trait("Category", "Settings"), Trait("Category", "FlaUI")]
    public void Delete_everything_asks_first_cancel_changes_nothing_and_confirm_wipes_the_index_but_not_the_documents() => Run(
        nameof(Delete_everything_asks_first_cancel_changes_nothing_and_confirm_wipes_the_index_but_not_the_documents), (app, core, folder) =>
    {
        string id = core.AddFolder(folder);
        core.IndexAndWait(id);
        var main = new MainWindowPage(app.MainWindow);
        main.OpenSettings();
        new SettingsCategoriesPage(app.MainWindow).SelectCategory("Chat Behavior");
        UiWait.ById(app.MainWindow, "Settings_Rag_Status_" + id, ControlType.Text);
        string noteBefore = UiWait.ById(app.MainWindow, "Settings_Rag_DeleteEverything_Note", ControlType.Text).Name;
        string[] documents = Directory.GetFiles(folder);
        string[] contents = documents.Select(File.ReadAllText).ToArray();

        // 1. Cancel with the button: the dialog closes, nothing is removed.
        OpenWipeDialog(app);
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => !DialogOpen(app), "the dialog closing on Cancel");
        Assert.Equal(new[] { id }, core.SourceIds());
        Assert.Equal(noteBefore, UiWait.ById(app.MainWindow, "Settings_Rag_DeleteEverything_Note", ControlType.Text).Name);
        Assert.NotNull(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Rag_Status_" + id)));

        // 2. Escape is a cancel as well (the dialog's default button is Cancel, never Delete).
        OpenWipeDialog(app);
        UiWait.InputUntil(() => Keyboard.Press(VirtualKeyShort.ESCAPE), () => !DialogOpen(app), "Escape closing the dialog");
        Assert.Equal(new[] { id }, core.SourceIds());
        Assert.Equal(noteBefore, UiWait.ById(app.MainWindow, "Settings_Rag_DeleteEverything_Note", ControlType.Text).Name);

        // 3. Confirm: the index and every folder entry are gone, the person's documents are not touched.
        OpenWipeDialog(app);
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton"))!.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => !DialogOpen(app), "the dialog closing on Delete");
        UiWait.For(() => UiWait.ById(app.MainWindow, "Settings_Rag_DeleteEverything_Note", ControlType.Text).Name != noteBefore,
            "the result line replacing the warning", TimeSpan.FromSeconds(60));
        UiWait.For(() => core.SourceIds().Length == 0, "the Core listing no folders");
        UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Rag_Status_" + id)) == null,
            "the folder row leaving Settings");
        Assert.Equal(documents.OrderBy(p => p), Directory.GetFiles(folder).OrderBy(p => p));
        Assert.Equal(contents, documents.Select(File.ReadAllText).ToArray());
    });
}
