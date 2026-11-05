using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed record ExternalLibraryGame(string Provider, string ExternalId, string Title);

/// <summary>
/// Read-only, bounded parser for explicit JSON exports. It deliberately ignores paths,
/// launch arguments, credentials, save files and all non-title metadata. A separate,
/// user-confirmed import transaction would be required to persist any candidate.
/// </summary>
public static class ExternalGameLibraryExportParser
{
    public static IReadOnlyList<ExternalLibraryGame> Parse(string provider, string json)
    {
        string kind = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (kind != "playnite" && kind != "heroic" && kind != "lutris")
            throw new InvalidDataException("Unsupported library-export provider.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > 512_000)
            throw new InvalidDataException("Library JSON is empty or too large.");
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        string arrayKey = kind == "heroic" ? "library" : "games";
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty(arrayKey, out JsonElement games)
            || games.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported export layout; no games were imported.");
        if (games.GetArrayLength() > 1000)
            throw new InvalidDataException("Preview at most 1000 entries per export.");

        var items = new List<ExternalLibraryGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string titleKey = kind == "playnite" ? "Name" : kind == "heroic" ? "title" : "name";
        string idKey = kind == "playnite" ? "GameId" : kind == "heroic" ? "app_name" : "slug";
        foreach (JsonElement game in games.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object) continue;
            string title = ReadString(game, titleKey);
            string id = ReadString(game, idKey);
            if (title.Length == 0 || title.Length > 200 || id.Length == 0 || id.Length > 200
                || title.Any(char.IsControl) || id.Any(char.IsControl)) continue;
            if (seen.Add(id)) items.Add(new(kind, id, title));
        }
        return items;
    }

    private static string ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
}

public sealed record PublicStorePriceSample(
    string Store, string ProductId, string Currency, int PriceMinor,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Parses a caller-supplied Steam public appdetails response; does NOT fetch an API,
/// run a timer, persist a wishlist or send a notification. Prices are store/region
/// specific and can become stale. All network access must be separately opted in.
/// </summary>
public static class PublicStorePriceWatchPolicy
{
    public static PublicStorePriceSample? ParseSteamAppDetails(
        string? appId, string? json, bool optIn, DateTimeOffset observedAtUtc)
    {
        if (!optIn) return null;
        if (string.IsNullOrEmpty(appId) || appId.Length > 12
            || appId.Any(c => c < '0' || c > '9')
            || string.IsNullOrWhiteSpace(json) || json.Length > 128_000)
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
            if (!doc.RootElement.TryGetProperty(appId, out JsonElement record)
                || !record.TryGetProperty("success", out JsonElement success)
                || success.ValueKind != JsonValueKind.True
                || !record.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("price_overview", out JsonElement price)
                || !price.TryGetProperty("final", out JsonElement amount)
                || !amount.TryGetInt32(out int minor) || minor < 0
                || !price.TryGetProperty("currency", out JsonElement currency)
                || currency.ValueKind != JsonValueKind.String)
                return null;
            string code = currency.GetString() ?? string.Empty;
            if (code.Length != 3 || code.Any(c => c < 'A' || c > 'Z')) return null;
            return new("steam", appId, code, minor, observedAtUtc.ToUniversalTime());
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static bool ShouldNotify(PublicStorePriceSample? sample,
        int userMaximumPriceMinor, bool notificationsOptedIn, DateTimeOffset nowUtc)
    {
        if (!notificationsOptedIn || sample is null || userMaximumPriceMinor < 0
            || sample.PriceMinor < 0 || !string.Equals(sample.Store, "steam", StringComparison.Ordinal))
            return false;
        TimeSpan age = nowUtc.ToUniversalTime() - sample.ObservedAtUtc;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromHours(24)
            && sample.PriceMinor <= userMaximumPriceMinor;
    }
}
