using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PaRuX.Server.Knowledge;

// ------------------------------------------------------------------------------------------------------------------ e-mail (.eml)
internal sealed class EmailExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".eml");
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly Regex EncodedWord = new(@"=\?([^?\s]+)\?([bBqQ])\?([^?]*)\?=", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public override string Name => "eml";
    protected override IReadOnlySet<string> Extensions => Exts;

    private sealed class Parsed
    {
        public List<(string Name, string Value)> Headers = new();
        public StringBuilder Plain = new();
        public StringBuilder Html = new();
        public List<string> Attachments = new();
    }

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        stream.Position = 0;
        int cap = (int)Math.Min(stream.Length, MaxBytes);
        byte[] data = new byte[cap];
        int read = 0, n;
        while (read < cap && (n = stream.Read(data, read, cap - read)) > 0) read += n;
        if (stream.Length > cap) sink.Truncated = true;
        string raw = Encoding.Latin1.GetString(data, 0, read);
        if (!Regex.IsMatch(raw.Length > 4096 ? raw[..4096] : raw, @"^[A-Za-z][A-Za-z0-9-]*:", RegexOptions.None, TimeSpan.FromMilliseconds(200)))
        { sink.Note = "Not an RFC 5322 message."; return; }

        var parsed = new Parsed();
        Parse(raw, 0, parsed, sink, true);

        sink.Section("headers");
        foreach (string want in new[] { "From", "To", "Cc", "Subject", "Date" })
            foreach (var h in parsed.Headers.Where(h => h.Name.Equals(want, StringComparison.OrdinalIgnoreCase)))
                sink.Line(want + ": " + DecodeWords(h.Value));
        sink.Section("body");
        string body = parsed.Plain.Length > 0 ? parsed.Plain.ToString() : "";
        foreach (string l in body.Split('\n')) if (l.Trim().Length > 0) sink.Line(l.TrimEnd('\r'));
        if (parsed.Plain.Length == 0 && parsed.Html.Length > 0)
            HtmlExtractor.ReadHtml(new StringReader(parsed.Html.ToString()), sink, "body");
        if (parsed.Attachments.Count > 0)
        {
            sink.Section("attachments");
            foreach (string a in parsed.Attachments.Take(500)) sink.Line("Attachment: " + a);
        }
    }

    private static void Parse(string text, int depth, Parsed result, ExtractionSink sink, bool top)
    {
        sink.Ct.ThrowIfCancellationRequested();
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal), skip = 4;
        int lf = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0 || (lf >= 0 && lf < split)) { split = lf; skip = 2; }
        string headerText = split < 0 ? text : text[..split];
        string body = split < 0 ? "" : text[(split + skip)..];

        var headers = new List<(string, string)>();
        foreach (string l in headerText.Split('\n'))
        {
            string line = l.TrimEnd('\r');
            if (line.Length == 0) continue;
            if ((line[0] == ' ' || line[0] == '\t') && headers.Count > 0) { var last = headers[^1]; headers[^1] = (last.Item1, last.Item2 + " " + line.Trim()); continue; }
            int colon = line.IndexOf(':');
            if (colon > 0) headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        if (top) result.Headers.AddRange(headers);
        else foreach (var h in headers.Where(h => h.Item1.Equals("Subject", StringComparison.OrdinalIgnoreCase) || h.Item1.Equals("From", StringComparison.OrdinalIgnoreCase)))
            result.Plain.AppendLine("[" + h.Item1 + ": " + DecodeWords(h.Item2) + "]");

        string H(string name) => headers.FirstOrDefault(h => h.Item1.Equals(name, StringComparison.OrdinalIgnoreCase)).Item2 ?? "";
        string ctype = H("Content-Type"), type = ctype.Split(';')[0].Trim().ToLowerInvariant();
        if (type.Length == 0) type = "text/plain";
        string disp = H("Content-Disposition");
        string? filename = Param(disp, "filename") ?? Param(ctype, "name");
        if (filename != null) filename = DecodeWords(filename);

        if (type.StartsWith("multipart/") && Param(ctype, "boundary") is string boundary && depth < 6)
        {
            foreach (string part in SplitMultipart(body, boundary)) Parse(part, depth + 1, result, sink, false);
            return;
        }
        if (type == "message/rfc822" && depth < 6) { Parse(body, depth + 1, result, sink, false); return; }

        bool attachment = filename != null || disp.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = Decode(body, H("Content-Transfer-Encoding"));
        if (attachment || !(type == "text/plain" || type == "text/html"))
        {
            result.Attachments.Add((filename ?? "(unnamed " + type + ")") + " [" + type + ", " + bytes.Length + " bytes]");
            return;
        }
        string decoded = Charset(Param(ctype, "charset")).GetString(bytes);
        (type == "text/html" ? result.Html : result.Plain).AppendLine(decoded);
    }

    private static List<string> SplitMultipart(string body, string boundary)
    {
        var parts = new List<string>();
        string delim = "--" + boundary;
        int pos = body.StartsWith(delim, StringComparison.Ordinal) ? 0 : body.IndexOf("\n" + delim, StringComparison.Ordinal) + 1;
        if (pos <= 0 && !body.StartsWith(delim, StringComparison.Ordinal)) return parts;
        while (parts.Count < 2000)
        {
            int lineEnd = body.IndexOf('\n', pos);
            if (lineEnd < 0) break;
            string delimLine = body.Substring(pos, lineEnd - pos).TrimEnd('\r');
            if (delimLine == delim + "--") break;
            int next = body.IndexOf("\n" + delim, lineEnd, StringComparison.Ordinal);
            string part = next < 0 ? body[(lineEnd + 1)..] : body.Substring(lineEnd + 1, Math.Max(0, next - lineEnd - 1));
            parts.Add(part.TrimEnd('\r'));
            if (next < 0) break;
            pos = next + 1;
        }
        return parts;
    }

    private static string? Param(string header, string name)
    {
        Match m = Regex.Match(header, @"(?:^|[;\s])" + name + @"\*?\s*=\s*(?:""(?<v>[^""]*)""|(?<v>[^;\s]+))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static Encoding Charset(string? name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return name == null ? Encoding.UTF8 : Encoding.GetEncoding(name); } catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static byte[] Decode(string body, string transfer)
    {
        transfer = transfer.Trim().ToLowerInvariant();
        if (transfer == "base64")
        {
            try { return Convert.FromBase64String(Regex.Replace(body, @"\s+", "")); } catch (FormatException) { return Array.Empty<byte>(); }
        }
        if (transfer == "quoted-printable") return QuotedPrintable(body);
        return Encoding.Latin1.GetBytes(body);
    }

    private static byte[] QuotedPrintable(string s)
    {
        var ms = new MemoryStream(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '=' && i + 1 < s.Length && s[i + 1] == '\n') { i++; continue; }
            if (c == '=' && i + 2 < s.Length && s[i + 1] == '\r' && s[i + 2] == '\n') { i += 2; continue; }
            if (c == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue;
            }
            ms.WriteByte((byte)c);
        }
        return ms.ToArray();
    }

    private static string DecodeWords(string value)
    {
        string collapsed = Regex.Replace(value, @"(\?=)\s+(=\?)", "$1$2", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return EncodedWord.Replace(collapsed, m =>
        {
            try
            {
                Encoding enc = Charset(m.Groups[1].Value);
                byte[] bytes = char.ToUpperInvariant(m.Groups[2].Value[0]) == 'B'
                    ? Convert.FromBase64String(m.Groups[3].Value)
                    : QuotedPrintable(m.Groups[3].Value.Replace('_', ' '));
                return enc.GetString(bytes);
            }
            catch (FormatException) { return m.Value; }
        });
    }
}

// ------------------------------------------------------------------------------------------------------------------ OpenDocument
internal sealed class OpenDocumentExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".odt", ".ods", ".odp", ".ott", ".ots", ".otp");
    private const string NsText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string NsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string NsDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

    public override string Name => "odf";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry entry = zip.GetEntry("content.xml") ?? throw new InvalidDataException("No content.xml.");
        long cap = Math.Clamp(sink.Limits.MaxCharacters * 8, 64L * 1024 * 1024, 1024L * 1024 * 1024);
        using var bounded = new BoundedReadStream(entry.Open(), cap);
        var settings = XmlExtractor.SecureSettings();
        settings.IgnoreWhitespace = false;
        using XmlReader xr = XmlReader.Create(bounded, settings);

        var stack = new HeadingStack();
        var para = new StringBuilder();
        var cell = new StringBuilder();
        var rowCells = new List<string>();
        var sheetBlock = new List<string>();
        string? sheetName = null;
        int paraDepth = 0, headingLevel = 0, slide = 0, tableDepth = 0, rowNo = 0, blockFirst = 0, blockLast = 0, maxCol = 1, colIndex = 0, rowsSeen = 0;
        bool inCell = false, sheetMode = false, headingOpen = false;

        void FlushSheet()
        {
            if (sheetBlock.Count == 0) return;
            sink.Block(string.Join("\n", sheetBlock), sheet: sheetName, cellRange: "A" + blockFirst + ":" + StructuredDocumentExtractor.ColumnName(maxCol) + blockLast);
            sheetBlock.Clear(); maxCol = 1;
        }

        while (xr.Read() && !sink.Exhausted)
        {
            sink.Ct.ThrowIfCancellationRequested();
            switch (xr.NodeType)
            {
                case XmlNodeType.Element:
                    string ns = xr.NamespaceURI, ln = xr.LocalName;
                    bool empty = xr.IsEmptyElement;
                    if (ns == NsText)
                    {
                        if (ln == "h") { headingOpen = true; headingLevel = int.TryParse(xr.GetAttribute("outline-level", NsText), out int lv) ? Math.Clamp(lv, 1, 10) : 1; para.Clear(); paraDepth++; }
                        else if (ln == "p") { if (paraDepth == 0) para.Clear(); paraDepth++; }
                        else if (ln == "s") para.Append(' ', int.TryParse(xr.GetAttribute("c", NsText), out int c) ? Math.Clamp(c, 1, 64) : 1);
                        else if (ln == "tab") para.Append('\t');
                        else if (ln == "line-break") para.Append(' ');
                        if ((ln == "h" || ln == "p") && empty) { paraDepth--; headingOpen = false; }
                    }
                    else if (ns == NsTable)
                    {
                        if (ln == "table")
                        {
                            tableDepth++;
                            if (sheetMode && tableDepth == 1) { sheetName = xr.GetAttribute("name", NsTable) ?? "Sheet"; rowNo = 0; }
                        }
                        else if (ln == "table-row") { rowCells.Clear(); colIndex = 0; }
                        else if (ln == "table-cell" || ln == "covered-table-cell")
                        {
                            inCell = !empty; cell.Clear(); colIndex++;
                            if (empty) { /* empty cell: no content */ }
                        }
                    }
                    else if (ns == NsDraw && ln == "page")
                    {
                        slide++;
                        sink.Section(null);
                        sink.Slide = slide;
                    }
                    else if (ln == "spreadsheet") sheetMode = true;
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (paraDepth > 0) para.Append(xr.Value);
                    break;

                case XmlNodeType.EndElement:
                    if (xr.NamespaceURI == NsText && (xr.LocalName == "h" || xr.LocalName == "p"))
                    {
                        paraDepth = Math.Max(0, paraDepth - 1);
                        if (paraDepth > 0 && !headingOpen) break;
                        string text = Regex.Replace(para.ToString(), @"[  ]{2,}", " ").Trim();
                        para.Clear();
                        bool wasHeading = headingOpen;
                        headingOpen = false;
                        if (text.Length == 0) break;
                        if (inCell) cell.Append(text).Append(' ');
                        else if (wasHeading) { sink.Section(stack.Push(headingLevel, text)); sink.Line(text); }
                        else sink.Line(text);
                    }
                    else if (xr.NamespaceURI == NsTable)
                    {
                        if (xr.LocalName == "table-cell" || xr.LocalName == "covered-table-cell")
                        {
                            inCell = false;
                            string v = cell.ToString().Trim();
                            if (v.Length > 0) { rowCells.Add(v); maxCol = Math.Max(maxCol, colIndex); }
                        }
                        else if (xr.LocalName == "table-row")
                        {
                            rowNo++;
                            if (rowCells.Count > 0)
                            {
                                string line = string.Join("\t", rowCells);
                                if (sheetMode)
                                {
                                    if (++rowsSeen > sink.Limits.MaxSpreadsheetRows) { sink.Truncated = true; FlushSheet(); return; }
                                    if (sheetBlock.Count == 0) blockFirst = rowNo;
                                    blockLast = rowNo;
                                    sheetBlock.Add(line);
                                    if (sheetBlock.Count >= sink.Limits.SpreadsheetRowsPerSegment) FlushSheet();
                                }
                                else sink.Line(line);
                            }
                        }
                        else if (xr.LocalName == "table") { if (sheetMode) FlushSheet(); tableDepth = Math.Max(0, tableDepth - 1); }
                    }
                    break;
            }
        }
        FlushSheet();
        sink.Slide = null;
    }
}

// ------------------------------------------------------------------------------------------------------------------ EPUB
internal sealed class EpubExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".epub");
    private const int MaxChapters = 5000;

    public override string Name => "epub";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 200_000) throw new InvalidDataException("Too many entries.");
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry e in zip.Entries) byName[e.FullName] = e;

        XDocument container = LoadXml(byName, "META-INF/container.xml") ?? throw new InvalidDataException("No container.xml.");
        string opfPath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("No rootfile.");
        XDocument opf = LoadXml(byName, opfPath) ?? throw new InvalidDataException("No package document.");
        string opfDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";

        var meta = opf.Descendants().Where(e => e.Name.LocalName is "title" or "creator" or "language").Take(10).ToList();
        if (meta.Count > 0)
        {
            sink.Prefix = null;
            sink.Section("metadata");
            foreach (XElement m in meta) if (m.Value.Trim().Length > 0) sink.Line(m.Name.LocalName + ": " + m.Value.Trim());
        }

        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .Where(e => e.Attribute("id") != null && e.Attribute("href") != null)
            .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.First().Attribute("href")!.Value);
        string? outer = sink.Prefix;
        long perEntryCap = Math.Clamp(sink.Limits.MaxCharacters * 8, 4L * 1024 * 1024, 256L * 1024 * 1024);
        int chapter = 0;
        foreach (XElement itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            sink.Ct.ThrowIfCancellationRequested();
            if (sink.Exhausted) break;
            if (chapter >= MaxChapters) { sink.Truncated = true; break; }
            string? id = itemref.Attribute("idref")?.Value;
            if (id == null || !manifest.TryGetValue(id, out string? href)) continue;
            string rel = Uri.UnescapeDataString(href.Split('#')[0]).Replace('\\', '/');
            string? path = ArchiveExtractor.Normalize(opfDir.Length > 0 ? opfDir + "/" + rel : rel);
            if (path == null || !byName.TryGetValue(path, out ZipArchiveEntry? entry)) continue;
            chapter++;
            sink.Prefix = (outer == null ? "" : outer + " > ") + "Chapter " + chapter;
            using var bounded = new BoundedReadStream(entry.Open(), perEntryCap);
            using var reader = new StreamReader(bounded, Encoding.UTF8, true, 64 * 1024);
            HtmlExtractor.ReadHtml(reader, sink, null);
        }
        sink.Prefix = outer;
        sink.Section(null);
    }

    private static XDocument? LoadXml(Dictionary<string, ZipArchiveEntry> byName, string path)
    {
        if (!byName.TryGetValue(path, out ZipArchiveEntry? e)) return null;
        using var bounded = new BoundedReadStream(e.Open(), 8 * 1024 * 1024);
        using XmlReader xr = XmlReader.Create(bounded, XmlExtractor.SecureSettings());
        return XDocument.Load(xr);
    }
}
