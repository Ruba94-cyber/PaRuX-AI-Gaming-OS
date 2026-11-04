using System.IO.Compression;
using System.Xml;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Streaming reader for ODX (ISO 22901-1) XML and PDX archives (a zip of ODX files, read entry by entry with size caps and
/// without ever using entry names as paths). Pass 1 collects small id -> summary tables (requests, responses, DOPs, DTC-DOPs);
/// pass 2 emits one segment per diagnostic service, job, request, response, data object property, structure, table and DTC,
/// resolving ID-REF links to names and service ids so that "service -> request (SID 0x22, DID ...)" is searchable.
/// </summary>
internal sealed class OdxExtractor : IDocumentExtractor
{
    private const int MaxNodesPerEntity = 3000;
    private const int MaxRefs = 300_000;
    private const int MaxArchiveEntries = 5000;
    private static readonly HashSet<string> NameTags = new(StringComparer.Ordinal) { "SHORT-NAME" };
    private static readonly HashSet<string> LayerNames = new(StringComparer.Ordinal)
        { "BASE-VARIANT", "ECU-VARIANT", "PROTOCOL", "FUNCTIONAL-GROUP", "ECU-SHARED-DATA", "COMPARAM-SPEC", "VEHICLE-INFO-SPEC", "MULTIPLE-ECU-JOB-SPEC" };
    private static readonly HashSet<string> Entities = new(StringComparer.Ordinal)
    {
        "DIAG-SERVICE", "SINGLE-ECU-JOB", "REQUEST", "POS-RESPONSE", "NEG-RESPONSE", "GLOBAL-NEG-RESPONSE", "DATA-OBJECT-PROP",
        "STRUCTURE", "TABLE", "DTC", "FUNCTIONAL-CLASS", "UNIT"
    };
    private static readonly HashSet<string> ReferencedKinds = new(StringComparer.Ordinal)
        { "REQUEST", "POS-RESPONSE", "NEG-RESPONSE", "GLOBAL-NEG-RESPONSE", "DATA-OBJECT-PROP", "STRUCTURE", "TABLE", "DIAG-SERVICE", "SINGLE-ECU-JOB" };
    private static readonly string[] Extensions =
        { ".odx", ".odx-d", ".odx-c", ".odx-e", ".odx-v", ".odx-cs", ".odx-f", ".odx-m", ".odx-p", ".odx-fd", ".pdx" };

    public string Name => "odx";
    public bool CanHandle(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var sink = new AutomotiveSink(limits);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        bool malformed = false;
        string? note = null;
        try
        {
            if (Path.GetExtension(path).Equals(".pdx", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                var entries = zip.Entries.Where(e => e.Name.Length > 0 && IsOdxEntry(e.Name)).Take(MaxArchiveEntries + 1).ToList();
                if (entries.Count > MaxArchiveEntries) { entries = entries.Take(MaxArchiveEntries).ToList(); sink.MarkTruncated(); }
                long budget = limits.MaxStructuredFileBytes;
                var openers = new List<Func<Stream>>();
                foreach (ZipArchiveEntry e in entries)
                {
                    budget -= e.Length;
                    if (budget < 0) { sink.MarkTruncated(); break; }
                    long cap = e.Length;
                    openers.Add(() => new LimitedReadStream(e.Open(), cap));
                }
                if (openers.Count == 0) note = "PDX archive contains no ODX files.";
                foreach (var open in openers) malformed |= !Walk(open, true, refs, sink, ct);
                foreach (var open in openers) { if (sink.Full) break; malformed |= !Walk(open, false, refs, sink, ct); }
            }
            else
            {
                Func<Stream> open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                malformed |= !Walk(open, true, refs, sink, ct);
                malformed |= !Walk(open, false, refs, sink, ct);
            }
        }
        catch (InvalidDataException) { return StructuredDocumentExtractor.Failed(path, "PDX archive could not be read (corrupt or unsupported).", Name); }

        if (malformed && sink.SegmentCount == 0) return AutomotiveSink.NotWellFormed(path, Name);
        if (malformed) note = "The XML became malformed partway; content up to that point was indexed.";
        if (note == null && sink.SegmentCount == 0) note = "No ODX diagnostic content found.";
        return sink.Build(path, Name, note);
    }

    private static bool IsOdxEntry(string name) =>
        Extensions.Take(Extensions.Length - 1).Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns false when the XML was malformed (partial results are kept).</summary>
    private static bool Walk(Func<Stream> open, bool collect, Dictionary<string, string> refs, AutomotiveSink sink, CancellationToken ct)
    {
        try
        {
            using Stream stream = open();
            using XmlReader r = AutomotiveXml.CreateReader(stream);
            var elementStack = new List<string>();
            var layers = new Stack<(string Kind, string Id, string? Name)>();
            string? dtcDop = null;
            int tick = 0;
            while (r.Read() && !sink.Full)
            {
                if ((++tick & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (layers.Count > 0 && r.LocalName == layers.Peek().Kind) layers.Pop();
                    if (r.LocalName == "DTC-DOP") dtcDop = null;
                    if (elementStack.Count > 0) elementStack.RemoveAt(elementStack.Count - 1);
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;
                string name = r.LocalName;
                string? parent = elementStack.Count > 0 ? elementStack[^1] : null;
                bool empty = r.IsEmptyElement;

                if (name == "SHORT-NAME" && parent != null)
                {
                    string text = AutomotiveXml.ReadBounded(r, 4, NameTags, ct).Text;
                    if (layers.Count > 0 && parent == layers.Peek().Kind && layers.Peek().Name == null)
                    {
                        var layer = layers.Pop();
                        layers.Push((layer.Kind, layer.Id, text));
                        if (!collect) sink.Add("ODX diagnostic layer " + text + " (" + layer.Kind + (layer.Id.Length > 0 ? ", ID " + layer.Id : "") + ")", text);
                    }
                    else if (parent == "DTC-DOP") dtcDop = text;
                    continue;
                }
                if (name == "DTC-DOP")
                {
                    string id = r.GetAttribute("ID") ?? "";
                    if (collect && id.Length > 0 && refs.Count < MaxRefs) refs[id] = "DTC-DOP (id " + id + ")";
                }
                if (LayerNames.Contains(name) && !empty) layers.Push((name, r.GetAttribute("ID") ?? "", null));

                if (Entities.Contains(name))
                {
                    XNodeLite entity = AutomotiveXml.ReadBounded(r, MaxNodesPerEntity, NameTags, ct);
                    if (collect) Collect(entity, refs);
                    else EmitEntity(entity, layers.Count > 0 ? layers.Peek().Name : null, layers.Count > 0 ? layers.Peek().Kind : null, dtcDop, refs, sink);
                    continue;
                }
                if (!empty) elementStack.Add(name);
            }
            return true;
        }
        catch (XmlException) { return false; }
    }

    private static void Collect(XNodeLite entity, Dictionary<string, string> refs)
    {
        string? id = entity.Attr("ID");
        if (id == null || refs.Count >= MaxRefs || !ReferencedKinds.Contains(entity.Name)) return;
        refs[id] = Summary(entity);
    }

    private static string Summary(XNodeLite entity)
    {
        string head = entity.Name + " " + (entity.ShortName ?? "(unnamed)");
        var coded = new List<string>();
        foreach (XNodeLite p in AutomotiveXml.FindAll(entity, "PARAM"))
        {
            XNodeLite? cv = AutomotiveXml.Find(p, "CODED-VALUE") ?? AutomotiveXml.Find(p, "PHYS-CONSTANT-VALUE");
            if (cv == null || cv.Text.Length == 0) continue;
            coded.Add((p.ShortName ?? "param") + "=" + AutomotiveXml.Hex(cv.Text));
            if (coded.Count >= 8) break;
        }
        return coded.Count == 0 ? head : head + " [" + string.Join(", ", coded) + "]";
    }

    private static void EmitEntity(XNodeLite entity, string? layerName, string? layerKind, string? dtcDop,
        Dictionary<string, string> refs, AutomotiveSink sink)
    {
        string name = entity.ShortName ?? "(unnamed)";
        string where = layerName == null ? "" : " in layer " + layerName;
        string title = "ODX " + entity.Name + " " + name + where;
        string heading = (layerName == null ? "" : layerName + " > ") + (entity.Name == "DTC" && dtcDop != null ? dtcDop + " > " : "") + name;

        string body = AutomotiveXml.Describe(title, entity, NameTags, n =>
        {
            if (!n.Name.EndsWith("-REF", StringComparison.Ordinal)) return null;
            string? idRef = n.Attr("ID-REF");
            return idRef != null && refs.TryGetValue(idRef, out string? s) ? n.Name + " " + idRef + " = " + s : null;
        });

        if (entity.Name == "DTC")
        {
            XNodeLite? code = AutomotiveXml.Find(entity, "TROUBLE-CODE");
            if (code != null && code.Text.Length > 0) body += "Trouble code (hex): " + AutomotiveXml.Hex(code.Text) + "\n";
        }
        sink.Add(body, heading);
    }
}
