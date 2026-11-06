using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record KnowledgeHitView(string FileName, string FilePath, string Citation, string Text, string Match,
        int? Page = null, string? Sheet = null, string? CellRange = null, int? Slide = null, string? Heading = null);

    public sealed record KnowledgeProblem(string RelativePath, string Kind, string Reason);

    public sealed record KnowledgeProgress(int FilesSeen, int Indexed, int Unchanged, int MetadataOnly, int Failed, int Skipped, int Removed, int Renamed, int Duplicates,
        string? Current = null, int? CurrentPage = null, int? TotalPages = null);

    public sealed record KnowledgeFullStatus(string State, KnowledgeProgress? Progress, int Files, int Chunks, IReadOnlyList<KnowledgeProblem> Problems, string? Error);

    public sealed record KnowledgeSettingsView(bool OcrEnabled, bool StrictOffline, bool CorporateOffline, string Profile, bool ProfileIsAutomatic,
        string RecommendedProfile, int MemoryBudgetMb, int EmbeddingThreads, int TotalRamMb, bool? OcrAvailable, string? OcrReason, string OcrLanguages);

    /// <summary>The Core refused to add a folder: <see cref="Code"/> is "sensitive_blocked" or "consent_required".</summary>
    public sealed class SensitiveFolderException : Exception
    {
        public string Code { get; }
        public SensitiveFolderException(string code, string message) : base(message) { Code = code; }
    }

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
                hits.Add(new KnowledgeHitView(Str(e, "fileName"), Str(e, "filePath"), Str(e, "citation"), Str(e, "text"), Str(e, "match"),
                    NullableInt(e, "page"), NullableStr(e, "sheet"), NullableStr(e, "cellRange"), NullableInt(e, "slide"), NullableStr(e, "heading")));
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

        public async Task<KnowledgeFullStatus> FullStatusAsync(string id, CancellationToken ct)
        {
            JsonElement e = await SendAsync(HttpMethod.Get, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/status", ct).ConfigureAwait(false);
            string state = "idle"; string? error = null; KnowledgeProgress? progress = null;
            if (e.TryGetProperty("job", out JsonElement job) && job.ValueKind == JsonValueKind.Object)
            {
                state = Str(job, "state"); error = NullableStr(job, "error");
                if (job.TryGetProperty("progress", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
                    progress = new KnowledgeProgress(Int(p, "filesSeen"), Int(p, "indexed"), Int(p, "unchanged"), Int(p, "metadataOnly"), Int(p, "failed"),
                        Int(p, "skipped"), Int(p, "removed"), Int(p, "renamed"), Int(p, "duplicates"),
                        NullableStr(p, "current"), NullableInt(p, "currentPage"), NullableInt(p, "totalPages"));
            }
            var problems = new List<KnowledgeProblem>();
            if (e.TryGetProperty("problems", out JsonElement pr) && pr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement x in pr.EnumerateArray()) problems.Add(new KnowledgeProblem(Str(x, "relativePath"), Str(x, "kind"), Str(x, "reason")));
            return new KnowledgeFullStatus(state, progress, Int(e, "files"), Int(e, "chunks"), problems, error);
        }

        public async Task<KnowledgeSettingsView> GetSettingsAsync(bool probeOcr, CancellationToken ct)
        {
            JsonElement e = await SendAsync(HttpMethod.Get, "/api/v1/knowledge/settings" + (probeOcr ? "?probe=true" : string.Empty), ct).ConfigureAwait(false);
            bool? available = null; string? reason = null; string langs = string.Empty;
            if (e.TryGetProperty("ocr", out JsonElement ocr) && ocr.ValueKind == JsonValueKind.Object)
            {
                available = ocr.TryGetProperty("available", out JsonElement a) && a.ValueKind == JsonValueKind.True;
                reason = NullableStr(ocr, "reason");
                if (ocr.TryGetProperty("languages", out JsonElement l) && l.ValueKind == JsonValueKind.Array)
                {
                    var tags = new List<string>(); foreach (JsonElement x in l.EnumerateArray()) tags.Add(x.GetString() ?? string.Empty);
                    langs = string.Join(", ", tags);
                }
            }
            return new KnowledgeSettingsView(Bool(e, "ocrEnabled"), Bool(e, "strictOffline"), Bool(e, "corporateOffline"), Str(e, "profile"), Bool(e, "profileIsAutomatic"),
                Str(e, "recommendedProfile"), Int(e, "memoryBudgetMb"), Int(e, "embeddingThreads"), Int(e, "totalRamMb"), available, reason, langs);
        }

        public Task SaveSettingsAsync(bool? ocrEnabled, string? profile, bool? strictOffline, CancellationToken ct) =>
            SendAsync(HttpMethod.Put, "/api/v1/knowledge/settings", ct, new { ocrEnabled, profile, strictOffline });

        /// <summary>Secure "delete everything": folders, index rows, FTS/trigram/vector entries and database files. Documents are never touched.</summary>
        public async Task<(int Files, int Chunks, bool DatabaseFilesRemoved)> DeleteEverythingAsync(CancellationToken ct)
        {
            JsonElement e = await SendAsync(HttpMethod.Post, "/api/v1/knowledge/delete-everything", ct).ConfigureAwait(false);
            return (Int(e, "files"), Int(e, "chunks"), Bool(e, "databaseFilesRemoved"));
        }

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
        private static string? NullableStr(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int? NullableInt(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        private static async Task<JsonElement> SendAsync(HttpMethod method, string path, CancellationToken ct, object? payload = null)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (payload != null) request.Content = System.Net.Http.Json.JsonContent.Create(payload);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException("Knowledge request failed (" + (int)response.StatusCode + ").");
            if (string.IsNullOrWhiteSpace(body)) return default;
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }
}
