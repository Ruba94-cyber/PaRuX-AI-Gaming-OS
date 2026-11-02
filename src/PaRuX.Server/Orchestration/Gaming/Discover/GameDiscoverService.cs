using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

/// <summary>Validated actions per recommendation. PaRuX returns links only; the UI opens them. No download here.</summary>
public static class GameDiscoverActions
{
    /// <summary>"steam://install/&lt;appid&gt;" for a strictly numeric app id, else null.</summary>
    public static string? SteamInstallLink(string? appId) =>
        SteamStoreCatalogProvider.IsValidAppId(appId) ? "steam://install/" + appId : null;

    public static GameDiscoverActionsDto? For(string? store, string? storeId)
    {
        if (!string.Equals(store, "steam", StringComparison.OrdinalIgnoreCase)) return null; // Epic/GOG: no official catalogue
        if (!SteamStoreCatalogProvider.IsValidAppId(storeId)) return null;
        return new GameDiscoverActionsDto("steam", storeId!, SteamStoreCatalogProvider.StorePageUrl(storeId!),
            SteamInstallLink(storeId), InstallRequiresConfirmation: true,
            "Opens the Steam client, which downloads and installs the game. PaRuX downloads nothing.");
    }
}

public sealed class GameDiscoverService
{
    public const int MaxProfileGames = 8;
    public const int MaxCandidateDetails = 20;

    private readonly IGameCatalogProvider _steam;
    private readonly GameDiscoverGate _gate;
    private readonly GameDiscoverSettingsStore _settings;
    private readonly Func<DateTime> _now;

    public GameDiscoverService(IGameCatalogProvider steam, GameDiscoverGate gate, GameDiscoverSettingsStore settings, Func<DateTime>? now = null)
    {
        _steam = steam;
        _gate = gate;
        _settings = settings;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public async Task<GameDiscoverResultDto> RecommendAsync(IReadOnlyList<GameTitleDto> library, CancellationToken ct)
    {
        string? blocked = _gate.Check();
        if (blocked is not null) return Result(blocked);

        // Library facts used locally: owned Steam ids, and every owned title name (any store) for exclusion.
        var ownedKeys = new HashSet<string>(StringComparer.Ordinal);
        var ownedTitles = new HashSet<string>(StringComparer.Ordinal);
        var steamTitles = new List<(string Id, GameTitleDto Title)>();
        foreach (GameTitleDto t in library)
        {
            ownedTitles.Add(GameDiscoverRecommender.NormalizeTitle(t.Name));
            foreach (var inst in t.Installations)
                if (string.Equals(inst.Source, "steam", StringComparison.OrdinalIgnoreCase) && SteamStoreCatalogProvider.IsValidAppId(inst.SourceGameId))
                {
                    ownedKeys.Add(GameDiscoverRecommender.OwnedKey("steam", inst.SourceGameId));
                    steamTitles.Add((inst.SourceGameId, t));
                }
        }

        using var hardOff = new GamingHardOffWatch(_gate.GamingActive, ct);
        try
        {
            var played = new List<PlayedGame>();
            foreach (var (id, title) in steamTitles
                         .OrderByDescending(s => s.Title.LastPlayedUtc ?? DateTime.MinValue)
                         .ThenByDescending(s => s.Title.PlaySeconds)
                         .Take(MaxProfileGames))
            {
                var detail = await _steam.DetailAsync(id, hardOff.Token);
                if (detail.Status is GameDiscoverStatus.Disabled or GameDiscoverStatus.GamingActive) return Result(detail.Status);
                if (!detail.IsOk) continue; // one unknown title must not stop the others
                played.Add(new PlayedGame(title.Name, title.PlaySeconds, title.LastPlayedUtc, detail.Value!.Genres, detail.Value.Tags));
            }
            if (played.Count == 0)
                return new GameDiscoverResultDto(GameDiscoverStatus.Ok,
                    "No played Steam games with known genres yet, so there is nothing to base recommendations on.", Array.Empty<GameRecommendationDto>());

            var featured = await _steam.FeaturedAsync(hardOff.Token);
            if (!featured.IsOk) return Result(featured.Status, featured.Message);

            var hidden = new HashSet<string>(_settings.Hidden(), StringComparer.Ordinal);
            var candidates = new List<GameCatalogEntryDto>();
            foreach (GameCatalogEntryDto f in featured.Value!)
            {
                if (candidates.Count >= MaxCandidateDetails) break;
                string key = GameDiscoverRecommender.OwnedKey(f.Store, f.StoreId);
                if (ownedKeys.Contains(key) || hidden.Contains(key) ||
                    ownedTitles.Contains(GameDiscoverRecommender.NormalizeTitle(f.Title))) continue;
                var detail = await _steam.DetailAsync(f.StoreId, hardOff.Token);
                if (detail.Status is GameDiscoverStatus.Disabled or GameDiscoverStatus.GamingActive) return Result(detail.Status);
                if (detail.IsOk) candidates.Add(detail.Value!);
            }

            // No scoring while Gaming owns the machine (or Discover was switched off mid-run).
            blocked = _gate.Check();
            if (blocked is not null) return Result(blocked);
            ct.ThrowIfCancellationRequested();

            var profile = GameDiscoverRecommender.BuildProfile(played, _now());
            var recs = GameDiscoverRecommender.Recommend(profile, candidates, ownedKeys, ownedTitles, hidden);
            return new GameDiscoverResultDto(GameDiscoverStatus.Ok, null, recs);
        }
        catch (OperationCanceledException) when (hardOff.Triggered && !ct.IsCancellationRequested)
        {
            return Result(GameDiscoverStatus.GamingActive);
        }
    }

    private static GameDiscoverResultDto Result(string status, string? message = null) =>
        new(status, message ?? (status == GameDiscoverStatus.Ok ? null : GameDiscoverGate.MessageFor(status)), Array.Empty<GameRecommendationDto>());
}
