using System.Xml;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Streaming reader for AUTOSAR XML (ARXML). Every identifiable element directly under an AR-PACKAGE's ELEMENTS (ECU instances,
/// I-SIGNALs, PDUs, frames, clusters, SW components, interfaces, data types, COMPU-METHODs, DEM/DCM items, ...) becomes one
/// segment whose heading is the SHORT-NAME package path. References stay as written (SHORT-NAME paths such as
/// /Pkg/Sub/Name), so relations like signal -> PDU -> frame can be found by searching the path. The file is never loaded as a DOM.
/// </summary>
internal sealed class ArxmlExtractor : IDocumentExtractor
{
    private const int MaxNodesPerEntity = 4000;
    private static readonly HashSet<string> NameTags = new(StringComparer.Ordinal) { "SHORT-NAME" };

    public string Name => "arxml";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".arxml", StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var sink = new AutomotiveSink(limits);
        bool malformed = false;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            using XmlReader r = AutomotiveXml.CreateReader(stream);
            var elementStack = new List<string>();
            var pkgNames = new List<string>();
            var pkgPushed = new Stack<bool>();
            int tick = 0;
            while (r.Read() && !sink.Full)
            {
                if ((++tick & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "AR-PACKAGE" && pkgPushed.Count > 0 && pkgPushed.Pop()) pkgNames.RemoveAt(pkgNames.Count - 1);
                    if (elementStack.Count > 0) elementStack.RemoveAt(elementStack.Count - 1);
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;

                string name = r.LocalName;
                string? parent = elementStack.Count > 0 ? elementStack[^1] : null;

                if (name == "SHORT-NAME" && parent == "AR-PACKAGE")
                {
                    XNodeLite n = AutomotiveXml.ReadBounded(r, 4, NameTags, ct);
                    if (n.Text.Length > 0 && pkgPushed.Count > 0) { pkgNames.Add(n.Text); pkgPushed.Pop(); pkgPushed.Push(true); }
                    continue;
                }
                if (parent == "ELEMENTS")
                {
                    XNodeLite entity = AutomotiveXml.ReadBounded(r, MaxNodesPerEntity, NameTags, ct);
                    string shortName = entity.ShortName ?? "(unnamed)";
                    string reference = "/" + string.Join("/", pkgNames.Append(shortName));
                    string title = "AUTOSAR " + entity.Name + " " + reference;
                    string heading = string.Join(" > ", pkgNames.Append(shortName));
                    sink.Add(AutomotiveXml.Describe(title, entity, NameTags), heading);
                    continue;
                }
                if (name == "AR-PACKAGE" && !r.IsEmptyElement) pkgPushed.Push(false);
                if (!r.IsEmptyElement) elementStack.Add(name);
            }
        }
        catch (XmlException) { malformed = true; }
        catch (InvalidDataException) { malformed = true; }

        if (malformed && sink.SegmentCount == 0) return AutomotiveSink.NotWellFormed(path, Name);
        string? note = malformed ? "The XML became malformed partway; content up to that point was indexed." : null;
        if (!malformed && sink.SegmentCount == 0) note = "No AUTOSAR elements found.";
        return sink.Build(path, Name, note);
    }
}
