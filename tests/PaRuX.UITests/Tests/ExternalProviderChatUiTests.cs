using System.Net;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// An OpenAI-compatible server on this PC (Ollama, LM Studio and so on) is registered in Settings as a comparison target; the Core keeps
/// using its own managed runtime for chat. Simulated: that server (a loopback listener that answers /v1/models and
/// streams a fixed reply). Real: Settings, the Core's provider routing, the chat window and the streamed answer.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ExternalProviderChatUiTests
{
    private const string Reply = "Hello from the external model server.";

    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private sealed class FakeOpenAiServer : IDisposable
    {
        private readonly HttpListener _http = new();
        public int Port { get; }
        public List<string> ChatBodies { get; } = new();

        public FakeOpenAiServer()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(async () =>
            {
                while (_http.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _http.GetContextAsync(); } catch (Exception) { break; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        private void Serve(HttpListenerContext c)
        {
            try
            {
                string path = c.Request.Url!.AbsolutePath;
                if (path.EndsWith("/models", StringComparison.Ordinal))
                {
                    Write(c, "application/json", "{\"object\":\"list\",\"data\":[{\"id\":\"fake-model\",\"object\":\"model\"}]}");
                    return;
                }
                if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
                {
                    string body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
                    lock (ChatBodies) ChatBodies.Add(body);
                    bool stream = body.Contains("\"stream\":true", StringComparison.Ordinal) || body.Contains("\"stream\": true", StringComparison.Ordinal);
                    if (!stream)
                    {
                        Write(c, "application/json", "{\"id\":\"x\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + Reply + "\"},\"finish_reason\":\"stop\"}]}");
                        return;
                    }
                    c.Response.ContentType = "text/event-stream";
                    c.Response.StatusCode = 200;
                    foreach (string word in Reply.Split(' '))
                    {
                        byte[] chunk = Encoding.UTF8.GetBytes("data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + word + " \"}}]}\n\n");
                        c.Response.OutputStream.Write(chunk);
                        c.Response.OutputStream.Flush();
                        Thread.Sleep(30);
                    }
                    byte[] done = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                    c.Response.OutputStream.Write(done);
                    c.Response.Close();
                    return;
                }
                c.Response.StatusCode = 404;
                c.Response.Close();
            }
            catch (Exception) { try { c.Response.Abort(); } catch { } }
        }

        private static void Write(HttpListenerContext c, string type, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            c.Response.ContentType = type;
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes);
            c.Response.Close();
        }

        public void Dispose() { try { _http.Stop(); _http.Close(); } catch { } }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public async Task A_registered_openai_compatible_server_is_a_comparison_target_and_chat_is_not_redirected_to_it()
    {
        const string name = nameof(A_registered_openai_compatible_server_is_a_comparison_target_and_chat_is_not_redirected_to_it);
        UiTestApplication.WaitForCorePortReleased();
        using var server = new FakeOpenAiServer();
        using var app = new UiTestApplication(name);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_Models_Button");

            UiWait.ById(app.MainWindow, "ManualBackendNameBox").AsTextBox().Text = "Fake server";
            UiWait.ById(app.MainWindow, "ManualBackendEndpointBox").AsTextBox().Text = $"http://127.0.0.1:{server.Port}";
            var use = app.MainWindow.FindAllDescendants().First(e => Text(e) == "Use" && e.ControlType == ControlType.Button);
            try { use.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
            use.AsButton().Invoke();

            var status = UiWait.ById(app.MainWindow, "ExternalBackendStatusText", ControlType.Text);
            UiWait.For(() => Text(status).Contains("Fake server", StringComparison.Ordinal), "the backend is chosen", TimeSpan.FromSeconds(60));

            // The page is honest about what this does: a comparison target, not a redirect of chat.
            var description = app.MainWindow.FindAllDescendants().FirstOrDefault(e => Text(e).Contains("comparison target for benchmarks", StringComparison.Ordinal));
            Assert.NotNull(description);
            Assert.Contains("Chat keeps using PaRuX's own local model", Text(description!), StringComparison.Ordinal);

            // And it is true: the Core still answers chat with its own managed runtime (here missing, so it says so) and never sends
            // the message to the registered server.
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(app.DataRoot, "server", "bootstrap.json"))))
            {
                string endpoint = doc.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/');
                string token = doc.RootElement.GetProperty("token").GetString()!;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/v1/chat/completions")
                { Content = new StringContent("{\"model\":\"local-default\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Say hello\"}]}", Encoding.UTF8, "application/json") };
                request.Headers.Add("X-PaRuX-Token", token);
                await http.SendAsync(request);
            }
            lock (server.ChatBodies) Assert.Empty(server.ChatBodies);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
