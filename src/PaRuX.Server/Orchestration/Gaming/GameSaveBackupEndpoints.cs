using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Authenticated local-only, explicitly requested save backup operations.</summary>
public static class GameSaveBackupEndpoints
{
    public static void MapGameSaveBackupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/save-backups", (GameSaveBackupService backup) =>
        {
            try { return Results.Ok(backup.List()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Save backup inventory is unavailable."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/save-backups", async (
            GameSaveBackupRequest request, GameSaveBackupService backup, CancellationToken ct) =>
        {
            try { return Results.Ok(await backup.CreateAsync(request, ct)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return Results.Conflict(new { error = "Gaming interrupted the save backup." }); }
            catch (OperationCanceledException)
            { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex)
            { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Problem("The backup could not be written."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/save-backups/{id}/verify", async (
            string id, GameSaveBackupService backup, CancellationToken ct) =>
        {
            try { return Results.Ok(await backup.VerifyAsync(id, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The backup verification could not be completed."); }
        }).RequireDesktopOwner();

        // Dry run: verifies the backup and lists what a restore would create/overwrite. Writes nothing.
        app.MapPost("/api/v1/gaming/save-backups/{id}/restore-plan", async (
            string id, GameSaveRestoreRequest request, GameSaveBackupService backup, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest();
            try { return Results.Ok(await backup.PlanRestoreAsync(id, request.TargetFolder, ct)); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The restore preview could not be completed."); }
        }).RequireDesktopOwner();

        // Confirmed restore: automatic pre-restore backup, SHA-256 verified staging, post-restore verification.
        app.MapPost("/api/v1/gaming/save-backups/{id}/restore", async (
            string id, GameSaveRestoreRequest request, GameSaveBackupService backup, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest();
            try { return Results.Ok(await backup.RestoreAsync(id, request, ct)); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The restore could not be completed."); }
        }).RequireDesktopOwner();
    }
}
