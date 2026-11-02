using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

public sealed record CatalogResult<T>(string Status, string? Message, T? Value)
{
    public bool IsOk => Status == GameDiscoverStatus.Ok;
    public static CatalogResult<T> Ok(T value) => new(GameDiscoverStatus.Ok, null, value);
    public static CatalogResult<T> Fail(string status, string? message = null) =>
        new(status, message ?? GameDiscoverGate.MessageFor(status), default);
}

/// <summary>
/// A read-only view of one store's OFFICIAL public catalogue. Implementations must not scrape, use unofficial
/// APIs, need an account or API key, or send anything beyond a store id (or an explicit user search term).
/// </summary>
public interface IGameCatalogProvider
{
    /// <summary>Lowercase store id: "steam", "epic", "gog".</summary>
    string Id { get; }
    string DisplayName { get; }
    Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> FeaturedAsync(CancellationToken ct);
    Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> SearchAsync(string query, CancellationToken ct);
    Task<CatalogResult<GameCatalogEntryDto>> DetailAsync(string storeId, CancellationToken ct);
}

/// <summary>
/// Epic Games Store has no public, key-less catalogue API; the only routes are scraping or unofficial endpoints,
/// both of which PaRuX refuses. Stub until Epic publishes an official one. Never touches the network.
/// </summary>
public sealed class EpicCatalogProviderStub : NotAvailableCatalogProvider
{
    public override string Id => "epic";
    public override string DisplayName => "Epic Games Store";
}

/// <summary>GOG has no official public catalogue API for third parties (Galaxy APIs need client credentials). Stub; no network.</summary>
public sealed class GogCatalogProviderStub : NotAvailableCatalogProvider
{
    public override string Id => "gog";
    public override string DisplayName => "GOG";
}

public abstract class NotAvailableCatalogProvider : IGameCatalogProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> FeaturedAsync(CancellationToken ct) =>
        Task.FromResult(CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(GameDiscoverStatus.NotAvailable));
    public Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult(CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(GameDiscoverStatus.NotAvailable));
    public Task<CatalogResult<GameCatalogEntryDto>> DetailAsync(string storeId, CancellationToken ct) =>
        Task.FromResult(CatalogResult<GameCatalogEntryDto>.Fail(GameDiscoverStatus.NotAvailable));
}
