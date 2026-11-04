using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

// ------------------------------------------------------------------------------------------------------------------ JSON (streamed)
internal sealed class JsonExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".json", ".jsonc", ".geojson", ".jsonl", ".ndjson", ".webmanifest");
    private const int MaxTokenBuffer = 64 * 1024 * 1024;

    public override string Name => "json";
    protected override IReadOnlySet<string> Extensions => Exts;

    private sealed class Frame { public bool Arr; public int Index = -1; public string? Key; }

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        Stream? input = sink.OpenUtf8(stream);
        if (input == null) return;
        bool lines = fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase);
        var options = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 256 };
        var state = new JsonReaderState(options);
        var frames = new List<Frame>();
        byte[] buffer = new byte[64 * 1024];
        int leftover = 0;
        int lineIndex = 0;

        while (!sink.Exhausted)
        {
            sink.Ct.ThrowIfCancellationRequested();
            int n = input.Read(buffer, leftover, buffer.Length - leftover);
            int total = leftover + n;
            bool final = n == 0;
            int consumed = 0;
            if (lines)
            {
                // JSON Lines: one document per line, parsed independently.
                while (true)
                {
                    int nl = buffer.AsSpan(consumed, total - consumed).IndexOf((byte)'\n');
                    if (nl < 0 && !(final && total > consumed)) break;
                    int len = nl < 0 ? total - consumed : nl;
                    var slice = buffer.AsSpan(consumed, len);
                    if (slice.Trim((byte)'\r').Trim((byte)' ').Length > 0)
                    {
                        frames.Clear();
                        lineIndex++;
                        var r = new Utf8JsonReader(slice, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
                        var rootFrame = new Frame { Arr = true, Index = lineIndex - 2 };
                        frames.Add(rootFrame);
                        while (r.Read()) Token(ref r, frames, sink);
                    }
                    consumed += len + (nl < 0 ? 0 : 1);
                    if (sink.Exhausted) break;
                }
            }
            else
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(0, total), final, state);
                while (reader.Read()) Token(ref reader, frames, sink);
                state = reader.CurrentState;
                consumed = (int)reader.BytesConsumed;
            }
            leftover = total - consumed;
            if (final) break;
            if (leftover == buffer.Length)
            {
                if (buffer.Length >= MaxTokenBuffer) throw new InvalidDataException("Single JSON token is too large.");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            if (leftover > 0 && consumed > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
        }
    }

    private static void Begin(List<Frame> frames)
    {
        if (frames.Count > 0 && frames[^1].Arr) frames[^1].Index++;
    }

    private static void Token(ref Utf8JsonReader r, List<Frame> frames, ExtractionSink sink)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject: Begin(frames); frames.Add(new Frame()); break;
            case JsonTokenType.StartArray: Begin(frames); frames.Add(new Frame { Arr = true }); break;
            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray: if (frames.Count > 0) frames.RemoveAt(frames.Count - 1); break;
            case JsonTokenType.PropertyName:
                string key = r.GetString() ?? "";
                frames[^1].Key = key.Length > 100 ? key[..100] : key;
                break;
            case JsonTokenType.String:
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
                Begin(frames);
                string value = r.TokenType switch
                {
                    JsonTokenType.String => r.GetString() ?? "",
                    JsonTokenType.Number => Encoding.UTF8.GetString(r.ValueSpan),
                    JsonTokenType.True => "true",
                    _ => "false"
                };
                if (value.Length > 0) sink.Leaf(HeadingOf(frames), PathOf(frames), value);
                break;
            case JsonTokenType.Null: Begin(frames); break;
        }
    }

    private static string PathOf(List<Frame> frames)
    {
        var sb = new StringBuilder();
        foreach (Frame f in frames)
        {
            if (f.Arr) { if (f.Index >= 0) sb.Append('[').Append(f.Index).Append(']'); }
            else if (f.Key != null) { if (sb.Length > 0) sb.Append('.'); sb.Append(f.Key); }
        }
        return sb.Length == 0 ? "$" : sb.ToString();
    }

    private static string HeadingOf(List<Frame> frames)
    {
        var keys = frames.Where(f => !f.Arr && f.Key != null).Select(f => f.Key!).Take(2).ToList();
        return keys.Count == 0 ? "$" : string.Join(".", keys);
    }
}

// ------------------------------------------------------------------------------------------------------------------ Jupyter
internal sealed class NotebookExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".ipynb");
    private const long MaxBytes = 128L * 1024 * 1024;

    public override string Name => "ipynb";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        if (stream.Length > MaxBytes) { sink.Note = "Notebook is larger than the 128 MB parsing limit; indexed by name and metadata only."; return; }
        Stream? input = sink.OpenUtf8(stream);
        if (input == null) return;
        using JsonDocument doc = JsonDocument.Parse(input, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        if (!doc.RootElement.TryGetProperty("cells", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
        {
            sink.Note = "Notebook has no cells array (unsupported nbformat).";
            return;
        }
        int index = 0;
        foreach (JsonElement cell in cells.EnumerateArray())
        {
            sink.Ct.ThrowIfCancellationRequested();
            if (sink.Exhausted) return;
            index++;
            string type = cell.TryGetProperty("cell_type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "cell" : "cell";
            sink.Section("cell " + index + " (" + type + ")");
            if (cell.TryGetProperty("source", out JsonElement src)) foreach (string l in Text(src)) sink.Line(l);
            if (type == "code" && cell.TryGetProperty("outputs", out JsonElement outs) && outs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement o in outs.EnumerateArray())
                {
                    string kind = o.TryGetProperty("output_type", out JsonElement k) ? k.GetString() ?? "" : "";
                    IEnumerable<string> text = Enumerable.Empty<string>();
                    if (kind == "stream" && o.TryGetProperty("text", out JsonElement st)) text = Text(st);
                    else if (o.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("text/plain", out JsonElement tp)) text = Text(tp);
                    else if (kind == "error") text = new[] { (o.TryGetProperty("ename", out JsonElement en) ? en.GetString() : "error") + ": " + (o.TryGetProperty("evalue", out JsonElement ev) ? ev.GetString() : "") };
                    bool first = true;
                    foreach (string l in text)
                    {
                        if (first) { sink.Line("[output]"); first = false; }
                        sink.Line(l);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Text(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return (e.GetString() ?? "").Split('\n').Select(l => l.TrimEnd('\r'));
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).SelectMany(x => (x.GetString() ?? "").Split('\n')).Select(l => l.TrimEnd('\r'));
        return Enumerable.Empty<string>();
    }
}

// ------------------------------------------------------------------------------------------------------------------ YAML (key paths, no parser dependency)
internal sealed class YamlExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".yaml", ".yml");
    private static readonly Regex KeyValue = new(@"^(?<k>""[^""]*""|'[^']*'|[^\s#'""\[\]{}:,-][^:#]*?|-[^\s:][^:#]*?)\s*:(?:\s+(?<v>.*))?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public override string Name => "yaml";
    protected override IReadOnlySet<string> Extensions => Exts;

    private sealed record YFrame(int Indent, string Key, bool Item);

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        var stack = new List<YFrame>();
        int blockIndent = -1;
        string blockPath = "";
        var block = new StringBuilder();

        string Path() { var sb = new StringBuilder(); foreach (YFrame f in stack) { if (f.Item) sb.Append("[]"); else { if (sb.Length > 0) sb.Append('.'); sb.Append(f.Key); } } return sb.ToString(); }
        string Heading() => string.Join(".", stack.Where(f => !f.Item).Select(f => f.Key).Take(2));
        void FlushBlock()
        {
            if (blockIndent < 0) return;
            sink.Leaf(Heading().Length == 0 ? null : Heading(), blockPath, block.ToString().Trim());
            blockIndent = -1; block.Clear();
        }
        void Emit(string key, string value)
        {
            string p = Path();
            sink.Leaf(Heading().Length == 0 ? key : Heading(), p.Length == 0 ? key : p + "." + key, value);
        }

        for (string? line = lines.ReadLine(); line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;
            if (blockIndent >= 0)
            {
                if (trimmed.Length == 0) continue;
                if (indent > blockIndent) { if (block.Length < sink.Limits.SegmentCharacters) block.Append(trimmed).Append(' '); continue; }
                FlushBlock();
            }
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed == "---" || trimmed.StartsWith("--- ") || trimmed == "...") { stack.Clear(); continue; }

            if (trimmed == "-" || trimmed.StartsWith("- "))
            {
                while (stack.Count > 0 && stack[^1].Indent > indent) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0 && stack[^1].Item && stack[^1].Indent == indent) stack.RemoveAt(stack.Count - 1);
                string rest = trimmed.Length > 1 ? trimmed[1..].TrimStart() : "";
                int restIndent = indent + (trimmed.Length - rest.Length);
                Match km = rest.Length > 0 ? KeyValue.Match(StripComment(rest)) : Match.Empty;
                stack.Add(new YFrame(indent, "", true));
                if (km.Success) { trimmed = rest; indent = restIndent; }
                else { if (rest.Length > 0) sink.Leaf(Heading().Length == 0 ? null : Heading(), Path(), Unquote(StripComment(rest))); continue; }
            }

            Match m = KeyValue.Match(StripComment(trimmed));
            if (!m.Success)
            {
                sink.Leaf(Heading().Length == 0 ? null : Heading(), Path().Length == 0 ? "text" : Path(), trimmed);
                continue;
            }
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            string key = Unquote(m.Groups["k"].Value.Trim());
            string value = m.Groups["v"].Success ? m.Groups["v"].Value.Trim() : "";
            if (value.Length == 0) stack.Add(new YFrame(indent, key, false));
            else if (value[0] == '|' || value[0] == '>')
            {
                stack.Add(new YFrame(indent, key, false));
                blockIndent = indent; blockPath = Path(); block.Clear();
            }
            else Emit(key, Unquote(value));
        }
        FlushBlock();
    }

    private static string StripComment(string s)
    {
        int i = s.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 && !s.Contains('"') && !s.Contains('\'') ? s[..i].TrimEnd() : s.TrimEnd();
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
}

// ------------------------------------------------------------------------------------------------------------------ TOML / INI / properties
internal sealed class TomlIniExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".toml", ".ini", ".cfg", ".conf", ".properties", ".inf");

    public override string Name => "toml-ini";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        string section = "";
        for (string? line = lines.ReadLine(); line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';' || t[0] == '!') continue;
            if (t[0] == '[' && t.EndsWith(']') || t.StartsWith("[[") && t.EndsWith("]]"))
            {
                section = t.Trim('[', ']').Trim();
                sink.Section(section.Length == 0 ? null : section);
                continue;
            }
            int eq = t.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) { sink.Line(t); continue; }
            string key = t[..eq].Trim(), value = t[(eq + 1)..].Trim();
            if (value.StartsWith("\"\"\"") && !(value.Length >= 6 && value.EndsWith("\"\"\"")))
            {
                var sb = new StringBuilder(value[3..]);
                for (string? more = lines.ReadLine(); more != null && sb.Length < sink.Limits.SegmentCharacters; more = lines.ReadLine())
                {
                    int close = more.IndexOf("\"\"\"", StringComparison.Ordinal);
                    if (close >= 0) { sb.Append(' ').Append(more[..close]); break; }
                    sb.Append(' ').Append(more.Trim());
                }
                value = sb.ToString().Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            if (value.Length == 0) continue;
            sink.Leaf(section.Length == 0 ? null : section, section.Length == 0 ? key : section + "." + key, value);
        }
    }
}

// ------------------------------------------------------------------------------------------------------------------ CSV / TSV
internal sealed class CsvExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".csv", ".tsv", ".tab", ".psv");

    public override string Name => "csv";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? probe = sink.Open(stream);
        if (probe == null) return;
        char[] sample = new char[16384];
        int got = probe.Read(sample, 0, sample.Length);
        char delimiter = Sniff(new string(sample, 0, Math.Max(0, got)), Path.GetExtension(fileName).ToLowerInvariant());
        TextReader? reader = sink.Open(stream); // restart after the sniff
        if (reader == null) return;

        int cap = sink.Limits.SegmentCharacters;
        var fields = new List<string>();
        var field = new StringBuilder();
        var block = new List<string>();
        string[]? header = null;
        int row = 0, blockFirst = 0, perBlock = Math.Max(1, sink.Limits.SpreadsheetRowsPerSegment), maxCols = 1;

        void FlushBlock()
        {
            if (block.Count == 0) return;
            sink.Block(string.Join("\n", block), sheet: fileName, cellRange: "rows " + blockFirst + "-" + row);
            block.Clear(); maxCols = 1;
        }

        while (ReadRecord(reader, delimiter, fields, field, cap) && !sink.Exhausted)
        {
            sink.Ct.ThrowIfCancellationRequested();
            if (fields.Count == 1 && fields[0].Length == 0) continue;
            row++;
            if (row > sink.Limits.MaxSpreadsheetRows) { row--; sink.Truncated = true; break; }
            if (header == null)
            {
                bool numeric = fields.All(f => f.Length > 0 && double.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
                header = numeric ? fields.Select((_, i) => StructuredDocumentExtractor.ColumnName(i + 1)).ToArray() : fields.Select(f => f.Trim()).ToArray();
                if (!numeric) { blockFirst = row; block.Add("columns: " + string.Join(" | ", header)); if (block.Count >= perBlock) FlushBlock(); continue; }
            }
            if (block.Count == 0) blockFirst = row;
            var parts = new List<string>();
            for (int i = 0; i < fields.Count; i++)
            {
                string v = fields[i].Trim();
                if (v.Length == 0) continue;
                string h = i < header.Length && header[i].Length > 0 ? header[i] : "col" + (i + 1);
                parts.Add(h + "=" + v);
            }
            if (parts.Count > 0) block.Add(string.Join("; ", parts));
            maxCols = Math.Max(maxCols, fields.Count);
            if (block.Count >= perBlock) FlushBlock();
        }
        FlushBlock();
    }

    internal static char Sniff(string sample, string ext)
    {
        if (ext == ".tsv" || ext == ".tab") return '\t';
        if (ext == ".psv") return '|';
        char[] candidates = { ',', ';', '\t', '|' };
        var lines = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        foreach (char c in sample)
        {
            if (c == '"') inQ = !inQ;
            if (c == '\n' && !inQ) { lines.Add(sb.ToString()); sb.Clear(); if (lines.Count >= 20) break; }
            else sb.Append(c);
        }
        if (lines.Count == 0) lines.Add(sb.ToString());
        char best = ',';
        int bestScore = 0;
        foreach (char cand in candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, cand)).ToList();
            int first = counts[0];
            if (first == 0) continue;
            int score = counts.Count(x => x == first) * 100 + first;
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char d)
    {
        int n = 0; bool inQ = false;
        foreach (char c in line) { if (c == '"') inQ = !inQ; else if (c == d && !inQ) n++; }
        return n;
    }

    private static bool ReadRecord(TextReader r, char delim, List<string> fields, StringBuilder sb, int cap)
    {
        fields.Clear(); sb.Clear();
        bool inQ = false, any = false;
        int c;
        void Add(char ch) { if (sb.Length < cap) sb.Append(ch); }
        while ((c = r.Read()) >= 0)
        {
            any = true;
            char ch = (char)c;
            if (inQ)
            {
                if (ch == '"') { if (r.Peek() == '"') { r.Read(); Add('"'); } else inQ = false; }
                else Add(ch);
            }
            else if (ch == '"' && sb.Length == 0) inQ = true;
            else if (ch == delim) { fields.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\r') { if (r.Peek() == '\n') r.Read(); fields.Add(sb.ToString()); return true; }
            else if (ch == '\n') { fields.Add(sb.ToString()); return true; }
            else Add(ch);
        }
        if (!any) return false;
        fields.Add(sb.ToString());
        return true;
    }
}

// ------------------------------------------------------------------------------------------------------------------ SRT / VTT
internal sealed class SubtitleExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".srt", ".vtt");
    private static readonly Regex Timing = new(@"^\s*((?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*((?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{1,3})", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Markup = new(@"<[^>]+>|\{\\[^}]*\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public override string Name => "subtitles";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        var block = new StringBuilder();
        string? start = null, end = null;
        bool inCue = false;

        void Flush()
        {
            if (block.Length == 0) return;
            sink.Block(block.ToString().TrimEnd(), heading: Norm(start!) + " --> " + Norm(end!));
            block.Clear(); start = end = null;
        }

        for (string? line = lines.ReadLine(); line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            Match m = Timing.Match(line);
            if (m.Success)
            {
                inCue = true;
                if (block.Length >= sink.Limits.SegmentCharacters / 2) Flush();
                start ??= m.Groups[1].Value;
                end = m.Groups[2].Value;
                continue;
            }
            if (line.Trim().Length == 0) { inCue = false; continue; }
            if (!inCue) continue;
            string text = Markup.Replace(line, "").Trim();
            if (text.Length > 0) block.Append(text).Append('\n');
        }
        Flush();
    }

    private static string Norm(string ts)
    {
        ts = ts.Replace(',', '.');
        return ts.Count(c => c == ':') == 1 ? "00:" + ts : ts;
    }
}
