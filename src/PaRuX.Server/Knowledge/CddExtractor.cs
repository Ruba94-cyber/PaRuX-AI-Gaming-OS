using System.Xml;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Best-effort streaming reader for Vector CANdela CDD (XML) diagnostic descriptions. The format is vendor-specific and not
/// formally published, so this is heuristic and based on public knowledge only: elements are matched by normalised local
/// name (case, '-' and '_' ignored); qualifier/name children (QUAL, NAME/TUV) provide identifiers. Container elements (ECU,
/// variants, diagnostic classes) build the heading path; entity elements (diagnostic instances/services, DTCs, data objects,
/// DIDs, routines, environment data) become segments rendered generically so every identifier and value in them stays
/// searchable. Unrecognised files yield an honest "no CDD structure" note instead of guessed content.
/// </summary>
internal sealed class CddExtractor : IDocumentExtractor
{
    private const int MaxNodesPerEntity = 3000;
    private static readonly HashSet<string> NameTags = new(StringComparer.Ordinal) { "QUAL" };
    private static readonly HashSet<string> ContainerKinds = new(StringComparer.Ordinal)
        { "ECU", "VARIANT", "BASEVARIANT", "ECUVARIANT", "DIAGCLASS", "DIAGCLASSINST", "DTCTABLE", "DTCTABLEINST" };
    private static readonly HashSet<string> EntityKinds = new(StringComparer.Ordinal)
    {
        "DIAGINST", "DIAGSERVICE", "SERVICE", "SVC", "SERVICEINST", "DTC", "DTCDEF", "DTCINST", "DATAOBJ", "DATAOBJECT", "DATAOBJINST",
        "DID", "DIDINST", "DATAIDENTIFIER", "ROUTINE", "ROUTINEINST", "ENVDATA", "ENVCOND", "ENVCONDITION", "DATATYPE", "COMPUMETHOD", "FAULTMEMORY"
    };

    public string Name => "cdd";
    public bool CanHandle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cdd" or ".cddt";

    private static string Norm(string s) => s.Replace("-", "").Replace("_", "").ToUpperInvariant();

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var sink = new AutomotiveSink(limits);
        bool malformed = false;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            using XmlReader r = AutomotiveXml.CreateReader(stream);
            var elementStack = new List<string>();
            var containers = new List<(string Kind, string? Name, int Depth)>();
            int tick = 0;
            while (r.Read() && !sink.Full)
            {
                if ((++tick & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (elementStack.Count > 0) elementStack.RemoveAt(elementStack.Count - 1);
                    while (containers.Count > 0 && containers[^1].Depth > elementStack.Count) containers.RemoveAt(containers.Count - 1);
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;
                string raw = r.LocalName, norm = Norm(raw);
                bool empty = r.IsEmptyElement;

                if (norm == "QUAL" && containers.Count > 0 && containers[^1].Name == null && containers[^1].Depth == elementStack.Count)
                {
                    string q = AutomotiveXml.ReadBounded(r, 4, NameTags, ct).Text;
                    containers[^1] = (containers[^1].Kind, q.Length > 0 ? q : null, containers[^1].Depth);
                    continue;
                }
                if (EntityKinds.Contains(norm))
                {
                    XNodeLite entity = AutomotiveXml.ReadBounded(r, MaxNodesPerEntity, NameTags, ct);
                    EmitEntity(entity, containers.Select(c => c.Name).Where(n => n != null).Select(n => n!).ToList(), sink);
                    continue;
                }
                if (ContainerKinds.Contains(norm) && !empty) containers.Add((raw, null, elementStack.Count + 1));
                if (!empty) elementStack.Add(raw);
            }
        }
        catch (XmlException) { malformed = true; }

        if (malformed && sink.SegmentCount == 0) return AutomotiveSink.NotWellFormed(path, Name);
        string? note = malformed ? "The XML became malformed partway; content up to that point was indexed." : null;
        if (!malformed && sink.SegmentCount == 0)
            note = "No recognised CDD diagnostic structure (CDD is a vendor format; only the file name is searchable).";
        return sink.Build(path, Name, note);
    }

    private static void EmitEntity(XNodeLite entity, List<string> context, AutomotiveSink sink)
    {
        string? name = entity.ShortName;
        if (name == null)
        {
            XNodeLite? n = AutomotiveXml.Find(entity, "NAME");
            XNodeLite? tuv = n == null ? null : AutomotiveXml.Find(n, "TUV");
            name = tuv?.Text is { Length: > 0 } ? tuv.Text : n?.Text is { Length: > 0 } ? n.Text : "(unnamed)";
        }
        string title = "CDD " + entity.Name + " " + name + (context.Count > 0 ? " in " + string.Join(" > ", context) : "");
        sink.Add(AutomotiveXml.Describe(title, entity, NameTags), string.Join(" > ", context.Append(name)));
    }
}
