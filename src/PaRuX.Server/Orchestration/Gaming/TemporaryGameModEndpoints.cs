using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Owner-only local UI/API boundary. Chat/AI code may suggest a proposal, but
/// cannot apply/restore it through a general network route or avoid owner consent.
/// The manager itself also revalidates the exact Quick Resume process identity.
/// </summary>
public static class TemporaryGameModEndpoints
{
    public static void MapTemporaryGameModEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/mods/temporary", (TemporaryGameModService mods) =>
        {
            try { return Results.Ok(mods.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or InvalidOperationException)
            { return Results.Problem("Temporary mod recovery data is unavailable."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/mods/temporary/previews", (TemporaryGameModService mods) =>
        {
            try { return Results.Ok(mods.ListPreviews()) as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Pending game mod proposals cannot be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/temporary/preview", (
            GameModProposalRequest request, TemporaryGameModService mods) =>
        {
            try { return Results.Ok(mods.Preview(request)) as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Temporary game config could not be safely inspected."); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(150_000));

        app.MapPost("/api/v1/gaming/mods/temporary/apply", (
            GameModApplyRequest request, TemporaryGameModService mods) =>
        {
            try { return Results.Ok(mods.Apply(request)) as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Temporary mod could not be applied. Its backup may require recovery."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/temporary/restore", (
            GameModRestoreRequest request, TemporaryGameModService mods) =>
        {
            try { return mods.Restore(request) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The game mod could not be restored safely. Recovery data was kept."); }
        }).RequireDesktopOwner();
    }
}
