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
            SteamGameDiscoveryProvider steam, EpicGameDiscoveryProvider epic, GogGameDiscoveryProvider gog,
            ManualGameLibrary library, GameTitleStore store, GameTitleReadCache cache,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            IResult FromCache()
            {
                // A cold Core that rebooted during Gaming has no valid snapshot;
                // a conflict is honest, an empty catalogue would hide user games.
                if (!cache.TryRead(out var snapshot))
                    return Results.Conflict(new { error = "The game catalogue has not been scanned yet. Return to AI before refreshing it." });
                return Results.Ok(GameTitleCatalog.Filter(snapshot, q, source, favorite == true, tag, collection));
            }

            // Gamepad search while playing must not wake disk-heavy store discovery.
            if (runtime.GamingMode) return FromCache();
            if (refresh != true && cache.TryReadRecent(out var recent))
                return Results.Ok(GameTitleCatalog.Filter(recent, q, source, favorite == true, tag, collection));
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
                    return Results.Ok(GameTitleCatalog.Filter(titles, q, source, favorite == true, tag, collection));
                // A game import or owner edit raced with this scan. A newer,
                // confirmed fresh catalogue is safe to serve; an invalidated old
                // snapshot is NOT a successful refresh of newly imported games.
                if (cache.TryReadRecent(out var current))
                    return Results.Ok(GameTitleCatalog.Filter(current, q, source, favorite == true, tag, collection));
                return Results.Conflict(new { error = "The game catalogue changed during discovery. Refresh it again." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) when (runtime.GamingMode) { return FromCache(); }
            catch (InvalidDataException) { return Results.Problem("The game library could not be read."); }
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
