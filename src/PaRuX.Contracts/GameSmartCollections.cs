namespace PaRuX.Contracts;

/// <summary>One condition of a smart collection, e.g. field "playHours", op "gte", value "10".</summary>
public sealed class GameSmartRule
{
    /// <summary>One of <see cref="GameSmartCollectionRules.Fields"/>.</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>"eq", "neq", "gte", "lte" or "contains" (text fields only).</summary>
    public string Op { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>A saved filter that fills itself, like Playnite's filter presets: "Playing and rated 8+", "Not played for 90 days".</summary>
public sealed class GameSmartCollection
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>True: every rule must match. False: any rule is enough.</summary>
    public bool MatchAll { get; set; } = true;
    public List<GameSmartRule> Rules { get; set; } = new();
}

/// <summary>Rules and a pure evaluator over <see cref="GameTitleDto"/>. No I/O, so the app and the server share it.</summary>
public static class GameSmartCollectionRules
{
    public const int MaximumRules = 8;
    public const int MaximumNameLength = 40;

    /// <summary>Numeric: playHours, score, daysSincePlayed, daysSinceAdded. Text: status, tag, collection, name. Flag: favorite, hidden.</summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "playHours", "score", "daysSincePlayed", "daysSinceAdded", "status", "tag", "collection", "name", "favorite", "hidden",
    };

    private static bool IsNumeric(string f) => f is "playHours" or "score" or "daysSincePlayed" or "daysSinceAdded";
    private static bool IsFlag(string f) => f is "favorite" or "hidden";

    /// <summary>Null when the collection is acceptable, otherwise a short reason.</summary>
    public static string? Validate(GameSmartCollection? c)
    {
        if (c is null) return "A smart collection is required.";
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > MaximumNameLength || c.Name.Any(char.IsControl))
            return "A name of 1 to " + MaximumNameLength + " characters without control characters is required.";
        if (c.Rules is null || c.Rules.Count == 0) return "Add at least one rule.";
        if (c.Rules.Count > MaximumRules) return "At most " + MaximumRules + " rules.";
        foreach (GameSmartRule r in c.Rules)
        {
            if (r is null || !Fields.Contains(r.Field)) return "Unknown field.";
            if ((r.Value ?? string.Empty).Length > 80 || (r.Value ?? string.Empty).Any(char.IsControl)) return "A rule value is too long or has control characters.";
            if (IsNumeric(r.Field))
            {
                if (r.Op is not ("eq" or "neq" or "gte" or "lte")) return r.Field + " supports eq, neq, gte and lte.";
                if (!double.TryParse(r.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0 || v > 1_000_000)
                    return r.Field + " needs a number from 0 to 1000000.";
            }
            else if (IsFlag(r.Field))
            {
                if (r.Op is not ("eq" or "neq")) return r.Field + " supports eq and neq.";
                if (r.Value is not ("true" or "false")) return r.Field + " needs true or false.";
            }
            else
            {
                if (r.Op is not ("eq" or "neq" or "contains")) return r.Field + " supports eq, neq and contains.";
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a title belongs to the collection right now. Hidden titles never match unless a rule asks for hidden=true, so a
    /// smart collection does not resurrect titles the owner hid. A title never played has no "days since played" and never matches
    /// a daysSincePlayed rule, except neq.
    /// </summary>
    public static bool Matches(GameTitleDto title, GameSmartCollection collection, DateTime nowUtc)
    {
        if (Validate(collection) is not null) return false;
        bool asksForHidden = collection.Rules.Any(r => r.Field == "hidden" && r.Op == "eq" && r.Value == "true");
        if (title.Hidden && !asksForHidden) return false;
        IEnumerable<bool> results = collection.Rules.Select(r => Evaluate(title, r, nowUtc));
        return collection.MatchAll ? results.All(x => x) : results.Any(x => x);
    }

    private static bool Evaluate(GameTitleDto t, GameSmartRule r, DateTime now)
    {
        if (IsNumeric(r.Field))
        {
            double target = double.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture);
            double? actual = r.Field switch
            {
                "playHours" => t.PlaySeconds / 3600.0,
                "score" => t.Score,
                "daysSincePlayed" => t.LastPlayedUtc is { } p ? Math.Floor((now - p).TotalDays) : null,
                "daysSinceAdded" => t.AddedUtc is { } a ? Math.Floor((now - a).TotalDays) : null,
                _ => null,
            };
            if (actual is null) return r.Op == "neq";
            return r.Op switch { "eq" => Math.Abs(actual.Value - target) < 1e-9, "neq" => Math.Abs(actual.Value - target) >= 1e-9, "gte" => actual >= target, "lte" => actual <= target, _ => false };
        }
        if (IsFlag(r.Field))
        {
            bool actual = r.Field == "favorite" ? t.Favorite : t.Hidden;
            bool wanted = r.Value == "true";
            return r.Op == "eq" ? actual == wanted : actual != wanted;
        }
        IEnumerable<string> values = r.Field switch
        {
            "status" => new[] { t.Status ?? string.Empty },
            "tag" => t.Tags ?? Array.Empty<string>(),
            "collection" => t.Collections ?? Array.Empty<string>(),
            _ => new[] { t.Name ?? string.Empty },
        };
        bool any = r.Op == "contains"
            ? values.Any(v => v.Contains(r.Value, StringComparison.OrdinalIgnoreCase))
            : values.Any(v => string.Equals(v, r.Value, StringComparison.OrdinalIgnoreCase));
        return r.Op == "neq" ? !any : any;
    }
}
