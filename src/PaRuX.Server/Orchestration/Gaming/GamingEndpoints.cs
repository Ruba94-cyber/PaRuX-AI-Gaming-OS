using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public static class GamingEndpoints
{
    public static void MapGamingEndpoints(this WebApplication app)
    {
        // Enumerate local Steam manifests only: no installer, launcher or untrusted command execution.
        app.MapGet("/api/v1/gaming/library/discover/steam", async (SteamGameDiscoveryProvider provider, CancellationToken ct) =>
        {
            try { return Results.Ok(await provider.DiscoverAsync(ct)) as IResult; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Steam installation metadata could not be read."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/library/discover/epic", async (EpicGameDiscoveryProvider provider, CancellationToken ct) =>
        {
            try { return Results.Ok(await provider.DiscoverAsync(ct)) as IResult; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Epic installation metadata could not be read."); }
        }).RequireDesktopOwner();

        // Explicitly authorized handoff to the official Steam client. No command
        // arguments or executable paths are accepted from the HTTP caller.
        app.MapPost("/api/v1/gaming/library/discover/steam/{id}/launch", async (
            string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchSteamAsync(id, ct);
            return result.Launched ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/library/discover/gog", async (GogGameDiscoveryProvider provider, CancellationToken ct) =>
        {
            try { return Results.Ok(await provider.DiscoverAsync(ct)) as IResult; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
            { return Results.Problem("GOG installation metadata could not be read."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/library", (ManualGameLibrary library) =>
        {
            try { return Results.Ok(library.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library", (AddManualGameRequest request, ManualGameLibrary library) =>
        {
            try { return Results.Ok(library.Add(request)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid game name or executable path." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Game already exists or the library is full." }); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library/{id}/launch", async (
            string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchAsync(id, ct);
            return result.Launched ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/library/{id}", (string id, ManualGameLibrary library) =>
        {
            try { return library.Remove(id) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/status", (WorkloadOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetStatus()));

        app.MapPost("/api/v1/gaming/enter", async (GamingActionRequest request, WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.EnterGamingSessionAsync(request.ProfileName, ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/exit", async (
            WorkloadOrchestrator orchestrator, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            bool success = await sessions.ExitGamingAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/suspend", async (WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.SuspendActiveGameAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/resume", async (WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.ResumeSuspendedGameAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/profiles", (GamingProfileStore profiles) =>
            profiles.LoadError is { } error ? Results.Problem(error, statusCode: 409) : Results.Ok(profiles.List()));

        app.MapPost("/api/v1/gaming/profiles", (GamingProfileDto profile, GamingProfileStore profiles) =>
        {
            if (profiles.LoadError is { } error) return Results.Problem(error, statusCode: 409);
            try { return Results.Ok(profiles.Save(profile)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid Gaming profile fields." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Gaming profile name or capacity conflict." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The Gaming profile could not be saved safely.", statusCode: 500); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/profiles/{id}", (string id, GamingProfileStore profiles) =>
        {
            if (profiles.LoadError is { } error) return Results.Problem(error, statusCode: 409);
            try { return profiles.Delete(id) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The Gaming profile could not be deleted safely.", statusCode: 500); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/providers", (WorkloadOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetProviderStatuses()));
    }
}
