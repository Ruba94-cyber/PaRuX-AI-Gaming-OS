using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public static class GameTitleEndpoints
{
    /// <summary>A title key is what <see cref="GameTitleCatalog.KeyFor"/> produces: letters and digits, or "id:" plus an installation id.</summary>
    internal static bool ValidTitleKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 300 &&
        (key.StartsWith("id:", StringComparison.Ordinal)
            ? key.Length > 3 && key[3..].All(c => char.IsLetterOrDigit(c) || c is ':' or '_' or '-' or '.')
            : key.All(char.IsLetterOrDigit));

    /// <summary>Read back the persisted, validated state after an explicit owner edit.
    /// Keep cache changes best-effort: a successful persisted mutation must not be
    /// misreported as failed because a separate cache refresh encountered I/O.</summary>
    private static void UpdateCachedState(string key, GameTitleStore store, GameTitleReadCache cache)
    {
        try
        {
            if (store.Snapshot().TryGetValue(key, out GameTitleState? state))
                cache.ApplyState(key, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    public static void MapGameTitleEndpoints(this WebApplication app)
    {
        // Merged read-only view over local discovery and the manual library, plus the user's per-title state.
        app.MapGet("/api/v1/gaming/titles", async (
            string? q, string? source, bool? favorite, string? tag, string? collection, bool? refresh,
            string? sort, string? platform, bool? installed, string? hidden,
            SteamGameDiscoveryProvider steam, EpicGameDiscoveryProvider epic, GogGameDiscoveryProvider gog,
            ManualGameLibrary library, GameTitleStore store, GameTitleReadCache cache,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            // Reject unknown sort/hidden values instead of silently ignoring a typo.
            if (!GameTitleSorts.IsValid(sort) || GameTitleCatalog.HiddenModes.Parse(hidden) is null ||
                (platform?.Length ?? 0) > 80 || (source?.Length ?? 0) > 40 || (tag?.Length ?? 0) > 80 ||
                (collection?.Length ?? 0) > 80 || (q?.Length ?? 0) > 200)
                return Results.BadRequest(new { error = "Invalid sort, hidden or filter value." });
            var query = new GameTitleQuery(q, source, favorite == true, tag, collection)
            { Platform = platform, Installed = installed, Hidden = hidden, Sort = sort };

            IResult FromCache()
            {
                // A cold Core that rebooted during Gaming has no valid snapshot;
                // a conflict is honest, an empty catalogue would hide user games.
                if (!cache.TryRead(out var snapshot))
                    return Results.Conflict(new { error = "The game catalogue has not been scanned yet. Return to AI before refreshing it." });
                return Results.Ok(GameTitleCatalog.Query(snapshot, query));
            }

            // Gamepad search while playing must not wake disk-heavy store discovery.
            if (runtime.GamingMode) return FromCache();
            if (refresh != true && cache.TryReadRecent(out var recent))
                return Results.Ok(GameTitleCatalog.Query(recent, query));
            long scanRevision = cache.Revision;
            using var hardOff = new GamingHardOffWatch(() => runtime.GamingMode, ct);
            try
            {
                CancellationToken scanToken = hardOff.Token;
                var discovered = new List<DiscoveredGameDto>();
                foreach (var provider in new Func<Task<IReadOnlyList<DiscoveredGameDto>>>[]
                         { () => steam.DiscoverAsync(scanToken), () => epic.DiscoverAsync(scanToken), () => gog.DiscoverAsync(scanToken) })
                {
                    if (runtime.GamingMode) return FromCache();
                    scanToken.ThrowIfCancellationRequested();
                    try { discovered.AddRange(await provider()); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { /* One store failing must not hide other providers. */ }
                }
                if (runtime.GamingMode) return FromCache();
                scanToken.ThrowIfCancellationRequested();
                var titles = GameTitleCatalog.Build(discovered, library.List(), store.Snapshot());
                if (runtime.GamingMode) return FromCache();
                scanToken.ThrowIfCancellationRequested();
                if (cache.PublishIfUnchanged(scanRevision, titles))
                    return Results.Ok(GameTitleCatalog.Query(titles, query));
                // A game import or owner edit raced with this scan. A newer,
                // confirmed fresh catalogue is safe to serve; an invalidated old
                // snapshot is NOT a successful refresh of newly imported games.
                if (cache.TryReadRecent(out var current))
                    return Results.Ok(GameTitleCatalog.Query(current, query));
                return Results.Conflict(new { error = "The game catalogue changed during discovery. Refresh it again." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) when (runtime.GamingMode) { return FromCache(); }
            catch (InvalidDataException) { return Results.Problem("The game library could not be read."); }
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/hidden", (
            string key, bool hidden, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (!ValidTitleKey(key)) return Results.BadRequest() as IResult;
            if (!store.SetHidden(key, hidden)) return Results.Problem("The state could not be saved.");
            UpdateCachedState(key, store, cache);
            return Results.NoContent();
        }).RequireDesktopOwner();

        // One atomic edit of up to 100 titles: all keys and values are validated first, one write persists them.
        app.MapPut("/api/v1/gaming/titles/bulk", (
            BulkGameTitleRequest request, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (request?.TitleKeys is null || request.TitleKeys.Count is 0 or > BulkGameTitleRequest.MaxKeys ||
                request.TitleKeys.Any(k => !ValidTitleKey(k)))
                return Results.BadRequest(new { error = "Provide 1 to 100 valid title keys." }) as IResult;
            if (request.Favorite is null && request.Hidden is null && request.Tags is null && request.Collections is null)
                return Results.BadRequest(new { error = "Nothing to change." });
            int? updated = store.BulkUpdate(request.TitleKeys, request.Favorite, request.Hidden, request.Tags, request.Collections);
            if (updated is null)
                return Results.BadRequest(new { error = "Invalid tags or collections (tags: at most 20, collections: at most 12, each up to 40 characters, no control characters) or the state could not be saved." });
            try
            {
                var snapshot = store.Snapshot();
                foreach (string key in request.TitleKeys.Distinct(StringComparer.Ordinal))
                    if (snapshot.TryGetValue(key, out GameTitleState? state)) cache.ApplyState(key, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return Results.Ok(new BulkGameTitleResult(updated.Value));
        }).RequireDesktopOwner();

        // Edit a saved manual entry with the same validation as adding one. Never touches game files.
        app.MapPut("/api/v1/gaming/library/{id}", (
            string id, UpdateManualGameRequest request, ManualGameLibrary library, GameTitleReadCache cache) =>
        {
            if (request is null) return Results.BadRequest() as IResult;
            try
            {
                ManualGameDto? updated = library.Update(id, request);
                if (updated is null) return Results.NotFound();
                cache.Invalidate();
                return Results.Ok(updated);
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid game name, executable path or options." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "This executable and launch configuration are already in the library." }); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/favorite", (
            string key, bool favorite, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (!ValidTitleKey(key)) return Results.BadRequest() as IResult;
            if (!store.SetFavorite(key, favorite)) return Results.Problem("The state could not be saved.");
            UpdateCachedState(key, store, cache);
            return Results.NoContent();
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/tags", (
            string key, SetGameTagsRequest request, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (!ValidTitleKey(key)) return Results.BadRequest() as IResult;
            if (!store.SetTags(key, request.Tags))
                return Results.BadRequest(new { error = "Tags: at most 20, each up to 40 characters, no control characters." });
            UpdateCachedState(key, store, cache);
            return Results.NoContent();
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/collections", (
            string key, SetGameCollectionsRequest request, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (!ValidTitleKey(key)) return Results.BadRequest() as IResult;
            if (!store.SetCollections(key, request.Collections))
                return Results.BadRequest(new { error = "Collections: at most 12, each up to 40 characters, no control characters." });
            UpdateCachedState(key, store, cache);
            return Results.NoContent();
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/default-installation", (
            string key, SetDefaultInstallationRequest request, GameTitleStore store, GameTitleReadCache cache) =>
        {
            if (!ValidTitleKey(key)) return Results.BadRequest() as IResult;
            if (!store.SetDefaultInstallation(key, request.InstallationId)) return Results.BadRequest();
            UpdateCachedState(key, store, cache);
            return Results.NoContent();
        }).RequireDesktopOwner();
    }
}
