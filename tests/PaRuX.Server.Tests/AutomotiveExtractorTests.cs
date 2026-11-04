using System.IO.Compression;
using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>All fixtures are tiny, fictional and built as strings here; nothing real is read or committed.</summary>
public sealed class AutomotiveExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lks5-" + Guid.NewGuid().ToString("N"));

    public AutomotiveExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private const string Dbc = """
VERSION "1.0"

NS_ :

BS_:

BU_: ECU_ALPHA ECU_BETA

BO_ 291 MSG_SPEED: 8 ECU_ALPHA
 SG_ SIG_SPEED : 0|16@1+ (0.1,0) [0|250] "km/h" ECU_BETA,Vector__XXX
 SG_ SIG_MODE m1 : 16|4@0- (1,-2) [-2|13] "" ECU_BETA

BO_ 2147483905 MSG_EXT: 4 ECU_BETA
 SG_ SIG_FLAG : 0|1@1+ (1,0) [0|1] "" ECU_ALPHA

CM_ SG_ 291 SIG_SPEED "Vehicle speed
over two lines";
CM_ BO_ 291 "Speed message";
VAL_ 291 SIG_MODE 0 "off" 1 "on" ;
VAL_TABLE_ TBL_GEAR 0 "N" 1 "D" ;
BA_ "GenMsgCycleTime" BO_ 291 100;
this line is garbage and must not break anything
""";

    [Fact]
    public void Dbc_yields_signal_message_and_node_segments_with_relations()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("a.dbc", Dbc));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("dbc", doc.Extractor);
        DocumentSegment sig = Assert.Single(doc.Segments, s => s.Position.Heading == "ECU_ALPHA > MSG_SPEED > SIG_SPEED");
        Assert.Contains("ID 0x123", sig.Text);
        Assert.Contains("factor 0.1", sig.Text);
        Assert.Contains("unit \"km/h\"", sig.Text);
        Assert.Contains("Receivers: ECU_BETA", sig.Text);
        Assert.DoesNotContain("Vector__XXX", sig.Text);
        Assert.Contains("Vehicle speed", sig.Text);
        Assert.Contains("little-endian", sig.Text);

        DocumentSegment mode = Assert.Single(doc.Segments, s => s.Position.Heading!.EndsWith("SIG_MODE"));
        Assert.Contains("0 = \"off\"", mode.Text);
        Assert.Contains("signed", mode.Text);
        Assert.Contains("multiplexed, selected by value 1", mode.Text);

        DocumentSegment ext = Assert.Single(doc.Segments, s => s.Position.Heading == "ECU_BETA > MSG_EXT");
        Assert.Contains("extended 29-bit", ext.Text);
        Assert.Contains("0x101", ext.Text);

        Assert.Contains(doc.Segments, s => s.Position.Heading == "ECU_BETA" && s.Text.Contains("MSG_EXT") && s.Text.Contains("MSG_SPEED.SIG_SPEED"));
        Assert.Contains(doc.Segments, s => s.Text.Contains("TBL_GEAR") && s.Text.Contains("1 = \"D\""));
        Assert.Contains(doc.Segments, s => s.Text.Contains("GenMsgCycleTime = 100"));
        Assert.Null(doc.Note);
    }

    [Fact]
    public void Dbc_in_windows_1252_is_decoded()
    {
        string path = Path.Combine(_dir, "b.dbc");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes(Dbc.Replace("Vehicle speed", "Geschwindigkeit äö")));
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(path);
        Assert.Contains("Geschwindigkeit äö", doc.FullText);
    }

    [Fact]
    public void Dbc_binary_content_is_failed_without_quoting_content()
    {
        string path = Path.Combine(_dir, "bin.dbc");
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3, 0, 0, 9, 200, 0, 5 });
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(path);
        Assert.Equal(DocumentTier.Failed, doc.Tier);
        Assert.Empty(doc.Segments);
    }

    private const string Arxml = """
<?xml version="1.0" encoding="UTF-8"?>
<AUTOSAR xmlns="http://autosar.org/schema/r4.0">
  <AR-PACKAGES>
    <AR-PACKAGE>
      <SHORT-NAME>Demo</SHORT-NAME>
      <AR-PACKAGES>
        <AR-PACKAGE>
          <SHORT-NAME>Signals</SHORT-NAME>
          <ELEMENTS>
            <I-SIGNAL>
              <SHORT-NAME>ISig_Speed</SHORT-NAME>
              <LENGTH>16</LENGTH>
              <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Demo/Signals/SysSig_Speed</SYSTEM-SIGNAL-REF>
            </I-SIGNAL>
            <I-SIGNAL-I-PDU>
              <SHORT-NAME>Pdu_Motion</SHORT-NAME>
              <LENGTH>8</LENGTH>
              <I-SIGNAL-TO-PDU-MAPPINGS>
                <I-SIGNAL-TO-I-PDU-MAPPING>
                  <SHORT-NAME>Map_Speed</SHORT-NAME>
                  <I-SIGNAL-REF DEST="I-SIGNAL">/Demo/Signals/ISig_Speed</I-SIGNAL-REF>
                  <START-POSITION>0</START-POSITION>
                </I-SIGNAL-TO-I-PDU-MAPPING>
              </I-SIGNAL-TO-PDU-MAPPINGS>
            </I-SIGNAL-I-PDU>
          </ELEMENTS>
        </AR-PACKAGE>
        <AR-PACKAGE>
          <SHORT-NAME>Ecus</SHORT-NAME>
          <ELEMENTS>
            <ECU-INSTANCE><SHORT-NAME>Ecu_Gamma</SHORT-NAME></ECU-INSTANCE>
            <COMPU-METHOD>
              <SHORT-NAME>CM_Rpm</SHORT-NAME>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>
                <LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT>10</UPPER-LIMIT>
                <COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR><V>0</V><V>0.25</V></COMPU-NUMERATOR></COMPU-RATIONAL-COEFFS>
              </COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD>
          </ELEMENTS>
        </AR-PACKAGE>
      </AR-PACKAGES>
    </AR-PACKAGE>
  </AR-PACKAGES>
</AUTOSAR>
""";

    [Fact]
    public void Arxml_entities_carry_package_path_and_references()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("a.arxml", Arxml));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("arxml", doc.Extractor);
        DocumentSegment isig = Assert.Single(doc.Segments, s => s.Position.Heading == "Demo > Signals > ISig_Speed");
        Assert.Contains("I-SIGNAL /Demo/Signals/ISig_Speed", isig.Text);
        Assert.Contains("SYSTEM-SIGNAL-REF -> /Demo/Signals/SysSig_Speed", isig.Text);
        Assert.Contains("LENGTH: 16", isig.Text);

        DocumentSegment pdu = Assert.Single(doc.Segments, s => s.Position.Heading == "Demo > Signals > Pdu_Motion");
        Assert.Contains("Map_Speed", pdu.Text);
        Assert.Contains("I-SIGNAL-REF -> /Demo/Signals/ISig_Speed", pdu.Text);

        Assert.Contains(doc.Segments, s => s.Position.Heading == "Demo > Ecus > Ecu_Gamma");
        DocumentSegment cm = Assert.Single(doc.Segments, s => s.Position.Heading == "Demo > Ecus > CM_Rpm");
        Assert.Contains("UPPER-LIMIT=10", cm.Text);
        Assert.Contains("0.25", cm.Text);
    }

    [Fact]
    public void Arxml_truncated_midway_keeps_what_was_read()
    {
        string cut = Arxml[..Arxml.IndexOf("<COMPU-METHOD>", StringComparison.Ordinal)];
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("cut.arxml", cut));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Contains(doc.Segments, s => s.Position.Heading == "Demo > Ecus > Ecu_Gamma");
        Assert.Contains("malformed", doc.Note);
    }

    [Fact]
    public void Arxml_not_xml_is_failed_and_does_not_leak_content()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("bad.arxml", "SECRET_TEXT this is not xml at all"));

        Assert.Equal(DocumentTier.Failed, doc.Tier);
        Assert.DoesNotContain("SECRET_TEXT", doc.Note);
    }

    [Fact]
    public void Arxml_with_doctype_entities_is_not_expanded()
    {
        string xml = "<?xml version=\"1.0\"?><!DOCTYPE AUTOSAR [<!ENTITY a \"AAAAAAAAAA\"><!ENTITY b \"&a;&a;&a;&a;\">]>"
            + "<AUTOSAR><AR-PACKAGES><AR-PACKAGE><SHORT-NAME>P</SHORT-NAME><ELEMENTS><ECU-INSTANCE><SHORT-NAME>E1</SHORT-NAME></ECU-INSTANCE></ELEMENTS></AR-PACKAGE></AR-PACKAGES></AUTOSAR>";
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("dtd.arxml", xml));
        Assert.Contains(doc.Segments, s => s.Position.Heading == "P > E1");
    }

    [Fact]
    public void Arxml_large_file_honours_character_limit_and_marks_truncated()
    {
        var sb = new StringBuilder("<AUTOSAR><AR-PACKAGES><AR-PACKAGE><SHORT-NAME>Big</SHORT-NAME><ELEMENTS>");
        for (int i = 0; i < 3000; i++) sb.Append("<I-SIGNAL><SHORT-NAME>Sig_").Append(i).Append("</SHORT-NAME><LENGTH>8</LENGTH></I-SIGNAL>");
        sb.Append("</ELEMENTS></AR-PACKAGE></AR-PACKAGES></AUTOSAR>");

        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("big.arxml", sb.ToString()), new ExtractionLimits(MaxCharacters: 20_000));

        Assert.True(doc.Truncated);
        Assert.InRange(doc.Segments.Count, 10, 2999);
        Assert.True(doc.Segments.Sum(s => s.Text.Length) <= 20_000);
    }

    [Fact]
    public void Arxml_cancellation_is_honoured()
    {
        var sb = new StringBuilder("<AUTOSAR><AR-PACKAGES><AR-PACKAGE><SHORT-NAME>C</SHORT-NAME><ELEMENTS>");
        for (int i = 0; i < 5000; i++) sb.Append("<I-SIGNAL><SHORT-NAME>S").Append(i).Append("</SHORT-NAME></I-SIGNAL>");
        sb.Append("</ELEMENTS></AR-PACKAGE></AR-PACKAGES></AUTOSAR>");
        string path = Write("c.arxml", sb.ToString());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StructuredDocumentExtractor.Extract(path, null, cts.Token));
    }

    private const string Odx = """
<?xml version="1.0" encoding="UTF-8"?>
<ODX MODEL-VERSION="2.2.0">
  <DIAG-LAYER-CONTAINER ID="DLC_1">
    <SHORT-NAME>Container</SHORT-NAME>
    <BASE-VARIANTS>
      <BASE-VARIANT ID="BV_1">
        <SHORT-NAME>BV_Demo</SHORT-NAME>
        <DIAG-DATA-DICTIONARY-SPEC>
          <DTC-DOPS>
            <DTC-DOP ID="DD_1">
              <SHORT-NAME>DTCDOP_Main</SHORT-NAME>
              <DTCS>
                <DTC ID="DTC_1">
                  <SHORT-NAME>DTC_Boost</SHORT-NAME>
                  <TROUBLE-CODE>4660</TROUBLE-CODE>
                  <DISPLAY-TROUBLE-CODE>P1234</DISPLAY-TROUBLE-CODE>
                  <TEXT>Boost pressure low</TEXT>
                </DTC>
              </DTCS>
            </DTC-DOP>
          </DTC-DOPS>
          <DATA-OBJECT-PROPS>
            <DATA-OBJECT-PROP ID="DOP_1"><SHORT-NAME>DOP_Vin</SHORT-NAME></DATA-OBJECT-PROP>
          </DATA-OBJECT-PROPS>
        </DIAG-DATA-DICTIONARY-SPEC>
        <DIAG-COMMS>
          <DIAG-SERVICE ID="DS_1" SEMANTIC="DATA">
            <SHORT-NAME>DS_ReadVin</SHORT-NAME>
            <LONG-NAME>Read VIN</LONG-NAME>
            <REQUEST-REF ID-REF="RQ_1"/>
            <POS-RESPONSE-REFS><POS-RESPONSE-REF ID-REF="PR_1"/></POS-RESPONSE-REFS>
          </DIAG-SERVICE>
        </DIAG-COMMS>
        <REQUESTS>
          <REQUEST ID="RQ_1">
            <SHORT-NAME>RQ_ReadVin</SHORT-NAME>
            <PARAMS>
              <PARAM SEMANTIC="SERVICE-ID"><SHORT-NAME>SID</SHORT-NAME><CODED-VALUE>34</CODED-VALUE></PARAM>
              <PARAM><SHORT-NAME>DID</SHORT-NAME><CODED-VALUE>61840</CODED-VALUE></PARAM>
            </PARAMS>
          </REQUEST>
        </REQUESTS>
        <POS-RESPONSES>
          <POS-RESPONSE ID="PR_1"><SHORT-NAME>PR_ReadVin</SHORT-NAME><PARAMS><PARAM><SHORT-NAME>VinValue</SHORT-NAME><DOP-REF ID-REF="DOP_1"/></PARAM></PARAMS></POS-RESPONSE>
        </POS-RESPONSES>
      </BASE-VARIANT>
    </BASE-VARIANTS>
  </DIAG-LAYER-CONTAINER>
</ODX>
""";

    [Fact]
    public void Odx_service_is_linked_to_request_sid_and_did()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("a.odx-d", Odx));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("odx", doc.Extractor);
        Assert.Contains(doc.Segments, s => s.Position.Heading == "BV_Demo");
        DocumentSegment svc = Assert.Single(doc.Segments, s => s.Position.Heading == "BV_Demo > DS_ReadVin");
        Assert.Contains("REQUEST-REF RQ_1 = REQUEST RQ_ReadVin", svc.Text);
        Assert.Contains("SID=0x22", svc.Text);
        Assert.Contains("DID=0xF190", svc.Text);
        Assert.Contains("Read VIN", svc.Text);
        Assert.Contains("PR_ReadVin", svc.Text);

        DocumentSegment dtc = Assert.Single(doc.Segments, s => s.Position.Heading == "BV_Demo > DTCDOP_Main > DTC_Boost");
        Assert.Contains("P1234", dtc.Text);
        Assert.Contains("Trouble code (hex): 0x1234", dtc.Text);

        DocumentSegment resp = Assert.Single(doc.Segments, s => s.Position.Heading == "BV_Demo > PR_ReadVin");
        Assert.Contains("DOP-REF", resp.Text);
        Assert.Contains("DATA-OBJECT-PROP DOP_Vin", resp.Text);
    }

    [Fact]
    public void Pdx_archive_is_read_entry_by_entry()
    {
        string path = Path.Combine(_dir, "a.pdx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("../evil/x.odx-d").Open())) w.Write(Odx);
            using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("ignored");
        }
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(path);

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Contains(doc.Segments, s => s.Position.Heading == "BV_Demo > DS_ReadVin");
        Assert.DoesNotContain("ignored", doc.FullText);
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil", "x.odx-d")));
    }

    [Fact]
    public void Pdx_that_is_not_a_zip_is_failed()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("broken.pdx", "not a zip"));
        Assert.Equal(DocumentTier.Failed, doc.Tier);
    }

    private const string Cdd = """
<?xml version="1.0" encoding="UTF-8"?>
<CANDELA>
  <ECU>
    <QUAL>Ecu_Delta</QUAL>
    <NAME><TUV xml:lang="en-US">Delta controller</TUV></NAME>
    <VARIANT>
      <QUAL>Var_Std</QUAL>
      <DIAGINST>
        <QUAL>Svc_ReadId</QUAL>
        <NAME><TUV xml:lang="en-US">Read identifier</TUV></NAME>
        <SERVICEID>0x22</SERVICEID>
        <DID>0xF187</DID>
      </DIAGINST>
      <DTCDEF>
        <QUAL>Dtc_Bus</QUAL>
        <DTCCODE>0xC12345</DTCCODE>
        <NAME><TUV xml:lang="en-US">Bus off</TUV></NAME>
      </DTCDEF>
    </VARIANT>
  </ECU>
</CANDELA>
""";

    [Fact]
    public void Cdd_services_and_dtcs_get_ecu_variant_heading()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("a.cdd", Cdd));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("cdd", doc.Extractor);
        DocumentSegment svc = Assert.Single(doc.Segments, s => s.Position.Heading == "Ecu_Delta > Var_Std > Svc_ReadId");
        Assert.Contains("0x22", svc.Text);
        Assert.Contains("0xF187", svc.Text);
        Assert.Contains("Read identifier", svc.Text);
        DocumentSegment dtc = Assert.Single(doc.Segments, s => s.Position.Heading == "Ecu_Delta > Var_Std > Dtc_Bus");
        Assert.Contains("0xC12345", dtc.Text);
    }

    [Fact]
    public void Cdd_without_known_structure_is_metadata_only_with_honest_note()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("odd.cdd", "<root><x>1</x></root>"));
        Assert.Equal(DocumentTier.MetadataOnly, doc.Tier);
        Assert.Contains("No recognised CDD", doc.Note);
    }

    private const string A2l = """
/* fictional */
ASAP2_VERSION 1 71
/begin PROJECT PRJ ""
  /begin MODULE Mod_Demo ""
    /begin MEASUREMENT MEAS_Rpm "Engine speed" UWORD CM_Rpm 1 0.5 0 8000
      ECU_ADDRESS 0x1000
      /begin IF_DATA XCP x /end IF_DATA
    /end MEASUREMENT
    /begin CHARACTERISTIC CHAR_Gain "Gain map" VALUE 0x2000 RL_Std 0 CM_Rpm 0 100
    /end CHARACTERISTIC
    /begin COMPU_METHOD CM_Rpm "rpm" LINEAR "%6.1" "1/min"
      COEFFS_LINEAR 0.25 0
    /end COMPU_METHOD
  /end MODULE
/end PROJECT
""";

    [Fact]
    public void A2l_entities_have_labelled_fields()
    {
        ExtractedDocument doc = StructuredDocumentExtractor.Extract(Write("a.a2l", A2l));

        Assert.Equal(DocumentTier.Structured, doc.Tier);
        DocumentSegment m = Assert.Single(doc.Segments, s => s.Position.Heading == "Mod_Demo > MEAS_Rpm");
        Assert.Contains("Data type: UWORD", m.Text);
        Assert.Contains("Conversion: CM_Rpm", m.Text);
        Assert.Contains("ECU_ADDRESS 0x1000", m.Text);
        Assert.Contains("Nested IF_DATA", m.Text);
        DocumentSegment c = Assert.Single(doc.Segments, s => s.Position.Heading == "Mod_Demo > CHAR_Gain");
        Assert.Contains("Address: 0x2000", c.Text);
        Assert.Contains(doc.Segments, s => s.Position.Heading == "Mod_Demo > CM_Rpm" && s.Text.Contains("Unit: 1/min"));
    }

    [Fact]
    public void Registry_routes_automotive_extensions_before_generic_text()
    {
        foreach (string ext in new[] { ".dbc", ".arxml", ".odx-d", ".pdx", ".cdd", ".a2l" })
        {
            IDocumentExtractor first = StructuredDocumentExtractor.Registered.First(e => e.CanHandle("x" + ext));
            Assert.NotEqual("text", first.Name);
        }
    }
}
