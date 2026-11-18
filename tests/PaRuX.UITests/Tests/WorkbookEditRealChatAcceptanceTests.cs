using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

// Opt-in: copies a 2.5 GB model and starts a real runtime; it is not a routine UI smoke test.
internal sealed class RealWorkbookFactAttribute : FactAttribute
{
    public RealWorkbookFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARUX_UAI04_REAL_CHAT") != "1")
            Skip = "Set PARUX_UAI04_REAL_CHAT=1 for the isolated real-model workbook acceptance.";
    }
}

[Collection(UiTestCollection.Name)]
public sealed class WorkbookEditRealChatAcceptanceTests
{
    [RealWorkbookFact, Trait("Category", "UAI04"), Trait("Category", "FlaUI")]
    public async Task Real_chat_previews_discards_then_saves_a_source_preserving_copy()
    {
        const string name = nameof(Real_chat_previews_discards_then_saves_a_source_preserving_copy);
        string sourceFixture = Required("PARUX_UAI04_SOURCE");
        string verifier = Required("PARUX_UAI04_VERIFIER");
        string modelAsset = Required("PARUX_UAI04_MODEL");
        string runtimeAsset = Required("PARUX_UAI04_VULKAN_RUNTIME");
        using var app = new UiTestApplication(name, uiLanguage: "en");
        try
        {
            Checkpoint("app ready, isolated root " + app.DataRoot);
            string fixtureRoot = Path.Combine(app.DataRoot, "documents");
            Directory.CreateDirectory(fixtureRoot);
            string source = Path.Combine(fixtureRoot, "uai04-source.xlsx");
            File.Copy(sourceFixture, source);
            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            string local = Path.Combine(app.DataRoot, "LocalAI");
            string model = Path.GetFileName(modelAsset);
            string modelTarget = Path.Combine(app.DataRoot, "models", model);
            Directory.CreateDirectory(Path.GetDirectoryName(modelTarget)!);
            File.Copy(modelAsset, modelTarget);
            Checkpoint("model copied");
            CopyDirectory(runtimeAsset, Path.Combine(local, "runtime", "llama.cpp-b10549-vulkan"));
            Directory.CreateDirectory(Path.Combine(local, "config"));
            File.WriteAllText(Path.Combine(local, "config", "runtime-build-config.json"), "{\"text-vulkan\":\"b10549\"}");

            string bootstrap = Path.Combine(app.DataRoot, "server", "bootstrap.json");
            UiWait.For(() => File.Exists(bootstrap), "isolated Core bootstrap", TimeSpan.FromSeconds(60));
            using JsonDocument info = JsonDocument.Parse(File.ReadAllText(bootstrap));
            using var http = new HttpClient { BaseAddress = new Uri(info.RootElement.GetProperty("endpoint").GetString()!), Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Add("X-PaRuX-Token", info.RootElement.GetProperty("token").GetString());
            await Post(http, "/api/v1/desktop/documents/output", new { folder = fixtureRoot });
            Checkpoint("output configured; loading model");
            await Post(http, "/api/v1/models/library/" + model + "/load", new { role = "standard", persistRoleAssignment = true });
            Checkpoint("model load returned HTTP success");

            var chat = new ChatPage(app.MainWindow);
            const string prompt = "Edit the existing workbook uai04-source.xlsx and prepare a copy named uai04-reviewed.xlsx. In sheet Budget set cell B2 to the number 1500. Preserve all other cells and prepare the change for review.";
            Submit(chat, prompt);
            WaitForPreview(app);
            Checkpoint("first preview visible with requested values");
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Single(Directory.GetFiles(fixtureRoot));
            app.CaptureSnapshot("workbook-preview-before-discard");
            UiWait.ById(app.MainWindow, "Chat_WorkbookEdit_Discard_Button", ControlType.Button).AsButton().Invoke();
            UiWait.For(() => !UiWait.ById(app.MainWindow, "Chat_WorkbookEdit_Save_Button", ControlType.Button).IsEnabled
                && UiWait.ById(app.MainWindow, "Chat_WorkbookEdit_Status", ControlType.Text).Name.Contains("Changes discarded.", StringComparison.Ordinal),
                "discarded preview no longer actionable", TimeSpan.FromSeconds(30));
            Assert.Single(Directory.GetFiles(fixtureRoot));
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
            Checkpoint("discard completed without copy");

            Submit(chat, "I discarded the previous review. Please prepare a fresh proposal now. " + prompt);
            WaitForPreview(app);
            Checkpoint("second preview visible with requested values");
            Assert.Single(Directory.GetFiles(fixtureRoot));
            app.CaptureSnapshot("workbook-preview-before-save");
            UiWait.ById(app.MainWindow, "Chat_WorkbookEdit_Save_Button", ControlType.Button).AsButton().Invoke();
            UiWait.For(() => File.Exists(Path.Combine(fixtureRoot, "uai04-reviewed.xlsx")), "named saved copy", TimeSpan.FromSeconds(60));
            UiWait.For(() => UiWait.ById(app.MainWindow, "Chat_WorkbookEdit_Status", ControlType.Text).Name.Contains("Copy saved:", StringComparison.Ordinal),
                "saved confirmation", TimeSpan.FromSeconds(30));
            Assert.Equal(2, Directory.GetFiles(fixtureRoot, "*.xlsx").Length);
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
            using var process = Process.Start(new ProcessStartInfo("python") { UseShellExecute = false,
                ArgumentList = { verifier, "after-save", "--directory", fixtureRoot } })!;
            Assert.True(process.WaitForExit(30000) && process.ExitCode == 0, "Independent workbook readback failed.");
            string? evidence = Environment.GetEnvironmentVariable("PARUX_UAI04_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.Copy(source, Path.Combine(evidence, Path.GetFileName(source)));
                string copy = Path.Combine(fixtureRoot, "uai04-reviewed.xlsx");
                File.Copy(copy, Path.Combine(evidence, Path.GetFileName(copy)));
                Checkpoint("source SHA256 " + Convert.ToHexString(originalHash) + "; copy SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))));
            }
            Checkpoint("save and independent readback passed");

            void Submit(ChatPage page, string text)
            {
                page.EnterPrompt(text);
                UiWait.ForStable(() => page.Send.IsEnabled, "chat Send enabled", TimeSpan.FromSeconds(45));
                Checkpoint("Send enabled; submitting prompt");
                if (page.Send.Patterns.Invoke.PatternOrDefault != null) page.Send.Patterns.Invoke.Pattern.Invoke();
                else page.Send.Click(moveMouse: false);
                UiWait.For(() => string.IsNullOrWhiteSpace(page.Prompt.Text), "prompt accepted by Chat", TimeSpan.FromSeconds(30));
                Checkpoint("prompt accepted and composer cleared");
            }
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    private static void Checkpoint(string message)
    {
        string? path = Environment.GetEnvironmentVariable("PARUX_UAI04_CHECKPOINT");
        if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " must name a local test asset.");

    private static async Task Post(HttpClient http, string path, object body)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(path, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
    }

    private static void WaitForPreview(UiTestApplication app)
    {
        UiWait.For(() => PreviewVisible(app),
            "workbook preview from real chat", TimeSpan.FromMinutes(5));
        UiWait.For(() => app.FindRawDescendant(app.MainWindow, e =>
        {
            string value = e.Properties.Name.ValueOrDefault ?? "";
            return value.Contains("B2", StringComparison.OrdinalIgnoreCase)
                && value.Contains("1200", StringComparison.Ordinal) && value.Contains("1500", StringComparison.Ordinal);
        }) != null, "review shows cell B2 changing from 1200 to 1500", TimeSpan.FromSeconds(15));
    }

    private static bool PreviewVisible(UiTestApplication app)
    {
        var button = app.FindRawDescendant(app.MainWindow, e => e.Properties.AutomationId.ValueOrDefault == "Chat_WorkbookEdit_Save_Button");
        return button != null && !button.Properties.IsOffscreen.ValueOrDefault && button.IsEnabled;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }
}
