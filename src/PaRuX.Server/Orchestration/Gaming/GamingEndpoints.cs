using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

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

        // An explicit, owner-confirmed Epic protocol activation. HTTP cannot
        // supply a URI or executable: the service must rediscover the installed
        // game and construct the official deep link itself.
        app.MapPost("/api/v1/gaming/library/discover/epic/{id}/launch", async (
            string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchEpicAsync(id, ct);
            return result.Launched ? Results.Ok(result) : Results.Conflict(result);
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

        // Confirmed GOG GALAXY handoff, not direct game execution. The Core
        // resolves the installed title and client itself; caller supplies ID only.
        app.MapPost("/api/v1/gaming/library/discover/gog/{id}/launch", async (
            string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchGogAsync(id, ct);
            return result.Launched ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/library", (ManualGameLibrary library) =>
        {
            try { return Results.Ok(library.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library", (
            AddManualGameRequest request, ManualGameLibrary library, GameTitleReadCache cache) =>
        {
            try
            {
                ManualGameDto added = library.Add(request);
                cache.Invalidate();
                return Results.Ok(added) as IResult;
            }
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

        app.MapDelete("/api/v1/gaming/library/{id}", (
            string id, ManualGameLibrary library, GameTitleReadCache cache) =>
        {
            try
            {
                if (!library.Remove(id)) return Results.NotFound() as IResult;
                cache.Invalidate();
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        // The owner must choose a local package. This never installs, executes,
        // extracts or downloads anything; active Gaming cancels hashing.
        app.MapPost("/api/v1/gaming/installers/inspect", async (
            GameInstallerInspectionRequest request,
            GameInstallerInspectionService inspector, CancellationToken ct) =>
        {
            try { return Results.Ok(await inspector.InspectAsync(request, ct)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Choose an existing local installer file." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Inspection unavailable while Gaming or recovering AI." }); }
            catch (InvalidDataException) { return Results.Conflict(new { error = "The package is empty, oversized or changed during inspection." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted installer inspection." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Results.Problem("The selected local file could not be read."); }
        }).RequireDesktopOwner();

        // Explicit owner-only, independently SHA-256 checked, copy-only staging.
        // This never downloads, unpacks, invokes Windows installers or executes code.
        app.MapPost("/api/v1/gaming/installers/stage", async (
            GamePackageStageRequest request, GamePackageStageService stage, CancellationToken ct) =>
        {
            try { return Results.Ok(await stage.StageAsync(request, ct)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "A regular local file and a 64-character publisher SHA-256 are required." }); }
            catch (InvalidDataException) { return Results.Conflict(new { error = "Package integrity, size or destination checks failed. No package was published." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Staging is unavailable while Gaming or recovering AI." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted package staging." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Results.Problem("The selected local package could not be staged safely."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/installers/staged", (GamePackageStageService staged) =>
        {
            try { return Results.Ok(staged.ListStaged()) as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The managed package catalog could not be inspected."); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/installers/staged/{id}", async (
            string id, GamePackageStageService staged, CancellationToken ct) =>
        {
            try { return await staged.DeleteStagedAsync(id, ct) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Leave Gaming before removing a managed package." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The managed package could not be safely removed."); }
        }).RequireDesktopOwner();

        // Read-only: proposes the main executable and a title for a folder the user chose; adds nothing.
        app.MapPost("/api/v1/gaming/library/inspect-folder", (GameFolderInspectRequest request, CancellationToken ct) =>
        {
            try { return Results.Ok(GameFolderInspector.Inspect(request.Folder, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/controllers", (ControllerProfileStore profiles) =>
            Results.Ok(NativeXInputProbe.ReadStates(profiles.Get()))).RequireDesktopOwner();

        // Optional companion of a virtual controller: reports only whether HidHide's command-line tool is present.
        app.MapGet("/api/v1/gaming/hidhide", (PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.HidHideManager hidHide) =>
            Results.Ok(new
            {
                available = hidHide.IsAvailable,
                recoveryRequired = hidHide.RecoveryRequired,
                message = hidHide.RecoveryRequired
                    ? "PaRuX has an unfinished controller isolation recovery. Leave Gaming and request a restore; do not use a virtual mapper until recovery succeeds."
                    : hidHide.IsAvailable
                        ? "HidHide is installed. No PaRuX recovery is pending."
                        : "HidHide is optional and not installed; PaRuX never installs drivers on its own.",
            })).RequireDesktopOwner();

        // An explicit, owner-only retry of journaled PaRuX-owned HidHide changes;
        // never unhide physical devices while a game may be consuming virtual input.
        app.MapPost("/api/v1/gaming/hidhide/recover", async (
            PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.HidHideManager hidHide,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before restoring physical-controller visibility." }) as IResult;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var recovered = await hidHide.RestoreAsync(deadline.Token);
                return recovered.Success ? Results.Ok(recovered) : Results.Conflict(recovered);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (OperationCanceledException)
            { return Results.Conflict(new { error = "Recovery timed out; its journal remains for retry." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return Results.Conflict(new { error = "Recovery failed; inspect HidHide manually before trying a virtual controller." }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/virtual-controller", () =>
            Results.Ok(VirtualControllerProbe.Probe())).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/controller-profile", (ControllerProfileStore profiles) =>
            Results.Ok(new { profile = profiles.Get(), loadError = profiles.LoadError })).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/controller-profile", (GamingControllerProfile profile, ControllerProfileStore profiles) =>
        {
            string? error = profiles.Save(profile);
            return error is null ? Results.Ok(profiles.Get()) : Results.BadRequest(new { error });
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/controller-profile", (ControllerProfileStore profiles) =>
        {
            profiles.Reset();
            return Results.NoContent();
        }).RequireDesktopOwner();

        // Explicit, user-started ~0.6 s sampling of resting sticks; refused while Gaming owns the machine.
        app.MapPost("/api/v1/gaming/controllers/{slot:int}/calibrate", async (
            int slot, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before calibrating a controller." });
            var result = await NativeXInputProbe.CalibrateAsync(slot, ct);
            return result is { } r
                ? Results.Ok(new { leftDeadzone = r.Left, rightDeadzone = r.Right })
                : Results.NotFound(new { error = "No readable XInput controller in that slot." });
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/displays", (GamingDisplayTopologyService displays) =>
            Results.Ok(displays.Snapshot())).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/displays/change", (GamingDisplayModeService modes) =>
            Results.Ok(modes.Status())).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/displays/modes", (string display, GamingDisplayModeService modes) =>
        {
            try { return Results.Ok(modes.Modes(display)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/displays/change", (GamingDisplayModeChangeRequest request, GamingDisplayModeService modes) =>
        {
            try { return Results.Ok(modes.Request(request)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/displays/change/confirm", (GamingDisplayModeService modes) =>
        {
            try { return Results.Ok(modes.Confirm()); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/displays/change/revert", (GamingDisplayModeService modes) =>
            Results.Ok(modes.Revert())).RequireDesktopOwner();

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
