using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source contract: every process launch under src/PaRuX.Server must be listed, with a reviewed statement of how the
/// Gaming hard-off covers it. A new launch that nobody reviewed fails this test, so the class of "a child that keeps the
/// CPU/GPU while a game runs" gap cannot silently come back.
///
/// The check is per launching METHOD, not per file: each allow-list entry names the method(s) that contain the launch,
/// and a STOPPED-BY-HARDOFF / CALLER-GATED entry must show a Gaming mechanism inside that method's body, or name the
/// gating caller method (File.cs#Method) that the test then locates and checks. Comments and string literals are
/// stripped before matching, so a mention in prose never counts as a gate. src/PaRuX.App is covered by a second,
/// simpler inventory (<see cref="EveryAppProcessLaunchIsClassified"/>).
/// </summary>
public sealed class GamingHardOffProcessLaunchInventoryTests
{
    // Direct launches: new Process, target-typed "Process p = new()", Process.Start, P/Invoke CreateProcess/ShellExecute.
    private static readonly Regex LaunchSite = new(
        @"\bnew\s+Process\b|\bProcess\.Start\s*\(|\bProcess\??\s+[\w.]+\s*=\s*new\s*\(|\bCreateProcess\w*\s*\(|\bShellExecute\w*\s*\(",
        RegexOptions.Compiled);

    // A ProcessStartInfo handed to a wrapper: counted only for files that have no direct launch site of their own.
    private static readonly Regex StartInfoSite = new(@"\bnew\s+ProcessStartInfo\b", RegexOptions.Compiled);

    private static readonly string[] Classes = { "STOPPED-BY-HARDOFF", "CALLER-GATED", "NOT-AI", "GAMING-FEATURE" };
    private static readonly string[] AppClasses = { "GAMING-FEATURE", "USER-ACTION", "AI-CHILD", "NOT-AI" };

    // Identifiers that count as a Gaming mechanism once comments and strings are stripped.
    private static readonly string[] GamingMarkers =
    {
        "GamingChildGate", "StartOwned", "GamingHardOffWatch", "GamingCancellationToken", "GamingActive", "GamingMode",
        "IsGamingActive", "StopAll", "SpeechProcessAdmission", "StopForGaming", "GamingChildScope", "_gamingActive",
        "RuntimeUpdateDeferredException", "ForGaming", "ThrowIfGaming"
    };

    private sealed record Entry(string Path, int Count, string Class, string[] Methods, string[] Gates, string Reason);

    internal sealed record MethodBlock(string Name, int Start, int End);

    internal sealed class ScannedFile
    {
        public string Stripped = string.Empty;
        public List<MethodBlock> Methods = new();   // every method-like block, including local functions
        public List<MethodBlock> Outermost = new();  // methods not nested inside another method
        public List<(int Offset, bool Direct)> Sites = new();
        public bool Balanced = true;
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "tests", "PaRuX.Server.Tests", "GamingHardOffProcessLaunchAllowList.txt"))
                && Directory.Exists(Path.Combine(dir, "src", "PaRuX.Server")))
                return dir;
        }
        throw new DirectoryNotFoundException("The repository root with the Gaming hard-off allow-list was not found.");
    }

    private static List<Entry> ReadAllowList(string root)
    {
        var entries = new List<Entry>();
        foreach (string raw in File.ReadAllLines(Path.Combine(root, "tests", "PaRuX.Server.Tests", "GamingHardOffProcessLaunchAllowList.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split('|', 6);
            Assert.True(parts.Length == 6, "Allow-list line must be 'path|count|class|methods|gate|reason': " + line);
            Assert.True(int.TryParse(parts[1], out int count) && count > 0, "Bad launch-site count: " + line);
            entries.Add(new Entry(parts[0].Trim().Replace('\\', '/'), count, parts[2].Trim(),
                Split(parts[3]), Split(parts[4]), parts[5].Trim()));
        }
        return entries;
    }

    private static string[] Split(string value) =>
        value.Trim() == "-" ? Array.Empty<string>()
            : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- lexing: blank out comments and string/char literal contents, keep offsets and newlines ----

    internal static string Strip(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0, n = src.Length;
        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < n; k++) sb.Append(src[k] == '\n' || src[k] == '\r' ? src[k] : ' ');
        }
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int end = src.IndexOf('\n', i); if (end < 0) end = n;
                Blank(i, end); i = end; continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? n : end + 2;
                Blank(i, end); i = end; continue;
            }
            bool dollar = false, verbatim = false; int q = i;
            while (q < n && (src[q] == '$' || src[q] == '@'))
            {
                if (src[q] == '$') dollar = true; else verbatim = true;
                q++;
            }
            if (q < n && src[q] == '"' && (q == i || dollar || verbatim))
            {
                int quotes = 0; while (q + quotes < n && src[q + quotes] == '"') quotes++;
                int end;
                if (quotes >= 3)
                {
                    string closer = new string('"', quotes);
                    end = src.IndexOf(closer, q + quotes, StringComparison.Ordinal); end = end < 0 ? n : end + quotes;
                }
                else if (verbatim)
                {
                    int k = q + 1;
                    while (k < n) { if (src[k] == '"') { if (k + 1 < n && src[k + 1] == '"') { k += 2; continue; } break; } k++; }
                    end = Math.Min(n, k + 1);
                }
                else
                {
                    int k = q + 1;
                    while (k < n && src[k] != '"' && src[k] != '\n') { if (src[k] == '\\') k++; k++; }
                    end = Math.Min(n, k + 1);
                }
                Blank(i, end); i = end; continue;
            }
            if (c == '\'' && i + 2 < n)
            {
                int k = i + 1;
                if (src[k] == '\\') k++;
                k++;
                if (k < n && src[k] == '\'') { Blank(i, k + 1); i = k + 1; continue; }
            }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static readonly Regex TypeWord = new(@"\b(class|struct|interface|enum|namespace|record)\b", RegexOptions.Compiled);
    private static readonly Regex ControlWord = new(@"^\s*(if|for|foreach|while|switch|using|lock|catch|fixed|when|else|try|do|finally|unchecked|checked|unsafe)\b", RegexOptions.Compiled);
    private static readonly Regex NewWord = new(@"\bnew\b", RegexOptions.Compiled);

    internal static ScannedFile Scan(string source)
    {
        var file = new ScannedFile { Stripped = Strip(source) };
        string text = file.Stripped;
        var stack = new List<(char Kind, string Name, int Start)>();
        int headerStart = 0, parens = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') parens++;
            else if (c == ')') parens = Math.Max(0, parens - 1);
            else if (c == ';' && parens == 0) headerStart = i + 1;
            else if (c == '{')
            {
                string header = Regex.Replace(text.Substring(headerStart, i - headerStart), @"\[[^\[\]]*\]", " ");
                // Collapse generic/tuple type arguments first: "Task<(int A, string B)> Run(" has its first '(' inside the type.
                string flat = header;
                for (string previous = string.Empty; previous != flat;)
                {
                    previous = flat;
                    flat = Regex.Replace(flat, @"<[^<>]*>", "<>");
                }
                int paren = flat.IndexOf('(');
                string beforeParen = paren >= 0 ? flat[..paren] : flat;
                char kind = 'o'; string name = string.Empty;
                if (TypeWord.IsMatch(beforeParen)) kind = 't';
                else if (paren > 0 && !header.Contains("=>") && !NewWord.IsMatch(header) && !ControlWord.IsMatch(header)
                    && !beforeParen.Contains('='))
                {
                    Match id = Regex.Match(beforeParen, @"([A-Za-z_]\w*)\s*(<[^<>]*>)?\s*$");
                    if (id.Success) { kind = 'm'; name = id.Groups[1].Value; }
                }
                stack.Add((kind, name, i));
                headerStart = i + 1;
            }
            else if (c == '}')
            {
                if (stack.Count == 0) { file.Balanced = false; continue; }
                var top = stack[^1]; stack.RemoveAt(stack.Count - 1);
                if (top.Kind == 'm')
                {
                    var block = new MethodBlock(top.Name, top.Start, i + 1);
                    file.Methods.Add(block);
                    if (!stack.Any(s => s.Kind == 'm')) file.Outermost.Add(block);
                }
                headerStart = i + 1;
            }
        }
        if (stack.Count != 0) file.Balanced = false;
        foreach (Match m in LaunchSite.Matches(text)) file.Sites.Add((m.Index, true));
        if (file.Sites.Count == 0)
            foreach (Match m in StartInfoSite.Matches(text)) file.Sites.Add((m.Index, false));
        return file;
    }

    internal static string MethodOf(ScannedFile file, int offset) =>
        file.Outermost.FirstOrDefault(b => offset >= b.Start && offset < b.End)?.Name ?? "<member>";

    internal static string BodyOf(ScannedFile file, string method, int offset) =>
        file.Outermost.Where(b => b.Name == method && offset >= b.Start && offset < b.End)
            .Select(b => file.Stripped.Substring(b.Start, b.End - b.Start)).FirstOrDefault() ?? string.Empty;

    private static bool HasMarker(string body) => GamingMarkers.Any(m => body.Contains(m, StringComparison.Ordinal));

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static Dictionary<string, ScannedFile> ScanAll(string dir)
    {
        var result = new Dictionary<string, ScannedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in SourceFiles(dir))
            result[Path.GetRelativePath(dir, file).Replace('\\', '/')] = Scan(File.ReadAllText(file));
        return result;
    }

    [Fact]
    public void EveryProcessLaunchSiteIsListedWithItsReviewedGamingCoverage()
    {
        string root = RepoRoot();
        string server = Path.Combine(root, "src", "PaRuX.Server");
        List<Entry> allowList = ReadAllowList(root);
        Dictionary<string, ScannedFile> scanned = ScanAll(server);
        var problems = new List<string>();

        foreach ((string path, ScannedFile _) in scanned.Where(p => !p.Value.Balanced))
            problems.Add("The brace scanner lost balance in " + path + "; fix the scanner before trusting the inventory.");

        Assert.Equal(allowList.Count, allowList.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach ((string path, ScannedFile f) in scanned.Where(p => p.Value.Sites.Count > 0).OrderBy(p => p.Key))
        {
            string[] methods = f.Sites.Select(s => MethodOf(f, s.Offset)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Entry? entry = allowList.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            string suggestion = $"{path}|{f.Sites.Count}|<class>|{string.Join(",", methods)}|<gate or ->|<reason>";
            if (entry == null)
            {
                problems.Add("NEW process launch not reviewed: " + suggestion);
                continue;
            }
            if (entry.Count != f.Sites.Count)
                problems.Add($"Launch-site count changed in {path}: reviewed {entry.Count}, found {f.Sites.Count}. Review the new site. {suggestion}");
            if (!entry.Methods.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(methods))
                problems.Add($"Launching methods changed in {path}: reviewed [{string.Join(",", entry.Methods)}], found [{string.Join(",", methods)}].");
            if (entry.Class is not ("STOPPED-BY-HARDOFF" or "CALLER-GATED")) continue;

            // Each launching method must show a Gaming mechanism in its own body, unless the entry names the gating
            // caller method(s) (File.cs#Method). Named gates are always located and checked, so a rename cannot rot them.
            bool gatesOk = entry.Gates.Length > 0;
            foreach (string gate in entry.Gates)
            {
                string[] parts = gate.Split('#');
                if (parts.Length != 2) { problems.Add($"{path}: bad gate '{gate}', expected File.cs#Method."); gatesOk = false; continue; }
                string[] candidates = scanned.Keys.Where(k => k.EndsWith("/" + parts[0], StringComparison.OrdinalIgnoreCase)
                    || string.Equals(k, parts[0], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 0) { problems.Add($"{path}: gate file {parts[0]} does not exist."); gatesOk = false; continue; }
                bool found = false, gated = false;
                foreach (string candidate in candidates)
                {
                    ScannedFile g = scanned[candidate];
                    foreach (MethodBlock b in g.Methods.Where(m => m.Name == parts[1]))
                    {
                        found = true;
                        if (HasMarker(g.Stripped.Substring(b.Start, b.End - b.Start))) gated = true;
                    }
                }
                if (!found) { problems.Add($"{path}: gating caller method {gate} does not exist."); gatesOk = false; }
                else if (!gated) { problems.Add($"{path}: gating caller method {gate} has no Gaming mechanism in its body."); gatesOk = false; }
            }
            if (!gatesOk)
            {
                foreach ((int offset, _) in f.Sites)
                {
                    string method = MethodOf(f, offset);
                    if (!HasMarker(BodyOf(f, method, offset)))
                        problems.Add($"{path}: launching method '{method}' has no Gaming mechanism in its body. " +
                            "Gate it, or list the gating caller as File.cs#Method.");
                }
            }
        }
        foreach (Entry entry in allowList)
        {
            if (!scanned.TryGetValue(entry.Path, out ScannedFile? f) || f.Sites.Count == 0)
                problems.Add("Stale allow-list entry (no launch site any more): " + entry.Path);
            if (!Classes.Contains(entry.Class)) problems.Add($"Unknown class '{entry.Class}' for {entry.Path}.");
            if (entry.Reason.Length < 20) problems.Add("Coverage statement too short for " + entry.Path);
            if (entry.Methods.Length == 0) problems.Add("No launching method named for " + entry.Path);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(scanned.Values.Sum(f => f.Sites.Count) >= 50, "The inventory unexpectedly shrank; the scanner may be broken.");
    }

    /// <summary>The simpler second inventory: every process launch in the WPF app is recorded with a decision, so a new one is reviewed.</summary>
    [Fact]
    public void EveryAppProcessLaunchIsClassified()
    {
        string root = RepoRoot();
        string app = Path.Combine(root, "src", "PaRuX.App");
        string list = Path.Combine(root, "tests", "PaRuX.Server.Tests", "GamingHardOffAppProcessLaunchAllowList.txt");
        var entries = new Dictionary<string, (int Count, string Class, string Reason)>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(list))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split('|', 4);
            Assert.True(parts.Length == 4 && int.TryParse(parts[1], out _), "App allow-list line must be 'path|count|class|reason': " + line);
            entries[parts[0].Trim().Replace('\\', '/')] = (int.Parse(parts[1]), parts[2].Trim(), parts[3].Trim());
        }

        var problems = new List<string>();
        Dictionary<string, ScannedFile> scanned = ScanAll(app);
        foreach ((string path, ScannedFile f) in scanned.Where(p => p.Value.Sites.Count > 0).OrderBy(p => p.Key))
        {
            if (!entries.TryGetValue(path, out var entry))
                problems.Add($"NEW app process launch not classified: {path}|{f.Sites.Count}|<GAMING-FEATURE|USER-ACTION|AI-CHILD|NOT-AI>|<reason>");
            else if (entry.Count != f.Sites.Count)
                problems.Add($"App launch-site count changed in {path}: reviewed {entry.Count}, found {f.Sites.Count}.");
        }
        foreach ((string path, var entry) in entries)
        {
            if (!scanned.TryGetValue(path, out ScannedFile? f) || f.Sites.Count == 0) problems.Add("Stale app allow-list entry: " + path);
            if (!AppClasses.Contains(entry.Class)) problems.Add($"Unknown app class '{entry.Class}' for {path}.");
            if (entry.Reason.Length < 20) problems.Add("Decision statement too short for " + path);
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ScannerFindsTargetTypedStartInfoWrapperAndPInvokeLaunchesAndIgnoresComments()
    {
        ScannedFile f = Scan("""
            class A
            {
                void One() { Process p = new(); p.Start(); }
                [DllImport("kernel32")] static extern bool CreateProcessW(string a);
                void Four() { CreateProcessW("x"); ShellExecuteEx(ref info); }
                void Five() { // Process.Start(x) in a comment
                    var s = "new Process { }"; }
            }
            """);
        Assert.True(f.Balanced);
        Assert.Equal(4, f.Sites.Count); // Process p = new(), CreateProcessW declaration, CreateProcessW call, ShellExecuteEx
        Assert.Contains(f.Sites, s => MethodOf(f, s.Offset) == "One");
        Assert.Equal(2, f.Sites.Count(s => MethodOf(f, s.Offset) == "Four"));
        ScannedFile wrapper = Scan("class B { void Go() { var psi = new ProcessStartInfo(\"x\"); Helper.Run(psi); } }");
        Assert.Single(wrapper.Sites);
        Assert.False(wrapper.Sites[0].Direct);
        Assert.Equal("Go", MethodOf(wrapper, wrapper.Sites[0].Offset));
    }

    [Fact]
    public void AGamingMentionOutsideTheLaunchingMethodIsNotAGate()
    {
        ScannedFile f = Scan("""
            class C
            {
                bool GamingActive() => true;
                void Launch() { using var p = new Process(); p.Start(); }
                // GamingChildGate.StartOwned in a comment only
                void Other() { GamingChildGate.StartOwned(null, default); }
            }
            """);
        int offset = f.Sites.Single().Offset;
        Assert.Equal("Launch", MethodOf(f, offset));
        Assert.False(HasMarker(BodyOf(f, "Launch", offset)));
        Assert.True(HasMarker(BodyOf(f, "Other", f.Stripped.IndexOf("StartOwned", StringComparison.Ordinal))));
    }

    [Fact]
    public void BenchmarkAdaptersAreOnlyConstructedByTheGamingGatedEngineRace()
    {
        string root = RepoRoot();
        string server = Path.Combine(root, "src", "PaRuX.Server");
        var constructors = new[] { "new ExLlamaV3EngineBenchmarkAdapter(", "new WindowsMlGenAiEngineBenchmarkAdapter(" };
        foreach (string file in Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(server, file).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/")) continue;
            string text = File.ReadAllText(file);
            foreach (string constructor in constructors)
            {
                if (!text.Contains(constructor, StringComparison.Ordinal)) continue;
                Assert.Equal("ManagedRuntimeEngineRaceService.cs", relative);
                Assert.Contains("GamingHardOffWatch", text);
                Assert.Contains("_runtime.GamingActive", text);
            }
        }
    }
}
