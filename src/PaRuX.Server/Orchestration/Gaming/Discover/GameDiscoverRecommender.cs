using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

/// <summary>A game from the user's library whose store genres/tags are known.</summary>
public sealed record PlayedGame(
    string Title, long PlaySeconds, DateTime? LastPlayedUtc,
    IReadOnlyList<string> Genres, IReadOnlyList<string> Tags);

public sealed class TasteProfile
{
    internal Dictionary<string, double> Weights { get; } = new(StringComparer.Ordinal);
    /// <summary>term -> (played title, contribution), used to explain recommendations.</summary>
    internal Dictionary<string, List<(string Title, double Contribution)>> Sources { get; } = new(StringComparer.Ordinal);
    public bool IsEmpty => Weights.Count == 0;
    public IReadOnlyDictionary<string, double> TermWeights => Weights;
}

public sealed record RecommendOptions(int MaxResults = 12, int MaxPerGenre = 3, double MinScore = 0.05);

/// <summary>
/// Pure, local scoring: no I/O, no clock (the caller passes "now"), no network. A taste profile is the sum of
/// per-game weights over genres (weight 1.0) and tags/categories (0.5); a candidate scores by cosine similarity
/// with the profile, so a long tag list cannot win just by being long.
/// </summary>
public static class GameDiscoverRecommender
{
    private const double GenreFactor = 1.0, TagFactor = 0.5;

    /// <summary>Hours played (log-damped, so one 2000 h game does not own the profile) times a recency factor.</summary>
    public static double GameWeight(PlayedGame g, DateTime nowUtc)
    {
        double hours = Math.Max(0, g.PlaySeconds) / 3600.0;
        double playWeight = Math.Max(0.1, Math.Log(1 + hours));
        double recency = 0.5;
        if (g.LastPlayedUtc is { } last)
        {
            double days = Math.Max(0, (nowUtc - last).TotalDays);
            recency = 0.5 + 0.5 * Math.Exp(-days / 90.0);
        }
        return playWeight * recency;
    }

    public static TasteProfile BuildProfile(IEnumerable<PlayedGame> played, DateTime nowUtc)
    {
        var profile = new TasteProfile();
        foreach (PlayedGame g in played)
        {
            double w = GameWeight(g, nowUtc);
            foreach ((string term, double factor) in Terms(g.Genres, g.Tags))
            {
                double c = w * factor;
                profile.Weights[term] = profile.Weights.GetValueOrDefault(term) + c;
                if (!profile.Sources.TryGetValue(term, out var list)) profile.Sources[term] = list = new();
                list.Add((g.Title, c));
            }
        }
        return profile;
    }

    public static string OwnedKey(string store, string storeId) => store.ToLowerInvariant() + ":" + storeId;

    public static string NormalizeTitle(string title) =>
        new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <param name="ownedKeys">"store:id" of every owned title; hidden keys use the same form.</param>
    /// <param name="ownedTitles">Normalized names of all owned titles across stores (catches a game owned on another launcher).</param>
    public static IReadOnlyList<GameRecommendationDto> Recommend(
        TasteProfile profile, IEnumerable<GameCatalogEntryDto> candidates,
        ISet<string> ownedKeys, ISet<string> ownedTitles, ISet<string> hiddenKeys, RecommendOptions? options = null)
    {
        options ??= new RecommendOptions();
        if (profile.IsEmpty) return Array.Empty<GameRecommendationDto>();
        double profileNorm = Math.Sqrt(profile.Weights.Values.Sum(v => v * v));
        if (profileNorm <= 0) return Array.Empty<GameRecommendationDto>();

        var scored = new List<(GameRecommendationDto Rec, string Genre)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GameCatalogEntryDto c in candidates)
        {
            string key = OwnedKey(c.Store, c.StoreId);
            if (ownedKeys.Contains(key) || hiddenKeys.Contains(key) || !seen.Add(key)) continue;
            if (ownedTitles.Contains(NormalizeTitle(c.Title))) continue;

            var terms = Terms(c.Genres, c.Tags).ToList();
            if (terms.Count == 0) continue;
            double candNorm = Math.Sqrt(terms.Sum(t => t.Factor * t.Factor));
            double dot = 0;
            var shared = new List<(string Term, double Value)>();
            foreach ((string term, double factor) in terms)
                if (profile.Weights.TryGetValue(term, out double pw))
                {
                    double v = pw * factor;
                    dot += v;
                    shared.Add((term, v));
                }
            if (dot <= 0) continue;
            double score = Math.Round(dot / (profileNorm * candNorm), 4);
            if (score < options.MinScore) continue;

            shared.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Term, b.Term));
            var topTerms = shared.Take(3).Select(s => s.Term).ToList();
            // The played games that contributed most to the shared terms are the reason shown.
            var byGame = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach ((string term, _) in shared)
                foreach ((string title, double contribution) in profile.Sources[term])
                    byGame[title] = byGame.GetValueOrDefault(title) + contribution;
            var because = byGame.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(2).Select(p => p.Key).ToList();

            string explanation = $"Because you play {string.Join(" and ", because)}. Shared: {string.Join(", ", topTerms.Select(Pretty))}.";
            string genre = c.Genres.Count > 0 ? c.Genres[0].ToLowerInvariant() : "other";
            scored.Add((new GameRecommendationDto(c, score, because, topTerms.Select(Pretty).ToList(), explanation), genre));
        }

        var perGenre = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<GameRecommendationDto>();
        foreach (var (rec, genre) in scored
                     .OrderByDescending(s => s.Rec.Score)
                     .ThenBy(s => s.Rec.Game.Title, StringComparer.Ordinal))
        {
            if (result.Count >= options.MaxResults) break;
            int n = perGenre.GetValueOrDefault(genre);
            if (n >= options.MaxPerGenre) continue;
            perGenre[genre] = n + 1;
            result.Add(rec);
        }
        return result;
    }

    internal static IEnumerable<(string Term, double Factor)> Terms(IReadOnlyList<string> genres, IReadOnlyList<string> tags)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string g in genres) Add(g, GenreFactor);
        foreach (string t in tags) Add(t, TagFactor);
        return best.Select(p => (p.Key, p.Value));

        void Add(string raw, double factor)
        {
            string term = raw.Trim().ToLowerInvariant();
            if (term.Length == 0) return;
            best[term] = Math.Max(best.GetValueOrDefault(term), factor);
        }
    }

    private static string Pretty(string term) =>
        term.Length == 0 ? term : char.ToUpperInvariant(term[0]) + term[1..];
}
