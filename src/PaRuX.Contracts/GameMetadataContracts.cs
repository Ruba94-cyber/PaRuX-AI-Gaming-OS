using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

/// <summary>Cover and description of a game title, taken from a public catalogue (Steam store or Wikipedia).</summary>
public sealed record GameInfoDto(
    string TitleKey,
    string Name,
    string Description,
    IReadOnlyList<string> Genres,
    string Developer,
    string Released,
    string Source,
    string PageUrl,
    string? CoverUrl);

/// <summary>
/// Pure parsing and matching for the optional "covers and info" feature. Only the two public catalogues below
/// are ever read, and image URLs outside their CDNs are refused, so a hostile reply cannot redirect the download.
/// </summary>
public static partial class GameMetadataParsing
{
    public const string SourceSteam = "steam";
    public const string SourceWikipedia = "wikipedia";
    public const string SourceSteamGridDb = "steamgriddb";

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "dc", "directors", "director", "s", "cut", "edition", "deluxe", "definitive", "complete",
        "goty", "remastered", "remaster", "game", "of", "the", "and", "v", "version"
    };

    /// <summary>Lower-case comparable form: no trademark signs, bracketed release tags, punctuation or edition words.</summary>
    public static string NormalizeTitle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string text = name.Replace("™", " ").Replace("®", " ").Replace("©", " ");
        text = Bracketed().Replace(text, " ");
        text = NonWord().Replace(text.Normalize(NormalizationForm.FormKD), " ").Trim().ToLowerInvariant();
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !NoiseWords.Contains(t)));
    }

    /// <summary>Fraction of the wanted words that appear in the candidate (and the reverse), the smaller of the two.</summary>
    public static double Similarity(string? wanted, string? candidate)
    {
        string[] a = NormalizeTitle(wanted).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] b = NormalizeTitle(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0 || b.Length == 0) return 0;
        double forward = a.Count(w => b.Contains(w, StringComparer.Ordinal)) / (double)a.Length;
        double backward = b.Count(w => a.Contains(w, StringComparer.Ordinal)) / (double)b.Length;
        return Math.Min(forward, backward);
    }

    /// <summary>Steam store search reply: id and name of every app result.</summary>
    public static List<(long Id, string Name)> ParseSteamSearch(string? json)
    {
        var result = new List<(long, string)>();
        if (!TryParse(json, out JsonDocument? document)) return result;
        using (document)
        {
            if (!document!.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string type = item.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "app";
                if (!string.Equals(type, "app", StringComparison.OrdinalIgnoreCase)) continue;
                if (!item.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out long value)) continue;
                string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (name.Length > 0) result.Add((value, name));
            }
        }
        return result;
    }

    /// <summary>The best search result for <paramref name="wanted"/>, or null when nothing is close enough (0.8).</summary>
    public static long? PickSteamMatch(string wanted, IReadOnlyList<(long Id, string Name)> candidates)
    {
        (long Id, string Name)? best = null;
        double bestScore = 0;
        foreach ((long id, string name) in candidates)
        {
            double score = Similarity(wanted, name);
            if (score > bestScore) { bestScore = score; best = (id, name); }
        }
        return bestScore >= 0.8 ? best!.Value.Id : null;
    }

    /// <summary>Steam app details reply for one app id.</summary>
    public static GameInfoDto? ParseSteamDetails(string titleKey, long appId, string? json)
    {
        if (!TryParse(json, out JsonDocument? document)) return null;
        using (document)
        {
            if (!document!.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out JsonElement entry)
                || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("success", out JsonElement ok) || ok.ValueKind != JsonValueKind.True
                || !entry.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object) return null;
            string name = Text(data, "name");
            if (name.Length == 0) return null;
            var genres = new List<string>();
            if (data.TryGetProperty("genres", out JsonElement g) && g.ValueKind == JsonValueKind.Array)
                foreach (JsonElement genre in g.EnumerateArray())
                    if (Text(genre, "description") is { Length: > 0 } d && genres.Count < 6) genres.Add(d);
            string developer = data.TryGetProperty("developers", out JsonElement dev) && dev.ValueKind == JsonValueKind.Array
                && dev.GetArrayLength() > 0 && dev[0].ValueKind == JsonValueKind.String ? dev[0].GetString() ?? "" : "";
            string released = data.TryGetProperty("release_date", out JsonElement rd) && rd.ValueKind == JsonValueKind.Object ? Text(rd, "date") : "";
            string? cover = Text(data, "header_image") is { Length: > 0 } h && IsAllowedImageUrl(h) ? h : null;
            return new GameInfoDto(titleKey, Clip(name, 120), Clip(Text(data, "short_description"), 600), genres,
                Clip(developer, 80), Clip(released, 40), SourceSteam,
                "https://store.steampowered.com/app/" + appId.ToString(CultureInfo.InvariantCulture) + "/", cover);
        }
    }

    /// <summary>Wikipedia title search reply: titles whose short description says they are a video game.</summary>
    public static List<string> ParseWikipediaSearch(string? json)
    {
        var result = new List<string>();
        if (!TryParse(json, out JsonDocument? document)) return result;
        using (document)
        {
            if (!document!.RootElement.TryGetProperty("pages", out JsonElement pages) || pages.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement page in pages.EnumerateArray())
            {
                string description = Text(page, "description");
                string key = Text(page, "key");
                if (key.Length > 0 && description.Contains("video game", StringComparison.OrdinalIgnoreCase)) result.Add(key);
            }
        }
        return result;
    }

    /// <summary>Wikipedia page summary: description, short text and the article's lead (cover) image.</summary>
    public static GameInfoDto? ParseWikipediaSummary(string titleKey, string wantedName, string? json)
    {
        if (!TryParse(json, out JsonDocument? document)) return null;
        using (document)
        {
            JsonElement root = document!.RootElement;
            string title = Text(root, "title");
            string description = Text(root, "description");
            if (title.Length == 0 || !description.Contains("video game", StringComparison.OrdinalIgnoreCase)) return null;
            if (Similarity(wantedName, title) < 0.6) return null;
            string? cover = null;
            if (root.TryGetProperty("thumbnail", out JsonElement thumb) && thumb.ValueKind == JsonValueKind.Object
                && Text(thumb, "source") is { Length: > 0 } source && IsAllowedImageUrl(source)) cover = source;
            string page = root.TryGetProperty("content_urls", out JsonElement urls) && urls.ValueKind == JsonValueKind.Object
                && urls.TryGetProperty("desktop", out JsonElement desktop) && desktop.ValueKind == JsonValueKind.Object ? Text(desktop, "page") : "";
            if (!page.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) page = "";
            return new GameInfoDto(titleKey, Clip(title, 120), Clip(Text(root, "extract"), 600), Array.Empty<string>(),
                "", Clip(description, 40), SourceWikipedia, page, cover);
        }
    }

    /// <summary>Only https images served by the two catalogues' own CDNs may be downloaded.</summary>
    public static bool IsAllowedImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "upload.wikimedia.org" || host.EndsWith(".steamstatic.com", StringComparison.Ordinal)
            || host == "steamcdn-a.akamaihd.net" || IsSteamGridDbImageUrl(url);
    }

    /// <summary>SteamGridDB serves its images only from these two https hosts.</summary>
    public static bool IsSteamGridDbImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "cdn2.steamgriddb.com" || host == "cdn.steamgriddb.com";
    }

    /// <summary>SteamGridDB autocomplete reply (<c>{"success":true,"data":[{"id":..,"name":..}]}</c>): game id and name of every result.</summary>
    public static List<(long Id, string Name)> ParseSteamGridDbSearch(string? json)
    {
        var result = new List<(long, string)>();
        if (!TryParse(json, out JsonDocument? document)) return result;
        using (document)
        {
            JsonElement root = document!.RootElement;
            if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return result;
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out long value)) continue;
                string name = Text(item, "name");
                if (name.Length > 0) result.Add((value, name));
            }
        }
        return result;
    }

    /// <summary>The first (best ranked) autocomplete result that is close enough to <paramref name="wanted"/>, or null.</summary>
    public static long? PickSteamGridDbGame(string wanted, IReadOnlyList<(long Id, string Name)> candidates)
    {
        foreach ((long id, string name) in candidates)
            if (Similarity(wanted, name) >= 0.8) return id;
        return null;
    }

    /// <summary>
    /// SteamGridDB grids reply: the best portrait, static, safe-for-work grid on an allowed CDN host. A 600x900 grid is preferred,
    /// then the higher community score. Animated (gif/webm/apng), NSFW, humor and epilepsy-flagged grids and any other host are skipped.
    /// </summary>
    public static string? PickSteamGridDbGrid(string? json)
    {
        if (!TryParse(json, out JsonDocument? document)) return null;
        using (document)
        {
            JsonElement root = document!.RootElement;
            if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return null;
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return null;
            string? best = null;
            (bool Exact, long Score) bestRank = (false, long.MinValue);
            foreach (JsonElement grid in data.EnumerateArray())
            {
                if (grid.ValueKind != JsonValueKind.Object) continue;
                if (Flag(grid, "nsfw") || Flag(grid, "humor") || Flag(grid, "epilepsy")) continue;
                string mime = Text(grid, "mime");
                if (!mime.Equals("image/png", StringComparison.OrdinalIgnoreCase) && !mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                    && !mime.Equals("image/webp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!grid.TryGetProperty("width", out JsonElement w) || !w.TryGetInt32(out int width)
                    || !grid.TryGetProperty("height", out JsonElement h) || !h.TryGetInt32(out int height)
                    || width <= 0 || height <= width) continue; // portrait only
                string url = Text(grid, "url");
                if (!IsSteamGridDbImageUrl(url)) continue;
                long score = grid.TryGetProperty("score", out JsonElement sc) && sc.TryGetInt64(out long s) ? s : 0;
                (bool, long) rank = (width == 600 && height == 900, score);
                if (best is null || rank.Item1 && !bestRank.Exact || rank.Item1 == bestRank.Exact && rank.Item2 > bestRank.Score)
                {
                    best = url;
                    bestRank = rank;
                }
            }
            return best;
        }
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool TryParse(string? json, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 2_000_000) return false;
        try { document = JsonDocument.Parse(json); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
