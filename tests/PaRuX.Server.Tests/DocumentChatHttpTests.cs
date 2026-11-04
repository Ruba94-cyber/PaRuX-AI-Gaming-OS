using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Chat with documents through the real Core pipeline (POST /api/v1/chat/completions) with no language model running at all:
/// deterministic lookups carry citations, unsupported questions get the explicit "not enough information" answer, and the
/// scope picks which folders may answer. Synthetic documents are generated here.
/// </summary>
[Collection("KnowledgeStatic")]
public sealed class DocumentChatHttpTests : IClassFixture<DocumentChatHttpTests.Factory>
{
    private readonly Factory _factory;
    public DocumentChatHttpTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<global::Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lks-chat-" + Guid.NewGuid().ToString("N"));
        public string SourceA = string.Empty, SourceEmpty = string.Empty;
        public KnowledgeSettingsStore Settings { get; }

        public Factory()
        {
            Directory.CreateDirectory(Root);
            Settings = new KnowledgeSettingsStore(Path.Combine(Root, "settings.json"));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                string docs = Path.Combine(Root, "docs"), empty = Path.Combine(Root, "empty");
                Directory.CreateDirectory(docs); Directory.CreateDirectory(empty);
                File.WriteAllText(Path.Combine(docs, "maintenance.md"),
                    "# Pump service\nThe coolant pump parameter ZQ_4242 sets the flow limit to 12 litres per minute.\nReplace the filter every 500 hours.");
                File.WriteAllText(Path.Combine(docs, "warranty.txt"), "The warranty period for the gearbox is 24 months from delivery.");
                var rag = new RagStore(new EmbeddingService(Path.Combine(Root, "emb")), Path.Combine(Root, "ragdb"));
                SourceA = rag.AddSource(docs).Id;
                SourceEmpty = rag.AddSource(empty).Id;
                var index = new KnowledgeIndex(Path.Combine(Root, "k.db"));
                index.IndexFolderAsync(SourceA, docs).GetAwaiter().GetResult();
                var job = new KnowledgeJobService(rag, Settings, null, index, new UnavailableOcrEngine("test"));
                services.RemoveAll<KnowledgeJobService>();
                services.AddSingleton(job);
                services.AddSingleton<IStartupFilter, LoopbackRemoteAddressStartupFilter>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private HttpClient Client()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private async Task<string> Ask(string question, bool enabled = true, params string[] sources)
    {
        using HttpClient client = Client();
        var body = new ChatCompletionRequest
        {
            Model = "auto", Stream = true,
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = question } },
            Documents = new ChatDocumentScope { Enabled = enabled, SourceIds = sources.ToList() }
        };
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/chat/completions", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Exact_lookup_is_answered_from_the_documents_with_a_page_or_heading_citation_and_no_model()
    {
        string sse = await Ask("where is ZQ_4242 described", true, _factory.SourceA);
        Assert.Contains("\"parux_event\":\"document_answer\"", sse);
        Assert.Contains("\"kind\":\"deterministic\"", sse);
        Assert.Contains("\"usedModel\":false", sse);
        Assert.Contains("\"parux_event\":\"rag_sources\"", sse);
        Assert.Contains("maintenance.md", sse);
        Assert.Contains("12 litres per minute", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
    }

    [Fact]
    public async Task Unsupported_question_gets_the_explicit_not_enough_information_answer()
    {
        string sse = await Ask("what is the torque of the quantum flux capacitor", true, _factory.SourceA);
        Assert.Contains("\"kind\":\"not_enough_information\"", sse);
        Assert.Contains("Not enough information in the selected documents", sse);
        Assert.DoesNotContain("rag_sources", sse);
    }

    [Fact]
    public async Task Scope_limits_which_folders_may_answer()
    {
        string inScope = await Ask("where is ZQ_4242 described", true, _factory.SourceA);
        string outOfScope = await Ask("where is ZQ_4242 described", true, _factory.SourceEmpty);
        Assert.Contains("deterministic", inScope);
        Assert.Contains("not_enough_information", outOfScope);
    }

    [Fact]
    public async Task Non_stream_requests_get_the_same_answer_as_json()
    {
        using HttpClient client = Client();
        var body = new ChatCompletionRequest
        {
            Model = "auto", Stream = false,
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "who painted the ceiling of the moon base" } },
            Documents = new ChatDocumentScope { Enabled = true, SourceIds = { _factory.SourceA } }
        };
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/chat/completions", body);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_enough_information", doc.RootElement.GetProperty("parux_document_answer").GetProperty("kind").GetString());
        Assert.Contains("Not enough information", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Adding_a_credential_folder_is_refused_and_a_broad_folder_needs_consent()
    {
        using HttpClient client = Client();
        string blocked = Path.Combine(_factory.Root, ".ssh");
        Directory.CreateDirectory(blocked);
        HttpResponseMessage r1 = await client.PostAsJsonAsync("/api/v1/rag/sources", new RagAddSourceRequest { FolderPath = blocked, ConsentSensitive = true });
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);   // consent never overrides a credential store
        Assert.Contains("sensitive_blocked", await r1.Content.ReadAsStringAsync());

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        HttpResponseMessage r2 = await client.PostAsJsonAsync("/api/v1/rag/sources", new RagAddSourceRequest { FolderPath = profile });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Contains("consent_required", await r2.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Knowledge_settings_default_to_off_and_persist_changes()
    {
        using HttpClient client = Client();
        using (JsonDocument before = JsonDocument.Parse(await client.GetStringAsync("/api/v1/knowledge/settings")))
        {
            Assert.False(before.RootElement.GetProperty("ocrEnabled").GetBoolean());
            Assert.False(before.RootElement.GetProperty("strictOffline").GetBoolean());
        }
        HttpResponseMessage put = await client.PutAsJsonAsync("/api/v1/knowledge/settings", new { ocrEnabled = true, profile = "Eco", strictOffline = true });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using (JsonDocument after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/knowledge/settings")))
        {
            Assert.True(after.RootElement.GetProperty("ocrEnabled").GetBoolean());
            Assert.True(after.RootElement.GetProperty("strictOffline").GetBoolean());
            Assert.Equal("Eco", after.RootElement.GetProperty("profile").GetString());
            Assert.False(after.RootElement.GetProperty("profileIsAutomatic").GetBoolean());
        }
        await client.PutAsJsonAsync("/api/v1/knowledge/settings", new { ocrEnabled = false, profile = "auto", strictOffline = false });
        Assert.False(_factory.Settings.Get().StrictOffline);
        HttpResponseMessage bad = await client.PutAsJsonAsync("/api/v1/knowledge/settings", new { profile = "turbo" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

[CollectionDefinition("KnowledgeStatic", DisableParallelization = true)]
public sealed class KnowledgeStaticCollection { }
