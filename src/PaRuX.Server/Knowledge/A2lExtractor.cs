using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Streaming reader for ASAP2/A2L ECU description files (measurements, characteristics, axes, conversion methods, record
/// layouts, functions, groups). Characters are tokenised on the fly (comments and strings aware); each top-level entity block
/// becomes one segment with positional fields labelled, and its nested blocks listed. Nothing beyond one block is buffered.
/// </summary>
internal sealed class A2lExtractor : IDocumentExtractor
{
    private const int MaxTokensPerBlock = 3000;

    private static readonly Dictionary<string, string[]> Labels = new(StringComparer.Ordinal)
    {
        ["MEASUREMENT"] = new[] { "data type", "conversion", "resolution", "accuracy", "lower limit", "upper limit" },
        ["CHARACTERISTIC"] = new[] { "type", "address", "record layout", "max diff", "conversion", "lower limit", "upper limit" },
        ["AXIS_PTS"] = new[] { "address", "input quantity", "record layout", "max diff", "conversion", "max axis points", "lower limit", "upper limit" },
        ["COMPU_METHOD"] = new[] { "conversion type", "format", "unit" },
        ["COMPU_VTAB"] = new[] { "conversion type", "number of value pairs" },
        ["COMPU_TAB"] = new[] { "conversion type", "number of value pairs" },
    };
    private static readonly HashSet<string> EntityKinds = new(StringComparer.Ordinal)
        { "MEASUREMENT", "CHARACTERISTIC", "AXIS_PTS", "COMPU_METHOD", "COMPU_VTAB", "COMPU_TAB", "COMPU_VTAB_RANGE", "RECORD_LAYOUT", "FUNCTION", "GROUP", "MOD_PAR", "MOD_COMMON", "UNIT", "TYPEDEF_STRUCTURE", "INSTANCE" };

    public string Name => "a2l";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".a2l", StringComparison.OrdinalIgnoreCase);

    private sealed class Block
    {
        public string Kind = "";
        public List<string> Tokens = new();
        public List<string> Nested = new();
        public bool Cut;
    }

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] sample = new byte[(int)Math.Min(65536, stream.Length)];
        int read = stream.Read(sample, 0, sample.Length);
        Encoding? encoding = TextFileExtractor.Detect(sample.AsSpan(0, read), stream.Length > read, out int bom);
        if (encoding == null) return StructuredDocumentExtractor.Failed(path, "Not a text A2L file (binary content); nothing was indexed.", Name);
        stream.Position = bom;
        using var reader = new StreamReader(stream, encoding, false, 64 * 1024);

        var sink = new AutomotiveSink(limits);
        var stack = new Stack<Block>();
        var modules = new List<string>();
        long chars = 0;
        bool truncated = false;
        int tick = 0, unbalanced = 0;
        string? pendingBegin = null;

        foreach ((string token, bool quoted) in Tokenize(reader))
        {
            if ((++tick & 4095) == 0) ct.ThrowIfCancellationRequested();
            chars += token.Length + 1;
            if (chars > limits.MaxCharacters) { truncated = true; break; }
            if (sink.Full) break;

            if (!quoted && token == "/begin") { pendingBegin = ""; continue; }
            if (pendingBegin == "")
            {
                pendingBegin = null;
                stack.Push(new Block { Kind = token });
                if (token == "MODULE") modules.Add("");
                continue;
            }
            if (!quoted && token == "/end")
            {
                pendingBegin = "end";
                continue;
            }
            if (pendingBegin == "end")
            {
                pendingBegin = null;
                if (stack.Count == 0) { unbalanced++; continue; }
                Block done = stack.Pop();
                if (done.Kind == "MODULE") { modules.RemoveAt(modules.Count - 1); continue; }
                string module = modules.Count > 0 && modules[^1].Length > 0 ? modules[^1] : "";
                if (stack.Count > 0 && EntityKinds.Contains(stack.Peek().Kind) && !EntityKinds.Contains(done.Kind))
                {
                    stack.Peek().Nested.Add(done.Kind + ": " + Compact(done));
                    continue;
                }
                if (EntityKinds.Contains(done.Kind))
                {
                    string entityName = done.Tokens.Count > 0 ? done.Tokens[0] : "(unnamed)";
                    sink.Add(Format(done, entityName, module), (module.Length > 0 ? module + " > " : "") + entityName);
                }
                continue;
            }
            if (stack.Count == 0) continue;
            Block top = stack.Peek();
            if (top.Tokens.Count < MaxTokensPerBlock) top.Tokens.Add(quoted ? "\"" + token + "\"" : token); else top.Cut = true;
            if (top.Kind == "MODULE" && top.Tokens.Count == 1) modules[^1] = token;
        }

        if (truncated) sink.MarkTruncated();
        string? note = unbalanced > 0 ? "Unbalanced /begin ... /end blocks; affected content was skipped." : null;
        if (sink.SegmentCount == 0) note = "No ASAP2 entities found in this A2L file.";
        return sink.Build(path, Name, note);
    }

    private static string Compact(Block b)
    {
        string s = string.Join(" ", b.Tokens);
        return s.Length > 300 ? s[..300] + "..." : s;
    }

    private static string Format(Block b, string name, string module)
    {
        var sb = new StringBuilder("A2L ").Append(b.Kind).Append(' ').Append(name);
        if (module.Length > 0) sb.Append(" in module ").Append(module);
        sb.AppendLine();
        int i = 1;
        if (b.Tokens.Count > 1 && b.Tokens[1].StartsWith('"')) { sb.Append("Description: ").AppendLine(b.Tokens[1].Trim('"')); i = 2; }
        if (Labels.TryGetValue(b.Kind, out string[]? labels))
        {
            for (int l = 0; l < labels.Length && i < b.Tokens.Count; l++, i++)
                sb.Append(Char.ToUpperInvariant(labels[l][0])).Append(labels[l][1..]).Append(": ").AppendLine(b.Tokens[i].Trim('"'));
        }
        if (i < b.Tokens.Count)
        {
            string rest = string.Join(" ", b.Tokens.Skip(i));
            sb.Append("Details: ").AppendLine(rest.Length > 6000 ? rest[..6000] + "..." : rest);
        }
        foreach (string n in b.Nested) sb.Append("Nested ").AppendLine(n);
        if (b.Cut) sb.AppendLine("(block truncated)");
        return sb.ToString();
    }

    /// <summary>Whitespace-separated tokens; "quoted strings" are one token; /* */ and // comments are dropped.</summary>
    private static IEnumerable<(string Token, bool Quoted)> Tokenize(StreamReader r)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = r.Read()) >= 0)
        {
            char ch = (char)c;
            if (char.IsWhiteSpace(ch)) { if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); } continue; }
            if (ch == '"')
            {
                if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); }
                var str = new StringBuilder();
                while ((c = r.Read()) >= 0 && (char)c != '"')
                {
                    if (str.Length < 4000) str.Append((char)c);
                }
                yield return (str.ToString(), true);
                continue;
            }
            if (ch == '/' && r.Peek() == '*')
            {
                if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); }
                r.Read();
                int prev = 0;
                while ((c = r.Read()) >= 0) { if (prev == '*' && c == '/') break; prev = c; }
                continue;
            }
            if (ch == '/' && r.Peek() == '/')
            {
                if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); }
                while ((c = r.Read()) >= 0 && c != '\n') { }
                continue;
            }
            if (sb.Length < 500) sb.Append(ch);
        }
        if (sb.Length > 0) yield return (sb.ToString(), false);
    }
}
