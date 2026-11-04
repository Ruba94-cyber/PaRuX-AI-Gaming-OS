using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Shared plumbing for the general-purpose format extractors (LKS-1 generic formats): a segment sink that enforces
/// <see cref="ExtractionLimits"/>, a bounded line reader, a heading-path stack and the stream-based extractor base class.
/// Nothing here ever puts document content into an exception message or note.
/// </summary>
internal sealed class ExtractionSink
{
    private readonly List<DocumentSegment> _segments = new();
    private readonly StringBuilder _buffer = new();
    private long _total, _offset, _bufferStart;
    private int _firstLine, _lastLine;

    public ExtractionSink(ExtractionLimits limits, CancellationToken ct) { Limits = limits; Ct = ct; }

    public ExtractionLimits Limits { get; }
    public CancellationToken Ct { get; }

    /// <summary>A limit was hit anywhere (reported to the caller).</summary>
    public bool Truncated { get; set; }
    /// <summary>The character budget is used up: nothing more can be added.</summary>
    public bool Exhausted { get; private set; }
    public string? Note { get; set; }

    /// <summary>Virtual path or chapter prefix put in front of every heading (archives, EPUB chapters).</summary>
    private string? _prefix;
    public string? Prefix { get => _prefix; set { Flush(); _prefix = value; } }
    public string? Heading { get; private set; }
    private int? _slide;
    public int? Slide { get => _slide; set { Flush(); _slide = value; } }
    /// <summary>Append "(lines a-b)" to headings; the caller keeps <see cref="LineNo"/> current.</summary>
    public bool LineRanges { get; set; }
    public int LineNo { get; set; }

    // Archive state shared across nested containers.
    public int ArchiveDepth { get; set; }
    public long ExpandedBytes { get; set; }

    public int SegmentCount => _segments.Count;

    /// <summary>Appends one logical line to the open section.</summary>
    public void Line(string text)
    {
        Ct.ThrowIfCancellationRequested();
        if (Exhausted) return;
        long room = Limits.MaxCharacters - _total;
        if (room <= 0) { Exhausted = Truncated = true; return; }
        if (text.Length + 1 > room) { text = text[..(int)Math.Max(0, room - 1)]; Exhausted = Truncated = true; }
        if (_buffer.Length == 0) { _bufferStart = _offset; _firstLine = LineNo; }
        _buffer.Append(text).Append('\n');
        _lastLine = LineNo;
        _total += text.Length + 1; _offset += text.Length + 1;
        if (_buffer.Length >= Limits.SegmentCharacters) Flush();
    }

    /// <summary>Starts a new section: pending text is closed and later text carries <paramref name="heading"/>.</summary>
    public void Section(string? heading) { Flush(); Heading = heading; }

    /// <summary>Key/value style leaf (JSON, YAML, XML, TOML): sections follow <paramref name="heading"/>.</summary>
    public void Leaf(string? heading, string path, string value)
    {
        if (!string.Equals(heading, Heading, StringComparison.Ordinal)) Section(heading);
        if (value.Length > Limits.SegmentCharacters) value = value[..Limits.SegmentCharacters];
        Line(path + ": " + value);
    }

    /// <summary>Adds an already-formed segment (sheet rows, subtitle blocks) with explicit coordinates.</summary>
    public void Block(string text, string? heading = null, string? sheet = null, string? cellRange = null, int? slide = null)
    {
        Ct.ThrowIfCancellationRequested();
        Flush();
        if (Exhausted || text.Length == 0) return;
        long room = Limits.MaxCharacters - _total;
        if (room <= 0) { Exhausted = Truncated = true; return; }
        if (text.Length > room) { text = text[..(int)room]; Exhausted = Truncated = true; }
        _segments.Add(new DocumentSegment(text, new DocumentPosition(Sheet: sheet, CellRange: cellRange, Slide: slide ?? Slide,
            Heading: Compose(heading), CharOffset: _offset)));
        _total += text.Length + 1; _offset += text.Length + 1;
    }

    public void Flush()
    {
        if (_buffer.Length == 0) return;
        string text = _buffer.ToString().TrimEnd();
        _buffer.Clear();
        if (text.Length == 0) return;
        string? head = Compose(Heading);
        if (LineRanges)
        {
            string range = _firstLine == _lastLine ? "line " + _firstLine : "lines " + _firstLine + "-" + _lastLine;
            head = head == null ? range : head + " (" + range + ")";
        }
        _segments.Add(new DocumentSegment(text, new DocumentPosition(Slide: Slide, Heading: head, CharOffset: _bufferStart)));
    }

    private string? Compose(string? heading)
    {
        if (Prefix == null) return heading;
        return heading == null ? Prefix : Prefix + " > " + heading;
    }

    public ExtractedDocument Build(string path, string extractor)
    {
        Flush();
        DocumentTier tier = _segments.Count == 0 ? DocumentTier.MetadataOnly : DocumentTier.Structured;
        return new ExtractedDocument(path, tier, _segments, Truncated, Note, extractor);
    }

    // ---- text opening ----

    /// <summary>Detects the encoding (BOM, UTF-8, Windows-1252) and positions the stream after the BOM; null = binary.</summary>
    public Encoding? DetectEncoding(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        stream.Position = 0;
        byte[] sample = new byte[(int)Math.Min(65536, stream.Length)];
        int read = 0, n;
        while (read < sample.Length && (n = stream.Read(sample, read, sample.Length - read)) > 0) read += n;
        Encoding? enc = TextFileExtractor.Detect(sample.AsSpan(0, read), stream.Length > read, out int bom);
        if (enc == null) { Note ??= "Content is binary, not text."; return null; }
        if (enc.CodePage == 1252) Note ??= "Decoded as Windows-1252 (not valid UTF-8).";
        stream.Position = bom;
        return enc;
    }

    public TextReader? Open(Stream stream)
    {
        Encoding? enc = DetectEncoding(stream);
        return enc == null ? null : new StreamReader(stream, enc, false, 64 * 1024, leaveOpen: true);
    }

    /// <summary>UTF-8 byte stream over a text file in any detected encoding; null = binary.</summary>
    public Stream? OpenUtf8(Stream stream)
    {
        Encoding? enc = DetectEncoding(stream);
        if (enc == null) return null;
        return enc.CodePage == 65001 ? stream : Encoding.CreateTranscodingStream(stream, enc, Encoding.UTF8, leaveOpen: true);
    }
}

/// <summary>Reads lines of bounded length so a minified single-line gigabyte file cannot exhaust memory.</summary>
internal sealed class LineReader
{
    private readonly TextReader _reader;
    private readonly char[] _buf = new char[16384];
    private readonly StringBuilder _sb = new();
    private readonly int _max;
    private int _pos, _len;
    private bool _skipLf;

    public LineReader(TextReader reader, int maxLineLength) { _reader = reader; _max = Math.Max(256, maxLineLength); }

    public string? ReadLine()
    {
        _sb.Clear();
        bool any = false;
        while (true)
        {
            if (_pos >= _len)
            {
                _len = _reader.Read(_buf, 0, _buf.Length); _pos = 0;
                if (_len <= 0) { _len = 0; return any ? _sb.ToString() : null; }
            }
            if (_skipLf) { _skipLf = false; if (_buf[_pos] == '\n') { _pos++; continue; } }
            char c = _buf[_pos++];
            any = true;
            if (c == '\n') return _sb.ToString();
            if (c == '\r') { _skipLf = true; return _sb.ToString(); }
            _sb.Append(c);
            if (_sb.Length >= _max) return _sb.ToString();
        }
    }
}

/// <summary>Heading path such as "Intro > Scope".</summary>
internal sealed class HeadingStack
{
    private readonly List<(int Level, string Text)> _items = new();

    public string Push(int level, string text)
    {
        while (_items.Count > 0 && _items[^1].Level >= level) _items.RemoveAt(_items.Count - 1);
        if (text.Length > 120) text = text[..120];
        _items.Add((level, text));
        return Path!;
    }

    public string? Path => _items.Count == 0 ? null : string.Join(" > ", _items.Select(i => i.Text));
}

/// <summary>Throws instead of returning more than <c>limit</c> bytes: a decompression-bomb guard for streamed XML/JSON.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _remaining;

    public BoundedReadStream(Stream inner, long limit, bool leaveOpen = false) { _inner = inner; _remaining = limit; _leaveOpen = leaveOpen; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            if (_inner.ReadByte() >= 0) throw new InvalidDataException("Expansion limit exceeded.");
            return 0;
        }
        int n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= n;
        return n;
    }

    protected override void Dispose(bool disposing) { if (disposing && !_leaveOpen) _inner.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An extractor that works on a seekable stream, so the same code reads files and archive entries held in memory.</summary>
internal abstract class StreamExtractor : IDocumentExtractor
{
    public abstract string Name { get; }
    protected abstract IReadOnlySet<string> Extensions { get; }

    public virtual bool CanHandle(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public abstract void Read(Stream stream, string fileName, ExtractionSink sink);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var sink = new ExtractionSink(limits, ct);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        Read(stream, Path.GetFileName(path), sink);
        return sink.Build(path, Name);
    }

    protected static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Registry of the generic-format extractors, in priority order. Registered before the sniffing text fallback.</summary>
internal static class GenericExtractors
{
    private static readonly StreamExtractor[] Streams =
    {
        new MarkdownExtractor(), new HtmlExtractor(), new JsonExtractor(), new NotebookExtractor(), new YamlExtractor(),
        new TomlIniExtractor(), new XmlExtractor(), new CsvExtractor(), new EmailExtractor(), new RtfExtractor(),
        new OpenDocumentExtractor(), new EpubExtractor(), new SubtitleExtractor(), new SqlExtractor(), new LatexExtractor(),
        new SourceCodeExtractor(), new ArchiveExtractor()
    };

    public static IReadOnlyList<IDocumentExtractor> All { get; } = Streams.Cast<IDocumentExtractor>().Append(new OutlookMsgExtractor()).ToArray();

    /// <summary>The stream extractor that would read an entry with this name, or null.</summary>
    public static StreamExtractor? Find(string fileName) => Streams.FirstOrDefault(e => e.CanHandle(fileName));
}

/// <summary>.msg is an OLE compound file; reading it needs a dedicated dependency, so it stays metadata-only with an honest note.</summary>
internal sealed class OutlookMsgExtractor : IDocumentExtractor
{
    public string Name => "msg";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".msg", StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct) =>
        new(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false,
            "Outlook .msg (OLE compound) is not parsed without a dedicated dependency; indexed by name and metadata only. Export as .eml for full text.", Name);
}
