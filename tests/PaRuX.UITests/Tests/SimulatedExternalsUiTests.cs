using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Real app, real Core, isolated profile. What cannot be done for real on this PC is simulated at its edge: a webhook receiver and a
/// Steam server are small local HTTP listeners; nothing inside PaRuX is replaced. Each test says what is simulated.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SimulatedExternalsUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Listener : IDisposable
    {
        private readonly HttpListener _http = new();
        public List<(string Path, string Query, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();
        public string Answer { get; set; } = "{}";
        public int Port { get; } = FreePort();

        public Listener()
        {
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(async () =>
            {
                while (_http.IsListening)
                {
                    try
                    {
                        HttpListenerContext c = await _http.GetContextAsync();
                        string body = await new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                        var headers = c.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => c.Request.Headers[k] ?? string.Empty);
                        lock (Requests) Requests.Add((c.Request.Url!.AbsolutePath, c.Request.Url.Query, headers, body));
                        byte[] bytes = Encoding.UTF8.GetBytes(Answer);
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = "application/json";
                        await c.Response.OutputStream.WriteAsync(bytes);
                        c.Response.Close();
                    }
                    catch (Exception) { break; }
                }
            });
        }

        public void Dispose() { try { _http.Stop(); _http.Close(); } catch { } }
    }

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

    private static AutomationElement Reveal(AutomationElement e)
    {
        try { e.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
        Thread.Sleep(250);
        return e;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_webhook_test_reaches_a_local_receiver_signed_with_the_secret_shown_once()
    {
        // Simulated: the receiver (a loopback HTTP listener). Real: Settings UI, Core, the delivery and the HMAC.
        const string name = nameof(A_webhook_test_reaches_a_local_receiver_signed_with_the_secret_shown_once);
        UiTestApplication.WaitForCorePortReleased();
        using var receiver = new Listener();
        using var app = new UiTestApplication(name);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_PrivacyNetwork_Button");
            ScrollTo(app, "Settings_Webhooks_Add_Button");
            UiWait.ById(app.MainWindow, "Settings_Webhooks_Url_TextBox").AsTextBox().Text = $"http://127.0.0.1:{receiver.Port}/parux";
            UiWait.ById(app.MainWindow, "Settings_Webhooks_Events_TextBox").AsTextBox().Text = "webhook.";
            Reveal(UiWait.ById(app.MainWindow, "Settings_Webhooks_Add_Button")).AsButton().Invoke();

            string secret = string.Empty;
            UiWait.For(() =>
            {
                foreach (AutomationElement e in app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
                {
                    try { string v = e.AsTextBox().Text; if (v.StartsWith("whsec_", StringComparison.Ordinal)) { secret = v; return true; } } catch { }
                }
                return false;
            }, "the signing secret is shown", TimeSpan.FromSeconds(60));
            app.MainWindow.FindAllDescendants().First(e => Text(e) == "I copied it").Click();

            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Settings_Webhooks_Test_", StringComparison.Ordinal)), "the hook is listed", TimeSpan.FromSeconds(30));
            var test = app.MainWindow.FindAllDescendants().First(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Settings_Webhooks_Test_", StringComparison.Ordinal));
            Reveal(test).AsButton().Invoke();

            // A hook subscribed to "webhook." also receives its own creation event; the test event is the one asked for here.
            UiWait.For(() => { lock (receiver.Requests) return receiver.Requests.Any(r => r.Headers.GetValueOrDefault("X-PaRuX-Event") == "webhook.test"); },
                "the receiver got the test delivery", TimeSpan.FromSeconds(30));
            (string path, _, Dictionary<string, string> headers, string body) = receiver.Requests.First(r => r.Headers.GetValueOrDefault("X-PaRuX-Event") == "webhook.test");
            Assert.Equal("/parux", path);
            Assert.Equal("webhook.test", headers["X-PaRuX-Event"]);
            string expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            Assert.Equal(expected, headers["X-PaRuX-Signature"]);
            Assert.Contains("webhook.test", body);

            // The result is reported in plain words next to the hook.
            var status = UiWait.ById(app.MainWindow, "Settings_Webhooks_Status_Text");
            UiWait.For(() => Text(status).Contains("Delivered", StringComparison.OrdinalIgnoreCase), "the owner is told it was delivered", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Smart_collections_filter_a_synthetic_library_by_a_saved_rule()
    {
        // Simulated: the library (synthetic titles, invented names, nothing launchable). Real: the dialog, Core's store and the filter.
        const string name = nameof(Smart_collections_filter_a_synthetic_library_by_a_saved_rule);
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(40);
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            app.ResizeMainWindow(1280, 800);
            Press(app, "GameHome_SmartCollections_Button");
            UiWait.ById(app.MainWindow, "SmartCollections_Dialog");
            UiWait.ById(app.MainWindow, "SmartCollections_Name_TextBox").AsTextBox().Text = "Top rated";
            UiWait.ById(app.MainWindow, "SmartCollections_Rule0_Field").AsComboBox().Select("Rating (0-10)");
            UiWait.ById(app.MainWindow, "SmartCollections_Rule0_Value").AsTextBox().Text = "9";
            Press(app, "SmartCollections_Save_Button");

            // Titles 0..39 with score = n % 11 and score >= 9: 9, 10, 20, 21, 31, 32.
            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => Text(e).StartsWith("Top rated", StringComparison.Ordinal) && Text(e).Contains("6")),
                "the saved collection shows how many games match", TimeSpan.FromSeconds(60));
            var show = app.MainWindow.FindAllDescendants().First(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("SmartCollections_Show_", StringComparison.Ordinal));
            show.AsButton().Invoke();

            UiWait.For(() => Text(UiWait.ById(app.MainWindow, "GameTitles_Status_Text")).StartsWith("6 games", StringComparison.Ordinal),
                "the library shows exactly the six matching games", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public async Task Steam_achievements_are_read_through_the_saved_key_from_a_simulated_steam()
    {
        // Simulated: Steam's Web API (a loopback listener reached through the test-only base URL). Real: Settings, Core, encryption.
        const string name = nameof(Steam_achievements_are_read_through_the_saved_key_from_a_simulated_steam);
        UiTestApplication.WaitForCorePortReleased();
        using var steam = new Listener
        {
            Answer = """{"playerstats":{"gameName":"Demo","success":true,"achievements":[{"apiname":"A","achieved":1,"unlocktime":1700000000,"name":"First","description":"d"},{"apiname":"B","achieved":0,"unlocktime":0,"name":"Second","description":""}]}}""",
        };
        using var app = new UiTestApplication(name, extraEnvironment: new Dictionary<string, string> { ["PARUX_STEAM_API_BASE_TEST"] = $"http://127.0.0.1:{steam.Port}" });
        try
        {
            const string key = "0123456789ABCDEF0123456789ABCDEF";
            const string steamId = "76561198000000000";
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_Integrations_Button");
            ScrollTo(app, "Settings_Steam_Save_Button");
            UiWait.ById(app.MainWindow, "Settings_Steam_Key_PasswordBox").Patterns.Value.Pattern.SetValue(key);
            UiWait.ById(app.MainWindow, "Settings_Steam_Id_TextBox").AsTextBox().Text = steamId;
            Reveal(UiWait.ById(app.MainWindow, "Settings_Steam_Save_Button")).AsButton().Invoke();
            var status = UiWait.ById(app.MainWindow, "Settings_Steam_Status_Text");
            UiWait.For(() => Text(status).Contains(steamId, StringComparison.Ordinal), "the credentials are saved", TimeSpan.FromSeconds(60));

            // The key is not stored in the clear.
            string stored = File.ReadAllText(Directory.EnumerateFiles(app.DataRoot, "steam-credentials.json", SearchOption.AllDirectories).First());
            Assert.DoesNotContain(key, stored, StringComparison.OrdinalIgnoreCase);

            // Ask the real Core for the achievements (the game card is only shown for a Steam installation, which this PC lacks).
            string bootstrap = File.ReadAllText(Path.Combine(app.DataRoot, "server", "bootstrap.json"));
            using JsonDocument doc = JsonDocument.Parse(bootstrap);
            string endpoint = doc.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/');
            string token = doc.RootElement.GetProperty("token").GetString()!;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var offline = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/v2/steam/achievements/440");
            offline.Headers.Add("X-PaRuX-Token", token);
            var refused = JsonDocument.Parse(await (await http.SendAsync(offline)).Content.ReadAsStringAsync()).RootElement;
            Assert.Contains("Internet", refused.GetProperty("problem").GetString());
            Assert.Empty(steam.Requests);   // nothing is asked of Steam while the Internet switch is off

            using var on = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/v1/internet") { Content = JsonContent.Create(new { enabled = true }) };
            on.Headers.Add("X-PaRuX-Token", token);
            Assert.True((await http.SendAsync(on)).IsSuccessStatusCode);

            using var ask = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/v2/steam/achievements/440");
            ask.Headers.Add("X-PaRuX-Token", token);
            var result = JsonDocument.Parse(await (await http.SendAsync(ask)).Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(string.Empty, result.GetProperty("problem").GetString());
            Assert.Equal(2, result.GetProperty("total").GetInt32());
            Assert.Equal(1, result.GetProperty("unlocked").GetInt32());
            (string path, string query, _, _) = Assert.Single(steam.Requests);
            Assert.Contains("GetPlayerAchievements", path);
            Assert.Contains("key=" + key, query);
            Assert.Contains("appid=440", query);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
