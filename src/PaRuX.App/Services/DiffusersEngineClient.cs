using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Status and explicit install of the PaRuX-managed Diffusers image engine (Core owns the work).</summary>
    public sealed class DiffusersEngineClient
    {
        public static DiffusersEngineClient Current { get; } = new DiffusersEngineClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(3));
        private static readonly HttpClient LongRunningHttp = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(45));
        private DiffusersEngineClient() { }

        public async Task<ManagedMediaEngineStatus> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v2/media/engines/diffusers");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ManagedMediaEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new ManagedMediaEngineStatus();
        }

        /// <summary>Runs the install; a refusal throws, and the caller re-reads the status for the truthful reason code.</summary>
        public async Task InstallAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v2/media/engines/diffusers/install");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await LongRunningHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Diffusers engine install failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
