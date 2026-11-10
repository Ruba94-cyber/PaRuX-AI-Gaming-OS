using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

public enum DocumentSkillKind { RevisionDiff, Contradictions }

/// <summary>A document skill the person asked for in the classic Chat (a slash command or a quick action in the Documents flyout).</summary>
public sealed record DocumentSkillInvocation(DocumentSkillKind Kind, IReadOnlyList<string> Arguments, bool Explain);

/// <summary>
/// Document skills for the classic Chat: revision diff between two versions of a document and a contradiction check between
/// passages. Both are deterministic first (text units, numbers, units, identifier values) and cite file and position. A model is
/// only involved when the person adds <c>--explain</c>, and then it receives the deterministic findings as its only evidence.
/// When the selected documents do not hold enough to answer, the answer says so instead of guessing.
/// </summary>
public static class DocumentSkills
{
    public const string Usage =
        "Document skills (they read only the selected folders):\n"
        + "- /diff <document A> | <document B>  - compare two versions of a document, found by file name or hash. With a single name, the two most recent versions of that name are compared.\n"
        + "- /contradictions [topic]  - look for passages that give different numbers, units or identifier values for the same thing.\n"
        + "Add --explain to let the local model explain the findings.";

    private static readonly string[] DiffCommands = { "/diff", "/compare", "/confronta", "/revisione" };
    private static readonly string[] ContradictionCommands = { "/contradictions", "/contradiction", "/contraddizioni", "/conflicts", "/conflitti" };

    /// <summary>Recognises "/diff A | B", "/contradictions topic" and their Italian forms. Returns null for ordinary questions.</summary>
    public static DocumentSkillInvocation? TryParse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string text = message.Trim();
        if (!text.StartsWith('/')) return null;
        int space = text.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        string command = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        bool explain = Regex.IsMatch(rest, @"(^|\s)--explain(\s|$)", RegexOptions.IgnoreCase);
        if (explain) rest = Regex.Replace(rest, @"(^|\s)--explain(?=\s|$)", " ", RegexOptions.IgnoreCase).Trim();

        if (DiffCommands.Contains(command))
        {
            string[] parts = Regex.Split(rest, @"\s*(?:\||\s->\s|\svs\.?\s|\scon\s)\s*", RegexOptions.IgnoreCase)
                .Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToArray();
            return new DocumentSkillInvocation(DocumentSkillKind.RevisionDiff, parts, explain);
        }
        if (ContradictionCommands.Contains(command))
            return new DocumentSkillInvocation(DocumentSkillKind.Contradictions, rest.Length == 0 ? Array.Empty<string>() : new[] { rest.Trim('"') }, explain);
        return null;
    }

    /// <summary>Runs the skill over the chosen sources (every enabled source when none are chosen).</summary>
    public static DocumentChatPlan Run(DocumentSkillInvocation skill, IReadOnlyCollection<string>? sourceIds, KnowledgeJobService? service = null)
    {
        service ??= KnowledgeContextProvider.Current;
        if (service == null) return NotEnough("The document index is not available.");
        IReadOnlyCollection<string> scope;
        try { scope = service.ScopedSourceIds(sourceIds); } catch (Exception) { return NotEnough("The document index is not available."); }
        if (scope.Count == 0) return NotEnough("No indexed folder is selected.");
        try
        {
            DocumentChatPlan plan = skill.Kind == DocumentSkillKind.RevisionDiff
                ? RevisionDiff(service.Index, scope, skill.Arguments)
                : Contradictions(service.Index, scope, skill.Arguments.FirstOrDefault());
            return skill.Explain ? WithExplanation(skill, plan) : plan;
        }
        catch (Exception ex) { return NotEnough("The skill could not read the index (" + ex.GetType().Name + ")."); }
    }

    private static DocumentChatPlan WithExplanation(DocumentSkillInvocation skill, DocumentChatPlan plan)
    {
        if (plan.Kind != DocumentChatKind.Deterministic) return plan;   // nothing to explain, or not enough information: stay deterministic
        string task = skill.Kind == DocumentSkillKind.RevisionDiff
            ? "Explain in plain language what changed between these two versions and why it may matter. Do not add changes that are not listed."
            : "Explain which of these candidate contradictions are real conflicts and which may be explained by context. Do not add conflicts that are not listed.";
        return plan with { Kind = DocumentChatKind.NeedsModel, RewrittenQuestion = task + "\n\nDeterministic findings (cite them as given):\n" + plan.Text };
    }

    private static DocumentChatPlan NotEnough(string reason) =>
        new(DocumentChatKind.NotEnoughInformation, DocumentChatPlanner.NotEnoughInformationText + "\n\n" + reason, Array.Empty<string>(), Array.Empty<KnowledgeHit>());

    // ------------------------------------------------------------------------------------------------------ revision diff

    private sealed record Unit(string Text, string Norm, DocumentPosition Position);

    public static DocumentChatPlan RevisionDiff(KnowledgeIndex index, IReadOnlyCollection<string> scope, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Count > 2) return new(DocumentChatKind.Deterministic, Usage, Array.Empty<string>(), Array.Empty<KnowledgeHit>());
        IndexedFile a, b;
        string pairNote = string.Empty;
        if (args.Count == 1)
        {
            // One name: its two most recent distinct versions.
            var versions = index.FindFiles(args[0], scope).GroupBy(f => f.Hash).Select(g => g.First()).OrderByDescending(f => f.MtimeTicks).Take(2).ToList();
            if (versions.Count == 0) return NotEnough("No indexed document matches \"" + args[0] + "\" in the selected folders.");
            if (versions.Count == 1) return NotEnough("Only one version of \"" + args[0] + "\" is indexed (" + versions[0].Name + "). Give two names or hashes: /diff <A> | <B>.");
            b = versions[0]; a = versions[1];
            pairNote = " The two most recent versions matching \"" + args[0] + "\" were compared (older = A, newer = B).";
        }
        else
        {
            (IndexedFile? fa, string? ea) = Resolve(index, scope, args[0]);
            if (fa == null) return NotEnough(ea!);
            (IndexedFile? fb, string? eb) = Resolve(index, scope, args[1]);
            if (fb == null) return NotEnough(eb!);
            a = fa; b = fb;
        }
        if (string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase))
            return new(DocumentChatKind.Deterministic, "A and B have the same content hash (" + a.ShortHash + "): the files are identical, nothing changed.\nA = " + Label(a) + "\nB = " + Label(b),
                Array.Empty<string>(), Array.Empty<KnowledgeHit>());

        // Whole-document diff materializes passages from BOTH versions.
        // A 30,000-page Corporate manual must not make the local CPU process
        // allocate gigabytes simply because the user selected /diff.
        // Never compare only the first N chunks while claiming a complete diff.
        const int MaxWholeDocumentDiffChunks = 12_000;
        int firstChunks = index.PassageChunkCount(a.Id);
        int secondChunks = index.PassageChunkCount(b.Id);
        if (firstChunks > MaxWholeDocumentDiffChunks || secondChunks > MaxWholeDocumentDiffChunks)
            return NotEnough("These manuals are too large for a safe complete in-memory diff (A: "
                + firstChunks.ToString("N0") + " chunks; B: " + secondChunks.ToString("N0")
                + "). Nothing was compared or silently truncated. Use precise identifiers, page references,"
                + " or scoped section searches; streaming large-document diff is a separate feature.");

        List<Unit> ua = Units(index.GetPassages(a.Id)), ub = Units(index.GetPassages(b.Id));
        if (ua.Count == 0 || ub.Count == 0)
            return NotEnough((ua.Count == 0 ? Label(a) : Label(b)) + " has no readable text in the index (metadata-only or empty), so its content cannot be compared.");

        DiffResult diff = Diff(ua, ub);
        return Format(a, b, ua.Count, ub.Count, diff, pairNote);
    }

    private static (IndexedFile? File, string? Error) Resolve(KnowledgeIndex index, IReadOnlyCollection<string> scope, string arg)
    {
        IReadOnlyList<IndexedFile> found = index.FindFiles(arg, scope);
        if (found.Count == 0) return (null, "No indexed document matches \"" + arg + "\" in the selected folders.");
        var distinct = found.GroupBy(f => f.Hash).Select(g => g.First()).ToList();
        if (distinct.Count == 1) return (distinct[0], null);
        string options = string.Join("; ", distinct.Take(5).Select(f => f.Name + " (" + f.ShortHash + ")"));
        return (null, "\"" + arg + "\" matches several different documents: " + options + ". Use the full file name or a hash prefix.");
    }

    private static string Label(IndexedFile f) => f.Name + " (" + f.ShortHash + ", modified " + new DateTime(f.MtimeTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";

    private static string Cite(IndexedFile f, DocumentPosition p) => f.Name + ", " + p.Describe();

    /// <summary>Lines of a passage; long lines are split into sentences. Whitespace-normalised for comparison.</summary>
    private static List<Unit> Units(IReadOnlyList<IndexedPassage> passages)
    {
        var units = new List<Unit>();
        foreach (IndexedPassage passage in passages)
        {
            int cursor = 0;
            foreach (string line in passage.Text.Split('\n'))
            {
                int lineStart = cursor;
                cursor += line.Length + 1;
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                IEnumerable<string> parts = trimmed.Length > 500 ? Regex.Split(trimmed, @"(?<=[.!?])\s+") : new[] { trimmed };
                foreach (string part in parts)
                {
                    string norm = Regex.Replace(part, @"\s+", " ").Trim();
                    if (norm.Length == 0) continue;
                    units.Add(new Unit(norm, norm, passage.Position with { CharOffset = passage.Position.CharOffset + lineStart }));
                }
            }
        }
        return units;
    }

    private sealed record Change(Unit? Old, Unit? New);
    private sealed record DiffResult(IReadOnlyList<Change> Changes, bool SetMode);

    private const long MaxDpCells = 4_000_000;

    private static DiffResult Diff(List<Unit> a, List<Unit> b)
    {
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix].Norm == b[prefix].Norm) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix].Norm == b[b.Count - 1 - suffix].Norm) suffix++;
        List<Unit> ma = a.GetRange(prefix, a.Count - prefix - suffix), mb = b.GetRange(prefix, b.Count - prefix - suffix);
        var removed = new List<Unit>(); var added = new List<Unit>(); var changes = new List<Change>();
        bool setMode = (long)(ma.Count + 1) * (mb.Count + 1) > MaxDpCells;

        if (setMode)
        {
            var setA = new HashSet<string>(ma.Select(u => u.Norm)); var setB = new HashSet<string>(mb.Select(u => u.Norm));
            removed.AddRange(ma.Where(u => !setB.Contains(u.Norm)));
            added.AddRange(mb.Where(u => !setA.Contains(u.Norm)));
            Pair(removed, added, changes);
            return new DiffResult(changes, true);
        }

        var lcs = new int[ma.Count + 1, mb.Count + 1];
        for (int i = ma.Count - 1; i >= 0; i--)
            for (int j = mb.Count - 1; j >= 0; j--)
                lcs[i, j] = ma[i].Norm == mb[j].Norm ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        var runRemoved = new List<Unit>(); var runAdded = new List<Unit>();
        void FlushRun() { Pair(runRemoved, runAdded, changes); runRemoved.Clear(); runAdded.Clear(); }
        while (x < ma.Count || y < mb.Count)
        {
            if (x < ma.Count && y < mb.Count && ma[x].Norm == mb[y].Norm) { FlushRun(); x++; y++; }
            else if (y < mb.Count && (x == ma.Count || lcs[x, y + 1] >= lcs[x + 1, y])) runAdded.Add(mb[y++]);
            else runRemoved.Add(ma[x++]);
        }
        FlushRun();
        return new DiffResult(changes, false);
    }

    /// <summary>Within a run of removed and added units, a removed unit that is mostly the same words as an added one is a change; the rest are removals/additions.</summary>
    private static void Pair(List<Unit> removed, List<Unit> added, List<Change> output)
    {
        var usedAdded = new HashSet<int>();
        var pendingRemoved = new List<Unit>();
        foreach (Unit r in removed)
        {
            int best = -1; double bestScore = 0.35;
            for (int i = 0; i < added.Count; i++)
            {
                if (usedAdded.Contains(i)) continue;
                double score = Similarity(r.Norm, added[i].Norm);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) { usedAdded.Add(best); output.Add(new Change(r, added[best])); }
            else pendingRemoved.Add(r);
        }
        foreach (Unit r in pendingRemoved) output.Add(new Change(r, null));
        for (int i = 0; i < added.Count; i++) if (!usedAdded.Contains(i)) output.Add(new Change(null, added[i]));
    }

    private static double Similarity(string a, string b)
    {
        var wa = new HashSet<string>(Regex.Matches(a.ToLowerInvariant(), @"[\p{L}\p{N}_.,%/-]+").Select(m => m.Value));
        var wb = new HashSet<string>(Regex.Matches(b.ToLowerInvariant(), @"[\p{L}\p{N}_.,%/-]+").Select(m => m.Value));
        if (wa.Count == 0 || wb.Count == 0) return 0;
        return (double)wa.Intersect(wb).Count() / wa.Union(wb).Count();
    }

    private static string Snip(string text) => text.Length > 220 ? text[..220] + "…" : text;

    private static DocumentChatPlan Format(IndexedFile a, IndexedFile b, int unitsA, int unitsB, DiffResult diff, string pairNote)
    {
        var changed = diff.Changes.Where(c => c.Old != null && c.New != null).ToList();
        var removed = diff.Changes.Where(c => c.Old != null && c.New == null).ToList();
        var added = diff.Changes.Where(c => c.Old == null && c.New != null).ToList();
        var citations = new List<string>();
        var sb = new StringBuilder("Revision comparison (deterministic text diff, no language model was used).");
        sb.Append(pairNote).Append('\n');
        sb.Append("A = ").Append(Label(a)).Append('\n').Append("B = ").Append(Label(b)).Append('\n');
        if (diff.Changes.Count == 0)
        {
            sb.Append("\nThe two files have different bytes but the same readable text (formatting or metadata changes only): no passage changed.");
            return new(DocumentChatKind.Deterministic, sb.ToString(), Array.Empty<string>(), Array.Empty<KnowledgeHit>());
        }
        sb.Append("Passages: A ").Append(unitsA).Append(", B ").Append(unitsB).Append(" - ")
          .Append(changed.Count).Append(" changed, ").Append(added.Count).Append(" added in B, ").Append(removed.Count).Append(" removed from A.");
        if (diff.SetMode) sb.Append(" (Very large files: compared as sets of passages, so moved text is not reported as a change and ordering changes are not detected.)");
        sb.Append('\n');

        const int Cap = 12;
        if (changed.Count > 0) sb.Append("\nChanged\n");
        foreach (Change c in changed.Take(Cap))
        {
            string ca = Cite(a, c.Old!.Position), cb = Cite(b, c.New!.Position);
            citations.Add(ca); citations.Add(cb);
            sb.Append("- A [").Append(ca).Append("]: ").Append(Snip(c.Old.Text)).Append('\n');
            sb.Append("  B [").Append(cb).Append("]: ").Append(Snip(c.New.Text)).Append('\n');
            string numbers = NumberDelta(c.Old.Text, c.New.Text);
            if (numbers.Length > 0) sb.Append("  Values: ").Append(numbers).Append('\n');
        }
        if (changed.Count > Cap) sb.Append("  ... and ").Append(changed.Count - Cap).Append(" more changed passages.\n");
        if (added.Count > 0) sb.Append("\nAdded in B\n");
        foreach (Change c in added.Take(Cap)) { string cb = Cite(b, c.New!.Position); citations.Add(cb); sb.Append("- [").Append(cb).Append("] ").Append(Snip(c.New.Text)).Append('\n'); }
        if (added.Count > Cap) sb.Append("  ... and ").Append(added.Count - Cap).Append(" more.\n");
        if (removed.Count > 0) sb.Append("\nRemoved from A\n");
        foreach (Change c in removed.Take(Cap)) { string ca = Cite(a, c.Old!.Position); citations.Add(ca); sb.Append("- [").Append(ca).Append("] ").Append(Snip(c.Old.Text)).Append('\n'); }
        if (removed.Count > Cap) sb.Append("  ... and ").Append(removed.Count - Cap).Append(" more.\n");
        return new(DocumentChatKind.Deterministic, sb.ToString().TrimEnd(), citations.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Array.Empty<KnowledgeHit>());
    }

    /// <summary>"24 months -> 36 months" for numbers that appear in only one of the two texts, in order.</summary>
    private static string NumberDelta(string oldText, string newText)
    {
        List<string> o = NumberTokens(oldText), n = NumberTokens(newText);
        var onlyOld = o.Where(t => !n.Contains(t)).ToList(); var onlyNew = n.Where(t => !o.Contains(t)).ToList();
        if (onlyOld.Count == 0 && onlyNew.Count == 0) return string.Empty;
        int pairs = Math.Min(onlyOld.Count, onlyNew.Count);
        var parts = new List<string>();
        for (int i = 0; i < pairs; i++) parts.Add(onlyOld[i] + " -> " + onlyNew[i]);
        parts.AddRange(onlyOld.Skip(pairs).Select(t => t + " removed"));
        parts.AddRange(onlyNew.Skip(pairs).Select(t => t + " added"));
        return string.Join("; ", parts.Take(6));
    }

    private static List<string> NumberTokens(string text) =>
        NumberWithUnit.Matches(text).Select(m => (m.Groups["num"].Value + " " + m.Groups["unit"].Value).Trim()).ToList();

    // ------------------------------------------------------------------------------------------------------ contradictions

    private static readonly Regex NumberWithUnit = new(
        @"(?<![\w.,/:\-])(?<num>-?\d+(?:[.,]\d+)*)(?![\d/:]|-\d|[.,]\d)\s?(?<unit>%|°C|°F|[A-Za-zµΩ]{1,9}(?:/[A-Za-z]{1,4})?)?",
        RegexOptions.Compiled);

    // KEY = value / KEY: value / KEY is value / KEY shall be value for code-like identifiers.
    private static readonly Regex IdentifierAssignment = new(
        @"(?<![A-Za-z0-9])(?<id>[A-Za-z][A-Za-z0-9]*(?:[_\-][A-Za-z0-9]+)+|[A-Za-z]{2,}\d{2,}[A-Za-z0-9_]*)\s*(?:=|:|\bis\b|\bshall be\b|\bset to\b|\bequals\b|\bè\b|\bvale\b)\s*(?<val>-?\d+(?:[.,]\d+)*(?:\s?(?:%|°C|[A-Za-zµΩ]{1,9}(?:/[A-Za-z]{1,4})?))?|[A-Za-z0-9_]{1,24})",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KeyStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","be","been","shall","must","should","will","can","may","to","of","at","by","in","on","for","with","and","or","than","set","value","equal","equals",
        "il","lo","la","le","gli","un","una","è","sono","di","da","in","su","per","con","e","o","deve","devono","pari","valore","vale","fino","circa","about","approximately","up","only","just","ca",
        "has","have","had","its","their","this","that","these","those","it","not","no","as","from","every","each"
    };

    private static readonly HashSet<string> TokenStop = new(StringComparer.OrdinalIgnoreCase) { "the","a","an","is","are","to","of","be","see","il","la","lo","un","una","di","e" };

    // Numbers that name a place in a document or a version, not a measured value.
    private static readonly HashSet<string> KeyIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "section","chapter","page","table","figure","step","item","article","clause","version","revision","release","rev","line","slide","sheet","row","column","note","paragraph","annex","appendix",
        "sezione","capitolo","pagina","tabella","figura","passo","punto","articolo","clausola","versione","revisione","riga","colonna","nota","paragrafo","allegato","year","anno","date","data","iso","din","rfc","no","nr","number"
    };

    private static readonly Regex Condition = new(@"\b(if|when|while|unless|at|above|below|under|over|during|se|quando|mentre|sopra|sotto|durante)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record Fact(string Key, string Display, string Dimension, double? Number, string? Token, string ValueText, KnowledgeHit Source, string Sentence);

    private static readonly Dictionary<string, (string Dimension, double Factor)> UnitTable = BuildUnits();

    private static Dictionary<string, (string, double)> BuildUnits()
    {
        var d = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        void Add(string dim, params (string unit, double factor)[] items) { foreach ((string u, double f) in items) d[u] = (dim, f); }
        Add("time", ("ms", 0.001), ("s", 1), ("sec", 1), ("secs", 1), ("second", 1), ("seconds", 1), ("secondi", 1), ("min", 60), ("mins", 60), ("minute", 60), ("minutes", 60), ("minuti", 60),
            ("h", 3600), ("hr", 3600), ("hrs", 3600), ("hour", 3600), ("hours", 3600), ("ora", 3600), ("ore", 3600), ("d", 86400), ("day", 86400), ("days", 86400), ("giorno", 86400), ("giorni", 86400),
            ("week", 604800), ("weeks", 604800), ("settimana", 604800), ("settimane", 604800));
        Add("months", ("month", 1), ("months", 1), ("mese", 1), ("mesi", 1), ("year", 12), ("years", 12), ("anno", 12), ("anni", 12));
        Add("voltage", ("mV", 0.001), ("V", 1), ("kV", 1000));
        Add("current", ("mA", 0.001), ("A", 1), ("kA", 1000));
        Add("power", ("mW", 0.001), ("W", 1), ("kW", 1000), ("MW", 1e6));
        Add("length", ("mm", 0.001), ("cm", 0.01), ("m", 1), ("km", 1000));
        Add("mass", ("mg", 1e-6), ("g", 0.001), ("kg", 1), ("t", 1000));
        Add("data", ("B", 1), ("KB", 1024), ("MB", 1048576), ("GB", 1073741824), ("TB", 1099511627776));
        Add("frequency", ("Hz", 1), ("kHz", 1000), ("MHz", 1e6), ("GHz", 1e9));
        Add("percent", ("%", 1));
        Add("temperature", ("°C", 1));
        Add("pressure", ("Pa", 1), ("kPa", 1000), ("MPa", 1e6), ("bar", 1e5), ("mbar", 100));
        Add("resistance", ("Ω", 1), ("kΩ", 1000), ("ohm", 1));
        return d;
    }

    private static (string Dimension, double Factor) NormalizeUnit(string unit) =>
        string.IsNullOrEmpty(unit) ? ("", 1) : UnitTable.TryGetValue(unit, out var u) ? u : ("u:" + unit.ToLowerInvariant(), 1);

    private static bool IsRecognisedUnit(string unit) => !string.IsNullOrEmpty(unit) && UnitTable.ContainsKey(unit);

    public static double? ParseNumber(string text)
    {
        string t = text.Trim().Replace(" ", string.Empty);
        if (t.Length == 0) return null;
        if (t.Contains(',') && t.Contains('.'))
        {
            bool commaIsDecimal = t.LastIndexOf(',') > t.LastIndexOf('.');
            t = commaIsDecimal ? t.Replace(".", string.Empty).Replace(',', '.') : t.Replace(",", string.Empty);
        }
        else if (t.Contains(','))
            t = Regex.IsMatch(t, @"^-?\d{1,3}(,\d{3})+$") ? t.Replace(",", string.Empty) : t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            foreach (string s in Regex.Split(trimmed, @"(?<=[.!?;])\s+(?=[A-ZÀ-Ý0-9])")) if (s.Trim().Length > 0) yield return s.Trim();
        }
    }

    private static List<Fact> ExtractFacts(KnowledgeHit hit)
    {
        var facts = new List<Fact>();
        foreach (string sentence in Sentences(hit.Text))
        {
            var numberMatches = NumberWithUnit.Matches(sentence).Cast<Match>().Where(m => ParseNumber(m.Groups["num"].Value) != null).ToList();

            // 1) identifier-keyed: "MAX_TORQUE = 120 Nm", "Parameter ABC_123 is 5 V".
            var idSpans = new List<(int start, int end)>();
            foreach (Match m in IdentifierAssignment.Matches(sentence))
            {
                string id = m.Groups["id"].Value;
                if (!id.Any(char.IsDigit) && !id.Contains('_')) continue;                  // "e-mail", "well-known"
                if (Regex.IsMatch(id, @"^[A-Za-z]+\.[A-Za-z]{1,4}$")) continue;            // file names
                string valText = m.Groups["val"].Value.Trim();
                Match nm = NumberWithUnit.Match(valText);
                if (nm.Success && nm.Index == 0 && ParseNumber(nm.Groups["num"].Value) is double num)
                {
                    string unit = nm.Groups["unit"].Value;
                    if (unit.Length > 0 && !IsRecognisedUnit(unit)) unit = string.Empty;   // trailing word, not a unit
                    var (dim, factor) = NormalizeUnit(unit);
                    facts.Add(new Fact("id:" + id.ToLowerInvariant(), id, dim, num * factor, null, (nm.Groups["num"].Value + " " + unit).Trim(), hit, sentence));
                }
                else if (valText.Length > 0 && !TokenStop.Contains(valText) && !char.IsDigit(valText[0]))
                    facts.Add(new Fact("id:" + id.ToLowerInvariant(), id, "token", null, valText.ToLowerInvariant(), valText, hit, sentence));
                idSpans.Add((m.Index, m.Index + m.Length));
            }

            // 2) word-keyed: only plain single-value statements ("The warranty period is 24 months.").
            if (numberMatches.Count != 1 || Condition.IsMatch(sentence)) continue;
            Match one = numberMatches[0];
            if (idSpans.Any(s => one.Index >= s.start && one.Index < s.end)) continue;
            string unitText = one.Groups["unit"].Value;
            if (unitText.Length > 0 && !IsRecognisedUnit(unitText)) unitText = string.Empty;
            string before = sentence[..one.Index];
            var words = Regex.Matches(before, @"[\p{L}][\p{L}\p{N}]*").Select(w => w.Value.ToLowerInvariant()).ToList();
            var keyWords = new List<string>();
            for (int i = words.Count - 1; i >= 0 && keyWords.Count < 3; i--)
            {
                if (KeyStop.Contains(words[i])) { if (keyWords.Count > 0) break; continue; }
                keyWords.Add(words[i]);
            }
            if (keyWords.Count == 0 || keyWords.Any(w => KeyIgnore.Contains(w))) continue;
            var (dimension, f) = NormalizeUnit(unitText);
            if (keyWords.Count < 2 && dimension.Length == 0) continue;                     // one loose word and no unit is too weak
            double? value = ParseNumber(one.Groups["num"].Value);
            if (value == null) continue;
            string display = string.Join(" ", Enumerable.Reverse(keyWords));
            facts.Add(new Fact("w:" + string.Join(" ", keyWords.OrderBy(w => w, StringComparer.Ordinal)) + "|" + dimension, display, dimension, value * f, null,
                (one.Groups["num"].Value + " " + unitText).Trim(), hit, sentence));
        }
        return facts;
    }

    public static DocumentChatPlan Contradictions(KnowledgeIndex index, IReadOnlyCollection<string> scope, string? topic)
    {
        IReadOnlyList<KnowledgeHit> chunks = string.IsNullOrWhiteSpace(topic)
            ? index.ScanChunks(scope, 5000)
            : index.Search(topic, KnowledgeSearchMode.Auto, 40, scope);
        if (chunks.Count < 2)
            return NotEnough(string.IsNullOrWhiteSpace(topic)
                ? "Fewer than two indexed passages are available, so there is nothing to compare."
                : "Fewer than two passages match \"" + topic + "\" in the selected folders, so there is nothing to compare.");

        var facts = chunks.SelectMany(ExtractFacts).ToList();
        if (facts.Count < 2)
            return NotEnough("The passages contain no comparable values (numbers with units, or identifiers with values), so no contradiction can be checked from them.");

        var findings = new List<(Fact First, Fact Second)>();
        foreach (var group in facts.GroupBy(f => f.Key))
        {
            var distinct = new List<Fact>();
            foreach (Fact f in group)
                if (!distinct.Any(d => SameValue(d, f))) distinct.Add(f);
            for (int i = 0; i < distinct.Count; i++)
                for (int j = i + 1; j < distinct.Count; j++)
                    if (distinct[i].Dimension == distinct[j].Dimension && !SamePlace(distinct[i].Source, distinct[j].Source))
                        findings.Add((distinct[i], distinct[j]));
        }

        var citations = new List<string>();
        var sb = new StringBuilder();
        if (findings.Count == 0)
        {
            sb.Append("No contradictions found by the numeric and identifier checks across ").Append(chunks.Count).Append(" passages (")
              .Append(facts.Count).Append(" comparable values). This does not prove the documents agree: statements that need reading to compare are not covered by these rules.");
            return new(DocumentChatKind.Deterministic, sb.ToString(), Array.Empty<string>(), Array.Empty<KnowledgeHit>());
        }
        sb.Append("Possible contradictions (deterministic rules on numbers, units and identifier values; no language model was used):\n");
        int n = 0;
        foreach ((Fact first, Fact second) in findings.Take(15))
        {
            n++;
            sb.Append('\n').Append(n).Append(". ").Append(first.Display).Append(first.Dimension is "" or "token" ? string.Empty : " (" + first.Dimension.Replace("u:", string.Empty) + ")").Append('\n');
            foreach (Fact f in new[] { first, second })
            {
                sb.Append("   - ").Append(f.ValueText).Append(" [").Append(f.Source.Citation).Append("] ").Append(Snip(f.Sentence)).Append('\n');
                citations.Add(f.Source.Citation);
            }
        }
        if (findings.Count > 15) sb.Append("\n... and ").Append(findings.Count - 15).Append(" more candidates.\n");
        sb.Append("\nThese are candidates, not verdicts: a condition, version or scope that the rules cannot see may explain a difference.");
        return new(DocumentChatKind.Deterministic, sb.ToString(), citations.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), findings.Take(15).SelectMany(f => new[] { f.First.Source, f.Second.Source }).DistinctBy(h => h.ChunkId).ToList());
    }

    private static bool SamePlace(KnowledgeHit a, KnowledgeHit b) => a.ChunkId == b.ChunkId;

    private static bool SameValue(Fact a, Fact b)
    {
        if (a.Number.HasValue && b.Number.HasValue)
        {
            double scale = Math.Max(Math.Abs(a.Number.Value), Math.Abs(b.Number.Value));
            return a.Dimension == b.Dimension && Math.Abs(a.Number.Value - b.Number.Value) <= 1e-9 * Math.Max(1, scale);
        }
        return string.Equals(a.Token, b.Token, StringComparison.OrdinalIgnoreCase) && a.Number == b.Number;
    }
}
