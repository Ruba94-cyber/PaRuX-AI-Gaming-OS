using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class GamingOrchestratorClient
    {
        public static GamingOrchestratorClient Current { get; } = new GamingOrchestratorClient();

        private static readonly HttpClient HttpClient = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(10));
        // Large local archives need a dedicated opt-in transfer timeout. Ordinary
        // Gaming control/status requests retain their tight ten-second budget.
        private static readonly HttpClient InspectionHttpClient =
            PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(10));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private GamingOrchestratorClient() { }

        // Read paths keep their null/empty results: the status page refreshes them and shows what it has.
        public async Task<GamingStatusDto?> GetStatusAsync(CancellationToken ct = default)
        {
            return (await SendAsync<GamingStatusDto>(HttpMethod.Get, "/api/v1/gaming/status", null, ct).ConfigureAwait(false)).Value;
        }

        // Actions and saves return the reason on refusal, so the Gaming page can show it.
        public Task<GamingCallResult<GamingStatusDto>> EnterGamingAsync(string? profileName = null, CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/enter",
                new GamingActionRequest { Action = "enter", ProfileName = profileName }, ct);

        public Task<GamingCallResult<GamingStatusDto>> ExitGamingAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/exit", null, ct);

        public Task<GamingCallResult<GamingStatusDto>> SuspendGameAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/suspend", null, ct);

        public Task<GamingCallResult<GamingStatusDto>> ResumeGameAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/resume", null, ct);

        public async Task<List<GamingProfileDto>> ListProfilesAsync(CancellationToken ct = default)
        {
            List<GamingProfileDto>? list = (await SendAsync<List<GamingProfileDto>>(HttpMethod.Get, "/api/v1/gaming/profiles", null, ct).ConfigureAwait(false)).Value;
            return list ?? new List<GamingProfileDto>();
        }

        // The save path must tell a failed load from an empty list, so it receives the reason as well.
        public Task<GamingCallResult<List<GamingProfileDto>>> ListProfilesResultAsync(CancellationToken ct = default) =>
            SendAsync<List<GamingProfileDto>>(HttpMethod.Get, "/api/v1/gaming/profiles", null, ct);

        public Task<GamingCallResult<GamingProfileDto>> SaveProfileAsync(GamingProfileDto profile, CancellationToken ct = default) =>
            SendAsync<GamingProfileDto>(HttpMethod.Post, "/api/v1/gaming/profiles", profile, ct);

        public async Task<List<GamingProviderStatusDto>> ListProvidersAsync(CancellationToken ct = default)
        {
            List<GamingProviderStatusDto>? list = (await SendAsync<List<GamingProviderStatusDto>>(HttpMethod.Get, "/api/v1/gaming/providers", null, ct).ConfigureAwait(false)).Value;
            return list ?? new List<GamingProviderStatusDto>();
        }

        // First-party HTTP(S) game download queue. All mutations require desktop ownership in Core.
        // Transfers never execute the downloaded file or run while Gaming is active.
        public Task<GamingCallResult<List<GameDownloadDto>>> ListGameDownloadsAsync(CancellationToken ct = default) =>
            SendAsync<List<GameDownloadDto>>(HttpMethod.Get, "/api/v1/gaming/downloads", null, ct);

        public Task<GamingCallResult<GameDownloadDto>> EnqueueGameDownloadAsync(GameDownloadRequest request, CancellationToken ct = default) =>
            SendAsync<GameDownloadDto>(HttpMethod.Post, "/api/v1/gaming/downloads", request, ct);

        public Task<GamingCallResult<object>> SetGameDownloadLimitAsync(long bytesPerSecond, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/gaming/downloads/limit",
                new GameDownloadLimitRequest(bytesPerSecond), ct);

        public Task<GamingCallResult<object>> PauseGameDownloadAsync(string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Post, "/api/v1/gaming/downloads/" + Uri.EscapeDataString(id) + "/pause", null, ct);

        public Task<GamingCallResult<object>> ResumeGameDownloadAsync(string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Post, "/api/v1/gaming/downloads/" + Uri.EscapeDataString(id) + "/resume", null, ct);

        public Task<GamingCallResult<object>> RemoveGameDownloadAsync(string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Delete, "/api/v1/gaming/downloads/" + Uri.EscapeDataString(id), null, ct);

        public Task<GamingCallResult<List<StagedGamePackageDto>>> ListStagedPackagesAsync(
            CancellationToken ct = default) =>
            SendAsync<List<StagedGamePackageDto>>(
                HttpMethod.Get, "/api/v1/gaming/installers/staged", null, ct);

        public Task<GamingCallResult<object>> DeleteStagedPackageAsync(
            string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Delete,
                "/api/v1/gaming/installers/staged/" + Uri.EscapeDataString(id), null, ct);

        public Task<GamingCallResult<GamePackageStageResult>> StageGamePackageAsync(
            GamePackageStageRequest request, CancellationToken ct = default) =>
            SendAsync<GamePackageStageResult>(
                HttpMethod.Post, "/api/v1/gaming/installers/stage", request, ct, InspectionHttpClient);

        public Task<GamingCallResult<GameInstallerInspectionResult>> InspectGameInstallerAsync(
            GameInstallerInspectionRequest request, CancellationToken ct = default) =>
            SendAsync<GameInstallerInspectionResult>(
                HttpMethod.Post, "/api/v1/gaming/installers/inspect", request, ct, InspectionHttpClient);

        public Task<GamingCallResult<List<GamingControllerStateDto>>> GetControllersAsync(CancellationToken ct = default) =>
            SendAsync<List<GamingControllerStateDto>>(HttpMethod.Get, "/api/v1/gaming/controllers", null, ct);

        public Task<GamingCallResult<GamingDisplaySnapshotDto>> GetDisplayTopologyAsync(CancellationToken ct = default) =>
            SendAsync<GamingDisplaySnapshotDto>(HttpMethod.Get, "/api/v1/gaming/displays", null, ct);

        public Task<GamingCallResult<List<GamingDisplayModeOptionDto>>> GetDisplayModesAsync(string displayId, CancellationToken ct = default) =>
            SendAsync<List<GamingDisplayModeOptionDto>>(HttpMethod.Get,
                "/api/v1/gaming/displays/modes?display=" + Uri.EscapeDataString(displayId), null, ct);

        public Task<GamingCallResult<GamingDisplayChangeStatusDto>> GetDisplayChangeAsync(CancellationToken ct = default) =>
            SendAsync<GamingDisplayChangeStatusDto>(HttpMethod.Get, "/api/v1/gaming/displays/change", null, ct);

        public Task<GamingCallResult<GamingDisplayChangeStatusDto>> RequestDisplayChangeAsync(GamingDisplayModeChangeRequest request, CancellationToken ct = default) =>
            SendAsync<GamingDisplayChangeStatusDto>(HttpMethod.Post, "/api/v1/gaming/displays/change", request, ct);

        public Task<GamingCallResult<GamingDisplayChangeStatusDto>> ConfirmDisplayChangeAsync(CancellationToken ct = default) =>
            SendAsync<GamingDisplayChangeStatusDto>(HttpMethod.Post, "/api/v1/gaming/displays/change/confirm", null, ct);

        public Task<GamingCallResult<GamingDisplayChangeStatusDto>> RevertDisplayChangeAsync(CancellationToken ct = default) =>
            SendAsync<GamingDisplayChangeStatusDto>(HttpMethod.Post, "/api/v1/gaming/displays/change/revert", null, ct);

        public Task<GamingCallResult<ControllerProfileEnvelope>> GetControllerProfileAsync(CancellationToken ct = default) =>
            SendAsync<ControllerProfileEnvelope>(HttpMethod.Get, "/api/v1/gaming/controller-profile", null, ct);

        public Task<GamingCallResult<GamingControllerProfile>> SaveControllerProfileAsync(GamingControllerProfile profile, CancellationToken ct = default) =>
            SendAsync<GamingControllerProfile>(HttpMethod.Put, "/api/v1/gaming/controller-profile", profile, ct);

        public Task<GamingCallResult<GamingControllerProfile>> ResetControllerProfileAsync(CancellationToken ct = default) =>
            SendAsync<GamingControllerProfile>(HttpMethod.Delete, "/api/v1/gaming/controller-profile", null, ct);

        public Task<GamingCallResult<ControllerCalibrationResult>> CalibrateControllerAsync(int slot, CancellationToken ct = default) =>
            SendAsync<ControllerCalibrationResult>(HttpMethod.Post, "/api/v1/gaming/controllers/" + slot + "/calibrate", null, ct);

        public Task<GamingCallResult<GameFolderInspectionDto>> InspectGameFolderAsync(string folder, CancellationToken ct = default) =>
            SendAsync<GameFolderInspectionDto>(HttpMethod.Post, "/api/v1/gaming/library/inspect-folder", new GameFolderInspectRequest(folder), ct);

        public Task<GamingCallResult<List<GameTitleDto>>> GetTitlesAsync(string? text, string? source, bool favoritesOnly, CancellationToken ct = default) =>
            SendAsync<List<GameTitleDto>>(HttpMethod.Get,
                "/api/v1/gaming/titles?q=" + Uri.EscapeDataString(text ?? "") + "&source=" + Uri.EscapeDataString(source ?? "")
                + (favoritesOnly ? "&favorite=true" : ""), null, ct);

        public Task<GamingCallResult<object>> SetTitleFavoriteAsync(string key, bool favorite, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put,
                "/api/v1/gaming/titles/" + Uri.EscapeDataString(key) + "/favorite?favorite=" + (favorite ? "true" : "false"), null, ct);

        public Task<GamingCallResult<object>> SetTitleTagsAsync(string key, List<string> tags, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/gaming/titles/" + Uri.EscapeDataString(key) + "/tags", new SetGameTagsRequest(tags), ct);

        public Task<GamingCallResult<object>> SetTitleDefaultInstallationAsync(string key, string? installationId, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/gaming/titles/" + Uri.EscapeDataString(key) + "/default-installation",
                new SetDefaultInstallationRequest(installationId), ct);

        public Task<GamingCallResult<EmulatorListDto>> GetEmulatorsAsync(CancellationToken ct = default) =>
            SendAsync<EmulatorListDto>(HttpMethod.Get, "/api/v1/gaming/emulators", null, ct);

        public Task<GamingCallResult<EmulatorScanDto>> ScanEmulatorGamesAsync(string emulatorId, string folder, CancellationToken ct = default) =>
            SendAsync<EmulatorScanDto>(HttpMethod.Post, "/api/v1/gaming/emulators/scan", new EmulatorScanRequestDto(emulatorId, folder), ct);

        public Task<GamingCallResult<EmulatorImportResultDto>> ImportEmulatorGamesAsync(EmulatorImportRequestDto request, CancellationToken ct = default) =>
            SendAsync<EmulatorImportResultDto>(HttpMethod.Post, "/api/v1/gaming/emulators/import", request, ct);

        public Task<GamingCallResult<List<DiscoveredGameDto>>> DiscoverSteamGamesAsync(CancellationToken ct = default) =>
            SendAsync<List<DiscoveredGameDto>>(HttpMethod.Get, "/api/v1/gaming/library/discover/steam", null, ct);

        public Task<GamingCallResult<List<DiscoveredGameDto>>> DiscoverEpicGamesAsync(CancellationToken ct = default) =>
            SendAsync<List<DiscoveredGameDto>>(HttpMethod.Get, "/api/v1/gaming/library/discover/epic", null, ct);

        public Task<GamingCallResult<ManualGameLaunchResult>> RequestEpicGameLaunchAsync(string appId, CancellationToken ct = default) =>
            SendAsync<ManualGameLaunchResult>(HttpMethod.Post,
                "/api/v1/gaming/library/discover/epic/" + Uri.EscapeDataString(appId) + "/launch", null, ct);

        public Task<GamingCallResult<ManualGameLaunchResult>> RequestSteamGameLaunchAsync(string appId, CancellationToken ct = default) =>
            SendAsync<ManualGameLaunchResult>(HttpMethod.Post,
                "/api/v1/gaming/library/discover/steam/" + Uri.EscapeDataString(appId) + "/launch", null, ct);

        public Task<GamingCallResult<List<DiscoveredGameDto>>> DiscoverGogGamesAsync(CancellationToken ct = default) =>
            SendAsync<List<DiscoveredGameDto>>(HttpMethod.Get, "/api/v1/gaming/library/discover/gog", null, ct);

        public Task<GamingCallResult<ManualGameLaunchResult>> RequestGogGameLaunchAsync(string gameId, CancellationToken ct = default) =>
            SendAsync<ManualGameLaunchResult>(HttpMethod.Post,
                "/api/v1/gaming/library/discover/gog/" + Uri.EscapeDataString(gameId) + "/launch", null, ct);

        public Task<GamingCallResult<List<ManualGameDto>>> ListManualGamesAsync(CancellationToken ct = default) =>
            SendAsync<List<ManualGameDto>>(HttpMethod.Get, "/api/v1/gaming/library", null, ct);

        public Task<GamingCallResult<ManualGameDto>> AddManualGameAsync(AddManualGameRequest request, CancellationToken ct = default) =>
            SendAsync<ManualGameDto>(HttpMethod.Post, "/api/v1/gaming/library", request, ct);

        public Task<GamingCallResult<ManualGameLaunchResult>> LaunchManualGameAsync(string id, CancellationToken ct = default) =>
            SendAsync<ManualGameLaunchResult>(HttpMethod.Post,
                "/api/v1/gaming/library/" + Uri.EscapeDataString(id) + "/launch", null, ct);

        public Task<GamingCallResult<object>> RemoveManualGameAsync(string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Delete, "/api/v1/gaming/library/" + Uri.EscapeDataString(id), null, ct);

        private static async Task<GamingCallResult<T>> SendAsync<T>(
            HttpMethod method, string path, object? body, CancellationToken ct, HttpClient? client = null)
        {
            try
            {
                ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                if (body != null)
                {
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                }

                using HttpResponseMessage response = await (client ?? HttpClient).SendAsync(request, ct).ConfigureAwait(false);
                return await GamingResponseReader.ReadAsync<T>(response, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GamingCallResult<T>.Fail(ex.Message);
            }
        }
    }
}
