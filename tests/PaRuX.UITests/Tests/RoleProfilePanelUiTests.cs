using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The "Use for" role-profile panel (Chat / Coding / Reasoning / Vision / Documents) in the Model Hub Start board and in
/// Settings, against the real app and Core. With no model installed it must say so honestly; with synthetic placeholder model
/// files (never loaded) "Apply recommended" must fill only roles the person has not set and must never replace one they chose.
/// Controls are found by AutomationId only, so the checks hold in English and Italian.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RoleProfilePanelUiTests
{
    private static readonly string[] Profiles = { "Chat", "Coding", "Reasoning", "Vision", "Documents" };

    private static string Id(string profile, string part) => "RoleProfile_" + profile + "_" + part;

    // An empty TextBlock exposes no Name at all (before the panel has read the models), so absence reads as "".
    private static string TextOf(UiTestApplication app, string id) =>
        UiWait.ById(app.MainWindow, id, ControlType.Text).Properties.Name.ValueOrDefault ?? string.Empty;

    private static bool Exists(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)) != null;

    private static bool Either(string text, string english, string italian) =>
        text.Contains(english, StringComparison.Ordinal) || text.Contains(italian, StringComparison.Ordinal);

    private static void OpenStartBoard(UiTestApplication app)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.Click("Navigation_ModelHub_Item", ControlType.ListItem);
        main.SelectModelHubTab("ModelHub_Tab_Start");
        UiWait.ById(app.MainWindow, "RoleProfile_Summary", ControlType.Text);
    }

    private static void OpenSettingsPanel(UiTestApplication app)
    {
        new SetupProfilePage(app).OpenSettingsGeneral();
        UiWait.ById(app.MainWindow, "RoleProfile_Summary", ControlType.Text);
    }

    private static void AssertHonestEmptyState(UiTestApplication app, string surface)
    {
        UiWait.For(() => Either(TextOf(app, "RoleProfile_Summary"), "Install a model to get started.", "Installa un modello per iniziare."),
            surface + ": the summary asks for a model instead of claiming one is ready");
        // Positive waits first (the rows reach the UIA tree a moment after the summary); only then are the absences meaningful.
        UiWait.For(() => Profiles.All(profile => Exists(app, Id(profile, "Model")) && Exists(app, Id(profile, "Explain"))),
            surface + ": all five rows shown");
        foreach (string profile in new[] { "Chat", "Coding", "Reasoning", "Vision" })
            UiWait.For(() => Either(TextOf(app, Id(profile, "Model")), "Recommended: none", "Consigliato: nessuno"),
                surface + ": " + profile + " must not recommend a model that does not exist");
        UiWait.For(() => Either(TextOf(app, Id("Chat", "Explain")), "No chat model is installed", "Nessun modello di chat installato"),
            surface + ": Chat says what is missing");
        UiWait.For(() => Either(TextOf(app, Id("Vision", "Explain")), "text-only model cannot read images", "solo testo non può leggere immagini"),
            surface + ": Vision never pretends a text model can see");
        foreach (string profile in Profiles)
            Assert.False(Exists(app, Id(profile, "Apply")), surface + ": nothing to apply for " + profile);
        Assert.False(UiWait.ById(app.MainWindow, "RoleProfile_ApplyRecommended", ControlType.Button).Properties.IsEnabled.ValueOrDefault,
            surface + ": Apply recommended is disabled with nothing to apply");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "RoleProfiles"), Trait("Category", "FlaUI")]
    public void Use_for_panel_renders_an_honest_empty_state_in_model_hub_and_settings_with_no_models()
    {
        const string step = nameof(Use_for_panel_renders_an_honest_empty_state_in_model_hub_and_settings_with_no_models);
        using var app = new UiTestApplication(step);
        try
        {
            OpenStartBoard(app);
            AssertHonestEmptyState(app, "Model Hub Start board");
            OpenSettingsPanel(app);
            AssertHonestEmptyState(app, "Settings");
            Assert.False(File.Exists(Path.Combine(app.DataRoot, "model-role-overrides.json")) && JsonRoleFile(app).Count > 0,
                "viewing the panel assigned nothing");
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static string SeedModel(UiTestApplication app, string fileName, int kibibytes)
    {
        string directory = Path.Combine(app.DataRoot, "models");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[kibibytes * 1024]);   // a placeholder: listed by name and size, never loaded
        return Path.GetFullPath(path);
    }

    private static Dictionary<string, string> JsonRoleFile(UiTestApplication app)
    {
        string path = Path.Combine(app.DataRoot, "model-role-overrides.json");
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return roles;
        using JsonDocument doc = JsonDocument.Parse(SharedFile.ReadAllText(path));
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) roles[property.Name] = property.Value.GetString() ?? string.Empty;
        return roles;
    }

    private static bool RoleIs(UiTestApplication app, string role, string path) =>
        JsonRoleFile(app).TryGetValue(role, out string? value)
        && string.Equals(Path.GetFullPath(value), path, StringComparison.OrdinalIgnoreCase);

    private static void ChooseOverride(UiTestApplication app, string profile, string itemId)
    {
        AutomationElement combo = UiWait.ById(app.MainWindow, Id(profile, "Override"), ControlType.ComboBox);
        var seen = new List<string>();
        // The panel sits low on a long page: a drop-down that is off-screen cannot open, so bring the combo into view first.
        try { combo.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { /* not every WinUI element exposes ScrollItem */ }
        try { combo.Focus(); } catch { /* focusing also scrolls it into view; best effort */ }
        try
        {
            UiWait.For(() =>
            {
                var expand = combo.Patterns.ExpandCollapse.Pattern;
                if (expand.ExpandCollapseState.Value != ExpandCollapseState.Expanded) expand.Expand();
                AutomationElement[] options = combo.AsComboBox().Items;
                seen.Clear();
                seen.AddRange(options.Select(o => (o.Properties.ControlType.ValueOrDefault) + ":" + o.Properties.AutomationId.ValueOrDefault + ":" + o.Properties.Name.ValueOrDefault));
                AutomationElement? item = options.FirstOrDefault(o => o.Properties.AutomationId.ValueOrDefault == itemId)
                    ?? app.FindRawDescendant(app.MainWindow, e => e.Properties.AutomationId.ValueOrDefault == itemId);
                if (item == null) return false;
                item.Patterns.SelectionItem.Pattern.Select();
                return true;
            }, "override item " + itemId);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " Combo exposed: [" + string.Join(" | ", seen) + "] state="
                + combo.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault
                + " offscreen=" + combo.Properties.IsOffscreen.ValueOrDefault + " rect=" + combo.BoundingRectangle, ex);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "RoleProfiles"), Trait("Category", "FlaUI")]
    public void Apply_recommended_fills_unset_roles_and_never_replaces_a_role_the_person_chose()
    {
        const string step = nameof(Apply_recommended_fills_unset_roles_and_never_replaces_a_role_the_person_chose);
        using var app = new UiTestApplication(step);
        try
        {
            string large = SeedModel(app, "synthetic-general-large.gguf", 3072);
            string small = SeedModel(app, "synthetic-general-small.gguf", 1024);
            string coder = SeedModel(app, "synthetic-coder-medium.gguf", 2048);
            var main = new MainWindowPage(app.MainWindow);

            OpenStartBoard(app);
            // Recommendations come from what is installed: the biggest general model for Chat, the coder for Coding, the Chat
            // model for Reasoning (no reasoning model), and still nothing for Vision.
            UiWait.For(() => Exists(app, Id("Chat", "Apply")) && Exists(app, Id("Coding", "Apply")), "the installed models being recommended");
            Assert.Contains("synthetic-general-large", TextOf(app, Id("Chat", "Model")));
            Assert.Contains("synthetic-coder-medium", TextOf(app, Id("Coding", "Model")));
            Assert.Contains("synthetic-general-large", TextOf(app, Id("Reasoning", "Model")));
            Assert.True(Either(TextOf(app, Id("Vision", "Model")), "Recommended: none", "Consigliato: nessuno"));
            Assert.False(Exists(app, Id("Vision", "Apply")));
            Assert.True(UiWait.ById(app.MainWindow, "RoleProfile_ApplyRecommended", ControlType.Button).Properties.IsEnabled.ValueOrDefault);
            Assert.Empty(JsonRoleFile(app));   // looking at recommendations assigned nothing

            // The person picks their own model for Coding (Advanced).
            main.Click("RoleProfile_Advanced", ControlType.Button);
            ChooseOverride(app, "Coding", Id("Coding", "Override_synthetic_general_small_gguf_Item"));
            UiWait.For(() => RoleIs(app, "coding", small), "the person's Coding choice saved");
            UiWait.For(() => TextOf(app, Id("Coding", "Model")).Contains("synthetic-general-small", StringComparison.Ordinal),
                "the Coding row showing the person's model as the current one");
            Assert.True(Either(TextOf(app, Id("Coding", "Explain")), "Your choice, left as is", "Scelta tua, non modificata"));

            // Apply recommended: Chat is filled; Coding (a role the person set) is left alone.
            main.Click("RoleProfile_ApplyRecommended", ControlType.Button);
            UiWait.For(() => RoleIs(app, "standard", large), "the unset Chat role filled with the recommendation");
            UiWait.For(() => !UiWait.ById(app.MainWindow, "RoleProfile_ApplyRecommended", ControlType.Button).Properties.IsEnabled.ValueOrDefault,
                "nothing left to apply in bulk");
            Assert.True(RoleIs(app, "coding", small), "Apply recommended replaced a role the person chose");
            Assert.Contains("synthetic-general-small", TextOf(app, Id("Coding", "Model")));

            // The row's own button is an explicit action on that role and may replace the person's earlier choice.
            main.Click(Id("Coding", "Apply"), ControlType.Button);
            UiWait.For(() => RoleIs(app, "coding", coder), "the explicit per-row Use recommended");
            Assert.True(RoleIs(app, "standard", large));

            // Settings reads the same role store.
            OpenSettingsPanel(app);
            UiWait.For(() => TextOf(app, Id("Coding", "Model")).Contains("synthetic-coder-medium", StringComparison.Ordinal),
                "Settings showing the same Coding model");
            Assert.Contains("synthetic-general-large", TextOf(app, Id("Chat", "Model")));
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }
}
