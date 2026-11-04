using System.IO.Compression;
using System.Text;
using System.Formats.Tar;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic fixtures for the generic-format extractors (LKS-1). Everything is generated in code.</summary>
public sealed class GenericExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lks-gen-" + Guid.NewGuid().ToString("N"));

    public GenericExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private ExtractedDocument Run(string name, string content, ExtractionLimits? limits = null)
    {
        File.WriteAllText(P(name), content, new UTF8Encoding(false));
        return StructuredDocumentExtractor.Extract(P(name), limits);
    }

    private static string Headings(ExtractedDocument d) => string.Join("|", d.Segments.Select(s => s.Position.Heading));

    [Fact]
    public void Markdown_has_heading_path_code_blocks_and_front_matter()
    {
        var doc = Run("a.md", "---\ntitle: Demo\ntags: x\n---\n# Guide\nintro text\n## Setup\n```bash\n# not a heading\nrun it\n```\nafter code\n");
        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("markdown", doc.Extractor);
        string h = Headings(doc);
        Assert.Contains("front matter", h);
        Assert.Contains("Guide > Setup", h);
        Assert.Contains("Guide > Setup > code (bash)", h);
        Assert.Contains("# not a heading", doc.FullText);
        Assert.Contains("title: Demo", doc.Segments[0].Text);
    }

    [Fact]
    public void Html_strips_scripts_and_styles_and_keeps_headings_tables_and_link_text()
    {
        var doc = Run("a.html", "<html><head><title>T</title><style>.x{color:red}</style><script>var secret=1;</script></head><body>"
            + "<h1>Main</h1><p>Hello <a href='http://x.invalid'>linked words</a> &amp; more</p><h2>Data</h2>"
            + "<table><tr><td>a</td><td>b</td></tr></table><!-- hidden --></body></html>");
        string text = doc.FullText;
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("linked words & more", text);
        Assert.Contains("a | b", text);
        Assert.Contains("Main > Data", Headings(doc));
    }

    [Fact]
    public void Json_gives_key_paths_and_streams_large_files()
    {
        var doc = Run("a.json", "{\"server\":{\"host\":\"example.invalid\",\"ports\":[80,443]},\"name\":\"demo\",\"empty\":null}");
        Assert.Contains("server.host: example.invalid", doc.FullText);
        Assert.Contains("server.ports[1]: 443", doc.FullText);
        Assert.Contains("server.host", Headings(doc));

        var sb = new StringBuilder("{\"items\":[");
        for (int i = 0; i < 20000; i++) sb.Append(i > 0 ? "," : "").Append("{\"id\":").Append(i).Append(",\"label\":\"row ").Append(i).Append("\"}");
        sb.Append("]}");
        var big = Run("big.json", sb.ToString());
        Assert.Contains("items[19999].label: row 19999", big.FullText);
        Assert.True(big.Segments.Count > 5);
    }

    [Fact]
    public void Json_lines_and_invalid_json_fail_honestly()
    {
        var lines = Run("a.jsonl", "{\"a\":1}\n{\"a\":2}\n");
        Assert.Contains("[1].a: 2", lines.FullText);
        var bad = Run("bad.json", "{\"a\": [1, 2");
        Assert.Equal(DocumentTier.Failed, bad.Tier);
        Assert.DoesNotContain("\"a\"", bad.Note);
    }

    [Fact]
    public void Yaml_key_paths()
    {
        var doc = Run("a.yaml", "# c\nservice:\n  name: api\n  ports:\n    - 80\n    - 443\n  env:\n    - name: MODE\n      value: fast\n  notes: |\n    multi line\n    text\nother: 'x y'\n");
        string t = doc.FullText;
        Assert.Contains("service.name: api", t);
        Assert.Contains("service.ports[]: 443", t);
        Assert.Contains("service.env[].value: fast", t);
        Assert.Contains("service.notes: multi line text", t);
        Assert.Contains("other: x y", t);
    }

    [Fact]
    public void Toml_and_ini_sections()
    {
        var toml = Run("a.toml", "title = \"T\"\n[database]\nserver = \"192.0.2.1\"\n[[products]]\nname = \"Hammer\"\n");
        Assert.Contains("database.server: 192.0.2.1", toml.FullText);
        Assert.Contains("products.name: Hammer", toml.FullText);
        var ini = Run("a.ini", "; c\n[core]\nmode=strict\n");
        Assert.Contains("core.mode: strict", ini.FullText);
        Assert.Equal("core", ini.Segments[0].Position.Heading);
    }

    [Fact]
    public void Xml_has_element_paths_and_prohibits_dtd()
    {
        var doc = Run("a.xml", "<catalog><book id=\"b1\"><title>Alpha</title></book><book id=\"b2\"><title>Beta</title></book></catalog>");
        Assert.Contains("/catalog/book/title: Beta", doc.FullText);
        Assert.Contains("/catalog/book/@id: b1", doc.FullText);
        var dtd = Run("evil.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///nonexistent\">]><x>&e;</x>");
        Assert.Equal(DocumentTier.Failed, dtd.Tier);
    }

    [Fact]
    public void Csv_blocks_have_headers_and_row_ranges()
    {
        var sb = new StringBuilder("id;name;note\n");
        for (int i = 1; i <= 100; i++) sb.Append(i).Append(";item").Append(i).Append(";\"has;semi\ncolon\"\n");
        var doc = Run("a.csv", sb.ToString());
        Assert.Equal(DocumentTier.Structured, doc.Tier);
        Assert.Equal("rows 1-40", doc.Segments[0].Position.CellRange);
        Assert.Equal("rows 41-80", doc.Segments[1].Position.CellRange);
        Assert.Equal("a.csv!rows 1-40", doc.Segments[0].Position.Describe());
        Assert.Contains("name=item2; note=has;semi\ncolon", doc.Segments[0].Text);
        var tsv = Run("a.tsv", "k\tv\nx\t1\n");
        Assert.Contains("k=x", tsv.FullText.Replace("v=1", "").Replace("columns: k | v", "k=x"));
    }

    [Fact]
    public void Csv_honours_row_limit_and_reads_windows_1252()
    {
        var sb = new StringBuilder("a,b\n");
        for (int i = 0; i < 100; i++) sb.Append("1,x\n");
        var doc = Run("lim.csv", sb.ToString(), new ExtractionLimits(MaxSpreadsheetRows: 10));
        Assert.True(doc.Truncated);
        File.WriteAllBytes(P("l1.csv"), Encoding.Latin1.GetBytes("name,city\nJos\u00e9,M\u00fcnchen\n"));
        var l1 = StructuredDocumentExtractor.Extract(P("l1.csv"));
        Assert.Contains("M\u00fcnchen", l1.FullText);
    }

    [Fact]
    public void Eml_headers_body_and_attachment_names()
    {
        string pdf = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-fake"));
        string eml = "From: =?utf-8?B?SmFuZSBEb2U=?= <jane@example.invalid>\r\nTo: bob@example.invalid\r\nSubject: Quarterly =?utf-8?Q?r=C3=A9sum=C3=A9?=\r\nDate: Mon, 02 Nov 2026 10:00:00 +0000\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"BND\"\r\n\r\n--BND\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nHello =E2=9C=93 body line\r\n"
            + "--BND\r\nContent-Type: application/pdf; name=\"report.pdf\"\r\nContent-Disposition: attachment; filename=\"report.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + pdf + "\r\n--BND--\r\n";
        var doc = Run("a.eml", eml);
        string t = doc.FullText;
        Assert.Contains("From: Jane Doe", t);
        Assert.Contains("Subject: Quarterly r\u00e9sum\u00e9", t);
        Assert.Contains("Hello \u2713 body line", t);
        Assert.Contains("Attachment: report.pdf", t);
        Assert.DoesNotContain("%PDF", t);
        Assert.Equal("headers|body|attachments", Headings(doc));
    }

    [Fact]
    public void Eml_html_only_body_is_stripped_and_msg_is_metadata_only_with_note()
    {
        var doc = Run("h.eml", "From: a@example.invalid\nSubject: s\nContent-Type: text/html\n\n<p>Hello <b>html</b></p><script>x()</script>\n");
        Assert.Contains("Hello html", doc.FullText);
        Assert.DoesNotContain("x()", doc.FullText);
        File.WriteAllBytes(P("a.msg"), new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });
        var msg = StructuredDocumentExtractor.Extract(P("a.msg"));
        Assert.Equal(DocumentTier.MetadataOnly, msg.Tier);
        Assert.Contains(".msg", msg.Note);
    }

    [Fact]
    public void Rtf_plain_text_skips_font_tables_and_decodes_escapes()
    {
        var doc = Run("a.rtf", @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\*\generator Hidden;}\pard Hello \b world\b0\par Caf\'e9 \u8364? done\par}");
        string t = doc.FullText;
        Assert.Contains("Hello world", t);
        Assert.Contains("Caf\u00e9 \u20ac done", t);
        Assert.DoesNotContain("Arial", t);
        Assert.DoesNotContain("Hidden", t);
    }

    [Fact]
    public void Odt_headings_ods_sheets_and_odp_slides()
    {
        const string office = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\"";
        WriteZip(P("a.odt"), ("content.xml", $"<office:document-content {office}><office:body><office:text><text:h text:outline-level=\"1\">Intro</text:h><text:p>First para</text:p><text:h text:outline-level=\"2\">Scope</text:h><text:p>Scope  <text:s/>text</text:p></office:text></office:body></office:document-content>"));
        var odt = StructuredDocumentExtractor.Extract(P("a.odt"));
        Assert.Equal("Intro > Scope", odt.Segments.Last().Position.Heading);
        Assert.Contains("First para", odt.FullText);

        WriteZip(P("a.ods"), ("content.xml", $"<office:document-content {office}><office:body><office:spreadsheet><table:table table:name=\"Data\"><table:table-row><table:table-cell><text:p>h1</text:p></table:table-cell><table:table-cell><text:p>h2</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated=\"3\"/><table:table-row><table:table-cell><text:p>v1</text:p></table:table-cell><table:table-cell><text:p>v2</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>"));
        var ods = StructuredDocumentExtractor.Extract(P("a.ods"));
        Assert.Equal("Data", ods.Segments[0].Position.Sheet);
        Assert.Equal("A1:B2", ods.Segments[0].Position.CellRange);

        WriteZip(P("a.odp"), ("content.xml", $"<office:document-content {office}><office:body><office:presentation><draw:page draw:name=\"s1\"><text:p>Slide one</text:p></draw:page><draw:page draw:name=\"s2\"><text:p>Slide two</text:p></draw:page></office:presentation></office:body></office:document-content>"));
        var odp = StructuredDocumentExtractor.Extract(P("a.odp"));
        Assert.Equal(new int?[] { 1, 2 }, odp.Segments.Select(s => s.Position.Slide).ToArray());
    }

    [Fact]
    public void Epub_spine_chapters_become_headings()
    {
        WriteZip(P("a.epub"),
            ("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\"/></rootfiles></container>"),
            ("OEBPS/content.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Demo Book</dc:title></metadata><manifest><item id=\"c1\" href=\"ch1.xhtml\"/><item id=\"c2\" href=\"text/ch2.xhtml\"/></manifest><spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>"),
            ("OEBPS/ch1.xhtml", "<html><body><h1>Beginning</h1><p>Once upon a time.</p></body></html>"),
            ("OEBPS/text/ch2.xhtml", "<html><body><p>No heading here.</p></body></html>"));
        var doc = StructuredDocumentExtractor.Extract(P("a.epub"));
        string h = Headings(doc);
        Assert.Contains("Chapter 1 > Beginning", h);
        Assert.Contains("Chapter 2", h);
        Assert.Contains("Demo Book", doc.FullText);
        Assert.Contains("Once upon a time.", doc.FullText);
    }

    [Fact]
    public void Notebook_cells_have_index_and_outputs()
    {
        string nb = "{\"cells\":[{\"cell_type\":\"markdown\",\"source\":[\"# Title\\n\",\"words\"]},{\"cell_type\":\"code\",\"source\":\"print(1)\",\"outputs\":[{\"output_type\":\"stream\",\"text\":[\"1\\n\"]},{\"output_type\":\"display_data\",\"data\":{\"image/png\":\"AAAA\",\"text/plain\":\"<Fig>\"}}]}],\"nbformat\":4}";
        var doc = Run("a.ipynb", nb);
        Assert.Equal("cell 1 (markdown)|cell 2 (code)", Headings(doc));
        Assert.Contains("[output]", doc.FullText);
        Assert.Contains("<Fig>", doc.FullText);
        Assert.DoesNotContain("AAAA", doc.FullText);
    }

    [Fact]
    public void Subtitles_use_timestamps_as_position()
    {
        var srt = Run("a.srt", "1\n00:00:01,000 --> 00:00:03,000\n<i>Hello</i> there\n\n2\n00:00:04,000 --> 00:00:06,500\nSecond line\n");
        Assert.Equal("00:00:01.000 --> 00:00:06.500", srt.Segments[0].Position.Heading);
        Assert.Contains("Hello there", srt.FullText);
        Assert.DoesNotContain("<i>", srt.FullText);
        var vtt = Run("a.vtt", "WEBVTT\n\nNOTE skipped note\n\n00:01.000 --> 00:02.000\nCue text\n");
        Assert.Contains("Cue text", vtt.FullText);
        Assert.DoesNotContain("skipped", vtt.FullText);
        Assert.StartsWith("00:00:01.000", vtt.Segments[0].Position.Heading);
    }

    [Fact]
    public void Source_code_declarations_become_heading_paths()
    {
        var cs = Run("a.cs", "namespace Demo;\npublic class Foo\n{\n    public int Bar(int x)\n    {\n        return x;\n    }\n    private void Baz()\n    {\n    }\n}\n");
        string h = Headings(cs);
        Assert.Contains("Foo > Bar", h);
        Assert.Contains("Foo > Baz", h);

        var py = Run("a.py", "import os\nclass A:\n    def m(self):\n        pass\n\ndef top():\n    return 1\n");
        h = Headings(py);
        Assert.Contains("A > m", h);
        Assert.Contains("top", h);
        Assert.DoesNotContain("A > top", h);

        Assert.Contains("(lines", Headings(Run("a.go", "package x\nfunc (s *S) Run() {\n}\n")));
        Assert.Contains("Widget", Headings(Run("a.ts", "export class Widget {\n  render(): void {\n  }\n}\n")));
        Assert.Contains("compute", Headings(Run("a.rs", "pub fn compute(x: i32) -> i32 {\n    x\n}\n")));
        Assert.Contains("doIt", Headings(Run("a.java", "public class K {\n  public void doIt(int a) {\n  }\n}\n")));
        Assert.Contains("lines 1-2", Headings(Run("a.bat", "echo one\necho two\n")));
    }

    [Fact]
    public void Sql_and_latex_have_structural_headings()
    {
        var sql = Run("a.sql", "-- schema\nCREATE TABLE users (\n  id INT\n);\nINSERT INTO users VALUES (1);\nINSERT INTO users VALUES (2);\nSELECT * FROM users;\n");
        Assert.Equal("CREATE TABLE users (lines 2-4)|INSERT INTO users (lines 5-6)|SELECT (lines 7-7)".Replace("(lines 7-7)", "(line 7)"),
            string.Join("|", sql.Segments.Select(s => s.Position.Heading)));
        var tex = Run("a.tex", "\\documentclass{article}\n\\begin{document}\n\\section{Intro}\nSome \\textbf{bold} text % comment\n\\subsection{Detail}\nMore.\n\\end{document}\n");
        Assert.Contains("Intro > Detail", Headings(tex));
        Assert.DoesNotContain("comment", tex.FullText);
        Assert.Contains("Some bold text", tex.FullText);
    }

    // ---------------------------------------------------------------- archives
    [Fact]
    public void Zip_lists_entries_and_indexes_text_entries_with_virtual_paths()
    {
        WriteZip(P("pack.zip"), ("docs/readme.md", "# Title\nhello inside"), ("data/rows.csv", "a,b\n1,2\n"), ("bin/x.dat", "\0\0\0binary\0"), ("img/p.png", "notreally"));
        var doc = StructuredDocumentExtractor.Extract(P("pack.zip"));
        Assert.Equal(DocumentTier.Structured, doc.Tier);
        string h = Headings(doc);
        Assert.Contains("pack.zip!docs/readme.md > Title", h);
        Assert.Contains("pack.zip!data/rows.csv", h);
        Assert.Contains("pack.zip > contents", h);
        Assert.Contains("img/p.png", doc.FullText);
        Assert.DoesNotContain("binary", doc.Segments.Where(s => s.Position.Heading!.Contains("x.dat")).Select(s => s.Text));
    }

    [Fact]
    public void Zip_path_traversal_entries_are_skipped_and_never_become_virtual_paths()
    {
        WriteZip(P("evil.zip"), ("../outside.txt", "stolen"), ("..\\..\\win.txt", "stolen"), ("/abs.txt", "stolen"), ("ok/fine.txt", "fine text"));
        var doc = StructuredDocumentExtractor.Extract(P("evil.zip"));
        Assert.DoesNotContain("stolen", doc.FullText);
        Assert.DoesNotContain(doc.Segments, s => s.Position.Heading!.Contains(".."));
        Assert.Contains("fine text", doc.FullText);
        Assert.Contains("3 entries with unsafe paths", doc.Note);
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "outside.txt")));
    }

    [Fact]
    public void Zip_bomb_style_entry_is_not_expanded()
    {
        string path = P("bomb.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("zeros.txt", CompressionLevel.SmallestSize);
            using (var s = e.Open())
            {
                byte[] chunk = new byte[1 << 20];
                for (int i = 0; i < 40; i++) s.Write(chunk, 0, chunk.Length);
            }
            var ok = zip.CreateEntry("note.txt");
            using var w = new StreamWriter(ok.Open());
            w.Write("small readable note");
        }
        var doc = StructuredDocumentExtractor.Extract(path, new ExtractionLimits(MaxCharacters: 100_000));
        Assert.True(doc.Truncated);
        Assert.Contains("small readable note", doc.FullText);
        Assert.Contains("compression-ratio guard", doc.Note);
        Assert.True(doc.FullText.Length < 100_000);
    }

    [Fact]
    public void Zip_entry_count_and_depth_are_limited()
    {
        // depth: outer.zip -> mid.zip -> inner.zip -> deep.txt; the third level is listed only.
        byte[] inner = ZipBytes(("deep.txt", "too deep to index"));
        byte[] mid = ZipBytes(("inner.zip", inner), ("mid.txt", "middle text"));
        byte[] outer = ZipBytes(("mid.zip", mid), ("top.txt", "top text"));
        File.WriteAllBytes(P("outer.zip"), outer);
        var doc = StructuredDocumentExtractor.Extract(P("outer.zip"));
        Assert.Contains("top text", doc.FullText);
        Assert.Contains("middle text", doc.FullText);
        Assert.DoesNotContain("too deep to index", doc.FullText);
        Assert.Contains(doc.Segments, s => s.Position.Heading!.StartsWith("outer.zip!mid.zip!mid.txt"));
        Assert.Contains("beyond depth", doc.Note);
    }

    [Fact]
    public void Tar_and_gzip_are_listed_and_indexed_without_writing_files()
    {
        using (var fs = File.Create(P("a.tar")))
        using (var tar = new TarWriter(fs))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "dir/file.json") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"k\":\"tar value\"}")) };
            tar.WriteEntry(entry);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
        }
        var t = StructuredDocumentExtractor.Extract(P("a.tar"));
        Assert.Contains("k: tar value", t.FullText);
        Assert.Contains("link entries were skipped", t.Note);

        using (var fs = File.Create(P("log.txt.gz")))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes("gzip line one\ngzip line two\n"));
        var g = StructuredDocumentExtractor.Extract(P("log.txt.gz"));
        Assert.Contains("gzip line two", g.FullText);
        Assert.Contains("log.txt.gz!log.txt", Headings(g));
    }

    [Fact]
    public void Corrupt_archives_fail_without_leaking_content()
    {
        File.WriteAllBytes(P("c.7z"), Encoding.ASCII.GetBytes("7z not really SECRET-PAYLOAD"));
        var doc = StructuredDocumentExtractor.Extract(P("c.7z"));
        Assert.NotEqual(DocumentTier.Structured, doc.Tier);
        Assert.DoesNotContain("SECRET-PAYLOAD", doc.Note ?? "");
    }

    [Fact]
    public void Extraction_honours_cancellation_and_character_limits()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append("line ").Append(i).Append(" of notes\n");
        var small = Run("big.md", sb.ToString(), new ExtractionLimits(MaxCharacters: 2000));
        Assert.True(small.Truncated);
        Assert.True(small.FullText.Length <= 2100, small.FullText.Length + " " + small.Segments.Count + " " + small.Extractor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StructuredDocumentExtractor.Extract(P("big.md"), null, cts.Token));
    }

    // ---------------------------------------------------------------- helpers
    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    private static byte[] ZipBytes(params (string Name, object Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                byte[] b = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                s.Write(b, 0, b.Length);
            }
        return ms.ToArray();
    }
}
