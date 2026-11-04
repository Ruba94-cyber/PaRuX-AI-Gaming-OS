using System.Text;
using System.Xml;

namespace PaRuX.Server.Knowledge;

/// <summary>Collects entity segments for the automotive extractors and enforces <see cref="ExtractionLimits"/>.</summary>
internal sealed class AutomotiveSink
{
    private readonly ExtractionLimits _limits;
    private readonly List<DocumentSegment> _segments = new();
    private long _total, _offset;

    public AutomotiveSink(ExtractionLimits limits) => _limits = limits;

    public bool Truncated { get; private set; }
    public int Entities { get; private set; }
    public int SegmentCount => _segments.Count;
    public bool Full => Truncated;

    /// <summary>Adds one entity. Text longer than the segment size is split on line boundaries and keeps the same heading.</summary>
    public bool Add(string text, string? heading)
    {
        text = text.TrimEnd();
        if (text.Length == 0) return true;
        if (Truncated) return false;
        foreach (string part in Split(text, Math.Max(200, _limits.SegmentCharacters)))
        {
            if (_total + part.Length > _limits.MaxCharacters) { Truncated = true; return false; }
            _segments.Add(new DocumentSegment(part, new DocumentPosition(Heading: heading, CharOffset: _offset)));
            _total += part.Length; _offset += part.Length + 1;
        }
        Entities++;
        return true;
    }

    public void MarkTruncated() => Truncated = true;

    public ExtractedDocument Build(string path, string extractor, string? note)
    {
        DocumentTier tier = _segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured;
        return new ExtractedDocument(path, tier, _segments, Truncated, note, extractor);
    }

    private static IEnumerable<string> Split(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var sb = new StringBuilder();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            while (line.Length > max)
            {
                if (sb.Length > 0) { yield return sb.ToString().TrimEnd(); sb.Clear(); }
                yield return line[..max];
                line = line[max..];
            }
            if (sb.Length + line.Length + 1 > max && sb.Length > 0) { yield return sb.ToString().TrimEnd(); sb.Clear(); }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0) yield return sb.ToString().TrimEnd();
    }

    public static ExtractedDocument NotWellFormed(string path, string extractor) =>
        StructuredDocumentExtractor.Failed(path, "Not well-formed " + extractor.ToUpperInvariant() + " (XML could not be parsed); nothing was indexed.", extractor);
}

/// <summary>Small bounded copy of one XML element subtree; the document itself is never loaded as a DOM.</summary>
internal sealed class XNodeLite
{
    public string Name = "";
    public string Text = "";
    public string? ShortName;
    public bool HasNamedDescendant;
    public bool Cut;
    public List<(string Key, string Value)> Attrs = new();
    public List<XNodeLite> Kids = new();

    public string? Attr(string key) => Attrs.FirstOrDefault(a => a.Key == key).Value;
}

internal static class AutomotiveXml
{
    private static readonly HashSet<string> IgnoredAttrs = new(StringComparer.Ordinal) { "UUID", "T", "S" };
    private static readonly HashSet<string> SkippedElements = new(StringComparer.Ordinal) { "ADMIN-DATA", "SDGS", "SDG", "ADMINDATA" };

    public static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CheckCharacters = false,
        CloseInput = false
    });

    private static XNodeLite Make(XmlReader r)
    {
        var n = new XNodeLite { Name = r.LocalName };
        if (r.HasAttributes)
        {
            for (int i = 0; i < r.AttributeCount && n.Attrs.Count < 6; i++)
            {
                r.MoveToAttribute(i);
                string key = r.LocalName;
                if (r.Prefix is "xmlns" or "xsi" || key == "xmlns" || IgnoredAttrs.Contains(key)) continue;
                string v = r.Value;
                n.Attrs.Add((key, v.Length > 200 ? v[..200] : v));
            }
            r.MoveToElement();
        }
        return n;
    }

    /// <summary>
    /// Consumes the element the reader is positioned on (start tag to end tag) and keeps at most <paramref name="maxNodes"/> elements.
    /// The reader is left on the element's last node, so a plain <c>Read()</c> continues after it.
    /// </summary>
    public static XNodeLite ReadBounded(XmlReader r, int maxNodes, HashSet<string> nameTags, CancellationToken ct)
    {
        XNodeLite root = Make(r);
        if (r.IsEmptyElement) return root;
        var stack = new Stack<XNodeLite>();
        stack.Push(root);
        int count = 1, tick = 0;
        while (stack.Count > 0 && r.Read())
        {
            if ((++tick & 1023) == 0) ct.ThrowIfCancellationRequested();
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                {
                    XNodeLite n = Make(r);
                    bool empty = r.IsEmptyElement;
                    if (count < maxNodes) { stack.Peek().Kids.Add(n); count++; } else stack.Peek().Cut = true;
                    if (empty) Finish(n, stack.Peek(), nameTags); else stack.Push(n);
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                {
                    XNodeLite top = stack.Peek();
                    if (top.Text.Length < 600)
                    {
                        string t = r.Value.Trim();
                        if (t.Length > 0) top.Text = top.Text.Length == 0 ? t : top.Text + " " + t;
                    }
                    break;
                }
                case XmlNodeType.EndElement:
                {
                    XNodeLite done = stack.Pop();
                    if (stack.Count > 0) Finish(done, stack.Peek(), nameTags);
                    break;
                }
            }
        }
        return root;
    }

    private static void Finish(XNodeLite done, XNodeLite parent, HashSet<string> nameTags)
    {
        if (nameTags.Contains(done.Name))
        {
            string text = done.Text;
            if (text.Length == 0 && done.Kids.Count > 0) text = done.Kids.Select(k => k.Text).FirstOrDefault(t => t.Length > 0) ?? "";
            if (text.Length > 0) { parent.ShortName ??= text; parent.HasNamedDescendant = true; }
        }
        else if (done.ShortName != null || done.HasNamedDescendant) parent.HasNamedDescendant = true;
    }

    /// <summary>First descendant (depth-first) with this element name.</summary>
    public static XNodeLite? Find(XNodeLite root, string name)
    {
        var stack = new Stack<XNodeLite>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            XNodeLite n = stack.Pop();
            if (n != root && n.Name == name) return n;
            for (int i = n.Kids.Count - 1; i >= 0; i--) stack.Push(n.Kids[i]);
        }
        return null;
    }

    public static IEnumerable<XNodeLite> FindAll(XNodeLite root, string name)
    {
        var stack = new Stack<XNodeLite>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            XNodeLite n = stack.Pop();
            if (n != root && n.Name == name) yield return n;
            for (int i = n.Kids.Count - 1; i >= 0; i--) stack.Push(n.Kids[i]);
        }
    }

    /// <summary>Readable indented text of an entity: names, values and references, exactly as written in the file.</summary>
    public static string Describe(string title, XNodeLite root, HashSet<string> nameTags, Func<XNodeLite, string?>? resolve = null)
    {
        var sb = new StringBuilder(title).AppendLine();
        if (root.Attrs.Count > 0) sb.Append("Attributes: ").AppendLine(string.Join(", ", root.Attrs.Select(a => a.Key + "=" + a.Value)));
        foreach (XNodeLite kid in root.Kids) Render(kid, sb, 0, nameTags);
        if (root.Cut) sb.AppendLine("(entity listing truncated)");
        if (resolve != null)
        {
            var stack = new Stack<XNodeLite>();
            stack.Push(root);
            int lines = 0;
            while (stack.Count > 0 && lines < 40)
            {
                XNodeLite n = stack.Pop();
                string? r = resolve(n);
                if (r != null) { sb.AppendLine("Resolved: " + r); lines++; }
                for (int i = n.Kids.Count - 1; i >= 0; i--) stack.Push(n.Kids[i]);
            }
        }
        return sb.ToString();
    }

    private static void Render(XNodeLite n, StringBuilder sb, int depth, HashSet<string> nameTags)
    {
        if (sb.Length > 400_000 || SkippedElements.Contains(n.Name) || nameTags.Contains(n.Name)) return;
        string pad = new(' ', Math.Min(depth, 12) * 2);
        bool isRef = n.Name.EndsWith("-REF", StringComparison.Ordinal) || n.Name.EndsWith("-TREF", StringComparison.Ordinal)
            || n.Name.EndsWith("-REFS", StringComparison.Ordinal);
        if (n.Kids.Count == 0)
        {
            string attrs = n.Attrs.Count == 0 ? "" : " (" + string.Join(", ", n.Attrs.Select(a => a.Key + "=" + a.Value)) + ")";
            if (n.Text.Length > 0) sb.Append(pad).Append(n.Name).Append(isRef ? " -> " : ": ").Append(n.Text).AppendLine(attrs);
            else if (n.Attrs.Count > 0) sb.Append(pad).Append(n.Name).Append(" -> ").AppendLine(string.Join(", ", n.Attrs.Select(a => a.Value)));
            return;
        }
        if (n.ShortName != null)
        {
            string attrs = n.Attrs.Count == 0 ? "" : " (" + string.Join(", ", n.Attrs.Select(a => a.Key + "=" + a.Value)) + ")";
            sb.Append(pad).Append(n.Name).Append(' ').Append(n.ShortName).AppendLine(attrs);
            if (depth >= 40) { sb.Append(pad).AppendLine("  ..."); return; }
            foreach (XNodeLite k in n.Kids) Render(k, sb, depth + 1, nameTags);
            return;
        }
        if (!n.HasNamedDescendant)
        {
            string inline = Inline(n, 0, nameTags);
            sb.Append(pad).Append(n.Name).Append(": ").AppendLine(inline.Length > 600 ? inline[..600] + "..." : inline);
            return;
        }
        // Plain wrapper that holds named items: show the items one level up.
        if (depth >= 40) return;
        foreach (XNodeLite k in n.Kids) Render(k, sb, depth, nameTags);
    }

    private static string Inline(XNodeLite n, int depth, HashSet<string> nameTags)
    {
        var parts = new List<string>();
        if (n.Text.Length > 0) parts.Add(n.Text);
        if (depth < 8)
            foreach (XNodeLite k in n.Kids)
            {
                if (SkippedElements.Contains(k.Name) || nameTags.Contains(k.Name)) continue;
                string inner = k.Kids.Count == 0 ? k.Text : Inline(k, depth + 1, nameTags);
                if (inner.Length == 0 && k.Attrs.Count > 0) inner = string.Join(",", k.Attrs.Select(a => a.Value));
                parts.Add(k.Name + "=" + inner);
            }
        return string.Join("; ", parts);
    }

    public static string Hex(string? numeric)
    {
        if (numeric == null) return "";
        string t = numeric.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out long h)) return "0x" + h.ToString("X");
        return long.TryParse(t, out long v) && v >= 0 ? "0x" + v.ToString("X") : t;
    }
}

/// <summary>Opens a file for the automotive extractors, with a hard cap on how much can ever be read from one stream.</summary>
internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public LimitedReadStream(Stream inner, long maxBytes) { _inner = inner; _remaining = maxBytes; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(1, _remaining)));
        _remaining -= n;
        if (_remaining < 0 || (_remaining == 0 && n > 0 && _inner.Read(new byte[1], 0, 1) > 0)) throw new InvalidDataException("Entry exceeds the allowed size.");
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
