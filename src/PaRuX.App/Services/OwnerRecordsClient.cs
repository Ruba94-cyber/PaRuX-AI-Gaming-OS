using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Desktop-owner calls for installed plugins, the activity (audit) log, webhooks and sampling presets. Core stays authoritative.</summary>
    public sealed class OwnerRecordsClient
    {
        public static OwnerRecordsClient Current { get; } = new OwnerRecordsClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(20));
        private OwnerRecordsClient() { }

        public Task<List<InstalledPluginDto>> ListPluginsAsync(CancellationToken ct) => Get<List<InstalledPluginDto>>("/api/v2/plugins", ct);
        public Task<PluginUninstallResult> UninstallPluginAsync(string id, string version, CancellationToken ct) =>
            Send<PluginUninstallResult>(HttpMethod.Delete, "/api/v2/plugins/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(version), null, ct);

        public Task<List<AuditEntry>> RecentAuditAsync(int limit, CancellationToken ct) => Get<List<AuditEntry>>("/api/v2/audit?limit=" + limit, ct);
        public Task<AuditVerification> VerifyAuditAsync(CancellationToken ct) => Get<AuditVerification>("/api/v2/audit/verify", ct);

        public Task<List<WebhookDto>> ListWebhooksAsync(CancellationToken ct) => Get<List<WebhookDto>>("/api/v2/webhooks", ct);
        public Task<WebhookCreated> CreateWebhookAsync(CreateWebhookRequest request, CancellationToken ct) =>
            Send<WebhookCreated>(HttpMethod.Post, "/api/v2/webhooks", request, ct);
        public Task<WebhookTestResult> TestWebhookAsync(string id, CancellationToken ct) =>
            Send<WebhookTestResult>(HttpMethod.Post, "/api/v2/webhooks/" + Uri.EscapeDataString(id) + "/test", null, ct);
        public Task DeleteWebhookAsync(string id, CancellationToken ct) =>
            SendNoContent(HttpMethod.Delete, "/api/v2/webhooks/" + Uri.EscapeDataString(id), ct);

        public Task<List<PrincipalRoleEntry>> ListRolesAsync(CancellationToken ct) => Get<List<PrincipalRoleEntry>>("/api/v2/roles", ct);
        public Task<List<PrincipalRoleEntry>> SetRoleAsync(string principalId, string role, CancellationToken ct) =>
            Send<List<PrincipalRoleEntry>>(HttpMethod.Put, "/api/v2/roles", new SetPrincipalRoleRequest { PrincipalId = principalId, Role = role }, ct);
        public Task<RemoteAccessStatus> RemoteStatusAsync(CancellationToken ct) => Get<RemoteAccessStatus>("/api/v1/remote/status", ct);

        public Task<XInputShimStatus> XInputShimStatusAsync(CancellationToken ct) => Get<XInputShimStatus>("/api/v1/gaming/xinput-shim", ct);
        public Task<XInputShimResult> InstallXInputShimAsync(string folder, CancellationToken ct) =>
            SendKeepBody<XInputShimResult>(HttpMethod.Post, "/api/v1/gaming/xinput-shim/install", new XInputShimFolderRequest { Folder = folder }, ct);
        public Task<XInputShimResult> UninstallXInputShimAsync(string folder, CancellationToken ct) =>
            SendKeepBody<XInputShimResult>(HttpMethod.Post, "/api/v1/gaming/xinput-shim/uninstall", new XInputShimFolderRequest { Folder = folder }, ct);

        /// <summary>Like Send, but a refusal (HTTP 400/409) still carries the result object with its plain-language reason.</summary>
        private static async Task<T> SendKeepBody<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using HttpRequestMessage request = await Build(method, path, body, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core answered " + (int)response.StatusCode + ".");
        }

        public Task<SteamCredentialsStatus> SteamStatusAsync(CancellationToken ct) => Get<SteamCredentialsStatus>("/api/v2/steam/credentials", ct);
        public Task<SteamCredentialsStatus> SaveSteamAsync(SteamCredentialsRequest request, CancellationToken ct) =>
            Send<SteamCredentialsStatus>(HttpMethod.Put, "/api/v2/steam/credentials", request, ct);
        public Task DeleteSteamAsync(CancellationToken ct) => SendNoContent(HttpMethod.Delete, "/api/v2/steam/credentials", ct);
        public Task<SteamAchievementsResult> SteamAchievementsAsync(int appId, CancellationToken ct) =>
            Get<SteamAchievementsResult>("/api/v2/steam/achievements/" + appId, ct);

        public Task<List<GameSmartCollection>> ListSmartCollectionsAsync(CancellationToken ct) =>
            Get<List<GameSmartCollection>>("/api/v1/gaming/smart-collections", ct);
        public Task<GameSmartCollection> SaveSmartCollectionAsync(GameSmartCollection collection, CancellationToken ct) =>
            Send<GameSmartCollection>(HttpMethod.Put, "/api/v1/gaming/smart-collections", collection, ct);
        public Task DeleteSmartCollectionAsync(string id, CancellationToken ct) =>
            SendNoContent(HttpMethod.Delete, "/api/v1/gaming/smart-collections/" + Uri.EscapeDataString(id), ct);

        public Task<List<SamplingPreset>> ListPresetsAsync(CancellationToken ct) => Get<List<SamplingPreset>>("/api/v2/sampling-presets", ct);
        public Task<SamplingPreset> SavePresetAsync(SamplingPreset preset, CancellationToken ct) =>
            Send<SamplingPreset>(HttpMethod.Put, "/api/v2/sampling-presets", preset, ct);
        public Task DeletePresetAsync(string id, CancellationToken ct) =>
            SendNoContent(HttpMethod.Delete, "/api/v2/sampling-presets/" + Uri.EscapeDataString(id), ct);

        private static Task<T> Get<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, path, null, ct);

        private static async Task<HttpRequestMessage> Build(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            return request;
        }

        private static async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using HttpRequestMessage request = await Build(method, path, body, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException(await ReadError(response, ct).ConfigureAwait(false));
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty answer.");
        }

        private static async Task SendNoContent(HttpMethod method, string path, CancellationToken ct)
        {
            using HttpRequestMessage request = await Build(method, path, null, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException(await ReadError(response, ct).ConfigureAwait(false));
        }

        /// <summary>The server's own sentence when it sent one ({"error":"..."}), otherwise a short generic line.</summary>
        private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken ct)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) return e.GetString()!;
            }
            catch (System.Text.Json.JsonException) { }
            return "PaRuX Core answered " + (int)response.StatusCode + ".";
        }
    }
}
