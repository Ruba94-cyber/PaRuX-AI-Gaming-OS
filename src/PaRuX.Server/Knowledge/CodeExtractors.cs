using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

// ------------------------------------------------------------------------------------------------------------------ source code
/// <summary>
/// Heuristic, regex-based declaration finder (no compiler, no network): class/function boundaries become the heading path,
/// everything else is cut into line-range blocks. Wrong guesses only affect the heading, never the indexed text.
/// </summary>
internal sealed class SourceCodeExtractor : StreamExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static Regex R(string p) => new(p, RegexOptions.Compiled, Timeout);

    private sealed record Lang(Regex[] Patterns, bool IndentBased, bool CommentHash);

    private static readonly Lang CSharp = new(new[]
    {
        R(@"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|file)\s+)*(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?|namespace)\s+(?<n>[A-Za-z_][\w.<>]*)"),
        R(@"^\s*(?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|extern|unsafe|new)\s+)+[\w<>\[\],.?\s]+?\s+(?<n>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\([^;]*$")
    }, false, false);
    private static readonly Lang Python = new(new[] { R(@"^\s*(?:async\s+)?def\s+(?<n>\w+)"), R(@"^\s*class\s+(?<n>\w+)") }, true, true);
    private static readonly Lang JavaScript = new(new[]
    {
        R(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(?:class|interface|enum|namespace)\s+(?<n>\w+)"),
        R(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<n>\w+)"),
        R(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<n>\w+)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*(?::[^=]+)?=>"),
        R(@"^\s*(?:(?:public|private|protected|static|async|readonly|get|set)\s+)*(?<n>(?!if\b|for\b|while\b|switch\b|catch\b|function\b|return\b)\w+)\s*\([^)]*\)\s*(?::\s*[^{]+)?\{\s*$")
    }, false, false);
    private static readonly Lang Java = new(new[]
    {
        R(@"^\s*(?:(?:public|private|protected|static|final|abstract|sealed)\s+)*(?:class|interface|enum|record)\s+(?<n>\w+)"),
        R(@"^\s*(?:(?:public|private|protected|static|final|abstract|synchronized|native|default)\s+)+[\w<>\[\],.?\s]+?\s+(?<n>\w+)\s*\([^;]*$")
    }, false, false);
    private static readonly Lang Go = new(new[] { R(@"^func\s+(?:\([^)]*\)\s*)?(?<n>\w+)"), R(@"^type\s+(?<n>\w+)\s+(?:struct|interface)") }, false, false);
    private static readonly Lang Rust = new(new[]
    {
        R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(?:const\s+)?fn\s+(?<n>\w+)"),
        R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:struct|enum|trait|mod)\s+(?<n>\w+)"),
        R(@"^\s*(?:unsafe\s+)?impl(?:<[^>]*>)?\s+(?<n>[\w:<>, ]+?)\s*(?:\{|where|$)")
    }, false, false);
    private static readonly Lang C = new(new[]
    {
        R(@"^\s*(?:typedef\s+)?(?:class|struct|namespace|union|enum(?:\s+class)?)\s+(?<n>[A-Za-z_]\w*)\s*(?:[:{]|$)"),
        R(@"^(?!\s*(?:if|for|while|switch|return|else|do|#)\b)[A-Za-z_][\w\s\*&:<>,~]*?\b(?<n>[A-Za-z_~][\w:~]*)\s*\([^;]*$")
    }, false, false);
    private static readonly Lang Generic = new(new[]
    {
        R(@"^\s*(?:export\s+)?(?:(?:public|private|protected|static|async|open|override|suspend)\s+)*(?:fun|func|def|function|sub)\s+(?<n>[A-Za-z_][\w.:]*)"),
        R(@"^\s*(?:(?:public|private|protected|abstract|final|open|data|sealed)\s+)*(?:class|module|object|trait|interface|struct|enum)\s+(?<n>\w+)"),
        R(@"^\s*(?<n>[A-Za-z_][\w-]*)\s*\(\)\s*\{")
    }, false, true);
    private static readonly Lang Plain = new(Array.Empty<Regex>(), false, false);

    private static readonly Dictionary<string, Lang> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = CSharp, [".py"] = Python, [".pyw"] = Python,
        [".js"] = JavaScript, [".jsx"] = JavaScript, [".mjs"] = JavaScript, [".cjs"] = JavaScript, [".ts"] = JavaScript, [".tsx"] = JavaScript,
        [".java"] = Java, [".go"] = Go, [".rs"] = Rust,
        [".c"] = C, [".h"] = C, [".cpp"] = C, [".cc"] = C, [".cxx"] = C, [".hpp"] = C, [".hh"] = C, [".hxx"] = C,
        [".kt"] = Generic, [".kts"] = Generic, [".swift"] = Generic, [".php"] = Generic, [".rb"] = Generic, [".lua"] = Generic,
        [".sh"] = Generic, [".bash"] = Generic, [".ps1"] = Generic, [".psm1"] = Generic, [".pl"] = Generic, [".scala"] = Generic,
        [".dart"] = Generic, [".vb"] = Generic, [".fs"] = Generic, [".r"] = Generic,
        [".bat"] = Plain, [".cmd"] = Plain, [".css"] = Plain, [".scss"] = Plain, [".less"] = Plain, [".vue"] = Plain, [".svelte"] = Plain,
        [".gradle"] = Plain, [".cmake"] = Plain, [".make"] = Plain, [".mk"] = Plain, [".dockerfile"] = Plain,
        // plain-text line blocks (also used for text entries inside archives)
        [".txt"] = Plain, [".log"] = Plain, [".text"] = Plain, [".rst"] = Plain, [".adoc"] = Plain, [".org"] = Plain, [".lst"] = Plain
    };

    private static readonly HashSet<string> NotClaimedAtTopLevel = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".text", ".rst", ".adoc", ".org", ".lst" };

    public override string Name => "source";
    protected override IReadOnlySet<string> Extensions => new HashSet<string>(ByExtension.Keys.Except(NotClaimedAtTopLevel), StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlySet<string> _extensions;
    public SourceCodeExtractor() { _extensions = new HashSet<string>(ByExtension.Keys.Except(NotClaimedAtTopLevel), StringComparer.OrdinalIgnoreCase); }

    public override bool CanHandle(string path) => _extensions.Contains(Path.GetExtension(path));

    /// <summary>Plain-text entries inside archives (which have no extractor of their own) are read as line blocks.</summary>
    internal static bool IsPlainText(string name) => ByExtension.ContainsKey(Path.GetExtension(name)) && NotClaimedAtTopLevel.Contains(Path.GetExtension(name));

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        Lang lang = ByExtension.TryGetValue(Path.GetExtension(fileName), out Lang? l) ? l : Plain;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        var stack = new List<(string Name, int Indent)>();
        sink.LineRanges = true;
        int lineNo = 0;
        string? Path_() => stack.Count == 0 ? null : string.Join(" > ", stack.Select(s => s.Name));

        for (string? line = lines.ReadLine(); line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            lineNo++;
            sink.LineNo = lineNo;
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            int indent = 0;
            foreach (char ch in line) { if (ch == ' ') indent++; else if (ch == '\t') indent += 4; else break; }

            bool comment = trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*") || (lang.CommentHash && trimmed.StartsWith('#'));
            string? name = null;
            if (!comment && lang.Patterns.Length > 0)
            {
                foreach (Regex p in lang.Patterns)
                {
                    try { Match m = p.Match(line); if (m.Success) { name = m.Groups["n"].Value.Trim(); break; } }
                    catch (RegexMatchTimeoutException) { }
                }
            }
            if (name != null && name.Length > 0)
            {
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                stack.Add((name, indent));
                sink.Section(Path_());
            }
            else if (lang.IndentBased && stack.Count > 0 && indent <= stack[^1].Indent && !comment)
            {
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                sink.Section(Path_());
            }
            sink.Line(line.TrimEnd());
            if (!lang.IndentBased && lang.Patterns.Length > 0 && trimmed[0] == '}' && stack.Count > 0 && indent <= stack[^1].Indent)
            {
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                sink.Section(Path_());
            }
        }
        sink.Flush();
        sink.LineRanges = false;
    }
}

// ------------------------------------------------------------------------------------------------------------------ SQL
internal sealed class SqlExtractor : StreamExtractor
{
    private static readonly IReadOnlySet<string> Exts = Set(".sql", ".ddl", ".psql", ".tsql");
    private static readonly Regex Statement = new(
        @"^\s*(?<k>create(?:\s+or\s+replace)?(?:\s+unique)?\s+\w+|alter\s+\w+|drop\s+\w+|insert\s+into|update|delete\s+from|select|with|grant|truncate\s+table|merge\s+into|exec(?:ute)?)\b\s*(?:if\s+(?:not\s+)?exists\s+)?(?<n>[\w.\[\]`""]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public override string Name => "sql";
    protected override IReadOnlySet<string> Extensions => Exts;

    public override void Read(Stream stream, string fileName, ExtractionSink sink)
    {
        TextReader? reader = sink.Open(stream);
        if (reader == null) return;
        var lines = new LineReader(reader, sink.Limits.SegmentCharacters);
        bool statementStart = true, inBlockComment = false;
        sink.LineRanges = true;
        int lineNo = 0;
        for (string? line = lines.ReadLine(); line != null && !sink.Exhausted; line = lines.ReadLine())
        {
            lineNo++;
            sink.LineNo = lineNo;
            string t = line.Trim();
            if (inBlockComment) { if (t.Contains("*/")) inBlockComment = false; continue; }
            if (t.Length == 0 || t.StartsWith("--")) continue;
            if (t.StartsWith("/*")) { if (!t.Contains("*/")) inBlockComment = true; continue; }
            if (statementStart)
            {
                string heading = "statement";
                Match m = Statement.Match(line);
                if (m.Success)
                {
                    heading = Regex.Replace(m.Groups["k"].Value, @"\s+", " ").ToUpperInvariant();
                    if (m.Groups["n"].Success && m.Groups["n"].Value.Length > 0) heading += " " + m.Groups["n"].Value.Trim('[', ']', '`', '"');
                }
                else if (t.Equals("go", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(heading, sink.Heading, StringComparison.Ordinal)) sink.Section(heading);
                statementStart = false;
            }
            sink.Line(line.TrimEnd());
            if (t.EndsWith(';') || t.Equals("go", StringComparison.OrdinalIgnoreCase)) statementStart = true;
        }
        sink.Flush();
        sink.LineRanges = false;
    }
}
