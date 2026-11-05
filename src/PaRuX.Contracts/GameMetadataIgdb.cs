using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

/// <summary>
/// Pure parsing for the optional IGDB cover source (the user brings their own free Twitch application credentials).
/// The request/response shapes follow IGDB's public API documentation and are covered by synthetic JSON only: they have
/// NOT been exercised against the live service (that needs a real key, so it is listed as needing a real credential).
/// </summary>
public static partial class GameMetadataParsing
{
    public const string SourceIgdb = "igdb";

    [GeneratedRegex("^[A-Za-z0-9]{10,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IgdbCredentialToken();

    [GeneratedRegex("^[a-z0-9_]{6,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IgdbImageId();

    /// <summary>
    /// Splits pasted "client-id:client-secret" (or two lines) into a validated pair. Both parts are alphanumeric Twitch
    /// application values; anything else (URLs, spaces, quotes, control characters) is refused so nothing odd reaches a request.
    /// </summary>
    public static (string ClientId, string ClientSecret)? ParseIgdbCredentials(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 256) return null;
        string[] parts = input.Split(new[] { ':', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        return IgdbCredentialToken().IsMatch(parts[0]) && IgdbCredentialToken().IsMatch(parts[1]) && parts[0] != parts[1]
            ? (parts[0], parts[1]) : null;
    }

    /// <summary>Twitch OAuth reply: the bearer token and its lifetime in seconds, or null.</summary>
    public static (string Token, int ExpiresInSeconds)? ParseIgdbToken(string? json)
    {
        if (!TryParse(json, out JsonDocument? document)) return null;
        using (document)
        {
            JsonElement root = document!.RootElement;
            string token = Text(root, "access_token");
            if (token.Length is < 10 or > 200 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.')) return null;
            int expires = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int seconds) ? seconds : 0;
            return expires > 0 ? (token, expires) : null;
        }
    }

    /// <summary>The IGDB query for one game name (quotes and backslashes removed so the name cannot break out of the string).</summary>
    public static string BuildIgdbQuery(string name)
    {
        string safe = new string(name.Where(c => !char.IsControl(c) && c != '"' && c != '\\' && c != ';').ToArray()).Trim();
        if (safe.Length > 100) safe = safe[..100];
        return "search \"" + safe + "\"; fields name,summary,first_release_date,genres.name,cover.image_id,involved_companies.developer,involved_companies.company.name; limit 6;";
    }

    /// <summary>IGDB game array: the best match (similarity 0.8 or more) with its portrait cover and genres, or null.</summary>
    public static GameInfoDto? ParseIgdbBest(string titleKey, string wantedName, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 2_000_000) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            JsonElement? best = null;
            double bestScore = 0;
            foreach (JsonElement game in document.RootElement.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object) continue;
                double score = Similarity(wantedName, Text(game, "name"));
                if (score > bestScore) { bestScore = score; best = game; }
            }
            if (best is not { } g || bestScore < 0.8) return null;
            var genres = new List<string>();
            if (g.TryGetProperty("genres", out JsonElement gl) && gl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement genre in gl.EnumerateArray())
                    if (Text(genre, "name") is { Length: > 0 } n && genres.Count < 6) genres.Add(n);
            string developer = "";
            if (g.TryGetProperty("involved_companies", out JsonElement ic) && ic.ValueKind == JsonValueKind.Array)
                foreach (JsonElement c in ic.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("developer", out JsonElement dv) && dv.ValueKind == JsonValueKind.True
                        && c.TryGetProperty("company", out JsonElement co) && Text(co, "name") is { Length: > 0 } cn) { developer = cn; break; }
            string released = g.TryGetProperty("first_release_date", out JsonElement rd) && rd.TryGetInt64(out long unix) && unix is > 0 and < 4_102_444_800
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            string? cover = null;
            if (g.TryGetProperty("cover", out JsonElement cv) && cv.ValueKind == JsonValueKind.Object
                && Text(cv, "image_id") is { Length: > 0 } imageId && IgdbImageId().IsMatch(imageId))
                cover = "https://images.igdb.com/igdb/image/upload/t_cover_big/" + imageId + ".jpg";
            long? id = g.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out long idValue) ? idValue : null;
            return new GameInfoDto(titleKey, Clip(Text(g, "name"), 120), Clip(Text(g, "summary"), 600), genres, Clip(developer, 80), released,
                SourceIgdb, id is null ? "" : "https://www.igdb.com/games/" + id.Value.ToString(CultureInfo.InvariantCulture), cover);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>IGDB serves cover images only from this https host.</summary>
    public static bool IsIgdbImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.Host.Equals("images.igdb.com", StringComparison.OrdinalIgnoreCase);
    }
}
