using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

public static class GameDownloadEndpoints
{
    public static void MapGameDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/downloads", (GameDownloadManager downloads) => Results.Ok(downloads.List())).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/downloads", (GameDownloadRequest request, GameDownloadManager downloads) =>
        {
            try { return Results.Accepted(value: downloads.Enqueue(request)); }
            catch (GameDownloadException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        // Inspection is read-only and accepts an opaque download ID, never a caller-provided path.
        // It does not launch, unpack, trust or install the downloaded file.
        app.MapPost("/api/v1/gaming/downloads/{id}/inspect", async (
            string id, GameDownloadManager downloads,
            PaRuX.Server.Orchestration.Gaming.GameInstallerInspectionService inspector,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await downloads.InspectCompletedAsync(id, inspector, ct)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "This is not a supported local installer file." }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted inspection." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Results.Problem("The completed package could not be read."); }
        }).RequireDesktopOwner();

        // Copy-only handoff. The user must have supplied an independent checksum
        // when starting the download; Core resolves the exact published path itself.
        app.MapPost("/api/v1/gaming/downloads/{id}/stage", async (
            string id, GameDownloadManager downloads,
            PaRuX.Server.Orchestration.Gaming.GamePackageStageService staging,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await downloads.StageCompletedAsync(id, staging, ct)) as IResult; }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "A supported local package is required." }); }
            catch (InvalidDataException) { return Results.Conflict(new { error = "Package integrity or staging capacity checks failed." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted package staging." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Results.Problem("The completed package could not be staged."); }
        }).RequireDesktopOwner();

        // Retry after Gaming interruption or disk pressure without redownloading,
        // only for a verified completed ZIP still owned in this Core session.
        // Does not accept a path, archive URL, script, setup command or EULA waiver.
        app.MapPost("/api/v1/gaming/downloads/{id}/portable/retry", async (
            string id, GameDownloadManager downloads, CancellationToken ct) =>
        {
            try { return Results.Ok(await downloads.RetryPortableInstallAsync(id, ct)) as IResult; }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException)
            { return Results.Conflict(new { error = "Portable installation interrupted by Gaming." }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/downloads/{id}/pause", (string id, GameDownloadManager downloads) =>
            downloads.Pause(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/downloads/{id}/resume", (string id, GameDownloadManager downloads) =>
            downloads.Resume(id) ? Results.NoContent() : Results.Conflict(new { error = "The download cannot be resumed now." })).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/downloads/{id}", (string id, GameDownloadManager downloads) =>
            downloads.CancelOrRemove(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();

        // Capability report only: the bundled engine starts no peer activity until
        // the desktop owner explicitly adds a magnet or a local .torrent file.
        app.MapGet("/api/v1/gaming/downloads/torrent-engine", (
            ExternalTorrentClientTransport external, EmbeddedTorrentTransport embedded) =>
            Results.Ok(new GameTorrentEngineStatusDto(
                EmbeddedAvailable: !CorporateMode.IsActive && StoreEditionPolicy.AllowsTorrent,
                ExternalConfigured: external.IsEnabled,
                NewJobTransport: external.IsEnabled ? "external" : "embedded",
                SingleFileOnly: external.IsEnabled,
                NoAutomaticSeeding: true,
                NoCatalogue: true))).RequireDesktopOwner();

        // Optional external torrent client adapter (the user's own qBittorrent, loopback only). Off by default;
        // the credentials live in memory only and are never echoed back.
        app.MapGet("/api/v1/gaming/downloads/torrent-client", (ExternalTorrentClientTransport torrent) =>
            Results.Ok(new GameTorrentClientStatusDto(torrent.IsEnabled, torrent.Port))).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/downloads/torrent-client", async (
            GameTorrentClientConfigRequest request, ExternalTorrentClientTransport torrent, CancellationToken ct) =>
        {
            try
            {
                await torrent.ConfigureAsync(request.Port, request.Username, request.Password, ct);
                return Results.Ok(new GameTorrentClientStatusDto(torrent.IsEnabled, torrent.Port));
            }
            catch (GameDownloadException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/downloads/torrent-client", (ExternalTorrentClientTransport torrent) =>
        {
            torrent.Disable();
            return Results.NoContent();
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/downloads/limit", (GameDownloadLimitRequest request, GameDownloadManager downloads) =>
        {
            downloads.SetBandwidthLimit(request.BytesPerSecond);
            return Results.NoContent();
        }).RequireDesktopOwner();
    }
}
