using System.Net.Http.Json;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Document search and chat-with-documents in the Corporate edition (PARUX_CORPORATE=1), against the real app and Core with an
/// isolated data root. Documents are synthetic and written here; folders are registered through the Core API because the native
/// folder picker is outside UI automation. No language model runs: the answers under test are the ones the Core gives without one.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class DocumentSearchUiTests
{
    private static readonly IReadOnlyDictionary<string, string> Corporate = new Dictionary<string, string> { ["PARUX_CORPORATE"] = "1" };

    private sealed class Core : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _endpoint;
        public Core(UiTestApplication app)
        {
            string path = Path.Combine(app.DataRoot, "server", "bootstrap.json");
            string? endpoint = null, token = null;
            UiWait.For(() =>
            {
                if (!File.Exists(path)) return false;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                endpoint = doc.RootElement.GetProperty("endpoint").GetString();
                token = doc.RootElement.GetProperty("token").GetString();
                return _http.GetAsync(endpoint!.TrimEnd('/') + "/api/v1/health").GetAwaiter().GetResult().IsSuccessStatusCode;
            }, "the PaRuX Core", TimeSpan.FromSeconds(90));
            _endpoint = endpoint!.TrimEnd('/');
            _http.DefaultRequestHeaders.Add("X-PaRuX-Token", token);
        }
        public string AddFolder(string folder)
        {
            using HttpResponseMessage r = _http.PostAsJsonAsync(_endpoint + "/api/v1/rag/sources", new { folderPath = folder }).GetAwaiter().GetResult();
            string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(r.IsSuccessStatusCode, body);
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString()!;
        }
        public void IndexAndWait(string id)
        {
            using HttpResponseMessage r = _http.PostAsync(_endpoint + "/api/v1/knowledge/sources/" + id + "/index", null).GetAwaiter().GetResult();
            Assert.True(r.IsSuccessStatusCode);
            UiWait.For(() =>
            {
                using JsonDocument doc = JsonDocument.Parse(_http.GetStringAsync(_endpoint + "/api/v1/knowledge/sources/" + id + "/status").GetAwaiter().GetResult());
                return doc.RootElement.TryGetProperty("job", out JsonElement job) && job.ValueKind == JsonValueKind.Object && job.GetProperty("state").GetString() == "done";
            }, "the index job", TimeSpan.FromSeconds(60));
        }
        public void Dispose() => _http.Dispose();
    }

    private static string SyntheticFolder()
    {
        string folder = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-docs", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "maintenance.md"),
            "# Pump service\nThe coolant pump parameter ZQ_4242 sets the flow limit to 12 litres per minute.\nReplace the filter every 500 hours.");
        File.WriteAllText(Path.Combine(folder, "warranty.txt"), "The warranty period for the gearbox is 24 months from delivery.");
        return folder;
    }

    private static void Run(string step, Action<UiTestApplication, Core, string> action)
    {
        string folder = SyntheticFolder();
        using var app = new UiTestApplication(step, extraEnvironment: Corporate);
        try
        {
            using var core = new Core(app);
            action(app, core, folder);
        }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    private static void OpenDocumentSearch(UiTestApplication app)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.Click("Navigation_DocumentSearch_Item", ControlType.ListItem);
        UiWait.ById(app.MainWindow, "DocSearch_Query_TextBox", ControlType.Edit);
    }

    /// <summary>The UI follows the OS language (English or Italian): "Up to date / Aggiornata · 2 documents / documenti".</summary>
    private static bool IsUpToDate(string t) =>
        (t.StartsWith("Up to date", StringComparison.Ordinal) || t.StartsWith("Aggiornata", StringComparison.Ordinal)) && t.Contains("2 doc");

    private static bool IsNothingFound(string t) =>
        t.StartsWith("Nothing found", StringComparison.Ordinal) || t.StartsWith("Nessun risultato", StringComparison.Ordinal);

    private static string[] AllText(UiTestApplication app) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(e => { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }).ToArray();

    [Fact, Trait("Category", "DocSearch")]
    public void Document_search_lists_the_indexed_folder_and_finds_an_exact_code_with_a_citation() => Run(
        nameof(Document_search_lists_the_indexed_folder_and_finds_an_exact_code_with_a_citation), (app, core, folder) =>
    {
        string id = core.AddFolder(folder);
        core.IndexAndWait(id);
        OpenDocumentSearch(app);
        UiWait.For(() => AllText(app).Any(t => t.Contains(folder, StringComparison.OrdinalIgnoreCase)), "the folder row");
        UiWait.For(() => AllText(app).Any(t => IsUpToDate(t)), "the index status line");

        var query = UiWait.ById(app.MainWindow, "DocSearch_Query_TextBox", ControlType.Edit).AsTextBox();
        query.Text = "ZQ_4242";
        UiWait.ById(app.MainWindow, "DocSearch_Search_Button", ControlType.Button).AsButton().Invoke();
        UiWait.For(() => AllText(app).Any(t => t.Contains("maintenance.md")), "a result citing maintenance.md");
        Assert.Contains(AllText(app), t => t.Contains("12 litres per minute"));
        // Open-at-position control exists for the result (heading position, so the honest label names the section).
        Assert.NotNull(UiWait.ById(app.MainWindow, "DocSearch_Open_Button", ControlType.Button));
    });

    [Fact, Trait("Category", "DocSearch")]
    public void Document_search_reports_nothing_found_instead_of_inventing_results() => Run(
        nameof(Document_search_reports_nothing_found_instead_of_inventing_results), (app, core, folder) =>
    {
        core.IndexAndWait(core.AddFolder(folder));
        OpenDocumentSearch(app);
        UiWait.ById(app.MainWindow, "DocSearch_Query_TextBox", ControlType.Edit).AsTextBox().Text = "ZQ_9999_NOPE";
        UiWait.ById(app.MainWindow, "DocSearch_Search_Button", ControlType.Button).AsButton().Invoke();
        UiWait.For(() => AllText(app).Any(t => IsNothingFound(t)), "the empty-result message");
        Assert.DoesNotContain(AllText(app), t => t.Contains("maintenance.md"));
    });

    [Fact, Trait("Category", "DocSearch")]
    public void Settings_rag_card_shows_index_status_ocr_off_by_default_profile_and_strict_offline() => Run(
        nameof(Settings_rag_card_shows_index_status_ocr_off_by_default_profile_and_strict_offline), (app, core, folder) =>
    {
        string id = core.AddFolder(folder);
        core.IndexAndWait(id);
        var main = new MainWindowPage(app.MainWindow);
        main.OpenSettings();
        new SettingsCategoriesPage(app.MainWindow).SelectCategory("Chat Behavior");
        var status = UiWait.ById(app.MainWindow, "Settings_Rag_Status_" + id, ControlType.Text);
        UiWait.For(() => IsUpToDate(status.Name), "the per-folder status");
        var ocr = UiWait.ById(app.MainWindow, "Settings_Rag_Ocr_Switch", ControlType.Button);
        Assert.Equal(ToggleState.Off, ocr.Patterns.Toggle.Pattern.ToggleState.Value);   // OCR is opt-in
        UiWait.ById(app.MainWindow, "Settings_Rag_Profile_ComboBox", ControlType.ComboBox);
        var offline = UiWait.ById(app.MainWindow, "Settings_Rag_StrictOffline_Switch", ControlType.Button);
        Assert.False(offline.IsEnabled);   // Corporate is offline by construction, shown as such
    });

    private static void SendInChat(UiTestApplication app, string question)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.Click("Navigation_Chat_Item", ControlType.ListItem);
        new ChatPage(app.MainWindow).SendPrompt(question);
    }

    private static void EnableDocumentsInChat(UiTestApplication app)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.Click("Navigation_Chat_Item", ControlType.ListItem);
        UiWait.ById(app.MainWindow, "Chat_Documents_Button", ControlType.Button).AsButton().Invoke();
        var toggle = UiWait.ById(app.MainWindow, "Chat_Documents_Switch", ControlType.Button);
        UiWait.For(() => { if (toggle.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On) toggle.Patterns.Toggle.Pattern.Toggle(); return toggle.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On; }, "Documents switch on");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    [Fact, Trait("Category", "DocSearch"), Trait("Category", "Chat")]
    public void Chat_with_documents_answers_a_lookup_from_the_folder_and_shows_the_citation() => Run(
        nameof(Chat_with_documents_answers_a_lookup_from_the_folder_and_shows_the_citation), (app, core, folder) =>
    {
        core.IndexAndWait(core.AddFolder(folder));
        EnableDocumentsInChat(app);
        SendInChat(app, "where is ZQ_4242 described");
        UiWait.For(() => AllText(app).Any(t => t.Contains("12 litres per minute")), "the document answer", TimeSpan.FromSeconds(60));
        UiWait.For(() => AllText(app).Any(t => t.Contains("Retrieved context") && t.Contains("maintenance.md")), "the citation footer", TimeSpan.FromSeconds(30));
    });

    [Fact, Trait("Category", "DocSearch"), Trait("Category", "Chat")]
    public void Chat_with_documents_says_not_enough_information_when_the_folder_does_not_contain_the_answer() => Run(
        nameof(Chat_with_documents_says_not_enough_information_when_the_folder_does_not_contain_the_answer), (app, core, folder) =>
    {
        core.IndexAndWait(core.AddFolder(folder));
        EnableDocumentsInChat(app);
        SendInChat(app, "what is the torque of the quantum flux capacitor");
        UiWait.For(() => AllText(app).Any(t => t.Contains("Not enough information in the selected documents")), "the explicit refusal to guess", TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(AllText(app), t => t.Contains("Retrieved context"));
    });
}
