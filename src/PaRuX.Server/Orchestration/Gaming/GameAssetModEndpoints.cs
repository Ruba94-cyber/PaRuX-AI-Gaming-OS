using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Local desktop-owner boundary for cross-game AI asset mod workspaces.</summary>
public static class GameAssetModEndpoints
{
    public static void MapGameAssetModEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/mods/adapters",
            () => Results.Ok(GameAssetModService.Adapters()) as IResult)
            .RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/mods/assets/drafts",
            (GameAssetModService service) => Results.Ok(service.ListDrafts()) as IResult)
            .RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/assets/generate", async (
            GameAssetModDraftRequest request, GameAssetModService service,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, runtime.GamingCancellationToken);
                return Results.Ok(await service.GenerateDraftAsync(request, linked.Token)) as IResult;
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "AI texture generation was interrupted by Gaming Mode or cancellation." }); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                UnauthorizedAccessException or TimeoutException or PlatformNotSupportedException)
            { return Results.Problem("Could not generate a safe local game texture draft. No game files were changed."); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(3_000_000));

        app.MapPost("/api/v1/gaming/mods/assets/export", (
            GameAssetModExportRequest request, GameAssetModService service) =>
        {
            try { return Results.Ok(service.Export(request)) as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or System.Security.SecurityException)
            { return Results.Problem("Could not safely create the asset ZIP; no user-managed game files were changed."); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(16384));
    }
}
