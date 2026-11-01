using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Offline, deterministic dataset quality checks for fine-tuning preparation: chat-template validity,
/// length distribution and near-duplicate detection. Token counts are ESTIMATES (characters / 4); this
/// analyzer deliberately does not claim to be a model tokenizer.
/// </summary>
public static class DatasetQualityAnalyzer
{
    public const int MaxIssueSamples = 20;
    public const int MaxConversationTurns = 200;
    private const int MaxNearDuplicateBucket = 64;

    private static readonly string[] ConversationColumns = { "messages", "conversations", "conversation" };
    private static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase)
        { "system", "user", "assistant", "tool", "human", "gpt", "function" };

    public static readonly IReadOnlyList<int> TokenBuckets = new[] { 128, 256, 512, 1024, 2048, 4096, 8192 };

    public static DatasetQualityReport Analyze(IReadOnlyList<Dictionary<string, string?>> rows)
    {
        var report = new DatasetQualityReport();
        var lengths = new List<int>(rows.Count);
        var signatures = new List<(int Row, ulong TextHash, uint[] Signature)>(rows.Count);
        var issueCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < rows.Count; i++)
        {
            Dictionary<string, string?> row = rows[i];
            string text;
            string? conversationJson = FirstValue(row, ConversationColumns);
            if (conversationJson is not null)
            {
                report.ConversationRows++;
                text = AnalyzeConversation(i, conversationJson, report, issueCounts, out int chars);
                lengths.Add(chars);
            }
            else
            {
                text = string.Join(" ", row.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
                lengths.Add(text.Length);
            }
            if (text.Length >= 20) signatures.Add((i, Fnv1a(text), MinHashSignature(text)));
        }

        report.IssueCounts = issueCounts;
        FillLengthStats(report, lengths);
        FindNearDuplicates(report, signatures);
        return report;
    }

    private static string? FirstValue(Dictionary<string, string?> row, string[] columns)
    {
        foreach (string column in columns)
            foreach (var pair in row)
                if (string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
        return null;
    }

    private static string AnalyzeConversation(
        int rowIndex, string json, DatasetQualityReport report, Dictionary<string, int> counts, out int characters)
    {
        characters = 0;
        void Issue(string kind, string detail)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (report.IssueSamples.Count < MaxIssueSamples) report.IssueSamples.Add($"row {rowIndex + 1}: {detail}");
        }

        List<(string Role, string Content)> turns;
        try { turns = ParseTurns(json); }
        catch (JsonException) { Issue("unparseable-conversation", "the messages column is not valid JSON"); return string.Empty; }
        if (turns.Count == 0) { Issue("empty-conversation", "no messages"); return string.Empty; }
        if (turns.Count > MaxConversationTurns) Issue("too-many-turns", $"{turns.Count} turns");

        var text = new System.Text.StringBuilder();
        string previous = string.Empty;
        int index = 0;
        foreach ((string rawRole, string content) in turns)
        {
            string role = NormalizeRole(rawRole);
            if (!Roles.Contains(rawRole)) Issue("unknown-role", $"unknown role '{Truncate(rawRole)}'");
            if (string.IsNullOrWhiteSpace(content)) Issue("empty-content", $"{role} message {index + 1} is empty");
            if (index == 0 && role == "assistant") Issue("starts-with-assistant", "the first message is from the assistant");
            if (role is "user" or "assistant" && role == previous)
                Issue("repeated-role", $"two consecutive {role} messages");
            if (role is "user" or "assistant") previous = role;
            characters += content.Length;
            text.Append(content).Append(' ');
            index++;
        }
        if (!turns.Any(t => NormalizeRole(t.Role) == "assistant")) Issue("no-assistant-reply", "no assistant message to learn from");
        else if (NormalizeRole(turns[^1].Role) != "assistant") Issue("ends-without-assistant", "the last message is not from the assistant");
        return text.ToString();
    }

    /// <summary>Accepts OpenAI-style {role, content} and ShareGPT-style {from, value} turns.</summary>
    private static List<(string Role, string Content)> ParseTurns(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not an array");
        var turns = new List<(string, string)>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) { turns.Add(("", "")); continue; }
            string role = StringProperty(element, "role") ?? StringProperty(element, "from") ?? string.Empty;
            string content = StringProperty(element, "content") ?? StringProperty(element, "value") ?? string.Empty;
            turns.Add((role, content));
        }
        return turns;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "human" => "user",
        "gpt" => "assistant",
        "function" => "tool",
        var other => other,
    };

    private static void FillLengthStats(DatasetQualityReport report, List<int> lengths)
    {
        report.EstimatedTokenBuckets = TokenBuckets.Select(limit => new TokenBucket(limit, 0)).ToList();
        report.OverflowRows = 0;
        if (lengths.Count == 0) return;
        var tokens = lengths.Select(chars => (chars + 3) / 4).OrderBy(t => t).ToArray();
        report.EstimatedTokensP50 = tokens[(int)Math.Ceiling(0.5 * tokens.Length) - 1];
        report.EstimatedTokensP95 = tokens[(int)Math.Ceiling(0.95 * tokens.Length) - 1];
        report.EstimatedTokensMax = tokens[^1];
        var buckets = new int[TokenBuckets.Count];
        foreach (int t in tokens)
        {
            int bucket = Array.FindIndex(TokenBuckets.ToArray(), limit => t <= limit);
            if (bucket < 0) report.OverflowRows++;
            else buckets[bucket]++;
        }
        report.EstimatedTokenBuckets = TokenBuckets.Select((limit, i) => new TokenBucket(limit, buckets[i])).ToList();
    }

    private const int SignatureSize = 32;
    private const int Bands = 8;                    // 8 bands x 4 rows
    private const int RowsPerBand = SignatureSize / Bands;
    private const double SimilarityThreshold = 0.75; // estimated Jaccard of word 3-shingles
    private const int MaxShinglesPerRow = 500;

    /// <summary>
    /// Rows whose estimated word-3-shingle Jaccard similarity to an EARLIER row is at least ~0.75
    /// (MinHash + banded LSH; a statistical estimate, not an exact comparison). Rows with identical text
    /// are exact duplicates and are reported by the existing duplicate count instead.
    /// </summary>
    private static void FindNearDuplicates(DatasetQualityReport report, List<(int Row, ulong TextHash, uint[] Signature)> items)
    {
        var buckets = new Dictionary<(int Band, ulong Key), List<int>>();
        var identical = new HashSet<ulong>();
        for (int k = 0; k < items.Count; k++)
        {
            (int row, ulong textHash, uint[] signature) = items[k];
            if (!identical.Add(textHash)) continue; // exact duplicate of an earlier row
            bool near = false;
            for (int band = 0; band < Bands; band++)
            {
                ulong key = 14695981039346656037UL;
                for (int r = 0; r < RowsPerBand; r++) { key ^= signature[band * RowsPerBand + r]; key *= 1099511628211UL; }
                if (!buckets.TryGetValue((band, key), out List<int>? bucket)) buckets[(band, key)] = bucket = new List<int>();
                if (!near)
                    foreach (int other in bucket)
                        if (Similarity(items[other].Signature, signature) >= SimilarityThreshold) { near = true; break; }
                if (bucket.Count < MaxNearDuplicateBucket) bucket.Add(k);
            }
            if (near)
            {
                report.NearDuplicateRows++;
                if (report.NearDuplicateSamples.Count < MaxIssueSamples) report.NearDuplicateSamples.Add(row + 1);
            }
        }
    }

    internal static double Similarity(uint[] a, uint[] b)
    {
        int equal = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) equal++;
        return equal / (double)a.Length;
    }

    /// <summary>MinHash signature of the lower-cased word 3-shingles (first 500 shingles per row).</summary>
    internal static uint[] MinHashSignature(string text)
    {
        string[] words = text.ToLowerInvariant().Split(
            new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries);
        var signature = new uint[SignatureSize];
        Array.Fill(signature, uint.MaxValue);
        int shingles = Math.Min(MaxShinglesPerRow, Math.Max(1, words.Length - 2));
        for (int i = 0; i < shingles; i++)
        {
            ulong h = Fnv1a(words.Length >= 3 ? string.Join(' ', words, i, 3) : string.Join(' ', words));
            for (int f = 0; f < SignatureSize; f++)
            {
                ulong mixed = (h ^ ((ulong)f * 0x9E3779B97F4A7C15UL)) * 0xBF58476D1CE4E5B9UL;
                mixed ^= mixed >> 31; mixed *= 0x94D049BB133111EBUL; mixed ^= mixed >> 29;
                uint value = (uint)(mixed >> 32);
                if (value < signature[f]) signature[f] = value;
            }
        }
        return signature;
    }

    private static ulong Fnv1a(string s)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in s) { hash ^= c; hash *= 1099511628211UL; }
        // Final avalanche so that short inputs still spread over all 64 bits.
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        return hash;
    }

    private static string Truncate(string s) => s.Length <= 30 ? s : s[..30] + "…";
}
