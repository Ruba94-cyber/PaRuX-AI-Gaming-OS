using System.Net.Http.Json;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Corporate Lite is the offline edition. It keeps what an administrator needs (the tamper-evident activity log, per-device access, the
/// plugin list) and refuses everything that calls out (webhooks, Steam), both in the window and in the Core.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CorporateOwnerRecordsUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static void ScrollTo(UiTestApplication app, string automationId)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < until)
        {
            if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) != null) return;
            var scroller = app.MainWindow.FindAllDescendants()
                .Where(e => { try { return e.Patterns.Scroll.IsSupported && e.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault; } catch { return false; } })
                .OrderByDescending(e => { try { return e.BoundingRectangle.Height; } catch { return 0; } }).FirstOrDefault();
            try { scroller?.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("Timed out scrolling to " + automationId + ".");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Corporate"), Trait("Category", "FlaUI")]
    public async Task Corporate_keeps_the_activity_log_and_device_access_and_refuses_webhooks_and_steam_in_the_core()
    {
        const string test = nameof(Corporate_keeps_the_activity_log_and_device_access_and_refuses_webhooks_and_steam_in_the_core);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(test, extraEnvironment: new Dictionary<string, string> { ["PARUX_CORPORATE"] = "1" });
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_PrivacyNetwork_Button");

            ScrollTo(app, "Settings_AuditLog_Status_Text");
            ScrollTo(app, "Settings_Roles_Status_Text");
            var verify = app.MainWindow.FindAllDescendants().First(e => Text(e) == "Check the log is untouched");
            try { verify.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
            verify.Click();
            var auditStatus = UiWait.ById(app.MainWindow, "Settings_AuditLog_Status_Text", ControlType.Text);
            UiWait.For(() => Text(auditStatus).Contains("intact", StringComparison.OrdinalIgnoreCase) || Text(auditStatus).Contains("empty", StringComparison.OrdinalIgnoreCase),
                "the log check answers", TimeSpan.FromSeconds(60));

            // Offline by construction: nothing that calls out is offered.
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Webhooks_Add_Button")));
            Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Steam_Save_Button")));

            // ...and the Core refuses them even if something asks directly.
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(app.DataRoot, "server", "bootstrap.json")));
            string endpoint = doc.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/');
            string token = doc.RootElement.GetProperty("token").GetString()!;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            foreach (string path in new[] { "/api/v2/webhooks", "/api/v2/steam/credentials" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + path);
                request.Headers.Add("X-PaRuX-Token", token);
                HttpResponseMessage response = await http.SendAsync(request);
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Contains("Corporate Lite", await response.Content.ReadAsStringAsync());
            }

            // The activity log and the device roles stay available in Corporate.
            foreach (string path in new[] { "/api/v2/audit/verify", "/api/v2/roles" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + path);
                request.Headers.Add("X-PaRuX-Token", token);
                Assert.True((await http.SendAsync(request)).IsSuccessStatusCode, path);
            }
        }
        catch (Exception ex) { app.CaptureFailure(test, ex); throw; }
    }
}
