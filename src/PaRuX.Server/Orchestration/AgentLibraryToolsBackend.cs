using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Downloads;

namespace PaRuX.Server.Orchestration;

/// <summary>Production adapter: delegates to the same stores and managers the Gaming endpoints use.
/// It performs no discovery scan of its own (no disk-heavy work from a chat turn): titles come from
/// the shared read cache, or from the saved library plus per-title state when no scan is cached.</summary>
public sealed class AgentLibraryBackend : IAgentLibraryBackend
{
    private readonly GameTitleStore _store;
    private readonly ManualGameLibrary _library;
    private readonly GameTitleReadCache _cache;
    private readonly ManualGameSessionService _sessions;
    private readonly GameDownloadManager _downloads;
    private readonly LocalRuntimeService _runtime;

    public AgentLibraryBackend(GameTitleStore store, ManualGameLibrary library, GameTitleReadCache cache,
        ManualGameSessionService sessions, GameDownloadManager downloads, LocalRuntimeService runtime)
    {
        _store = store; _library = library; _cache = cache; _sessions = sessions; _downloads = downloads; _runtime = runtime;
    }

    public bool GamingActive => _runtime.GamingMode;

    public IReadOnlyList<GameTitleDto> Titles()
    {
        if (_cache.TryRead(out IReadOnlyList<GameTitleDto> snapshot)) return snapshot;
        return GameTitleCatalog.Build(Array.Empty<DiscoveredGameDto>(), _library.List(), _store.Snapshot());
    }

    private bool Persisted(string key, bool ok)
    {
        if (!ok) return false;
        try { if (_store.Snapshot().TryGetValue(key, out GameTitleState? s)) _cache.ApplyState(key, s); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return true;
    }

    public bool SetFavorite(string titleKey, bool favorite) => Persisted(titleKey, _store.SetFavorite(titleKey, favorite));
    public bool SetTags(string titleKey, IReadOnlyList<string> tags) => Persisted(titleKey, _store.SetTags(titleKey, tags));
    public bool SetCollections(string titleKey, IReadOnlyList<string> collections) => Persisted(titleKey, _store.SetCollections(titleKey, collections));

    public Task<ManualGameLaunchResult> LaunchAsync(GameInstallationDto installation, CancellationToken ct) =>
        installation.Source switch
        {
            GameInstallationSources.Steam => _sessions.LaunchSteamAsync(installation.SourceGameId, ct),
            GameInstallationSources.Epic => _sessions.LaunchEpicAsync(installation.SourceGameId, ct),
            GameInstallationSources.Gog => _sessions.LaunchGogAsync(installation.SourceGameId, ct),
            _ => _sessions.LaunchAsync(installation.SourceGameId, ct, null),
        };

    public bool RemoveManualEntry(string entryId)
    {
        if (!_library.Remove(entryId)) return false;
        _cache.Invalidate();
        return true;
    }

    public string EnqueueDownload(string url, string? fileName, string? sha256)
    {
        // The agent never chooses a folder: downloads go to a PaRuX-owned folder.
        string directory = Path.Combine(ServerState.StateDirectory, "agent-downloads");
        Directory.CreateDirectory(directory);
        return _downloads.Enqueue(new GameDownloadRequest(url, directory, fileName, sha256)).Id;
    }
}

public static class AgentLibraryEndpoints
{
    /// <summary>Owner-only: the UI lists what the agent asked for and the user approves or denies it.
    /// There is deliberately no tool that reaches these routes.</summary>
    public static void MapAgentLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/agent-confirmations", (AgentLibraryConsentStore store) => Results.Ok(store.List()))
            .RequireDesktopOwner();
        app.MapPost("/api/v1/gaming/agent-confirmations/{id}/approve", (string id, AgentLibraryConsentStore store) =>
            store.Approve(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost("/api/v1/gaming/agent-confirmations/{id}/deny", (string id, AgentLibraryConsentStore store) =>
            store.Deny(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
    }
}
