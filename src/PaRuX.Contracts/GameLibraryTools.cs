using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PaRuX.Contracts;

/// <summary>One row of the "most played" list.</summary>
public sealed record GameInsightEntry(string Name, long PlaySeconds);

/// <summary>What the owner's own library says about their habits. Computed from local data only; nothing leaves the PC.</summary>
public sealed record GameLibraryInsights(
    int Titles,
    int Played,
    int NeverPlayed,
    int Favorites,
    long TotalPlaySeconds,
    int RatedTitles,
    double AverageScore,
    IReadOnlyList<GameInsightEntry> MostPlayed,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> BySource,
    string? LastPlayedName,
    DateTime? LastPlayedUtc);

public static class GameLibraryInsightsPolicy
{
    public const int TopCount = 5;

    /// <summary>Hidden titles are left out (they are not part of the visible library); duplicates by title key count once.</summary>
    public static GameLibraryInsights Compute(IEnumerable<GameTitleDto>? source)
    {
        GameTitleDto[] titles = (source ?? Array.Empty<GameTitleDto>())
            .Where(t => t is not null && !t.Hidden)
            .GroupBy(t => t.TitleKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();

        long total = 0;
        int played = 0, favorites = 0, rated = 0, scoreSum = 0;
        var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        GameTitleDto? last = null;
        foreach (GameTitleDto t in titles)
        {
            long seconds = Math.Max(0, t.PlaySeconds);
            total = total > long.MaxValue - seconds ? long.MaxValue : total + seconds;
            if (seconds > 0 || t.LastPlayedUtc.HasValue) played++;
            if (t.Favorite) favorites++;
            if (t.Score is >= 1 and <= 10) { rated++; scoreSum += t.Score; }
            string status = GameCompletionStatuses.Normalize(t.Status);
            string key = GameCompletionStatuses.IsValid(status) ? status : string.Empty;
            byStatus[key] = byStatus.GetValueOrDefault(key) + 1;
            string src = t.Installations?.FirstOrDefault()?.Source ?? string.Empty;
            if (src.Length > 0) bySource[src] = bySource.GetValueOrDefault(src) + 1;
            if (t.LastPlayedUtc.HasValue && (last is null || t.LastPlayedUtc > last.LastPlayedUtc)) last = t;
        }

        GameInsightEntry[] top = titles.Where(t => t.PlaySeconds > 0)
            .OrderByDescending(t => t.PlaySeconds).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopCount).Select(t => new GameInsightEntry(t.Name, t.PlaySeconds)).ToArray();

        return new GameLibraryInsights(
            titles.Length, played, titles.Length - played, favorites, total, rated,
            rated == 0 ? 0 : Math.Round((double)scoreSum / rated, 1),
            top, byStatus, bySource, last?.Name, last?.LastPlayedUtc);
    }
}

/// <summary>"Pick a game for me": a weighted random choice among titles that can start now. Never launches anything.</summary>
public static class GameRandomPicker
{
    /// <summary>Backlog and never-played titles are likelier than ones the owner finished or dropped; hidden or unstartable titles never.</summary>
    public static GameTitleDto? Pick(IEnumerable<GameTitleDto>? source, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var pool = new List<(GameTitleDto Title, double Weight)>();
        foreach (GameTitleDto t in source ?? Array.Empty<GameTitleDto>())
        {
            if (t is null || t.Hidden) continue;
            if (!(t.Installations ?? Array.Empty<GameInstallationDto>()).Any(GameTitleLaunchPolicy.CanStart)) continue;
            string status = GameCompletionStatuses.Normalize(t.Status);
            double weight = status switch
            {
                GameCompletionStatuses.Backlog => 3,
                GameCompletionStatuses.Playing => 2,
                GameCompletionStatuses.OnHold => 1.5,
                GameCompletionStatuses.Completed => 0.5,
                GameCompletionStatuses.Dropped => 0.25,
                _ => 1,
            };
            if (t.PlaySeconds <= 0 && !t.LastPlayedUtc.HasValue) weight += 1.5;
            pool.Add((t, weight));
        }
        if (pool.Count == 0) return null;
        double roll = random.NextDouble() * pool.Sum(p => p.Weight);
        foreach (var (title, weight) in pool)
        {
            roll -= weight;
            if (roll < 0) return title;
        }
        return pool[^1].Title;
    }
}

/// <summary>Plain-file export of the library the owner built, so it is never locked inside PaRuX. Names and personal fields only: no paths, no launch arguments.</summary>
public static class GameLibraryExport
{
    public const string Format = "parux-game-library";
    public const int Version = 1;

    public static string ToJson(IEnumerable<GameTitleDto>? source, DateTime exportedUtc)
    {
        var rows = Rows(source).Select(t => new
        {
            name = t.Name,
            platform = t.Installations?.FirstOrDefault()?.Platform ?? "",
            source = t.Installations?.FirstOrDefault()?.Source ?? "",
            favorite = t.Favorite,
            status = GameCompletionStatuses.Normalize(t.Status),
            score = t.Score,
            playSeconds = Math.Max(0, t.PlaySeconds),
            lastPlayedUtc = t.LastPlayedUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            addedUtc = t.AddedUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            tags = t.Tags ?? Array.Empty<string>(),
            collections = t.Collections ?? Array.Empty<string>(),
            notes = t.Notes ?? string.Empty,
        }).ToArray();
        return JsonSerializer.Serialize(
            new { format = Format, version = Version, exportedUtc = exportedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), titles = rows },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToCsv(IEnumerable<GameTitleDto>? source)
    {
        var sb = new StringBuilder();
        sb.Append("Name,Platform,Source,Favorite,Status,Score,PlayHours,LastPlayedUtc,Tags,Collections\r\n");
        foreach (GameTitleDto t in Rows(source))
        {
            GameInstallationDto? first = t.Installations?.FirstOrDefault();
            sb.Append(string.Join(",", new[]
            {
                Cell(t.Name), Cell(first?.Platform), Cell(first?.Source), t.Favorite ? "yes" : "no",
                Cell(GameCompletionStatuses.Normalize(t.Status)), t.Score.ToString(CultureInfo.InvariantCulture),
                (Math.Max(0, t.PlaySeconds) / 3600d).ToString("0.##", CultureInfo.InvariantCulture),
                t.LastPlayedUtc?.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                Cell(string.Join("; ", t.Tags ?? Array.Empty<string>())), Cell(string.Join("; ", t.Collections ?? Array.Empty<string>())),
            })).Append("\r\n");
        }
        return sb.ToString();
    }

    private static IEnumerable<GameTitleDto> Rows(IEnumerable<GameTitleDto>? source) =>
        (source ?? Array.Empty<GameTitleDto>()).Where(t => t is not null)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TitleKey, StringComparer.Ordinal);

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on cells a spreadsheet would run as a formula.</summary>
    internal static string Cell(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}

/// <summary>One title's personal data read back from an export made by <see cref="GameLibraryExport"/>.</summary>
public sealed record GameLibraryRestoreEntry(
    string Name, bool Favorite, string Status, int Score, long PlaySeconds, DateTime? LastPlayedUtc,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Collections, string Notes);

public sealed record RestoreGameLibraryRequest(string Json, bool Consent);

public sealed record GameLibraryRestoreResult(int Updated, int NotInLibrary, int Invalid);

/// <summary>
/// Reads a PaRuX library export (JSON) back, so favourites, status, rating, notes, tags, collections and play time follow the
/// owner to another PC. Everything is validated; nothing here touches a file path or starts a game.
/// </summary>
public static class GameLibraryRestoreParser
{
    public const int MaxEntries = 10_000;
    public const int MaxJsonCharacters = 8_000_000;
    public const long MaxPlaySeconds = 10L * 365 * 24 * 3600;

    /// <returns>The valid entries, or null when the text is not a PaRuX library export. <paramref name="invalid"/> counts entries that were skipped.</returns>
    public static IReadOnlyList<GameLibraryRestoreEntry>? Parse(string? json, out int invalid)
    {
        invalid = 0;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out JsonElement format) || format.GetString() != GameLibraryExport.Format
                || !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != GameLibraryExport.Version
                || !root.TryGetProperty("titles", out JsonElement titles) || titles.ValueKind != JsonValueKind.Array
                || titles.GetArrayLength() > MaxEntries) return null;

            var result = new List<GameLibraryRestoreEntry>();
            foreach (JsonElement row in titles.EnumerateArray())
            {
                GameLibraryRestoreEntry? entry = ReadEntry(row);
                if (entry is null) invalid++; else result.Add(entry);
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static GameLibraryRestoreEntry? ReadEntry(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        string name = Text(row, "name");
        if (name.Length == 0 || name.Length > 200 || name.Any(char.IsControl)) return null;
        string status = GameCompletionStatuses.Normalize(Text(row, "status"));
        if (!GameCompletionStatuses.IsValid(status)) status = string.Empty;
        int score = row.TryGetProperty("score", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int sv) && GameCompletionStatuses.IsValidScore(sv) ? sv : 0;
        long seconds = row.TryGetProperty("playSeconds", out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long pv) ? Math.Clamp(pv, 0, MaxPlaySeconds) : 0;
        DateTime? last = DateTime.TryParse(Text(row, "lastPlayedUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed) ? parsed : null;
        string notes = GameTitleNotes.Clean(Text(row, "notes")) ?? string.Empty;
        return new GameLibraryRestoreEntry(name, row.TryGetProperty("favorite", out JsonElement f) && f.ValueKind == JsonValueKind.True,
            status, score, seconds, last, List(row, "tags", 40, 20), List(row, "collections", 40, 12), notes);
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> List(JsonElement row, string name, int maxLength, int maxCount)
    {
        if (!row.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => (e.GetString() ?? string.Empty).Trim())
            .Where(t => t.Length > 0 && t.Length <= maxLength && !t.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToArray();
    }
}
