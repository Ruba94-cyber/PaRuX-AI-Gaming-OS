using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record KnowledgeHitView(string FileName, string FilePath, string Citation, string Text, string Match);

    public sealed record KnowledgeSourceStatus(string State, int Files, int Chunks, int Indexed, int Failed, int Problems, string? Current);

    /// <summary>Client of the Core's local knowledge index (search, per-folder index jobs). Local loopback only.</summary>
    public sealed class KnowledgeClient
    {
        public static KnowledgeClient Current { get; } = new KnowledgeClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(2));
        private KnowledgeClient() { }

        public async Task<List<KnowledgeHitView>> SearchAsync(string query, string mode, int k, CancellationToken ct)
        {
            JsonElement array = await SendAsync(HttpMethod.Get, "/api/v1/knowledge/search?q=" + Uri.EscapeDataString(query)
                + "&mode=" + Uri.EscapeDataString(mode) + "&k=" + k, ct).ConfigureAwait(false);
            var hits = new List<KnowledgeHitView>();
            if (array.ValueKind != JsonValueKind.Array) return hits;
            foreach (JsonElement e in array.EnumerateArray())
                hits.Add(new KnowledgeHitView(Str(e, "fileName"), Str(e, "filePath"), Str(e, "citation"), Str(e, "text"), Str(e, "match")));
            return hits;
        }

        public Task StartAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/index", ct);
        public Task PauseAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/pause", ct);
        public Task ResumeAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/resume", ct);
        public Task CancelAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/cancel", ct);

        public async Task<KnowledgeSourceStatus> StatusAsync(string id, CancellationToken ct)
        {
            JsonElement e = await SendAsync(HttpMethod.Get, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/status", ct).ConfigureAwait(false);
            string state = "idle"; int indexed = 0, failed = 0; string? current = null;
            if (e.TryGetProperty("job", out JsonElement job) && job.ValueKind == JsonValueKind.Object)
            {
                state = Str(job, "state");
                if (job.TryGetProperty("progress", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
                {
                    indexed = Int(p, "indexed"); failed = Int(p, "failed"); current = Str(p, "current");
                }
            }
            int problems = e.TryGetProperty("problems", out JsonElement pr) && pr.ValueKind == JsonValueKind.Array ? pr.GetArrayLength() : 0;
            return new KnowledgeSourceStatus(state, Int(e, "files"), Int(e, "chunks"), indexed, failed, problems, current);
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        private static async Task<JsonElement> SendAsync(HttpMethod method, string path, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException("Knowledge request failed (" + (int)response.StatusCode + ").");
            if (string.IsNullOrWhiteSpace(body)) return default;
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }
}
