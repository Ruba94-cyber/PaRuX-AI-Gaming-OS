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

        app.MapPost("/api/v1/gaming/downloads/{id}/pause", (string id, GameDownloadManager downloads) =>
            downloads.Pause(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/downloads/{id}/resume", (string id, GameDownloadManager downloads) =>
            downloads.Resume(id) ? Results.NoContent() : Results.Conflict(new { error = "The download cannot be resumed now." })).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/downloads/{id}", (string id, GameDownloadManager downloads) =>
            downloads.CancelOrRemove(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/downloads/limit", (GameDownloadLimitRequest request, GameDownloadManager downloads) =>
        {
            downloads.SetBandwidthLimit(request.BytesPerSecond);
            return Results.NoContent();
        }).RequireDesktopOwner();
    }
}
