using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

/// <summary>Lazily built per host so Program.cs needs only the single endpoint registration line.</summary>
internal sealed class GameDiscoverRuntime
{
    // Handler-injected IServiceProvider can be request scoped. Keying this cache by
    // that provider recreates the Steam client/rate limiter for every HTTP request.
    // Host lifetime is one stable key per Core, without sharing across test hosts.
    private static readonly ConditionalWeakTable<IHostApplicationLifetime, GameDiscoverRuntime> Instances = new();

    public GameDiscoverSettingsStore Settings { get; }
    public GameDiscoverGate Gate { get; }
    public SteamStoreCatalogProvider Steam { get; }
    public IReadOnlyList<IGameCatalogProvider> Providers { get; }
    public GameDiscoverService Service { get; }

    private GameDiscoverRuntime(LocalRuntimeService runtime)
    {
        Settings = new GameDiscoverSettingsStore(GameDiscoverSettingsStore.DefaultPath);
        Gate = new GameDiscoverGate(Settings, () => runtime.GamingMode);
        Steam = new SteamStoreCatalogProvider(SteamStoreCatalogProvider.CreateDefaultHttpClient(), Gate,
            Path.Combine(PaRuXDataRoot.Path, "cache", "game-discover"));
        Providers = new IGameCatalogProvider[] { Steam, new EpicCatalogProviderStub(), new GogCatalogProviderStub() };
        Service = new GameDiscoverService(Steam, Gate, Settings);
    }

    public static GameDiscoverRuntime For(IServiceProvider sp)
    {
        IHostApplicationLifetime lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
        return Instances.GetValue(lifetime, _ =>
            new GameDiscoverRuntime(sp.GetRequiredService<LocalRuntimeService>()));
    }
}

public static class GameDiscoverEndpoints
{
    public static void MapGameDiscoverEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/discover/settings", (IServiceProvider sp) =>
        {
            var rt = GameDiscoverRuntime.For(sp);
            return Results.Ok(new GameDiscoverSettingsDto(rt.Settings.Enabled, GameDiscoverSettingsStore.ConsentText, rt.Settings.Hidden().ToArray()));
        }).RequireDesktopOwner();

        // Enabling is the user's explicit consent to GameDiscoverSettingsStore.ConsentText.
        app.MapPut("/api/v1/gaming/discover/settings", (SetGameDiscoverSettingsRequest request, IServiceProvider sp) =>
        {
            var rt = GameDiscoverRuntime.For(sp);
            return rt.Settings.SetEnabled(request.Enabled)
                ? Results.Ok(new GameDiscoverSettingsDto(rt.Settings.Enabled, GameDiscoverSettingsStore.ConsentText, rt.Settings.Hidden().ToArray()))
                : Results.Problem("Discover settings could not be saved.");
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/discover/recommendations", async (IServiceProvider sp, GameTitleReadCache titles, CancellationToken ct) =>
        {
            var rt = GameDiscoverRuntime.For(sp);
            string? blocked = rt.Gate.Check();
            if (blocked is not null)
                return Results.Ok(new GameDiscoverResultDto(blocked, GameDiscoverGate.MessageFor(blocked), Array.Empty<GameRecommendationDto>()));
            if (!titles.TryRead(out var library))
                return Results.Conflict(new { error = "The game library has not been scanned yet. Open the library first." });
            try { return Results.Ok(await rt.Service.RecommendAsync(library, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/discover/featured", async (string? store, IServiceProvider sp, CancellationToken ct) =>
        {
            var provider = Pick(sp, store);
            if (provider is null) return Results.NotFound();
            var r = await provider.FeaturedAsync(ct);
            return Results.Ok(new GameCatalogListDto(r.Status, r.IsOk ? null : r.Message, r.Value ?? Array.Empty<GameCatalogEntryDto>()));
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/discover/search", async (string q, string? store, IServiceProvider sp, CancellationToken ct) =>
        {
            var provider = Pick(sp, store);
            if (provider is null) return Results.NotFound();
            var r = await provider.SearchAsync(q, ct);
            return Results.Ok(new GameCatalogListDto(r.Status, r.IsOk ? null : r.Message, r.Value ?? Array.Empty<GameCatalogEntryDto>()));
        }).RequireDesktopOwner();

        // URL strings only. The UI opens the store page; the UI launches the deep link after the user confirms.
        // The id is validated, so a crafted value cannot smuggle another URL or protocol.
        app.MapGet("/api/v1/gaming/discover/{store}/{id}/actions", (string store, string id) =>
        {
            var actions = GameDiscoverActions.For(store, id);
            return actions is null ? Results.NotFound() : Results.Ok(actions);
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/discover/hidden/{store}/{id}", (string store, string id, IServiceProvider sp) =>
        {
            if (GameDiscoverActions.For(store, id) is null) return Results.BadRequest();
            return GameDiscoverRuntime.For(sp).Settings.Hide(GameDiscoverRecommender.OwnedKey(store, id))
                ? Results.NoContent() : Results.Problem("The state could not be saved.");
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/discover/hidden/{store}/{id}", (string store, string id, IServiceProvider sp) =>
        {
            if (GameDiscoverActions.For(store, id) is null) return Results.BadRequest();
            return GameDiscoverRuntime.For(sp).Settings.Unhide(GameDiscoverRecommender.OwnedKey(store, id))
                ? Results.NoContent() : Results.Problem("The state could not be saved.");
        }).RequireDesktopOwner();
    }

    private static IGameCatalogProvider? Pick(IServiceProvider sp, string? store) =>
        GameDiscoverRuntime.For(sp).Providers.FirstOrDefault(p =>
            string.Equals(p.Id, string.IsNullOrWhiteSpace(store) ? "steam" : store, StringComparison.OrdinalIgnoreCase));
}
