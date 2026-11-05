using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Server.Knowledge;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

public static class GamingEndpoints
{
    /// <summary>Admission, cancellation and truthful read outcomes for all
    /// manifest/folder discovery that must not compete with active Gaming.</summary>
    internal static async Task<IResult> GuardedDiscoveryAsync<T>(
        Func<CancellationToken, Task<T>> discover, Func<bool> gamingActive,
        CancellationToken caller, string operation)
    {
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(gamingActive);
        if (gamingActive())
            return Results.Conflict(new { error = operation + " discovery is disabled while Gaming." });
        using var hardOff = new GamingHardOffWatch(gamingActive, caller);
        try
        {
            T result = await discover(hardOff.Token).ConfigureAwait(false);
            if (hardOff.Triggered || gamingActive())
                return Results.Conflict(new { error = operation + " discovery stopped when Gaming began." });
            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        { return Results.StatusCode(499); }
        catch (OperationCanceledException) when (hardOff.Triggered || gamingActive())
        { return Results.Conflict(new { error = operation + " discovery stopped when Gaming began." }); }
        catch (OperationCanceledException)
        { return Results.Conflict(new { error = operation + " discovery was interrupted." }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Security.SecurityException)
        { return Results.Problem(operation + " metadata could not be read."); }
    }

    public static void MapGamingEndpoints(this WebApplication app)
    {
        // Read-only preview of user-supplied JSON. This cannot inspect local files,
        // launch games or add entries to ManualGameLibrary. Desktop-only, Gaming hard-off.
        app.MapPost("/api/v1/gaming/library/import-preview", (
            ExternalLibraryPreviewRequest request, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "External library preview is disabled while Gaming." });
            try
            {
                IReadOnlyList<ExternalLibraryGame> preview =
                    ExternalGameLibraryExportParser.Parse(request.Provider ?? string.Empty, request.Json ?? string.Empty);
                return Results.Ok(preview.Select(game =>
                    new ExternalLibraryGamePreviewDto(game.Provider, game.ExternalId, game.Title)).ToArray());
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                // Never echo user-supplied JSON, filenames, tokens or launch arguments.
                return Results.BadRequest(new { error = "Unsupported or invalid external library JSON export." });
            }
        }).RequireDesktopOwner()
          // Encoded JSON requires some escaping headroom beyond the 512k-character
          // inner export cap; still refuse multi-megabyte request bodies at HTTP entry.
          .WithMetadata(new RequestSizeLimitAttribute(3_200_000));

        // Saved title-only external library snapshots are local and do not
        // grant launch, install, ownership or access to third-party accounts.
        app.MapGet("/api/v1/gaming/library/imported", (
            ExternalGameLibraryStore imported, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "External library management is disabled while Gaming." }) as IResult;
            try { return Results.Ok(imported.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Saved library metadata cannot be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library/import", (
            ExternalLibraryImportRequest request, ExternalGameLibraryStore imported,
            GameTitleReadCache cache, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "External library import is disabled while Gaming." }) as IResult;
            if (request is null || !request.Consent)
                return Results.BadRequest(new { error = "Importing requires a separate, explicit owner confirmation." });
            try
            {
                IReadOnlyList<ExternalLibraryGame> candidates =
                    ExternalGameLibraryExportParser.Parse(request.Provider ?? "", request.Json ?? "");
                // No external paths or launch parameters enter durable storage.
                var result = imported.Import(candidates);
                cache.Invalidate();
                return Results.Ok(result) as IResult;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            { return Results.BadRequest(new { error = "Unsupported, empty or invalid external library export." }); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "The saved external library has reached its limit." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Problem("Saved library metadata could not be written."); }
        }).RequireDesktopOwner()
          .WithMetadata(new RequestSizeLimitAttribute(3_200_000));

        app.MapDelete("/api/v1/gaming/library/imported/{provider}/{externalId}", (
            string provider, string externalId, ExternalGameLibraryStore imported,
            GameTitleReadCache cache, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "External library management is disabled while Gaming." }) as IResult;
            try
            {
                if (!imported.Remove(provider, externalId)) return Results.NotFound();
                cache.Invalidate();
                return Results.NoContent();
            }
            catch (ArgumentException)
            { return Results.BadRequest(new { error = "Invalid external library identifier." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Saved library metadata could not be changed."); }
        }).RequireDesktopOwner();

        // L8: a read-only, owner-prepared Chat handoff for a MANUALLY
        // registered game and explicitly selected indexed local sources.
        // Preparing never runs inference, changes Chat scope or wakes the GPU.
        app.MapPost("/api/v1/gaming/assistant/prepare", (
            LocalGameGuideRequest request, ManualGameLibrary library,
            KnowledgeJobService knowledge, WorkloadOrchestrator orchestrator,
            LocalRuntimeService runtime) =>
        {
            // Validate the explicit request before any expensive runtime/index read.
            if (request is null || !request.Consent || request.SourceIds is null
                || request.SourceIds.Count is < 1 or > 64
                || string.IsNullOrWhiteSpace(request.ManualGameId)
                || request.ManualGameId.Length > 100)
                return Results.BadRequest(new { error = "Explicit consent, a saved game and 1 to 64 selected sources are required." }) as IResult;
            if (string.IsNullOrWhiteSpace(request.Question) ||
                request.Question.Trim().Length > 3000 ||
                 request.Question.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
                return Results.BadRequest(new { error = "Enter a valid game question." });
            if (runtime.GamingMode || orchestrator.GetStatus() is not
                { IsGamingActive: false, State: "AiReady" })
                return Results.Conflict(new { error = "Exit Gaming and restore AI before preparing guide Chat." });

            try
            {
                ManualGameDto? game = library.List().FirstOrDefault(item =>
                    item.Id == request.ManualGameId &&
                    string.Equals(item.Kind, ManualEntryKinds.Game, StringComparison.OrdinalIgnoreCase));
                if (game is null) return Results.NotFound(new { error = "Selected local game not found." });

                // Every ID must exist, still be enabled AND have indexed content.
                // Never use ScopedSourceIds(empty): the general Chat contract
                // interprets an empty scope as ALL enabled document folders.
                var enabled = new HashSet<string>(knowledge.EnabledSourceIds(), StringComparer.Ordinal);
                var verified = new HashSet<string>(StringComparer.Ordinal);
                foreach (string? sourceId in request.SourceIds)
                {
                    if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 200
                        || sourceId.Any(char.IsControl) || !enabled.Contains(sourceId))
                        return Results.BadRequest(new { error = "A selected local document source is not enabled." });
                    if (!verified.Add(sourceId)) continue;
                    if (knowledge.Index.Stats(sourceId).chunks <= 0)
                        return Results.Conflict(new { error = "A selected guide has no indexed content." });
                }

                LocalGameChatPlan prepared = LocalGameChatPolicy.Build(
                    game.Name, request.Question, true, request.SourceIds, verified);
                if (!prepared.Ready || prepared.Documents is null)
                    return Results.BadRequest(new { error = "The game question or selected document scope is invalid." });
                if (runtime.GamingMode || orchestrator.GetStatus() is not
                    { IsGamingActive: false, State: "AiReady" })
                    return Results.Conflict(new { error = "Gaming started while preparing the guide." });
                return Results.Ok(new LocalGameGuidePreparationDto(
                    prepared.QuestionForChat, prepared.Documents, prepared.Explanation));
            }
            catch (InvalidDataException)
            { return Results.BadRequest(new { error = "The selected game, guide scope or question is invalid." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            { return Results.Problem("Local game or indexed-guide metadata could not be verified."); }
        }).RequireDesktopOwner();

        // Enumerate local Steam manifests only: no installer, launcher or untrusted command execution.
        app.MapGet("/api/v1/gaming/library/discover/steam", (
            SteamGameDiscoveryProvider provider, LocalRuntimeService runtime, CancellationToken ct) =>
            GuardedDiscoveryAsync(provider.DiscoverAsync, () => runtime.GamingMode, ct, "STEAM store"))
            .RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/library/discover/epic", (
            EpicGameDiscoveryProvider provider, LocalRuntimeService runtime, CancellationToken ct) =>
            GuardedDiscoveryAsync(provider.DiscoverAsync, () => runtime.GamingMode, ct, "EPIC store"))
            .RequireDesktopOwner();

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

        app.MapGet("/api/v1/gaming/library/discover/gog", (
            GogGameDiscoveryProvider provider, LocalRuntimeService runtime, CancellationToken ct) =>
            GuardedDiscoveryAsync(provider.DiscoverAsync, () => runtime.GamingMode, ct, "GOG store"))
            .RequireDesktopOwner();

        // Confirmed GOG GALAXY handoff, not direct game execution. The Core
        // resolves the installed title and client itself; caller supplies ID only.
        app.MapPost("/api/v1/gaming/library/discover/gog/{id}/launch", async (
            string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchGogAsync(id, ct);
            return result.Launched ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        // Off-by-default per-game pre/post-launch helper programs (see GameLaunchHookStore).
        app.MapGet("/api/v1/gaming/hooks", (GameLaunchHookStore hooks) => Results.Ok(hooks.Snapshot())).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/hooks/enabled", (bool enabled, GameLaunchHookStore hooks) =>
            hooks.SetEnabled(enabled) ? Results.NoContent() : Results.Problem("The hook switch could not be saved."))
            .RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/hooks", (
            string key, GameHooksDto request, GameLaunchHookStore hooks, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Hooks cannot be changed during Gaming." });
            string? error = hooks.Set(key, request);
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        }).RequireDesktopOwner();

        // EA app / Battle.net / Ubisoft Connect / Xbox: read-only local discovery and official-URI handoff.
        app.MapGet("/api/v1/gaming/library/discover/store/{provider}", (
            string provider, ExtraStoreCatalog catalog, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            IExtraStoreProvider? found = catalog.Find(provider);
            if (found is null) return Task.FromResult<IResult>(Results.NotFound());
            return GuardedDiscoveryAsync(found.DiscoverAsync, () => runtime.GamingMode, ct, found.Id + " store");
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library/discover/store/{provider}/{id}/launch", async (
            string provider, string id, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            ManualGameLaunchResult result = await sessions.LaunchExtraStoreAsync(provider, id, ct);
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
            string id, string? displayId, ManualGameSessionService sessions, CancellationToken ct) =>
        {
            // The display id is an optional local-owner hint. The service checks
            // current Windows topology before entering Gaming and never accepts
            // executable paths or arbitrary window handles from the caller.
            ManualGameLaunchResult result = await sessions.LaunchAsync(id, ct, displayId);
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
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted staged-package deletion." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("The managed package could not be safely removed."); }
        }).RequireDesktopOwner();

        // Read-only: proposes the main executable and a title for a folder the user chose; adds nothing.
        app.MapPost("/api/v1/gaming/library/inspect-folder", (
            GameFolderInspectRequest request, LocalRuntimeService runtime, CancellationToken ct) =>
            GuardedDiscoveryAsync(token =>
                Task.FromResult(GameFolderInspector.Inspect(request.Folder, token)),
                () => runtime.GamingMode, ct, "Local game folder"))
            .RequireDesktopOwner();

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

        // Double-input prevention: hides ONE pad after the Core itself proves that the Windows controller (vendor/product,
        // exactly one connected) maps to exactly one HID instance. Explicit consent is mandatory; fail closed otherwise.
        app.MapPost("/api/v1/gaming/hidhide/isolate", async (
            PaRuX.Contracts.VirtualPadIsolateRequest request,
            PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.HidHideManager hidHide,
            CancellationToken ct) =>
        {
            if (request is null || !request.Consent)
                return Results.BadRequest(new PaRuX.Contracts.VirtualPadIsolateResult(
                    false, "Hiding the physical controller needs your explicit consent.", null, false)) as IResult;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var outcome = await hidHide.HideVerifiedAsync(request.VendorId, request.ProductId,
                    request.SameIdentityControllers, request.Consent, request.AppExecutable ?? string.Empty, deadline.Token);
                var dto = new PaRuX.Contracts.VirtualPadIsolateResult(
                    outcome.Success, outcome.Message, outcome.InstancePath, outcome.BindingProven);
                return outcome.Success ? Results.Ok(dto) : Results.Conflict(dto);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (OperationCanceledException)
            {
                return Results.Conflict(new PaRuX.Contracts.VirtualPadIsolateResult(
                    false, "HidHide timed out; PaRuX undid its changes or kept a recovery journal.", null, false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Results.Conflict(new PaRuX.Contracts.VirtualPadIsolateResult(
                    false, "HidHide could not be used; check recovery before retrying.", null, false));
            }
        }).RequireDesktopOwner();

        // Live isolation check used as the forwarding safety gate (cloak on and the pad hidden).
        app.MapGet("/api/v1/gaming/hidhide/isolation", async (
            string path, PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.HidHideManager hidHide, CancellationToken ct) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { return Results.Ok(new PaRuX.Contracts.VirtualPadIsolationState(await hidHide.IsIsolatedAsync(path, deadline.Token))); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            { return Results.Ok(new PaRuX.Contracts.VirtualPadIsolationState(false)); }
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
