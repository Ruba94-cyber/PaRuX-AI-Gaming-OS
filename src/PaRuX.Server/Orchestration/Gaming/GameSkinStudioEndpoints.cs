using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Desktop-owner only. Local image generation is never callable from an
/// arbitrary game agent or the LAN; applying a draft requires separate consent.
/// </summary>
public static class GameSkinStudioEndpoints
{
    public static void MapGameSkinStudioEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/gaming/mods/skin-studio/generate", async (
            GameSkinDraftRequest request, GameSkinStudioService studio,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, runtime.GamingCancellationToken);
                GameSkinDraftDto draft = await studio.GenerateDraftAsync(request, linked.Token);
                return Results.Ok(draft) as IResult;
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or PlatformNotSupportedException or TimeoutException)
            { return Results.Problem("Local skin generation is unavailable. No game files were changed."); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(16_384));

        app.MapPost("/api/v1/gaming/mods/skin-studio/restyle", async (
            GameSkinRestyleRequest request, GameSkinStudioService studio,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, runtime.GamingCancellationToken);
                return Results.Ok(await studio.RestyleDraftAsync(request, linked.Token)) as IResult;
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted the AI skin restyle." }); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                UnauthorizedAccessException or PlatformNotSupportedException or TimeoutException)
            { return Results.Problem("Could not safely restyle this Minecraft skin; no original files were changed."); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(3_000_000));

        app.MapGet("/api/v1/gaming/mods/skin-studio/drafts",
            (GameSkinStudioService studio) => Results.Ok(studio.ListDrafts()))
            .RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/skin-studio/export", (
            GameSkinExportRequest request, GameSkinStudioService studio) =>
        {
            try { return Results.Ok(studio.Export(request)) as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or System.Security.SecurityException)
            { return Results.Problem("Could not safely export the PNG; no game files were touched."); }
        }).RequireDesktopOwner();
    }
}
