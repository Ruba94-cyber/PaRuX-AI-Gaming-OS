using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Tolerant line-based reader for Vector DBC (CAN database) files. One segment per signal (with its message, ID, sender,
/// scaling, unit, receivers, value table and comment), one per message (signal list), one per node and one for global value
/// tables. Unknown or malformed statements are skipped, never fatal. Comments/value tables come after the messages in a DBC,
/// so message/signal definitions are buffered (bounded by <see cref="ExtractionLimits.MaxCharacters"/> of source text).
/// </summary>
internal sealed class DbcExtractor : IDocumentExtractor
{
    public string Name => "dbc";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex MessageRx = new(@"^BO_\s+(\d+)\s+([^\s:]+)\s*:\s*(\d+)\s+(\S+)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex SignalRx = new(
        @"^SG_\s+([^\s:]+)(?:\s+(M|m\d+M?))?\s*:\s*(\d+)\|(\d+)@([01])([+-])\s*\(\s*([^,\s]+)\s*,\s*([^)\s]+)\s*\)\s*\[\s*([^|\s]*)\s*\|\s*([^\]\s]*)\s*\]\s*""([^""]*)""\s*(.*)$",
        RegexOptions.CultureInvariant, RegexTimeout);

    private sealed class Sig
    {
        public string Name = "";
        public string? Mux;
        public string Start = "", Length = "", Order = "", Sign = "", Factor = "", Offset = "", Min = "", Max = "", Unit = "";
        public List<string> Receivers = new();
        public List<(string Value, string Text)> Values = new();
        public string? Comment;
        public List<string> Attributes = new();
    }

    private sealed class Msg
    {
        public long RawId; public string Name = ""; public string Dlc = ""; public string Sender = "";
        public List<Sig> Signals = new();
        public string? Comment;
        public List<string> Attributes = new();
        public long Id => RawId & 0x1FFFFFFFL;
        public bool Extended => (RawId & 0x80000000L) != 0;
        public string IdText => "ID 0x" + Id.ToString("X") + (Extended ? " (extended 29-bit" : " (") + (Extended ? ", " : "") + "raw " + RawId + ")";
    }

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] sample = new byte[(int)Math.Min(65536, stream.Length)];
        int read = stream.Read(sample, 0, sample.Length);
        Encoding? encoding = TextFileExtractor.Detect(sample.AsSpan(0, read), stream.Length > read, out int bom);
        if (encoding == null)
            return StructuredDocumentExtractor.Failed(path, "Not a text DBC file (binary content); nothing was indexed.", Name);
        stream.Position = bom;
        using var reader = new StreamReader(stream, encoding, false, 64 * 1024);

        var messages = new List<Msg>();
        var byId = new Dictionary<long, Msg>();
        var nodes = new List<string>();
        var globalTables = new List<string>();
        var globalComments = new List<string>();
        string? version = null;
        Msg? current = null;
        bool truncated = false;
        long chars = 0;
        int lineNo = 0, skipped = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if ((++lineNo & 255) == 0) ct.ThrowIfCancellationRequested();
            chars += line.Length + 1;
            if (chars > limits.MaxCharacters) { truncated = true; break; }
            string t = line.Trim();
            if (t.Length == 0) { continue; }

            if (t.StartsWith("SG_ ", StringComparison.Ordinal) || t.StartsWith("SG_\t", StringComparison.Ordinal))
            {
                Match m = SafeMatch(SignalRx, t);
                if (!m.Success || current == null) { skipped++; continue; }
                var sig = new Sig
                {
                    Name = m.Groups[1].Value, Mux = m.Groups[2].Success ? m.Groups[2].Value : null,
                    Start = m.Groups[3].Value, Length = m.Groups[4].Value, Order = m.Groups[5].Value, Sign = m.Groups[6].Value,
                    Factor = m.Groups[7].Value, Offset = m.Groups[8].Value, Min = m.Groups[9].Value, Max = m.Groups[10].Value,
                    Unit = m.Groups[11].Value,
                    Receivers = m.Groups[12].Value.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(r => r != "Vector__XXX").ToList()
                };
                current.Signals.Add(sig);
                continue;
            }

            string keyword = FirstWord(t);
            switch (keyword)
            {
                case "BO_":
                {
                    Match m = SafeMatch(MessageRx, t);
                    if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id)) { current = null; skipped++; break; }
                    current = new Msg { RawId = id, Name = m.Groups[2].Value, Dlc = m.Groups[3].Value, Sender = m.Groups[4].Value };
                    messages.Add(current);
                    byId[id] = current;
                    break;
                }
                case "BU_:":
                case "BU_":
                    nodes.AddRange(t[(t.IndexOf(':') + 1)..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                    current = null;
                    break;
                case "VERSION":
                    version = Tokens(t).Skip(1).Select(x => x.Text).FirstOrDefault();
                    break;
                case "CM_":
                case "VAL_":
                case "VAL_TABLE_":
                case "BA_":
                {
                    string statement = ReadStatement(t, reader, ct, ref lineNo, ref chars);
                    ApplyStatement(keyword, statement, byId, globalTables, globalComments);
                    current = null;
                    break;
                }
                default:
                    break; // BS_, NS_, BA_DEF_*, SIG_VALTYPE_ ... are not relevant for retrieval.
            }
        }

        var sink = new AutomotiveSink(limits);
        if (version != null || globalComments.Count > 0)
            sink.Add("DBC database" + (version is { Length: > 0 } ? " version " + version : "") + "\n" + string.Join("\n", globalComments.Select(c => "Comment: " + c)), "DBC");

        var sent = nodes.ToDictionary(n => n, _ => new List<string>());
        var received = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (Msg msg in messages)
        {
            string sender = IsNoNode(msg.Sender) ? "(no sender)" : msg.Sender;
            if (!IsNoNode(msg.Sender)) { if (!sent.TryGetValue(msg.Sender, out var l)) sent[msg.Sender] = l = new List<string>(); l.Add(msg.Name); }
            var head = new StringBuilder();
            head.Append("CAN message ").Append(msg.Name).Append(" (").Append(msg.IdText).Append(", DLC ").Append(msg.Dlc).Append(") sent by ").AppendLine(sender);
            if (msg.Comment != null) head.Append("Comment: ").AppendLine(msg.Comment);
            foreach (string a in msg.Attributes) head.Append("Attribute: ").AppendLine(a);
            head.Append("Signals (").Append(msg.Signals.Count).Append("): ").Append(string.Join(", ", msg.Signals.Select(s => s.Name)));
            if (!sink.Add(head.ToString(), sender + " > " + msg.Name)) break;

            foreach (Sig s in msg.Signals)
            {
                foreach (string r in s.Receivers)
                {
                    if (!received.TryGetValue(r, out var l)) received[r] = l = new List<string>();
                    l.Add(msg.Name + "." + s.Name);
                }
                if (!sink.Add(SignalText(msg, s, sender), sender + " > " + msg.Name + " > " + s.Name)) break;
            }
            if (sink.Full) break;
        }

        if (!sink.Full)
            foreach (string node in sent.Keys.Union(received.Keys).Where(n => !IsNoNode(n)))
            {
                var sb = new StringBuilder("CAN node (ECU) ").AppendLine(node);
                if (sent.TryGetValue(node, out var s1) && s1.Count > 0) sb.Append("Sends messages: ").AppendLine(string.Join(", ", s1));
                if (received.TryGetValue(node, out var r1) && r1.Count > 0) sb.Append("Receives signals: ").AppendLine(string.Join(", ", r1));
                if (!sink.Add(sb.ToString(), node)) break;
            }

        foreach (string table in globalTables)
            if (!sink.Add(table, "Value tables")) break;

        if (truncated) sink.MarkTruncated();
        string? note = skipped > 0 ? skipped + " DBC statement(s) could not be understood and were skipped." : null;
        if (sink.SegmentCount == 0) note = "No CAN messages or signals found in this DBC file.";
        return sink.Build(path, Name, note);
    }

    private static bool IsNoNode(string n) => n is "Vector__XXX" or "";

    private static string SignalText(Msg msg, Sig s, string sender)
    {
        var sb = new StringBuilder();
        sb.Append("DBC signal ").Append(s.Name).Append(" in message ").Append(msg.Name).Append(" (").Append(msg.IdText).Append(", DLC ").Append(msg.Dlc)
          .Append(") sent by ").AppendLine(sender);
        sb.Append("Bits: start ").Append(s.Start).Append(", length ").Append(s.Length).Append(", ")
          .Append(s.Order == "1" ? "little-endian (Intel)" : "big-endian (Motorola)").Append(", ").AppendLine(s.Sign == "-" ? "signed" : "unsigned");
        sb.Append("Scaling: factor ").Append(s.Factor).Append(", offset ").Append(s.Offset).Append("; physical range [")
          .Append(s.Min).Append(" .. ").Append(s.Max).Append("]; unit \"").Append(s.Unit).AppendLine("\"");
        if (s.Mux != null) sb.Append("Multiplexing: ").AppendLine(s.Mux == "M" ? "multiplexer switch" : "multiplexed, selected by value " + s.Mux.TrimStart('m').TrimEnd('M'));
        sb.Append("Receivers: ").AppendLine(s.Receivers.Count == 0 ? "(none)" : string.Join(", ", s.Receivers));
        if (s.Values.Count > 0) sb.Append("Values: ").AppendLine(string.Join("; ", s.Values.Select(v => v.Value + " = \"" + v.Text + "\"")));
        if (s.Comment != null) sb.Append("Comment: ").AppendLine(s.Comment);
        foreach (string a in s.Attributes) sb.Append("Attribute: ").AppendLine(a);
        return sb.ToString();
    }

    private static Match SafeMatch(Regex rx, string input)
    {
        try { return rx.Match(input); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    private static string FirstWord(string t)
    {
        int i = 0;
        while (i < t.Length && !char.IsWhiteSpace(t[i])) i++;
        return t[..i];
    }

    /// <summary>Reads a possibly multi-line statement (comment text may contain line breaks) up to the terminating ';' outside quotes.</summary>
    private static string ReadStatement(string first, StreamReader reader, CancellationToken ct, ref int lineNo, ref long chars)
    {
        var sb = new StringBuilder(first);
        int extra = 0;
        while (!IsComplete(sb) && extra < 500 && sb.Length < 200_000)
        {
            string? next = reader.ReadLine();
            if (next == null) break;
            if ((++lineNo & 255) == 0) ct.ThrowIfCancellationRequested();
            chars += next.Length + 1;
            sb.Append('\n').Append(next);
            extra++;
        }
        return sb.ToString();
    }

    private static bool IsComplete(StringBuilder sb)
    {
        bool inQuote = false;
        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            if (c == '\\' && inQuote) { i++; continue; }
            if (c == '"') inQuote = !inQuote;
        }
        if (inQuote) return false;
        for (int i = sb.Length - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(sb[i])) return sb[i] == ';';
        return false;
    }

    private static List<(string Text, bool Quoted)> Tokens(string s)
    {
        var list = new List<(string, bool)>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"') { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i]); i++; }
                i++;
                list.Add((sb.ToString().Replace("\r", "").Trim(), true));
                continue;
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ';' && s[i] != '"') i++;
            list.Add((s[start..i], false));
        }
        return list;
    }

    private static void ApplyStatement(string keyword, string statement, Dictionary<long, Msg> byId, List<string> globalTables, List<string> globalComments)
    {
        var t = Tokens(statement);
        if (t.Count < 2) return;
        switch (keyword)
        {
            case "CM_":
                if (t[1].Quoted) { globalComments.Add(t[1].Text); return; }
                switch (t[1].Text)
                {
                    case "BO_" when t.Count >= 4 && long.TryParse(t[2].Text, out long id) && byId.TryGetValue(id, out Msg? m):
                        m.Comment = t[3].Text; break;
                    case "SG_" when t.Count >= 5 && long.TryParse(t[2].Text, out long sid) && byId.TryGetValue(sid, out Msg? sm):
                        Sig? s = sm.Signals.FirstOrDefault(x => x.Name == t[3].Text);
                        if (s != null) s.Comment = t[4].Text;
                        break;
                    case "BU_" when t.Count >= 4:
                        globalComments.Add("Node " + t[2].Text + ": " + t[3].Text); break;
                }
                break;
            case "VAL_" when t.Count >= 3 && long.TryParse(t[1].Text, out long vid) && byId.TryGetValue(vid, out Msg? vm):
            {
                Sig? s = vm.Signals.FirstOrDefault(x => x.Name == t[2].Text);
                if (s == null) return;
                for (int i = 3; i + 1 < t.Count; i += 2) s.Values.Add((t[i].Text, t[i + 1].Text));
                break;
            }
            case "VAL_TABLE_":
            {
                var pairs = new List<string>();
                for (int i = 2; i + 1 < t.Count; i += 2) pairs.Add(t[i].Text + " = \"" + t[i + 1].Text + "\"");
                globalTables.Add("DBC value table " + t[1].Text + ": " + string.Join("; ", pairs));
                break;
            }
            case "BA_" when t.Count >= 4 && t[1].Quoted:
            {
                string attr = t[1].Text;
                string kind = t[2].Text;
                if (kind == "BO_" && t.Count >= 5 && long.TryParse(t[3].Text, out long bid) && byId.TryGetValue(bid, out Msg? bm))
                    bm.Attributes.Add(attr + " = " + t[4].Text);
                else if (kind == "SG_" && t.Count >= 6 && long.TryParse(t[3].Text, out long gid) && byId.TryGetValue(gid, out Msg? gm))
                    gm.Signals.FirstOrDefault(x => x.Name == t[4].Text)?.Attributes.Add(attr + " = " + t[5].Text);
                break;
            }
        }
    }
}
