using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PaRuX.Server.Knowledge;

// ------------------------------------------------------------------------------------------------------------------ Markdown
internal sealed class MarkdownExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".md", ".markdown", ".mdx", ".mkd");
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public override string Name => "markdown";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        var stack = new HeadingStack();
        bool inFence = false;
        char fenceChar = '`';
        string? pending = lines.ReadLine();

        if (pending?.Trim() == "---")
        {
            var front = new List<string>();
            string? l;
            bool closed = false;
            while ((l = lines.ReadLine()) != null)
            {
                string t = l.Trim();
                if (t == "---" || t == "...") { closed = true; break; }
                front.Add(l);
                if (front.Count > 500) break;
            }
            if (closed)
            {
                sink.Section("front matter");
                foreach (string f in front) if (f.Trim().Length > 0) sink.Line(f);
                pending = lines.ReadLine();
            }
            else
            {
                // Not front matter after all (a horizontal rule): replay what was read.
                sink.Line("---");
                foreach (string f in front) sink.Line(f);
                pending = null;
            }
        }

        for (string? line = pending; line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            string trimmed = line.TrimStart();
            if (inFence)
            {
                sink.Line(line);
                if (trimmed.Length >= 3 && trimmed[0] == fenceChar && trimmed.TrimStart(fenceChar).Trim().Length == 0 && trimmed.StartsWith(new string(fenceChar, 3)))
                {
                    inFence = false;
                    sink.Section(stack.Path);
                }
                continue;
            }
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = true;
                fenceChar = trimmed[0];
                string lang = trimmed.TrimStart(fenceChar).Trim();
                int space = lang.IndexOf(' ');
                if (space > 0) lang = lang[..space];
                string label = lang.Length > 0 ? "code (" + lang + ")" : "code";
                sink.Section(stack.Path == null ? label : stack.Path + " > " + label);
                sink.Line(line);
                continue;
            }
            Match m = Heading.Match(line);
            if (m.Success && m.Groups[2].Length > 0)
            {
                string text = m.Groups[2].Value;
                sink.Section(stack.Push(m.Groups[1].Length, text));
                sink.Line(text);
                continue;
            }
            if (line.Trim().Length > 0) sink.Line(line);
        }
    }
}

// ------------------------------------------------------------------------------------------------------------------ HTML / XHTML
internal sealed class HtmlExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".html", ".htm", ".xhtml", ".xht");
    private static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "template", "svg", "math" };
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "ul", "ol", "tr", "table", "section", "article", "header", "footer", "blockquote", "pre", "dd", "dt",
        "dl", "form", "hr", "tbody", "thead", "tfoot", "figure", "figcaption", "caption", "nav", "aside", "main", "address", "body", "html"
    };
    private static readonly Regex Spaces = new(@"[ \t\r\n\u00a0]+", RegexOptions.Compiled);

    public override string Name => "html";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader != null) ReadHtml(reader, sink, null);
    }

    /// <summary>Streaming tag stripper: scripts/styles/comments are dropped, headings become the section path, table cells are joined by " | ".</summary>
    internal static void ReadHtml(TextReader r, ExtractionSink sink, string? baseHeading)
    {
        var stack = new HeadingStack();
        var cur = new StringBuilder();
        int headingLevel = 0, counter = 0;
        sink.Section(baseHeading);

        string Join(string path) => baseHeading == null ? path : baseHeading + " > " + path;
        string Clean() => Spaces.Replace(WebUtility.HtmlDecode(cur.ToString()), " ").Trim().TrimEnd('|').TrimEnd();

        void EmitLine()
        {
            string t = Clean();
            cur.Clear();
            if (t.Length > 0) sink.Line(t);
        }

        void EndHeading(int level)
        {
            string t = Clean();
            cur.Clear();
            headingLevel = 0;
            if (t.Length == 0) return;
            sink.Section(Join(stack.Push(level, t)));
            sink.Line(t);
        }

        int c;
        while ((c = r.Read()) >= 0)
        {
            if ((++counter & 0x1FFF) == 0) sink.Ct.ThrowIfCancellationRequested();
            if (sink.Exhausted) return;
            if (c != '<') { cur.Append((char)c); if (cur.Length > sink.Limits.SegmentCharacters && headingLevel == 0) EmitLine(); continue; }

            int next = r.Peek();
            if (next == '!')
            {
                r.Read();
                if (r.Peek() == '-')
                {
                    // comment: skip to "-->"
                    int dashes = 0;
                    while ((c = r.Read()) >= 0)
                    {
                        if (c == '-') dashes++;
                        else if (c == '>' && dashes >= 2) break;
                        else dashes = 0;
                    }
                }
                else while ((c = r.Read()) >= 0 && c != '>') { }
                continue;
            }
            if (next == '?') { while ((c = r.Read()) >= 0 && c != '>') { } continue; }
            if (next < 0 || !(char.IsLetter((char)next) || next == '/')) { cur.Append('<'); continue; }

            var tag = new StringBuilder();
            char quote = '\0';
            while ((c = r.Read()) >= 0)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = (char)c;
                else if (c == '>') break;
                if (tag.Length < 4096) tag.Append((char)c);
            }
            string body = tag.ToString();
            bool closing = body.StartsWith('/');
            int start = closing ? 1 : 0, end = start;
            while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == ':' || body[end] == '-')) end++;
            string name = body[start..end].ToLowerInvariant();

            if (!closing && Raw.Contains(name) && !body.EndsWith('/'))
            {
                SkipRaw(r, name, sink);
                continue;
            }
            if (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6')
            {
                if (closing) { if (headingLevel > 0) EndHeading(headingLevel); }
                else { EmitLine(); headingLevel = name[1] - '0'; }
            }
            else if (name == "title")
            {
                if (closing) { if (headingLevel > 0) EndHeading(headingLevel); }
                else { EmitLine(); headingLevel = 1; }
            }
            else if (closing && (name == "td" || name == "th")) cur.Append(" | ");
            else if (Blocks.Contains(name) && headingLevel == 0) EmitLine();
            else if (name == "br") cur.Append(' ');
        }
        EmitLine();
    }

    private static void SkipRaw(TextReader r, string name, ExtractionSink sink)
    {
        string close = "</" + name;
        int matched = 0, c, counter = 0;
        while ((c = r.Read()) >= 0)
        {
            if ((++counter & 0xFFFF) == 0) sink.Ct.ThrowIfCancellationRequested();
            char ch = char.ToLowerInvariant((char)c);
            if (ch == close[matched]) matched++;
            else matched = ch == close[0] ? 1 : 0;
            if (matched == close.Length) break;
        }
        while ((c = r.Read()) >= 0 && c != '>') { }
    }
}

// ------------------------------------------------------------------------------------------------------------------ generic XML
internal sealed class XmlExtractor : StreamExtractor
{
    // Automotive formats (ARXML, ODX, CDD, A2L ...) are deliberately not listed: they have dedicated extractors.
    private static readonly IReadOnlySet<string> Exts = Set(".xml", ".xsd", ".xsl", ".xslt", ".svg", ".rss", ".atom", ".kml", ".gpx",
        ".config", ".resx", ".wsdl", ".plist", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".nuspec", ".xaml");

    public override string Name => "xml";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        XmlReaderSettings settings = SecureSettings();
        using XmlReader xr = XmlReader.Create(stream, settings);
        var names = new List<string>();
        while (xr.Read() && !sink.Exhausted)
        {
            sink.Ct.ThrowIfCancellationRequested();
            switch (xr.NodeType)
            {
                case XmlNodeType.Element:
                    if (names.Count > xr.Depth) names.RemoveRange(xr.Depth, names.Count - xr.Depth);
                    names.Add(xr.LocalName);
                    if (xr.HasAttributes)
                    {
                        string path = "/" + string.Join("/", names);
                        string heading = Heading(names);
                        int shown = 0;
                        while (xr.MoveToNextAttribute() && shown++ < 32)
                            if (xr.Value.Length > 0 && xr.Prefix != "xmlns" && xr.Name != "xmlns")
                                sink.Leaf(heading, path + "/@" + xr.LocalName, xr.Value);
                        xr.MoveToElement();
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    string text = xr.Value.Trim();
                    if (text.Length > 0 && names.Count > 0) sink.Leaf(Heading(names), "/" + string.Join("/", names.Take(Math.Max(1, xr.Depth))), text);
                    break;
            }
        }
    }

    private static string Heading(List<string> names) => string.Join("/", names.Take(3));

    internal static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false
    };
}

// ------------------------------------------------------------------------------------------------------------------ RTF
internal sealed class RtfExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".rtf");
    private static readonly HashSet<string> SkipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping", "datastore", "latentstyles",
        "listtable", "listoverridetable", "rsidtbl", "generator", "header", "footer", "fldinst", "xmlnstbl", "mmath", "blipuid"
    };

    public override string Name => "rtf";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi = Encoding.GetEncoding(1252);
        stream.Position = 0;
        var head = new byte[5];
        if (stream.Read(head, 0, 5) < 5 || Encoding.ASCII.GetString(head) != "{\\rtf") { sink.Note = "Not an RTF document."; return; }
        stream.Position = 0;
        using var reader = new StreamReader(stream, ansi, false, 64 * 1024, leaveOpen: true);

        var cur = new StringBuilder();
        var skipStack = new Stack<bool>();
        bool skipping = false, first = false;
        int skipChars = 0, uc = 1, c, counter = 0;

        void Emit()
        {
            string t = cur.ToString().Trim();
            cur.Clear();
            if (t.Length > 0) sink.Line(t);
        }

        while ((c = reader.Read()) >= 0 && !sink.Exhausted)
        {
            if ((++counter & 0x1FFF) == 0) sink.Ct.ThrowIfCancellationRequested();
            switch (c)
            {
                case '{': skipStack.Push(skipping); first = true; continue;
                case '}': if (skipStack.Count > 0) skipping = skipStack.Pop(); first = false; continue;
                case '\r': case '\n': continue;
                case '\\': break;
                default:
                    first = false;
                    if (skipChars > 0) { skipChars--; continue; }
                    if (!skipping) cur.Append((char)c);
                    continue;
            }

            int n = reader.Read();
            if (n < 0) break;
            if (!char.IsLetter((char)n))
            {
                switch ((char)n)
                {
                    case '*': if (first) skipping = true; break;
                    case '\'':
                        int h1 = reader.Read(), h2 = reader.Read();
                        if (h1 >= 0 && h2 >= 0 && int.TryParse(new string(new[] { (char)h1, (char)h2 }), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            if (skipChars > 0) skipChars--;
                            else if (!skipping) cur.Append(ansi.GetString(new[] { (byte)code }));
                        }
                        break;
                    case '\\': case '{': case '}': if (!skipping) cur.Append((char)n); break;
                    case '~': if (!skipping) cur.Append(' '); break;
                    case '_': if (!skipping) cur.Append('-'); break;
                }
                first = false;
                continue;
            }

            var word = new StringBuilder().Append((char)n);
            while (reader.Peek() >= 0 && char.IsLetter((char)reader.Peek()) && word.Length < 32) word.Append((char)reader.Read());
            var number = new StringBuilder();
            if (reader.Peek() == '-') number.Append((char)reader.Read());
            while (reader.Peek() >= 0 && char.IsDigit((char)reader.Peek()) && number.Length < 10) number.Append((char)reader.Read());
            if (reader.Peek() == ' ') reader.Read();
            string w = word.ToString();
            int.TryParse(number.ToString(), out int param);

            if (first && SkipDestinations.Contains(w)) skipping = true;
            first = false;
            if (skipping) continue;
            switch (w)
            {
                case "par": case "line": case "sect": case "page": case "row": Emit(); break;
                case "tab": case "cell": cur.Append('\t'); break;
                case "emdash": cur.Append('\u2014'); break;
                case "endash": cur.Append('\u2013'); break;
                case "bullet": cur.Append('\u2022'); break;
                case "lquote": cur.Append('\u2018'); break;
                case "rquote": cur.Append('\u2019'); break;
                case "ldblquote": cur.Append('\u201C'); break;
                case "rdblquote": cur.Append('\u201D'); break;
                case "uc": uc = Math.Max(0, param); break;
                case "u":
                    cur.Append((char)(short)param);
                    skipChars = uc;
                    break;
            }
        }
        Emit();
    }
}

// ------------------------------------------------------------------------------------------------------------------ LaTeX
internal sealed class LatexExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".tex", ".latex");
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Sectioning = new(@"\\(?<k>part|chapter|section|subsection|subsubsection|paragraph|subparagraph)\*?(?:\[[^\]]*\])?\{(?<t>[^}]*)\}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Comment = new(@"(?<!\\)%.*$", RegexOptions.Compiled, Timeout);
    private static readonly Regex Preamble = new(@"^\s*\\(usepackage|documentclass|newcommand|renewcommand|def|setlength|input|include|bibliographystyle|geometry|pagestyle)\b", RegexOptions.Compiled, Timeout);
    private static readonly Regex Drop = new(@"\\(?:begin|end)\{[^}]*\}|\\(?:cite|ref|label|eqref|pageref)\*?(?:\[[^\]]*\])*\{[^}]*\}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Commands = new(@"\\[a-zA-Z]+\*?(?:\[[^\]]*\])*", RegexOptions.Compiled, Timeout);

    public override string Name => "latex";
    protected override IReadOnlySet<string> Extensions => Exts;

    private static int Level(string kind) => kind switch
    {
        "part" => 1, "chapter" => 2, "section" => 3, "subsection" => 4, "subsubsection" => 5, "paragraph" => 6, _ => 7
    };

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        var stack = new HeadingStack();
        for (string? raw = lines.ReadLine(); raw != null && !sink.Exhausted; raw = lines.ReadLine())
        {
            string line = Comment.Replace(raw, "");
            if (line.Trim().Length == 0 || Preamble.IsMatch(line)) continue;
            Match m = Sectioning.Match(line);
            if (m.Success)
            {
                string title = Commands.Replace(m.Groups["t"].Value, "").Trim();
                if (title.Length > 0)
                {
                    sink.Section(stack.Push(Level(m.Groups["k"].Value), title));
                    sink.Line(title);
                    line = line.Remove(m.Index, m.Length);
                }
            }
            string text = Commands.Replace(Drop.Replace(line, ""), "").Replace("{", "").Replace("}", "").Trim();
            if (text.Length > 0) sink.Line(text);
        }
    }
}
